// SPDX-License-Identifier: MPL-2.0
//
// MacXR Bridge: runtime side of the camera channel. Receives JPEG frames from the headset,
// decodes them to RGBA, and keeps the latest one (with world-space camera pose and intrinsics)
// for Unity to read through the C export (oxrsys_camera_*).

#pragma once

#include "../../common/CameraProtocol.h"

#include <cstdint>
#include <memory>
#include <atomic>
#include <condition_variable>
#include <mutex>
#include <thread>
#include <vector>

class CameraRelay
{
public:
    static CameraRelay& Get();

    void SetConnected(bool connected);
    bool IsConnected() const;

    // One spatial record payload from the camera channel (any thread).
    void OnMessage(const uint8_t* data, size_t size);

    // Latest frame for Unity. Copies RGBA into dst (dstCapacity bytes) when a new frame exists.
    // Returns the FrameInfo; info.valid = 0 when nothing available or capacity too small.
    oxr::protocol::camera::FrameInfo GetLatest(uint8_t* dst, uint32_t dstCapacity, uint32_t lastFrameId);

    // Latest environment depth (uint16 per pixel, row 0 = bottom). Copies into dst when newer than
    // lastFrameId and dstCount (in pixels) is large enough; dst == nullptr is a probe.
    oxr::protocol::camera::DepthInfo GetLatestDepth(uint16_t* dst, uint32_t dstCount, uint32_t lastFrameId);

    // Latest depth without copying, for runtime-side ray casts (XR_META_environment_raycast).
    // ageNs = time since it arrived. False when no depth is available.
    bool GetDepthSnapshot(oxr::protocol::camera::DepthInfo& info,
                          std::shared_ptr<const std::vector<uint16_t>>& pixels, int64_t& ageNs) const;

    // The camera channel's socket (StreamingServer), so the runtime can send to the headset on it.
    // -1 when detached. The sender holds sendMutex_ while writing, so a detach never races a write.
    void SetSocket(int socket);

    // Unity's audio thread: queue the Editor's final mix (interleaved float) for the headset.
    // Returns true while a headset is attached (the Editor then mutes the Mac's own output).
    bool PushAudio(const float* samples, uint32_t frames, uint32_t channels, uint32_t sampleRate);

    // Headset state sent by the client about once a second (developer HUD). False before the first one.
    bool GetClientStats(oxr::protocol::camera::ClientStats& stats, float& ageSeconds) const;

    // Session replay (Unity's BridgeSession): recorded depth replaces the headset's, so the runtime's environment
    // raycast (XR_META_environment_raycast) answers from the recording. While on, live depth is ignored.
    void SetDepthReplay(bool on);
    void InjectDepth(const oxr::protocol::camera::DepthInfo& info, const uint16_t* pixels, uint32_t count);

private:
    CameraRelay() = default;
    void AudioSenderLoop();
    void OnDepth(const uint8_t* payload, size_t size);

    // Decodes a JPEG buffer into rgba (width*height*4). Returns false on failure.
    bool DecodeJpeg(const uint8_t* jpeg, size_t size, uint32_t width, uint32_t height, std::vector<uint8_t>& rgba);

    mutable std::mutex mutex_;
    bool connected_ = false;
    std::vector<uint8_t> rgba_;
    oxr::protocol::camera::FrameInfo info_ = {};
    uint32_t nextFrameId_ = 1;
    oxr::protocol::camera::Pose headFromCamera_ = {{0, 0, 0, 1}, {0, 0, 0}};
    std::shared_ptr<const std::vector<uint16_t>> depth_;
    int64_t depthReceivedNs_ = 0;

    // Audio (runtime -> headset).
    std::mutex sendMutex_;
    int socket_ = -1;
    std::mutex audioMutex_;
    std::condition_variable audioCv_;
    std::vector<int16_t> audioQueue_;     // interleaved s16 waiting to be sent
    uint32_t audioChannels_ = 0;
    uint32_t audioRate_ = 0;
    uint64_t audioSequence_ = 0;
    std::thread audioSender_;
    std::atomic<bool> audioSenderStarted_{false};
    oxr::protocol::camera::DepthInfo depthInfo_ = {};
    oxr::protocol::camera::ClientStats clientStats_ = {};
    int64_t clientStatsNs_ = 0;
    std::atomic<bool> depthReplay_{false};
    uint32_t nextDepthId_ = 1;
};
