// SPDX-License-Identifier: MPL-2.0
//
// Link para Mac: runtime side of the camera channel. Receives JPEG frames from the headset,
// decodes them to RGBA, and keeps the latest one (with world-space camera pose and intrinsics)
// for Unity to read through the C export (oxrsys_camera_*).

#pragma once

#include "../../common/CameraProtocol.h"

#include <cstdint>
#include <mutex>
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

private:
    CameraRelay() = default;

    // Decodes a JPEG buffer into rgba (width*height*4). Returns false on failure.
    bool DecodeJpeg(const uint8_t* jpeg, size_t size, uint32_t width, uint32_t height, std::vector<uint8_t>& rgba);

    mutable std::mutex mutex_;
    bool connected_ = false;
    std::vector<uint8_t> rgba_;
    oxr::protocol::camera::FrameInfo info_ = {};
    uint32_t nextFrameId_ = 1;
    oxr::protocol::camera::Pose headFromCamera_ = {{0, 0, 0, 1}, {0, 0, 0}};
};
