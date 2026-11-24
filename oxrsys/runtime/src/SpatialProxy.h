// SPDX-License-Identifier: MPL-2.0
//
// MacXR Bridge: runtime side of the spatial channel. Sends requests to the headset client,
// matches responses, keeps the latest streamed pose of every remote space and queues the
// completion events the client forwards until the app thread drains them (xrPollEvent).

#pragma once

#include "../../common/SpatialProtocol.h"

#include <openxr/openxr.h>

#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <functional>
#include <mutex>
#include <optional>
#include <unordered_map>
#include <vector>

class SpatialProxy
{
public:
    using SendFn = std::function<bool(const void* data, size_t size)>;

    struct PendingEvent
    {
        oxr::protocol::spatial::Op op;
        std::vector<uint8_t> payload;
    };

    static SpatialProxy& Get();

    // Transport (called by StreamingServer's spatial thread).
    void AttachTransport(SendFn send);
    void DetachTransport();
    bool IsConnected() const;
    uint32_t GetFeatures() const; // Hello features of the connected client
    bool IsScenePermissionGranted() const;

    // Incoming spatial record payload (any thread).
    void OnMessage(const uint8_t* data, size_t size);

    // Synchronous request: returns the response payload (after MsgHeader), or nullopt on
    // timeout/disconnect. Blocks the calling (app) thread.
    std::optional<std::vector<uint8_t>> Call(oxr::protocol::spatial::Op op, const void* payload,
                                             size_t size,
                                             std::chrono::milliseconds timeout = std::chrono::milliseconds(4000));

    // Fire-and-forget request (no response expected).
    bool Notify(oxr::protocol::spatial::Op op, const void* payload, size_t size);

    uint64_t NextAsyncRequestId();

    // Latest streamed pose of a remote space (client XrSpace handle value).
    bool GetSpacePose(uint64_t spaceId, XrPosef* pose, XrSpaceLocationFlags* flags) const;
    void ForgetSpace(uint64_t spaceId);

    // Events forwarded by the client, drained on the app thread.
    std::vector<PendingEvent> TakeEvents();

private:
    SpatialProxy() = default;

    struct PendingCall
    {
        bool done = false;
        std::vector<uint8_t> response;
    };

    bool SendMsg(oxr::protocol::spatial::MsgKind kind, oxr::protocol::spatial::Op op,
                     uint32_t requestId, const void* payload, size_t size);

    mutable std::mutex transportMutex_;
    SendFn send_;
    uint32_t features_ = 0;
    bool scenePermission_ = false;

    std::mutex callsMutex_;
    std::condition_variable callsCv_;
    std::unordered_map<uint32_t, PendingCall*> calls_;
    uint32_t nextRequestId_ = 1;
    uint64_t nextAsyncRequestId_ = 1;

    struct StoredPose
    {
        XrPosef pose;
        XrSpaceLocationFlags flags;
    };
    mutable std::mutex posesMutex_;
    std::unordered_map<uint64_t, StoredPose> poses_;

    std::mutex eventsMutex_;
    std::deque<PendingEvent> events_;
};
