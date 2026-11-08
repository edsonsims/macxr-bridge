// SPDX-License-Identifier: MPL-2.0

#include "SpatialBridge.h"

#include "Protocol.h"

#include <android/log.h>
#include <sys/socket.h>
#include <unistd.h>

#include <algorithm>
#include <chrono>
#include <cstring>

#define LOG_TAG "OXRSys-Spatial"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

namespace protocol = oxr::protocol;
namespace sp = oxr::protocol::spatial;

namespace
{

bool SendAllBytes(int socket, const void* data, size_t size)
{
    const auto* bytes = static_cast<const uint8_t*>(data);
    size_t sent = 0;
    while (sent < size)
    {
        ssize_t n = send(socket, bytes + sent, size - sent, MSG_NOSIGNAL);
        if (n < 0)
        {
            if (errno == EINTR) continue;
            return false;
        }
        if (n == 0) return false;
        sent += static_cast<size_t>(n);
    }
    return true;
}

bool ReadAllBytes(int socket, void* data, size_t size)
{
    auto* bytes = static_cast<uint8_t*>(data);
    size_t received = 0;
    while (received < size)
    {
        ssize_t n = recv(socket, bytes + received, size - received, 0);
        if (n < 0)
        {
            if (errno == EINTR) continue;
            return false;
        }
        if (n == 0) return false;
        received += static_cast<size_t>(n);
    }
    return true;
}

template <typename T>
void Append(std::vector<uint8_t>& buffer, const T& value)
{
    const auto* bytes = reinterpret_cast<const uint8_t*>(&value);
    buffer.insert(buffer.end(), bytes, bytes + sizeof(T));
}

void AppendBytes(std::vector<uint8_t>& buffer, const void* data, size_t size)
{
    const auto* bytes = static_cast<const uint8_t*>(data);
    buffer.insert(buffer.end(), bytes, bytes + size);
}

template <typename T>
bool Read(const uint8_t*& cursor, size_t& remaining, T& out)
{
    if (remaining < sizeof(T)) return false;
    std::memcpy(&out, cursor, sizeof(T));
    cursor += sizeof(T);
    remaining -= sizeof(T);
    return true;
}

sp::Pose ToWire(const XrPosef& pose)
{
    sp::Pose out;
    out.orientation = {pose.orientation.x, pose.orientation.y, pose.orientation.z, pose.orientation.w};
    out.position = {pose.position.x, pose.position.y, pose.position.z};
    return out;
}

XrPosef FromWire(const sp::Pose& pose)
{
    XrPosef out;
    out.orientation = {pose.orientation.x, pose.orientation.y, pose.orientation.z, pose.orientation.w};
    out.position = {pose.position.x, pose.position.y, pose.position.z};
    return out;
}

sp::Uuid ToWire(const XrUuidEXT& uuid)
{
    sp::Uuid out;
    std::memcpy(out.data, uuid.data, sizeof(out.data));
    return out;
}

XrUuidEXT FromWire(const sp::Uuid& uuid)
{
    XrUuidEXT out;
    std::memcpy(out.data, uuid.data, sizeof(out.data));
    return out;
}

XrSpace ToSpace(uint64_t id)
{
    return reinterpret_cast<XrSpace>(static_cast<uintptr_t>(id));
}

uint64_t FromSpace(XrSpace space)
{
    return static_cast<uint64_t>(reinterpret_cast<uintptr_t>(space));
}

int64_t NowNs()
{
    return std::chrono::duration_cast<std::chrono::nanoseconds>(
               std::chrono::steady_clock::now().time_since_epoch()).count();
}

template <typename T>
T LoadFn(XrInstance instance, const char* name)
{
    PFN_xrVoidFunction fn = nullptr;
    if (XR_FAILED(xrGetInstanceProcAddr(instance, name, &fn)))
    {
        return nullptr;
    }
    return reinterpret_cast<T>(fn);
}

constexpr int64_t kPosesIntervalNs = 50'000'000; // 20 Hz

} // namespace

SpatialBridge::~SpatialBridge()
{
    Stop();
}

void SpatialBridge::Initialize(XrInstance instance, XrSession session, XrSpace stageSpace,
                               const std::vector<const char*>& enabledExtensions)
{
    instance_ = instance;
    session_ = session;
    stageSpace_ = stageSpace;
    features_ = 0;

    auto has = [&enabledExtensions](const char* name) {
        return std::any_of(enabledExtensions.begin(), enabledExtensions.end(),
                           [name](const char* ext) { return std::strcmp(ext, name) == 0; });
    };

    if (has(XR_FB_SPATIAL_ENTITY_EXTENSION_NAME))
    {
        xrCreateSpatialAnchorFB_ = LoadFn<PFN_xrCreateSpatialAnchorFB>(instance, "xrCreateSpatialAnchorFB");
        xrGetSpaceUuidFB_ = LoadFn<PFN_xrGetSpaceUuidFB>(instance, "xrGetSpaceUuidFB");
        xrEnumerateSpaceSupportedComponentsFB_ = LoadFn<PFN_xrEnumerateSpaceSupportedComponentsFB>(instance, "xrEnumerateSpaceSupportedComponentsFB");
        xrSetSpaceComponentStatusFB_ = LoadFn<PFN_xrSetSpaceComponentStatusFB>(instance, "xrSetSpaceComponentStatusFB");
        xrGetSpaceComponentStatusFB_ = LoadFn<PFN_xrGetSpaceComponentStatusFB>(instance, "xrGetSpaceComponentStatusFB");
        if (xrCreateSpatialAnchorFB_ && xrGetSpaceUuidFB_ && xrSetSpaceComponentStatusFB_)
        {
            features_ |= sp::FEATURE_ANCHORS;
        }
    }
    if (has(XR_FB_SPATIAL_ENTITY_STORAGE_EXTENSION_NAME))
    {
        xrSaveSpaceFB_ = LoadFn<PFN_xrSaveSpaceFB>(instance, "xrSaveSpaceFB");
        xrEraseSpaceFB_ = LoadFn<PFN_xrEraseSpaceFB>(instance, "xrEraseSpaceFB");
    }
    if (has(XR_FB_SPATIAL_ENTITY_QUERY_EXTENSION_NAME))
    {
        xrQuerySpacesFB_ = LoadFn<PFN_xrQuerySpacesFB>(instance, "xrQuerySpacesFB");
        xrRetrieveSpaceQueryResultsFB_ = LoadFn<PFN_xrRetrieveSpaceQueryResultsFB>(instance, "xrRetrieveSpaceQueryResultsFB");
    }
    if (has(XR_META_SPATIAL_ENTITY_PERSISTENCE_EXTENSION_NAME))
    {
        xrSaveSpacesMETA_ = LoadFn<PFN_xrSaveSpacesMETA>(instance, "xrSaveSpacesMETA");
        xrEraseSpacesMETA_ = LoadFn<PFN_xrEraseSpacesMETA>(instance, "xrEraseSpacesMETA");
    }
    if (has(XR_META_SPATIAL_ENTITY_DISCOVERY_EXTENSION_NAME))
    {
        xrDiscoverSpacesMETA_ = LoadFn<PFN_xrDiscoverSpacesMETA>(instance, "xrDiscoverSpacesMETA");
        xrRetrieveSpaceDiscoveryResultsMETA_ = LoadFn<PFN_xrRetrieveSpaceDiscoveryResultsMETA>(instance, "xrRetrieveSpaceDiscoveryResultsMETA");
    }
    if (xrSaveSpacesMETA_ && xrDiscoverSpacesMETA_ && xrRetrieveSpaceDiscoveryResultsMETA_)
    {
        features_ |= sp::FEATURE_PERSISTENCE_META;
    }
    if (has(XR_FB_SPATIAL_ENTITY_CONTAINER_EXTENSION_NAME))
    {
        xrGetSpaceContainerFB_ = LoadFn<PFN_xrGetSpaceContainerFB>(instance, "xrGetSpaceContainerFB");
    }
    if (has(XR_FB_SCENE_EXTENSION_NAME))
    {
        xrGetSpaceBoundingBox2DFB_ = LoadFn<PFN_xrGetSpaceBoundingBox2DFB>(instance, "xrGetSpaceBoundingBox2DFB");
        xrGetSpaceBoundingBox3DFB_ = LoadFn<PFN_xrGetSpaceBoundingBox3DFB>(instance, "xrGetSpaceBoundingBox3DFB");
        xrGetSpaceSemanticLabelsFB_ = LoadFn<PFN_xrGetSpaceSemanticLabelsFB>(instance, "xrGetSpaceSemanticLabelsFB");
        xrGetSpaceBoundary2DFB_ = LoadFn<PFN_xrGetSpaceBoundary2DFB>(instance, "xrGetSpaceBoundary2DFB");
        xrGetSpaceRoomLayoutFB_ = LoadFn<PFN_xrGetSpaceRoomLayoutFB>(instance, "xrGetSpaceRoomLayoutFB");
        if (xrGetSpaceContainerFB_ && xrGetSpaceRoomLayoutFB_ && xrGetSpaceSemanticLabelsFB_)
        {
            features_ |= sp::FEATURE_SCENE;
        }
    }
    if (has(XR_FB_SCENE_CAPTURE_EXTENSION_NAME))
    {
        xrRequestSceneCaptureFB_ = LoadFn<PFN_xrRequestSceneCaptureFB>(instance, "xrRequestSceneCaptureFB");
        if (xrRequestSceneCaptureFB_) features_ |= sp::FEATURE_SCENE_CAPTURE;
    }
    if (has(XR_META_SPATIAL_ENTITY_MESH_EXTENSION_NAME))
    {
        xrGetSpaceTriangleMeshMETA_ = LoadFn<PFN_xrGetSpaceTriangleMeshMETA>(instance, "xrGetSpaceTriangleMeshMETA");
        if (xrGetSpaceTriangleMeshMETA_) features_ |= sp::FEATURE_MESH;
    }
    LOGI("Spatial bridge ready: features=0x%x", features_);
}

void SpatialBridge::Start(int socket, bool scenePermissionGranted)
{
    Stop();
    if (socket < 0 || features_ == 0)
    {
        return;
    }
    socket_ = socket;
    scenePermission_ = scenePermissionGranted;
    running_.store(true);
    lastPosesSentNs_ = 0;

    sp::Hello hello = {};
    hello.features = features_;
    hello.scenePermissionGranted = scenePermissionGranted ? 1 : 0;
    SendMsg(MsgKind::Hello, Op::Hello, 0, &hello, sizeof(hello));

    reader_ = std::thread(&SpatialBridge::ReaderThread, this, socket);
    LOGI("Spatial bridge serving the runtime (scene permission %d)", scenePermissionGranted);
}

void SpatialBridge::Stop()
{
    const bool wasRunning = running_.exchange(false);
    if (reader_.joinable())
    {
        if (socket_ >= 0)
        {
            shutdown(socket_, SHUT_RDWR); // unblock the reader
        }
        reader_.join();
    }
    socket_ = -1;
    if (wasRunning)
    {
        DestroyTrackedSpaces();
        std::lock_guard<std::mutex> lock(requestsMutex_);
        asyncRequests_.clear();
        LOGI("Spatial bridge stopped");
    }
}

void SpatialBridge::ReaderThread(int socket)
{
    std::vector<uint8_t> payload;
    while (running_.load())
    {
        protocol::TcpRecordHeader header = {};
        if (!ReadAllBytes(socket, &header, sizeof(header)))
        {
            break;
        }
        if (header.magic != protocol::TCP_RECORD_MAGIC || header.version != protocol::TCP_RECORD_VERSION ||
            header.payloadSize > protocol::TCP_MAX_RECORD_PAYLOAD)
        {
            LOGW("Spatial channel: bad record header");
            break;
        }
        payload.resize(header.payloadSize);
        if (!payload.empty() && !ReadAllBytes(socket, payload.data(), payload.size()))
        {
            break;
        }
        if (header.type == protocol::TcpRecordType::Disconnect)
        {
            break;
        }
        if (header.type != protocol::TcpRecordType::Spatial || payload.size() < sizeof(sp::MsgHeader))
        {
            continue;
        }
        sp::MsgHeader msg = {};
        std::memcpy(&msg, payload.data(), sizeof(msg));
        if (msg.version != sp::SPATIAL_VERSION || msg.payloadSize + sizeof(sp::MsgHeader) != payload.size())
        {
            LOGW("Spatial channel: bad message header");
            continue;
        }
        if (msg.kind == MsgKind::Request)
        {
            HandleRequest(msg.op, msg.requestId, payload.data() + sizeof(sp::MsgHeader), msg.payloadSize);
        }
    }
    running_.store(false);
    LOGI("Spatial channel reader ended");
}

bool SpatialBridge::SendMsg(MsgKind kind, Op op, uint32_t requestId, const void* payload, size_t size)
{
    std::lock_guard<std::mutex> lock(sendMutex_);
    if (socket_ < 0)
    {
        return false;
    }
    protocol::TcpRecordHeader record = {};
    record.type = protocol::TcpRecordType::Spatial;
    record.payloadSize = static_cast<uint32_t>(sizeof(sp::MsgHeader) + size);
    sp::MsgHeader header = {};
    header.kind = kind;
    header.op = op;
    header.requestId = requestId;
    header.payloadSize = static_cast<uint32_t>(size);
    return SendAllBytes(socket_, &record, sizeof(record)) &&
           SendAllBytes(socket_, &header, sizeof(header)) &&
           (size == 0 || SendAllBytes(socket_, payload, size));
}

bool SpatialBridge::Respond(uint32_t requestId, const void* payload, size_t size)
{
    return SendMsg(MsgKind::Response, Op::None, requestId, payload, size);
}

bool SpatialBridge::SendEvent(Op op, const void* payload, size_t size)
{
    return SendMsg(MsgKind::Event, op, 0, payload, size);
}

// A space id the runtime sends must be one of ours: a stale id (spaces are destroyed on
// disconnect) must never reach the headset runtime.
XrSpace SpatialBridge::ResolveSpace(uint64_t spaceId)
{
    XrSpace space = ToSpace(spaceId);
    std::lock_guard<std::mutex> lock(spacesMutex_);
    return spaces_.count(space) != 0 ? space : XR_NULL_HANDLE;
}

void SpatialBridge::TrackSpace(XrSpace space)
{
    if (space == XR_NULL_HANDLE) return;
    std::lock_guard<std::mutex> lock(spacesMutex_);
    spaces_.insert(space);
}

void SpatialBridge::UntrackSpace(XrSpace space)
{
    std::lock_guard<std::mutex> lock(spacesMutex_);
    spaces_.erase(space);
}

void SpatialBridge::DestroyTrackedSpaces()
{
    std::unordered_set<XrSpace> spaces;
    {
        std::lock_guard<std::mutex> lock(spacesMutex_);
        spaces.swap(spaces_);
    }
    for (XrSpace space : spaces)
    {
        xrDestroySpace(space);
    }
    if (!spaces.empty())
    {
        LOGI("Destroyed %zu spaces held for the runtime", spaces.size());
    }
}

void SpatialBridge::MapAsyncRequest(XrAsyncRequestIdFB clientId, uint64_t runtimeId)
{
    std::lock_guard<std::mutex> lock(requestsMutex_);
    asyncRequests_[clientId] = runtimeId;
}

uint64_t SpatialBridge::TakeAsyncRequestId(XrAsyncRequestIdFB clientId, bool erase)
{
    std::lock_guard<std::mutex> lock(requestsMutex_);
    auto it = asyncRequests_.find(clientId);
    if (it == asyncRequests_.end())
    {
        return 0;
    }
    const uint64_t id = it->second;
    if (erase)
    {
        asyncRequests_.erase(it);
    }
    return id;
}

// ─── Requests (reader thread) ────────────────────────────────────────────────

void SpatialBridge::HandleRequest(Op op, uint32_t requestId, const uint8_t* payload, size_t size)
{
    lastRequestNs_.store(std::chrono::duration_cast<std::chrono::nanoseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count());
    const uint8_t* cursor = payload;
    size_t remaining = size;
    sp::RespResult simple = {};
    simple.result = XR_ERROR_FUNCTION_UNSUPPORTED;

    switch (op)
    {
        case Op::CreateSpatialAnchor:
        {
            sp::ReqCreateSpatialAnchor req = {};
            if (!Read(cursor, remaining, req) || !xrCreateSpatialAnchorFB_) break;
            XrSpatialAnchorCreateInfoFB info = {XR_TYPE_SPATIAL_ANCHOR_CREATE_INFO_FB};
            info.space = stageSpace_;
            info.poseInSpace = FromWire(req.poseInStage);
            info.time = frameTime_.load();
            XrAsyncRequestIdFB clientId = 0;
            simple.result = xrCreateSpatialAnchorFB_(session_, &info, &clientId);
            if (XR_SUCCEEDED(simple.result)) MapAsyncRequest(clientId, req.asyncRequestId);
            LOGI("xrCreateSpatialAnchorFB -> %d (request %llu)", simple.result, (unsigned long long)req.asyncRequestId);
            break;
        }
        case Op::GetSpaceUuid:
        {
            sp::ReqSpace req = {};
            sp::RespUuid resp = {};
            resp.result = XR_ERROR_FUNCTION_UNSUPPORTED;
            XrSpace space = Read(cursor, remaining, req) ? ResolveSpace(req.spaceId) : XR_NULL_HANDLE;
            if (space == XR_NULL_HANDLE) resp.result = XR_ERROR_HANDLE_INVALID;
            else if (xrGetSpaceUuidFB_)
            {
                XrUuidEXT uuid = {};
                resp.result = xrGetSpaceUuidFB_(space, &uuid);
                resp.uuid = ToWire(uuid);
            }
            Respond(requestId, &resp, sizeof(resp));
            return;
        }
        case Op::EnumerateSpaceComponents:
        {
            sp::ReqSpace req = {};
            sp::RespComponents resp = {};
            resp.result = XR_ERROR_FUNCTION_UNSUPPORTED;
            XrSpace space = Read(cursor, remaining, req) ? ResolveSpace(req.spaceId) : XR_NULL_HANDLE;
            if (space == XR_NULL_HANDLE) resp.result = XR_ERROR_HANDLE_INVALID;
            else if (xrEnumerateSpaceSupportedComponentsFB_)
            {
                XrSpaceComponentTypeFB types[sp::MAX_COMPONENT_TYPES] = {};
                uint32_t count = 0;
                resp.result = xrEnumerateSpaceSupportedComponentsFB_(space, sp::MAX_COMPONENT_TYPES, &count, types);
                resp.count = std::min<uint32_t>(count, sp::MAX_COMPONENT_TYPES);
                for (uint32_t i = 0; i < resp.count; ++i) resp.types[i] = static_cast<uint32_t>(types[i]);
            }
            Respond(requestId, &resp, sizeof(resp));
            return;
        }
        case Op::SetSpaceComponentStatus:
        {
            sp::ReqSetComponentStatus req = {};
            if (!Read(cursor, remaining, req) || !xrSetSpaceComponentStatusFB_) break;
            XrSpace space = ResolveSpace(req.spaceId);
            if (space == XR_NULL_HANDLE) { simple.result = XR_ERROR_HANDLE_INVALID; break; }
            XrSpaceComponentStatusSetInfoFB info = {XR_TYPE_SPACE_COMPONENT_STATUS_SET_INFO_FB};
            info.componentType = static_cast<XrSpaceComponentTypeFB>(req.componentType);
            info.enabled = req.enabled ? XR_TRUE : XR_FALSE;
            info.timeout = req.timeoutNs;
            XrAsyncRequestIdFB clientId = 0;
            simple.result = xrSetSpaceComponentStatusFB_(space, &info, &clientId);
            if (XR_SUCCEEDED(simple.result)) MapAsyncRequest(clientId, req.asyncRequestId);
            break;
        }
        case Op::GetSpaceComponentStatus:
        {
            sp::ReqGetComponentStatus req = {};
            sp::RespComponentStatus resp = {};
            resp.result = XR_ERROR_FUNCTION_UNSUPPORTED;
            XrSpace space = Read(cursor, remaining, req) ? ResolveSpace(req.spaceId) : XR_NULL_HANDLE;
            if (space == XR_NULL_HANDLE) resp.result = XR_ERROR_HANDLE_INVALID;
            else if (xrGetSpaceComponentStatusFB_)
            {
                XrSpaceComponentStatusFB status = {XR_TYPE_SPACE_COMPONENT_STATUS_FB};
                resp.result = xrGetSpaceComponentStatusFB_(space, static_cast<XrSpaceComponentTypeFB>(req.componentType), &status);
                resp.enabled = status.enabled ? 1 : 0;
                resp.changePending = status.changePending ? 1 : 0;
            }
            Respond(requestId, &resp, sizeof(resp));
            return;
        }
        case Op::DestroySpace:
        {
            sp::ReqSpace req = {};
            if (!Read(cursor, remaining, req)) break;
            XrSpace space = ResolveSpace(req.spaceId);
            if (space == XR_NULL_HANDLE) { simple.result = XR_ERROR_HANDLE_INVALID; break; }
            UntrackSpace(space);
            simple.result = xrDestroySpace(space);
            break;
        }
        case Op::SaveSpaceFB:
        {
            sp::ReqSaveSpaceFB req = {};
            if (!Read(cursor, remaining, req) || !xrSaveSpaceFB_) break;
            XrSpaceSaveInfoFB info = {XR_TYPE_SPACE_SAVE_INFO_FB};
            info.space = ResolveSpace(req.spaceId);
            if (info.space == XR_NULL_HANDLE) { simple.result = XR_ERROR_HANDLE_INVALID; break; }
            info.location = static_cast<XrSpaceStorageLocationFB>(req.location);
            info.persistenceMode = static_cast<XrSpacePersistenceModeFB>(req.persistenceMode);
            XrAsyncRequestIdFB clientId = 0;
            simple.result = xrSaveSpaceFB_(session_, &info, &clientId);
            if (XR_SUCCEEDED(simple.result)) MapAsyncRequest(clientId, req.asyncRequestId);
            break;
        }
        case Op::EraseSpaceFB:
        {
            sp::ReqEraseSpaceFB req = {};
            if (!Read(cursor, remaining, req) || !xrEraseSpaceFB_) break;
            XrSpaceEraseInfoFB info = {XR_TYPE_SPACE_ERASE_INFO_FB};
            info.space = ResolveSpace(req.spaceId);
            if (info.space == XR_NULL_HANDLE) { simple.result = XR_ERROR_HANDLE_INVALID; break; }
            info.location = static_cast<XrSpaceStorageLocationFB>(req.location);
            XrAsyncRequestIdFB clientId = 0;
            simple.result = xrEraseSpaceFB_(session_, &info, &clientId);
            if (XR_SUCCEEDED(simple.result)) MapAsyncRequest(clientId, req.asyncRequestId);
            break;
        }
        case Op::SaveSpacesMETA:
        {
            sp::ReqSpaceList req = {};
            if (!Read(cursor, remaining, req) || !xrSaveSpacesMETA_) break;
            if (remaining < static_cast<size_t>(req.spaceCount) * sizeof(uint64_t)) break;
            std::vector<XrSpace> spaces(req.spaceCount);
            bool valid = true;
            for (uint32_t i = 0; i < req.spaceCount; ++i)
            {
                uint64_t id = 0;
                Read(cursor, remaining, id);
                spaces[i] = ResolveSpace(id);
                valid &= spaces[i] != XR_NULL_HANDLE;
            }
            if (!valid) { simple.result = XR_ERROR_HANDLE_INVALID; break; }
            XrSpacesSaveInfoMETA info = {XR_TYPE_SPACES_SAVE_INFO_META};
            info.spaceCount = req.spaceCount;
            info.spaces = spaces.data();
            XrAsyncRequestIdFB clientId = 0;
            simple.result = xrSaveSpacesMETA_(session_, &info, &clientId);
            if (XR_SUCCEEDED(simple.result)) MapAsyncRequest(clientId, req.asyncRequestId);
            LOGI("xrSaveSpacesMETA(%u) -> %d", req.spaceCount, simple.result);
            break;
        }
        case Op::EraseSpacesMETA:
        {
            sp::ReqEraseSpacesMETA req = {};
            if (!Read(cursor, remaining, req) || !xrEraseSpacesMETA_) break;
            if (remaining < static_cast<size_t>(req.spaceCount) * sizeof(uint64_t) +
                                static_cast<size_t>(req.uuidCount) * sizeof(sp::Uuid)) break;
            std::vector<XrSpace> spaces(req.spaceCount);
            bool valid = true;
            for (uint32_t i = 0; i < req.spaceCount; ++i)
            {
                uint64_t id = 0;
                Read(cursor, remaining, id);
                spaces[i] = ResolveSpace(id);
                valid &= spaces[i] != XR_NULL_HANDLE;
            }
            if (!valid) { simple.result = XR_ERROR_HANDLE_INVALID; break; }
            std::vector<XrUuidEXT> uuids(req.uuidCount);
            for (uint32_t i = 0; i < req.uuidCount; ++i)
            {
                sp::Uuid uuid = {};
                Read(cursor, remaining, uuid);
                uuids[i] = FromWire(uuid);
            }
            XrSpacesEraseInfoMETA info = {XR_TYPE_SPACES_ERASE_INFO_META};
            info.spaceCount = req.spaceCount;
            info.spaces = spaces.empty() ? nullptr : spaces.data();
            info.uuidCount = req.uuidCount;
            info.uuids = uuids.empty() ? nullptr : uuids.data();
            XrAsyncRequestIdFB clientId = 0;
            simple.result = xrEraseSpacesMETA_(session_, &info, &clientId);
            if (XR_SUCCEEDED(simple.result)) MapAsyncRequest(clientId, req.asyncRequestId);
            LOGI("xrEraseSpacesMETA(%u spaces, %u uuids) -> %d", req.spaceCount, req.uuidCount, simple.result);
            break;
        }
        case Op::QuerySpacesFB:
        {
            sp::ReqQuerySpacesFB req = {};
            if (!Read(cursor, remaining, req) || !xrQuerySpacesFB_) break;
            if (remaining < static_cast<size_t>(req.uuidCount) * sizeof(sp::Uuid)) break;
            std::vector<XrUuidEXT> uuids(req.uuidCount);
            for (uint32_t i = 0; i < req.uuidCount; ++i)
            {
                sp::Uuid uuid = {};
                Read(cursor, remaining, uuid);
                uuids[i] = FromWire(uuid);
            }
            XrSpaceStorageLocationFilterInfoFB locationFilter = {XR_TYPE_SPACE_STORAGE_LOCATION_FILTER_INFO_FB};
            locationFilter.location = static_cast<XrSpaceStorageLocationFB>(req.storageLocation);
            XrSpaceUuidFilterInfoFB uuidFilter = {XR_TYPE_SPACE_UUID_FILTER_INFO_FB};
            uuidFilter.uuidCount = req.uuidCount;
            uuidFilter.uuids = uuids.empty() ? nullptr : uuids.data();
            XrSpaceComponentFilterInfoFB componentFilter = {XR_TYPE_SPACE_COMPONENT_FILTER_INFO_FB};
            componentFilter.componentType = static_cast<XrSpaceComponentTypeFB>(req.componentType);
            XrSpaceFilterInfoBaseHeaderFB* filter = nullptr;
            if (req.filterKind == sp::FILTER_UUIDS)
            {
                filter = reinterpret_cast<XrSpaceFilterInfoBaseHeaderFB*>(&uuidFilter);
            }
            else if (req.filterKind == sp::FILTER_COMPONENT)
            {
                filter = reinterpret_cast<XrSpaceFilterInfoBaseHeaderFB*>(&componentFilter);
            }
            if (filter != nullptr && req.storageLocation != 0)
            {
                filter->next = &locationFilter;
            }
            XrSpaceQueryInfoFB info = {XR_TYPE_SPACE_QUERY_INFO_FB};
            info.queryAction = XR_SPACE_QUERY_ACTION_LOAD_FB;
            info.maxResultCount = req.maxResultCount;
            info.timeout = req.timeoutNs;
            info.filter = filter;
            info.excludeFilter = nullptr;
            XrAsyncRequestIdFB clientId = 0;
            simple.result = xrQuerySpacesFB_(session_, reinterpret_cast<XrSpaceQueryInfoBaseHeaderFB*>(&info), &clientId);
            if (XR_SUCCEEDED(simple.result)) MapAsyncRequest(clientId, req.asyncRequestId);
            LOGI("xrQuerySpacesFB(filter=%u, max=%u) -> %d", req.filterKind, req.maxResultCount, simple.result);
            break;
        }
        case Op::DiscoverSpacesMETA:
        {
            sp::ReqDiscoverSpacesMETA req = {};
            if (!Read(cursor, remaining, req) || !xrDiscoverSpacesMETA_) break;
            std::vector<XrSpaceFilterUuidMETA> uuidFilters;
            std::vector<XrSpaceFilterComponentMETA> componentFilters;
            std::vector<std::vector<XrUuidEXT>> uuidStorage;
            uuidFilters.reserve(req.filterCount);
            componentFilters.reserve(req.filterCount);
            uuidStorage.reserve(req.filterCount);
            std::vector<std::pair<uint32_t, size_t>> order; // kind, index
            bool ok = true;
            for (uint32_t f = 0; f < req.filterCount && ok; ++f)
            {
                sp::DiscoveryFilter filter = {};
                if (!Read(cursor, remaining, filter)) { ok = false; break; }
                if (filter.kind == sp::FILTER_UUIDS)
                {
                    if (remaining < static_cast<size_t>(filter.uuidCount) * sizeof(sp::Uuid)) { ok = false; break; }
                    std::vector<XrUuidEXT> uuids(filter.uuidCount);
                    for (uint32_t i = 0; i < filter.uuidCount; ++i)
                    {
                        sp::Uuid uuid = {};
                        Read(cursor, remaining, uuid);
                        uuids[i] = FromWire(uuid);
                    }
                    uuidStorage.push_back(std::move(uuids));
                    XrSpaceFilterUuidMETA xrFilter = {XR_TYPE_SPACE_FILTER_UUID_META};
                    xrFilter.uuidCount = filter.uuidCount;
                    uuidFilters.push_back(xrFilter);
                    order.emplace_back(sp::FILTER_UUIDS, uuidFilters.size() - 1);
                    uuidFilters.back().uuids = uuidStorage.back().empty() ? nullptr : uuidStorage.back().data();
                }
                else if (filter.kind == sp::FILTER_COMPONENT)
                {
                    XrSpaceFilterComponentMETA xrFilter = {XR_TYPE_SPACE_FILTER_COMPONENT_META};
                    xrFilter.componentType = static_cast<XrSpaceComponentTypeFB>(filter.componentType);
                    componentFilters.push_back(xrFilter);
                    order.emplace_back(sp::FILTER_COMPONENT, componentFilters.size() - 1);
                }
            }
            if (!ok) break;
            // uuidStorage vectors may have reallocated: fix the pointers.
            for (size_t i = 0; i < uuidFilters.size(); ++i)
            {
                uuidFilters[i].uuids = uuidStorage[i].empty() ? nullptr : uuidStorage[i].data();
            }
            std::vector<const XrSpaceFilterBaseHeaderMETA*> filters;
            for (const auto& [kind, index] : order)
            {
                filters.push_back(kind == sp::FILTER_UUIDS
                                      ? reinterpret_cast<const XrSpaceFilterBaseHeaderMETA*>(&uuidFilters[index])
                                      : reinterpret_cast<const XrSpaceFilterBaseHeaderMETA*>(&componentFilters[index]));
            }
            XrSpaceDiscoveryInfoMETA info = {XR_TYPE_SPACE_DISCOVERY_INFO_META};
            info.filterCount = static_cast<uint32_t>(filters.size());
            info.filters = filters.empty() ? nullptr : filters.data();
            XrAsyncRequestIdFB clientId = 0;
            simple.result = xrDiscoverSpacesMETA_(session_, &info, &clientId);
            if (XR_SUCCEEDED(simple.result)) MapAsyncRequest(clientId, req.asyncRequestId);
            LOGI("xrDiscoverSpacesMETA(%u filters) -> %d", info.filterCount, simple.result);
            break;
        }
        case Op::GetSpaceContainer:
        {
            sp::ReqSpace req = {};
            std::vector<uint8_t> resp;
            sp::RespContainer head = {};
            head.result = XR_ERROR_FUNCTION_UNSUPPORTED;
            std::vector<XrUuidEXT> uuids;
            XrSpace space = Read(cursor, remaining, req) ? ResolveSpace(req.spaceId) : XR_NULL_HANDLE;
            if (space == XR_NULL_HANDLE) head.result = XR_ERROR_HANDLE_INVALID;
            else if (xrGetSpaceContainerFB_)
            {
                XrSpaceContainerFB container = {XR_TYPE_SPACE_CONTAINER_FB};
                head.result = xrGetSpaceContainerFB_(session_, space, &container);
                if (XR_SUCCEEDED(head.result) && container.uuidCountOutput > 0)
                {
                    uuids.resize(container.uuidCountOutput);
                    container.uuidCapacityInput = container.uuidCountOutput;
                    container.uuids = uuids.data();
                    head.result = xrGetSpaceContainerFB_(session_, space, &container);
                    uuids.resize(XR_SUCCEEDED(head.result) ? container.uuidCountOutput : 0);
                }
            }
            head.uuidCount = static_cast<uint32_t>(uuids.size());
            Append(resp, head);
            for (const auto& uuid : uuids) Append(resp, ToWire(uuid));
            Respond(requestId, resp.data(), resp.size());
            return;
        }
        case Op::GetSpaceBoundingBox2D:
        {
            sp::ReqSpace req = {};
            sp::RespBoundingBox2D resp = {};
            resp.result = XR_ERROR_FUNCTION_UNSUPPORTED;
            XrSpace space = Read(cursor, remaining, req) ? ResolveSpace(req.spaceId) : XR_NULL_HANDLE;
            if (space == XR_NULL_HANDLE) resp.result = XR_ERROR_HANDLE_INVALID;
            else if (xrGetSpaceBoundingBox2DFB_)
            {
                XrRect2Df rect = {};
                resp.result = xrGetSpaceBoundingBox2DFB_(session_, space, &rect);
                resp.offsetX = rect.offset.x; resp.offsetY = rect.offset.y;
                resp.extentWidth = rect.extent.width; resp.extentHeight = rect.extent.height;
            }
            Respond(requestId, &resp, sizeof(resp));
            return;
        }
        case Op::GetSpaceBoundingBox3D:
        {
            sp::ReqSpace req = {};
            sp::RespBoundingBox3D resp = {};
            resp.result = XR_ERROR_FUNCTION_UNSUPPORTED;
            XrSpace space = Read(cursor, remaining, req) ? ResolveSpace(req.spaceId) : XR_NULL_HANDLE;
            if (space == XR_NULL_HANDLE) resp.result = XR_ERROR_HANDLE_INVALID;
            else if (xrGetSpaceBoundingBox3DFB_)
            {
                XrRect3DfFB rect = {};
                resp.result = xrGetSpaceBoundingBox3DFB_(session_, space, &rect);
                resp.offset = {rect.offset.x, rect.offset.y, rect.offset.z};
                resp.extent = {rect.extent.width, rect.extent.height, rect.extent.depth};
            }
            Respond(requestId, &resp, sizeof(resp));
            return;
        }
        case Op::GetSpaceSemanticLabels:
        {
            sp::ReqSemanticLabels req = {};
            sp::RespSemanticLabels resp = {};
            resp.result = XR_ERROR_FUNCTION_UNSUPPORTED;
            XrSpace space = Read(cursor, remaining, req) ? ResolveSpace(req.spaceId) : XR_NULL_HANDLE;
            if (space == XR_NULL_HANDLE) resp.result = XR_ERROR_HANDLE_INVALID;
            else if (xrGetSpaceSemanticLabelsFB_ && remaining >= req.labelsByteCount)
            {
                std::string recognized(reinterpret_cast<const char*>(cursor), req.labelsByteCount);
                XrSemanticLabelsSupportInfoFB support = {XR_TYPE_SEMANTIC_LABELS_SUPPORT_INFO_FB};
                support.flags = static_cast<XrSemanticLabelsSupportFlagsFB>(req.supportFlags);
                support.recognizedLabels = recognized.c_str();
                XrSemanticLabelsFB labels = {XR_TYPE_SEMANTIC_LABELS_FB};
                if (req.labelsByteCount > 0)
                {
                    labels.next = &support;
                }
                char buffer[sp::MAX_SEMANTIC_LABELS_BYTES] = {};
                labels.bufferCapacityInput = sizeof(buffer);
                labels.buffer = buffer;
                resp.result = xrGetSpaceSemanticLabelsFB_(session_, space, &labels);
                if (XR_SUCCEEDED(resp.result))
                {
                    const uint32_t count = std::min<uint32_t>(labels.bufferCountOutput, sizeof(buffer));
                    static int logged = 0;
                    if (logged < 6)
                    {
                        ++logged;
                        LOGI("Semantic labels raw: count=%u last=%d text='%.*s' (recognized %u bytes, flags 0x%llx)", count,
                             count > 0 ? static_cast<int>(static_cast<unsigned char>(buffer[count - 1])) : -1,
                             static_cast<int>(count), buffer, req.labelsByteCount, (unsigned long long)req.supportFlags);
                    }
                    // bufferCountOutput includes the terminator.
                    resp.byteCount = count > 0 && buffer[count - 1] == '\0' ? count - 1 : count;
                    std::memcpy(resp.labels, buffer, resp.byteCount);
                }
            }
            Respond(requestId, &resp, sizeof(resp));
            return;
        }
        case Op::GetSpaceBoundary2D:
        {
            sp::ReqSpace req = {};
            std::vector<uint8_t> resp;
            sp::RespBoundary2D head = {};
            head.result = XR_ERROR_FUNCTION_UNSUPPORTED;
            std::vector<XrVector2f> vertices;
            XrSpace space = Read(cursor, remaining, req) ? ResolveSpace(req.spaceId) : XR_NULL_HANDLE;
            if (space == XR_NULL_HANDLE) head.result = XR_ERROR_HANDLE_INVALID;
            else if (xrGetSpaceBoundary2DFB_)
            {
                XrBoundary2DFB boundary = {XR_TYPE_BOUNDARY_2D_FB};
                head.result = xrGetSpaceBoundary2DFB_(session_, space, &boundary);
                if (XR_SUCCEEDED(head.result) && boundary.vertexCountOutput > 0)
                {
                    vertices.resize(boundary.vertexCountOutput);
                    boundary.vertexCapacityInput = boundary.vertexCountOutput;
                    boundary.vertices = vertices.data();
                    head.result = xrGetSpaceBoundary2DFB_(session_, space, &boundary);
                    vertices.resize(XR_SUCCEEDED(head.result) ? boundary.vertexCountOutput : 0);
                }
            }
            head.vertexCount = static_cast<uint32_t>(vertices.size());
            Append(resp, head);
            if (!vertices.empty()) AppendBytes(resp, vertices.data(), vertices.size() * sizeof(XrVector2f));
            Respond(requestId, resp.data(), resp.size());
            return;
        }
        case Op::GetSpaceRoomLayout:
        {
            sp::ReqSpace req = {};
            std::vector<uint8_t> resp;
            sp::RespRoomLayout head = {};
            head.result = XR_ERROR_FUNCTION_UNSUPPORTED;
            std::vector<XrUuidEXT> walls;
            XrSpace space = Read(cursor, remaining, req) ? ResolveSpace(req.spaceId) : XR_NULL_HANDLE;
            if (space == XR_NULL_HANDLE) head.result = XR_ERROR_HANDLE_INVALID;
            else if (xrGetSpaceRoomLayoutFB_)
            {
                XrRoomLayoutFB layout = {XR_TYPE_ROOM_LAYOUT_FB};
                head.result = xrGetSpaceRoomLayoutFB_(session_, space, &layout);
                if (XR_SUCCEEDED(head.result) && layout.wallUuidCountOutput > 0)
                {
                    walls.resize(layout.wallUuidCountOutput);
                    layout.wallUuidCapacityInput = layout.wallUuidCountOutput;
                    layout.wallUuids = walls.data();
                    head.result = xrGetSpaceRoomLayoutFB_(session_, space, &layout);
                    walls.resize(XR_SUCCEEDED(head.result) ? layout.wallUuidCountOutput : 0);
                }
                head.floorUuid = ToWire(layout.floorUuid);
                head.ceilingUuid = ToWire(layout.ceilingUuid);
            }
            head.wallCount = static_cast<uint32_t>(walls.size());
            Append(resp, head);
            for (const auto& uuid : walls) Append(resp, ToWire(uuid));
            Respond(requestId, resp.data(), resp.size());
            return;
        }
        case Op::GetSpaceTriangleMesh:
        {
            sp::ReqSpace req = {};
            std::vector<uint8_t> resp;
            sp::RespTriangleMesh head = {};
            head.result = XR_ERROR_FUNCTION_UNSUPPORTED;
            std::vector<XrVector3f> vertices;
            std::vector<uint32_t> indices;
            XrSpace space = Read(cursor, remaining, req) ? ResolveSpace(req.spaceId) : XR_NULL_HANDLE;
            if (space == XR_NULL_HANDLE) head.result = XR_ERROR_HANDLE_INVALID;
            else if (xrGetSpaceTriangleMeshMETA_)
            {
                XrSpaceTriangleMeshGetInfoMETA getInfo = {XR_TYPE_SPACE_TRIANGLE_MESH_GET_INFO_META};
                XrSpaceTriangleMeshMETA mesh = {XR_TYPE_SPACE_TRIANGLE_MESH_META};
                head.result = xrGetSpaceTriangleMeshMETA_(space, &getInfo, &mesh);
                if (XR_SUCCEEDED(head.result) && mesh.vertexCountOutput > 0)
                {
                    vertices.resize(mesh.vertexCountOutput);
                    indices.resize(mesh.indexCountOutput);
                    mesh.vertexCapacityInput = mesh.vertexCountOutput;
                    mesh.indexCapacityInput = mesh.indexCountOutput;
                    mesh.vertices = vertices.data();
                    mesh.indices = indices.data();
                    head.result = xrGetSpaceTriangleMeshMETA_(space, &getInfo, &mesh);
                    if (XR_FAILED(head.result))
                    {
                        vertices.clear();
                        indices.clear();
                    }
                    else
                    {
                        vertices.resize(mesh.vertexCountOutput);
                        indices.resize(mesh.indexCountOutput);
                    }
                }
            }
            head.vertexCount = static_cast<uint32_t>(vertices.size());
            head.indexCount = static_cast<uint32_t>(indices.size());
            Append(resp, head);
            if (!vertices.empty()) AppendBytes(resp, vertices.data(), vertices.size() * sizeof(XrVector3f));
            if (!indices.empty()) AppendBytes(resp, indices.data(), indices.size() * sizeof(uint32_t));
            LOGI("xrGetSpaceTriangleMeshMETA -> %d (%u vertices, %u indices)", head.result, head.vertexCount, head.indexCount);
            Respond(requestId, resp.data(), resp.size());
            return;
        }
        case Op::RequestSceneCapture:
        {
            sp::ReqSceneCapture req = {};
            if (!Read(cursor, remaining, req) || !xrRequestSceneCaptureFB_) break;
            if (remaining < req.requestByteCount) break;
            std::string request(reinterpret_cast<const char*>(cursor), req.requestByteCount);
            XrSceneCaptureRequestInfoFB info = {XR_TYPE_SCENE_CAPTURE_REQUEST_INFO_FB};
            info.requestByteCount = req.requestByteCount;
            info.request = req.requestByteCount > 0 ? request.c_str() : nullptr;
            XrAsyncRequestIdFB clientId = 0;
            simple.result = xrRequestSceneCaptureFB_(session_, &info, &clientId);
            if (XR_SUCCEEDED(simple.result)) MapAsyncRequest(clientId, req.asyncRequestId);
            LOGI("xrRequestSceneCaptureFB -> %d", simple.result);
            break;
        }
        default:
            LOGW("Spatial request op=0x%x not handled", static_cast<unsigned>(op));
            break;
    }
    Respond(requestId, &simple, sizeof(simple));
}

// ─── Events (XR thread) ──────────────────────────────────────────────────────

bool SpatialBridge::OnEvent(const XrEventDataBuffer& event)
{
    switch (event.type)
    {
        case XR_TYPE_EVENT_DATA_SPATIAL_ANCHOR_CREATE_COMPLETE_FB:
        {
            const auto& e = *reinterpret_cast<const XrEventDataSpatialAnchorCreateCompleteFB*>(&event);
            sp::EvtAnchorCreateComplete out = {};
            out.asyncRequestId = TakeAsyncRequestId(e.requestId, true);
            out.result = e.result;
            out.spaceId = XR_SUCCEEDED(e.result) ? FromSpace(e.space) : 0;
            out.uuid = ToWire(e.uuid);
            if (XR_SUCCEEDED(e.result)) TrackSpace(e.space);
            LOGI("Anchor create complete: result=%d space=%llu (request %llu)", e.result, (unsigned long long)out.spaceId, (unsigned long long)out.asyncRequestId);
            if (out.asyncRequestId != 0) SendEvent(Op::EvtAnchorCreateComplete, &out, sizeof(out));
            return true;
        }
        case XR_TYPE_EVENT_DATA_SPACE_SET_STATUS_COMPLETE_FB:
        {
            const auto& e = *reinterpret_cast<const XrEventDataSpaceSetStatusCompleteFB*>(&event);
            sp::EvtSetStatusComplete out = {};
            out.asyncRequestId = TakeAsyncRequestId(e.requestId, true);
            out.result = e.result;
            out.componentType = static_cast<uint32_t>(e.componentType);
            out.spaceId = FromSpace(e.space);
            out.uuid = ToWire(e.uuid);
            out.enabled = e.enabled ? 1 : 0;
            if (out.asyncRequestId != 0) SendEvent(Op::EvtSetStatusComplete, &out, sizeof(out));
            return true;
        }
        case XR_TYPE_EVENT_DATA_SPACE_SAVE_COMPLETE_FB:
        {
            const auto& e = *reinterpret_cast<const XrEventDataSpaceSaveCompleteFB*>(&event);
            sp::EvtSpaceStorageComplete out = {};
            out.asyncRequestId = TakeAsyncRequestId(e.requestId, true);
            out.result = e.result;
            out.location = static_cast<uint32_t>(e.location);
            out.spaceId = FromSpace(e.space);
            out.uuid = ToWire(e.uuid);
            if (out.asyncRequestId != 0) SendEvent(Op::EvtSaveCompleteFB, &out, sizeof(out));
            return true;
        }
        case XR_TYPE_EVENT_DATA_SPACE_ERASE_COMPLETE_FB:
        {
            const auto& e = *reinterpret_cast<const XrEventDataSpaceEraseCompleteFB*>(&event);
            sp::EvtSpaceStorageComplete out = {};
            out.asyncRequestId = TakeAsyncRequestId(e.requestId, true);
            out.result = e.result;
            out.location = static_cast<uint32_t>(e.location);
            out.spaceId = FromSpace(e.space);
            out.uuid = ToWire(e.uuid);
            if (out.asyncRequestId != 0) SendEvent(Op::EvtEraseCompleteFB, &out, sizeof(out));
            return true;
        }
        case XR_TYPE_EVENT_DATA_SPACES_SAVE_RESULT_META:
        {
            const auto& e = *reinterpret_cast<const XrEventDataSpacesSaveResultMETA*>(&event);
            sp::EvtRequestResult out = {};
            out.asyncRequestId = TakeAsyncRequestId(e.requestId, true);
            out.result = e.result;
            LOGI("Spaces save result: %d", e.result);
            if (out.asyncRequestId != 0) SendEvent(Op::EvtSaveSpacesResultMETA, &out, sizeof(out));
            return true;
        }
        case XR_TYPE_EVENT_DATA_SPACES_ERASE_RESULT_META:
        {
            const auto& e = *reinterpret_cast<const XrEventDataSpacesEraseResultMETA*>(&event);
            sp::EvtRequestResult out = {};
            out.asyncRequestId = TakeAsyncRequestId(e.requestId, true);
            out.result = e.result;
            if (out.asyncRequestId != 0) SendEvent(Op::EvtEraseSpacesResultMETA, &out, sizeof(out));
            return true;
        }
        case XR_TYPE_EVENT_DATA_SPACE_QUERY_RESULTS_AVAILABLE_FB:
        {
            const auto& e = *reinterpret_cast<const XrEventDataSpaceQueryResultsAvailableFB*>(&event);
            const uint64_t runtimeId = TakeAsyncRequestId(e.requestId, false);
            std::vector<uint8_t> payload;
            sp::EvtSpaceList head = {};
            head.asyncRequestId = runtimeId;
            std::vector<XrSpaceQueryResultFB> results;
            if (xrRetrieveSpaceQueryResultsFB_)
            {
                XrSpaceQueryResultsFB queryResults = {XR_TYPE_SPACE_QUERY_RESULTS_FB};
                head.result = xrRetrieveSpaceQueryResultsFB_(session_, e.requestId, &queryResults);
                if (XR_SUCCEEDED(head.result) && queryResults.resultCountOutput > 0)
                {
                    results.resize(queryResults.resultCountOutput);
                    queryResults.resultCapacityInput = queryResults.resultCountOutput;
                    queryResults.results = results.data();
                    head.result = xrRetrieveSpaceQueryResultsFB_(session_, e.requestId, &queryResults);
                    results.resize(XR_SUCCEEDED(head.result) ? queryResults.resultCountOutput : 0);
                }
            }
            else
            {
                head.result = XR_ERROR_FUNCTION_UNSUPPORTED;
            }
            head.count = static_cast<uint32_t>(results.size());
            Append(payload, head);
            for (const auto& r : results)
            {
                sp::SpaceListEntry entry = {};
                entry.spaceId = FromSpace(r.space);
                entry.uuid = ToWire(r.uuid);
                Append(payload, entry);
                TrackSpace(r.space);
            }
            LOGI("Query results: %u spaces (retrieve %d)", head.count, head.result);
            if (runtimeId != 0) SendEvent(Op::EvtQueryResultsFB, payload.data(), payload.size());
            return true;
        }
        case XR_TYPE_EVENT_DATA_SPACE_QUERY_COMPLETE_FB:
        {
            const auto& e = *reinterpret_cast<const XrEventDataSpaceQueryCompleteFB*>(&event);
            sp::EvtRequestResult out = {};
            out.asyncRequestId = TakeAsyncRequestId(e.requestId, true);
            out.result = e.result;
            if (out.asyncRequestId != 0) SendEvent(Op::EvtQueryCompleteFB, &out, sizeof(out));
            return true;
        }
        case XR_TYPE_EVENT_DATA_SPACE_DISCOVERY_RESULTS_AVAILABLE_META:
        {
            const auto& e = *reinterpret_cast<const XrEventDataSpaceDiscoveryResultsAvailableMETA*>(&event);
            const uint64_t runtimeId = TakeAsyncRequestId(e.requestId, false);
            std::vector<uint8_t> payload;
            sp::EvtSpaceList head = {};
            head.asyncRequestId = runtimeId;
            std::vector<XrSpaceDiscoveryResultMETA> results;
            if (xrRetrieveSpaceDiscoveryResultsMETA_)
            {
                XrSpaceDiscoveryResultsMETA discovery = {XR_TYPE_SPACE_DISCOVERY_RESULTS_META};
                head.result = xrRetrieveSpaceDiscoveryResultsMETA_(session_, e.requestId, &discovery);
                if (XR_SUCCEEDED(head.result) && discovery.resultCountOutput > 0)
                {
                    results.resize(discovery.resultCountOutput);
                    discovery.resultCapacityInput = discovery.resultCountOutput;
                    discovery.results = results.data();
                    head.result = xrRetrieveSpaceDiscoveryResultsMETA_(session_, e.requestId, &discovery);
                    results.resize(XR_SUCCEEDED(head.result) ? discovery.resultCountOutput : 0);
                }
            }
            else
            {
                head.result = XR_ERROR_FUNCTION_UNSUPPORTED;
            }
            head.count = static_cast<uint32_t>(results.size());
            Append(payload, head);
            for (const auto& r : results)
            {
                sp::SpaceListEntry entry = {};
                entry.spaceId = FromSpace(r.space);
                entry.uuid = ToWire(r.uuid);
                Append(payload, entry);
                TrackSpace(r.space);
            }
            LOGI("Discovery results: %u spaces (retrieve %d)", head.count, head.result);
            if (runtimeId != 0) SendEvent(Op::EvtDiscoveryResultsMETA, payload.data(), payload.size());
            return true;
        }
        case XR_TYPE_EVENT_DATA_SPACE_DISCOVERY_COMPLETE_META:
        {
            const auto& e = *reinterpret_cast<const XrEventDataSpaceDiscoveryCompleteMETA*>(&event);
            sp::EvtRequestResult out = {};
            out.asyncRequestId = TakeAsyncRequestId(e.requestId, true);
            out.result = e.result;
            LOGI("Discovery complete: %d", e.result);
            if (out.asyncRequestId != 0) SendEvent(Op::EvtDiscoveryCompleteMETA, &out, sizeof(out));
            return true;
        }
        case XR_TYPE_EVENT_DATA_SCENE_CAPTURE_COMPLETE_FB:
        {
            const auto& e = *reinterpret_cast<const XrEventDataSceneCaptureCompleteFB*>(&event);
            sp::EvtRequestResult out = {};
            out.asyncRequestId = TakeAsyncRequestId(e.requestId, true);
            out.result = e.result;
            LOGI("Scene capture complete: %d", e.result);
            if (out.asyncRequestId != 0) SendEvent(Op::EvtSceneCaptureComplete, &out, sizeof(out));
            return true;
        }
        default:
            return false;
    }
}

void SpatialBridge::SendPosesIfDue()
{
    if (!running_.load())
    {
        return;
    }
    const int64_t now = NowNs();
    if (now - lastPosesSentNs_ < kPosesIntervalNs)
    {
        return;
    }
    lastPosesSentNs_ = now;
    const XrTime time = frameTime_.load();
    if (time <= 0)
    {
        return;
    }
    std::vector<XrSpace> spaces;
    {
        std::lock_guard<std::mutex> lock(spacesMutex_);
        if (spaces_.empty())
        {
            return;
        }
        spaces.assign(spaces_.begin(), spaces_.end());
    }
    std::vector<uint8_t> payload;
    sp::SpacePosesHeader head = {};
    head.count = static_cast<uint32_t>(spaces.size());
    head.timeNs = time;
    Append(payload, head);
    for (XrSpace space : spaces)
    {
        XrSpaceLocation location = {XR_TYPE_SPACE_LOCATION};
        sp::SpacePose pose = {};
        pose.spaceId = FromSpace(space);
        if (XR_SUCCEEDED(xrLocateSpace(space, stageSpace_, time, &location)))
        {
            pose.locationFlags = location.locationFlags;
            pose.poseInStage = ToWire(location.pose);
        }
        Append(payload, pose);
    }
    SendMsg(MsgKind::Poses, Op::SpacePoses, 0, payload.data(), payload.size());
}
