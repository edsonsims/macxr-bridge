// SPDX-License-Identifier: MPL-2.0
//
// Link para Mac: client side of the camera channel. Receives JPEG frames from HeadsetCamera (JNI)
// and forwards them to the macOS runtime over port 9949 with the head pose at send time.

#pragma once

#include "CameraProtocol.h"

#include <openxr/openxr.h>

#include <atomic>
#include <cstdint>
#include <mutex>
#include <thread>
#include <vector>

class CameraSender
{
public:
    static CameraSender& Get();

    // Transport lifecycle (owned by XrApp alongside the other sockets).
    // Attach also starts a reader for runtime -> client messages on the same socket (audio).
    void Attach(int socket);
    void Detach();
    // After the owner shut the socket down: wait for the reader to finish.
    void JoinReader();
    bool IsAttached() const { return socket_.load() >= 0; }

    // XrApp updates the latest head pose (STAGE space) every frame.
    void SetHeadPose(const XrPosef& pose);

    // Called from the JNI thread with a fresh JPEG frame.
    void PushFrame(const uint8_t* jpeg, int length, int width, int height,
                   float fx, float fy, float cx, float cy,
                   const XrPosef& headFromCamera, int64_t captureTimeNs);

    // Sends one environment-depth image (uint16 per pixel). Blocks on the socket: call from a worker thread.
    void PushDepth(const oxr::protocol::camera::DepthHeader& header, const uint16_t* pixels);

private:
    CameraSender() = default;
    bool SendAll(const void* data, size_t size);
    void ReaderLoop(int socket);
    std::thread reader_;

    std::atomic<int> socket_{-1};
    std::mutex sendMutex_;
    std::mutex poseMutex_;
    XrPosef headPose_{{0, 0, 0, 1}, {0, 0, 0}};
    bool helloSent_ = false;
};
