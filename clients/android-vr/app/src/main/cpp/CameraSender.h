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
#include <vector>

class CameraSender
{
public:
    static CameraSender& Get();

    // Transport lifecycle (owned by XrApp alongside the other sockets).
    void Attach(int socket);
    void Detach();
    bool IsAttached() const { return socket_.load() >= 0; }

    // XrApp updates the latest head pose (STAGE space) every frame.
    void SetHeadPose(const XrPosef& pose);

    // Called from the JNI thread with a fresh JPEG frame.
    void PushFrame(const uint8_t* jpeg, int length, int width, int height,
                   float fx, float fy, float cx, float cy,
                   const XrPosef& headFromCamera, int64_t captureTimeNs);

private:
    CameraSender() = default;
    bool SendAll(const void* data, size_t size);

    std::atomic<int> socket_{-1};
    std::mutex sendMutex_;
    std::mutex poseMutex_;
    XrPosef headPose_{{0, 0, 0, 1}, {0, 0, 0}};
    bool helloSent_ = false;
};
