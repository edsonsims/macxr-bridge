// SPDX-License-Identifier: MPL-2.0

#include "SpatialProxy.h"

#include <spdlog/spdlog.h>

#include <cstring>

using namespace oxr::protocol::spatial;

SpatialProxy& SpatialProxy::Get()
{
    static SpatialProxy instance;
    return instance;
}

void SpatialProxy::AttachTransport(SendFn send)
{
    {
        std::lock_guard<std::mutex> lock(transportMutex_);
        send_ = std::move(send);
        features_ = 0;
        scenePermission_ = false;
    }
    {
        std::lock_guard<std::mutex> lock(posesMutex_);
        poses_.clear();
    }
    spdlog::info("SpatialProxy: headset spatial channel attached");
}

void SpatialProxy::DetachTransport()
{
    {
        std::lock_guard<std::mutex> lock(transportMutex_);
        if (!send_)
        {
            return;
        }
        send_ = nullptr;
        features_ = 0;
    }
    {
        // Fail every call still waiting for the headset.
        std::lock_guard<std::mutex> lock(callsMutex_);
        for (auto& [id, call] : calls_)
        {
            call->done = true;
            call->response.clear();
        }
        calls_.clear();
        callsCv_.notify_all();
    }
    {
        std::lock_guard<std::mutex> lock(posesMutex_);
        poses_.clear();
    }
    spdlog::info("SpatialProxy: headset spatial channel detached");
}

bool SpatialProxy::IsConnected() const
{
    std::lock_guard<std::mutex> lock(transportMutex_);
    return static_cast<bool>(send_);
}

uint32_t SpatialProxy::GetFeatures() const
{
    std::lock_guard<std::mutex> lock(transportMutex_);
    return send_ ? features_ : 0;
}

bool SpatialProxy::IsScenePermissionGranted() const
{
    std::lock_guard<std::mutex> lock(transportMutex_);
    return send_ && scenePermission_;
}

bool SpatialProxy::SendMsg(MsgKind kind, Op op, uint32_t requestId, const void* payload, size_t size)
{
    std::vector<uint8_t> buffer(sizeof(MsgHeader) + size);
    MsgHeader header = {};
    header.kind = kind;
    header.op = op;
    header.requestId = requestId;
    header.payloadSize = static_cast<uint32_t>(size);
    std::memcpy(buffer.data(), &header, sizeof(header));
    if (size > 0)
    {
        std::memcpy(buffer.data() + sizeof(header), payload, size);
    }
    std::lock_guard<std::mutex> lock(transportMutex_);
    return send_ && send_(buffer.data(), buffer.size());
}

std::optional<std::vector<uint8_t>> SpatialProxy::Call(Op op, const void* payload, size_t size,
                                                       std::chrono::milliseconds timeout)
{
    if (!IsConnected())
    {
        return std::nullopt;
    }
    PendingCall call;
    uint32_t requestId = 0;
    {
        std::lock_guard<std::mutex> lock(callsMutex_);
        requestId = nextRequestId_++;
        if (requestId == 0)
        {
            requestId = nextRequestId_++;
        }
        calls_[requestId] = &call;
    }
    if (!SendMsg(MsgKind::Request, op, requestId, payload, size))
    {
        std::lock_guard<std::mutex> lock(callsMutex_);
        calls_.erase(requestId);
        spdlog::warn("SpatialProxy: failed to send request op={:#x}", static_cast<int>(op));
        return std::nullopt;
    }
    std::unique_lock<std::mutex> lock(callsMutex_);
    callsCv_.wait_for(lock, timeout, [&call] { return call.done; });
    calls_.erase(requestId);
    if (!call.done)
    {
        spdlog::warn("SpatialProxy: request op={:#x} timed out after {} ms", static_cast<int>(op), timeout.count());
        return std::nullopt;
    }
    if (call.response.empty())
    {
        // Disconnected while waiting.
        return std::nullopt;
    }
    return call.response;
}

bool SpatialProxy::Notify(Op op, const void* payload, size_t size)
{
    return SendMsg(MsgKind::Request, op, 0, payload, size);
}

uint64_t SpatialProxy::NextAsyncRequestId()
{
    std::lock_guard<std::mutex> lock(callsMutex_);
    return nextAsyncRequestId_++;
}

void SpatialProxy::OnMessage(const uint8_t* data, size_t size)
{
    if (size < sizeof(MsgHeader))
    {
        return;
    }
    MsgHeader header = {};
    std::memcpy(&header, data, sizeof(header));
    if (header.version != SPATIAL_VERSION || header.payloadSize != size - sizeof(MsgHeader))
    {
        spdlog::warn("SpatialProxy: malformed message (version {} size {} payload {})", header.version, size, header.payloadSize);
        return;
    }
    const uint8_t* payload = data + sizeof(MsgHeader);
    const size_t payloadSize = header.payloadSize;

    switch (header.kind)
    {
        case MsgKind::Hello:
        {
            if (payloadSize >= sizeof(Hello))
            {
                Hello hello = {};
                std::memcpy(&hello, payload, sizeof(hello));
                std::lock_guard<std::mutex> lock(transportMutex_);
                features_ = hello.features;
                scenePermission_ = hello.scenePermissionGranted != 0;
                spdlog::info("SpatialProxy: headset spatial features={:#x} scenePermission={}", hello.features, hello.scenePermissionGranted);
            }
            break;
        }
        case MsgKind::Response:
        {
            std::lock_guard<std::mutex> lock(callsMutex_);
            auto it = calls_.find(header.requestId);
            if (it != calls_.end())
            {
                it->second->response.assign(payload, payload + payloadSize);
                if (it->second->response.empty())
                {
                    it->second->response.push_back(0); // never empty: empty means disconnect
                }
                it->second->done = true;
                callsCv_.notify_all();
            }
            break;
        }
        case MsgKind::Poses:
        {
            if (payloadSize < sizeof(SpacePosesHeader))
            {
                break;
            }
            SpacePosesHeader posesHeader = {};
            std::memcpy(&posesHeader, payload, sizeof(posesHeader));
            const size_t needed = sizeof(SpacePosesHeader) + static_cast<size_t>(posesHeader.count) * sizeof(SpacePose);
            if (payloadSize < needed)
            {
                break;
            }
            std::lock_guard<std::mutex> lock(posesMutex_);
            const uint8_t* cursor = payload + sizeof(SpacePosesHeader);
            for (uint32_t i = 0; i < posesHeader.count; ++i, cursor += sizeof(SpacePose))
            {
                SpacePose sp = {};
                std::memcpy(&sp, cursor, sizeof(sp));
                StoredPose stored = {};
                stored.flags = static_cast<XrSpaceLocationFlags>(sp.locationFlags);
                stored.pose.orientation = {sp.poseInStage.orientation.x, sp.poseInStage.orientation.y,
                                           sp.poseInStage.orientation.z, sp.poseInStage.orientation.w};
                stored.pose.position = {sp.poseInStage.position.x, sp.poseInStage.position.y, sp.poseInStage.position.z};
                poses_[sp.spaceId] = stored;
            }
            break;
        }
        case MsgKind::Event:
        {
            PendingEvent event;
            event.op = header.op;
            event.payload.assign(payload, payload + payloadSize);
            std::lock_guard<std::mutex> lock(eventsMutex_);
            events_.push_back(std::move(event));
            break;
        }
        default:
            break;
    }
}

bool SpatialProxy::GetSpacePose(uint64_t spaceId, XrPosef* pose, XrSpaceLocationFlags* flags) const
{
    std::lock_guard<std::mutex> lock(posesMutex_);
    auto it = poses_.find(spaceId);
    if (it == poses_.end())
    {
        return false;
    }
    *pose = it->second.pose;
    *flags = it->second.flags;
    return true;
}

void SpatialProxy::ForgetSpace(uint64_t spaceId)
{
    std::lock_guard<std::mutex> lock(posesMutex_);
    poses_.erase(spaceId);
}

std::vector<SpatialProxy::PendingEvent> SpatialProxy::TakeEvents()
{
    std::lock_guard<std::mutex> lock(eventsMutex_);
    std::vector<PendingEvent> out(std::make_move_iterator(events_.begin()), std::make_move_iterator(events_.end()));
    events_.clear();
    return out;
}
