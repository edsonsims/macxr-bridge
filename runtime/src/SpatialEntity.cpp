// SPDX-License-Identifier: MPL-2.0

#include "SpatialEntity.h"

#include "Instance.h"
#include "Runtime.h"
#include "Session.h"
#include "Space.h"
#include "SpatialProxy.h"

#include <spdlog/spdlog.h>

#include <algorithm>
#include <chrono>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>

namespace sp = oxr::protocol::spatial;

namespace
{

struct SpaceEntry
{
    XrSpace space;
    XrUuidEXT uuid;
};

struct CachedBlob
{
    std::chrono::steady_clock::time_point at;
    std::vector<uint8_t> bytes;
};

std::mutex gMutex;
std::unordered_map<uint64_t, Space*> gRemoteSpaces;                       // client space id -> runtime space
std::unordered_map<uint64_t, std::vector<SpaceEntry>> gQueryResults;      // async request id -> results (FB)
std::unordered_map<uint64_t, std::vector<SpaceEntry>> gDiscoveryResults;  // async request id -> results (META)
std::unordered_map<uint64_t, CachedBlob> gMeshCache;                      // client space id -> last mesh response
constexpr auto kMeshCacheTtl = std::chrono::seconds(3);

// Per-frame OVRPlugin queries would each cost a USB round trip: cache what is immutable (uuid,
// supported components) and what changes only through requests we see (component status).
struct ComponentStatusCache
{
    std::chrono::steady_clock::time_point at;
    bool enabled;
    bool changePending;
};
std::unordered_map<uint64_t, XrUuidEXT> gUuidCache;
std::unordered_map<uint64_t, std::vector<XrSpaceComponentTypeFB>> gComponentsCache;
std::unordered_map<uint64_t, std::unordered_map<uint32_t, ComponentStatusCache>> gStatusCache;
constexpr auto kStatusCacheTtl = std::chrono::milliseconds(500);

void InvalidateStatus(uint64_t remoteId, uint32_t componentType)
{
    std::lock_guard<std::mutex> lock(gMutex);
    auto it = gStatusCache.find(remoteId);
    if (it != gStatusCache.end())
    {
        it->second.erase(componentType);
    }
}

void ForgetSpaceCaches(uint64_t remoteId)
{
    gUuidCache.erase(remoteId);
    gComponentsCache.erase(remoteId);
    gStatusCache.erase(remoteId);
    gMeshCache.erase(remoteId);
}

template <typename T>
void Append(std::vector<uint8_t>& buffer, const T& value)
{
    const auto* bytes = reinterpret_cast<const uint8_t*>(&value);
    buffer.insert(buffer.end(), bytes, bytes + sizeof(T));
}

template <typename T>
bool ReadAt(const std::vector<uint8_t>& buffer, size_t& offset, T& out)
{
    if (buffer.size() < offset + sizeof(T))
    {
        return false;
    }
    std::memcpy(&out, buffer.data() + offset, sizeof(T));
    offset += sizeof(T);
    return true;
}

sp::Pose ToWire(const XrPosef& pose)
{
    sp::Pose out;
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

std::string UuidText(const XrUuidEXT& uuid)
{
    char text[40];
    std::snprintf(text, sizeof(text), "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
                  uuid.data[0], uuid.data[1], uuid.data[2], uuid.data[3], uuid.data[4], uuid.data[5], uuid.data[6],
                  uuid.data[7], uuid.data[8], uuid.data[9], uuid.data[10], uuid.data[11], uuid.data[12], uuid.data[13],
                  uuid.data[14], uuid.data[15]);
    return text;
}

Session* SessionFromHandle(XrSession session)
{
    return Runtime::Get().FromHandle<Session>(reinterpret_cast<uint64_t>(session));
}

Space* RemoteSpaceFromHandle(XrSpace space)
{
    auto* sp = Runtime::Get().FromHandle<Space>(reinterpret_cast<uint64_t>(space));
    if (sp == nullptr || sp->GetType() != Space::Type::Remote)
    {
        return nullptr;
    }
    return sp;
}

XrSpace SpaceHandleForRemoteId(uint64_t remoteId)
{
    std::lock_guard<std::mutex> lock(gMutex);
    auto it = gRemoteSpaces.find(remoteId);
    return it == gRemoteSpaces.end() ? XR_NULL_HANDLE : reinterpret_cast<XrSpace>(it->second->GetHandle());
}

// Creates (or reuses) the runtime-side Space for a client space id.
Space* EnsureRemoteSpace(Session* session, uint64_t remoteId)
{
    if (session == nullptr || remoteId == 0)
    {
        return nullptr;
    }
    std::lock_guard<std::mutex> lock(gMutex);
    auto it = gRemoteSpaces.find(remoteId);
    if (it != gRemoteSpaces.end())
    {
        return it->second;
    }
    Space* space = session->CreateRemoteSpace(remoteId);
    gRemoteSpaces[remoteId] = space;
    return space;
}

// The headset was unreachable or the call timed out.
constexpr XrResult kProxyFailure = XR_ERROR_RUNTIME_FAILURE;

template <typename Resp>
bool Call(sp::Op op, const void* req, size_t size, Resp& resp)
{
    auto response = SpatialProxy::Get().Call(op, req, size);
    if (!response || response->size() < sizeof(Resp))
    {
        return false;
    }
    std::memcpy(&resp, response->data(), sizeof(Resp));
    return true;
}

template <typename T>
XrResult FillArray(uint32_t capacityInput, uint32_t* countOutput, T* destination, const std::vector<T>& source)
{
    if (countOutput != nullptr)
    {
        *countOutput = static_cast<uint32_t>(source.size());
    }
    if (capacityInput == 0)
    {
        return XR_SUCCESS;
    }
    if (destination == nullptr)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    if (capacityInput < source.size())
    {
        return XR_ERROR_SIZE_INSUFFICIENT;
    }
    std::copy(source.begin(), source.end(), destination);
    return XR_SUCCESS;
}

// ─── XR_FB_spatial_entity ─────────────────────────────────────────────────────

XRAPI_ATTR XrResult XRAPI_CALL OxrCreateSpatialAnchorFB(XrSession session, const XrSpatialAnchorCreateInfoFB* info,
                                                        XrAsyncRequestIdFB* requestId)
{
    Session* sess = SessionFromHandle(session);
    if (sess == nullptr || info == nullptr || requestId == nullptr || info->type != XR_TYPE_SPATIAL_ANCHOR_CREATE_INFO_FB)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    auto* baseSpace = Runtime::Get().FromHandle<Space>(reinterpret_cast<uint64_t>(info->space));
    if (baseSpace == nullptr)
    {
        return XR_ERROR_HANDLE_INVALID;
    }
    XrPosef worldPose = {};
    if (!baseSpace->ToWorldPose(info->poseInSpace, &worldPose))
    {
        return XR_ERROR_POSE_INVALID;
    }
    sp::ReqCreateSpatialAnchor req = {};
    req.asyncRequestId = SpatialProxy::Get().NextAsyncRequestId();
    req.poseInStage = ToWire(worldPose);
    sp::RespResult resp = {};
    if (!Call(sp::Op::CreateSpatialAnchor, &req, sizeof(req), resp))
    {
        spdlog::warn("SpatialEntity: xrCreateSpatialAnchorFB without headset");
        return kProxyFailure;
    }
    spdlog::info("SpatialEntity: xrCreateSpatialAnchorFB at ({:.2f}, {:.2f}, {:.2f}) -> {} (request {})",
                 worldPose.position.x, worldPose.position.y, worldPose.position.z, resp.result, req.asyncRequestId);
    if (XR_SUCCEEDED(resp.result))
    {
        *requestId = req.asyncRequestId;
    }
    return static_cast<XrResult>(resp.result);
}

XRAPI_ATTR XrResult XRAPI_CALL OxrGetSpaceUuidFB(XrSpace space, XrUuidEXT* uuid)
{
    Space* remote = RemoteSpaceFromHandle(space);
    if (remote == nullptr || uuid == nullptr)
    {
        return remote == nullptr ? XR_ERROR_HANDLE_INVALID : XR_ERROR_VALIDATION_FAILURE;
    }
    {
        std::lock_guard<std::mutex> lock(gMutex);
        auto it = gUuidCache.find(remote->GetRemoteId());
        if (it != gUuidCache.end())
        {
            *uuid = it->second;
            return XR_SUCCESS;
        }
    }
    sp::ReqSpace req = {remote->GetRemoteId()};
    sp::RespUuid resp = {};
    if (!Call(sp::Op::GetSpaceUuid, &req, sizeof(req), resp))
    {
        return kProxyFailure;
    }
    *uuid = FromWire(resp.uuid);
    if (XR_SUCCEEDED(resp.result))
    {
        std::lock_guard<std::mutex> lock(gMutex);
        gUuidCache[remote->GetRemoteId()] = *uuid;
    }
    return static_cast<XrResult>(resp.result);
}

XRAPI_ATTR XrResult XRAPI_CALL OxrEnumerateSpaceSupportedComponentsFB(XrSpace space, uint32_t capacityInput,
                                                                      uint32_t* countOutput,
                                                                      XrSpaceComponentTypeFB* types)
{
    Space* remote = RemoteSpaceFromHandle(space);
    if (remote == nullptr)
    {
        return XR_ERROR_HANDLE_INVALID;
    }
    std::vector<XrSpaceComponentTypeFB> list;
    bool cached = false;
    {
        std::lock_guard<std::mutex> lock(gMutex);
        auto it = gComponentsCache.find(remote->GetRemoteId());
        if (it != gComponentsCache.end())
        {
            list = it->second;
            cached = true;
        }
    }
    if (!cached)
    {
        sp::ReqSpace req = {remote->GetRemoteId()};
        sp::RespComponents resp = {};
        if (!Call(sp::Op::EnumerateSpaceComponents, &req, sizeof(req), resp))
        {
            return kProxyFailure;
        }
        if (XR_FAILED(resp.result))
        {
            return static_cast<XrResult>(resp.result);
        }
        for (uint32_t i = 0; i < std::min<uint32_t>(resp.count, sp::MAX_COMPONENT_TYPES); ++i)
        {
            list.push_back(static_cast<XrSpaceComponentTypeFB>(resp.types[i]));
        }
        std::lock_guard<std::mutex> lock(gMutex);
        gComponentsCache[remote->GetRemoteId()] = list;
    }
    return FillArray(capacityInput, countOutput, types, list);
}

XRAPI_ATTR XrResult XRAPI_CALL OxrSetSpaceComponentStatusFB(XrSpace space, const XrSpaceComponentStatusSetInfoFB* info,
                                                            XrAsyncRequestIdFB* requestId)
{
    Space* remote = RemoteSpaceFromHandle(space);
    if (remote == nullptr)
    {
        return XR_ERROR_HANDLE_INVALID;
    }
    if (info == nullptr || requestId == nullptr || info->type != XR_TYPE_SPACE_COMPONENT_STATUS_SET_INFO_FB)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    sp::ReqSetComponentStatus req = {};
    req.asyncRequestId = SpatialProxy::Get().NextAsyncRequestId();
    req.spaceId = remote->GetRemoteId();
    req.componentType = static_cast<uint32_t>(info->componentType);
    req.enabled = info->enabled ? 1 : 0;
    req.timeoutNs = info->timeout;
    sp::RespResult resp = {};
    if (!Call(sp::Op::SetSpaceComponentStatus, &req, sizeof(req), resp))
    {
        return kProxyFailure;
    }
    InvalidateStatus(req.spaceId, req.componentType);
    spdlog::debug("SpatialEntity: xrSetSpaceComponentStatusFB(space {:#x}, component {}, enabled {}) -> {}",
                  req.spaceId, req.componentType, req.enabled, resp.result);
    if (XR_SUCCEEDED(resp.result))
    {
        *requestId = req.asyncRequestId;
    }
    return static_cast<XrResult>(resp.result);
}

XRAPI_ATTR XrResult XRAPI_CALL OxrGetSpaceComponentStatusFB(XrSpace space, XrSpaceComponentTypeFB componentType,
                                                            XrSpaceComponentStatusFB* status)
{
    Space* remote = RemoteSpaceFromHandle(space);
    if (remote == nullptr)
    {
        return XR_ERROR_HANDLE_INVALID;
    }
    if (status == nullptr || status->type != XR_TYPE_SPACE_COMPONENT_STATUS_FB)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    const uint64_t remoteId = remote->GetRemoteId();
    const uint32_t component = static_cast<uint32_t>(componentType);
    {
        std::lock_guard<std::mutex> lock(gMutex);
        auto spaceIt = gStatusCache.find(remoteId);
        if (spaceIt != gStatusCache.end())
        {
            auto it = spaceIt->second.find(component);
            if (it != spaceIt->second.end() && !it->second.changePending &&
                std::chrono::steady_clock::now() - it->second.at < kStatusCacheTtl)
            {
                status->enabled = it->second.enabled ? XR_TRUE : XR_FALSE;
                status->changePending = XR_FALSE;
                return XR_SUCCESS;
            }
        }
    }
    sp::ReqGetComponentStatus req = {};
    req.spaceId = remoteId;
    req.componentType = component;
    sp::RespComponentStatus resp = {};
    if (!Call(sp::Op::GetSpaceComponentStatus, &req, sizeof(req), resp))
    {
        return kProxyFailure;
    }
    status->enabled = resp.enabled ? XR_TRUE : XR_FALSE;
    status->changePending = resp.changePending ? XR_TRUE : XR_FALSE;
    if (XR_SUCCEEDED(resp.result))
    {
        std::lock_guard<std::mutex> lock(gMutex);
        gStatusCache[remoteId][component] = {std::chrono::steady_clock::now(), resp.enabled != 0, resp.changePending != 0};
    }
    return static_cast<XrResult>(resp.result);
}

// ─── XR_FB_spatial_entity_storage ────────────────────────────────────────────

XRAPI_ATTR XrResult XRAPI_CALL OxrSaveSpaceFB(XrSession session, const XrSpaceSaveInfoFB* info, XrAsyncRequestIdFB* requestId)
{
    if (SessionFromHandle(session) == nullptr || info == nullptr || requestId == nullptr || info->type != XR_TYPE_SPACE_SAVE_INFO_FB)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    Space* remote = RemoteSpaceFromHandle(info->space);
    if (remote == nullptr)
    {
        return XR_ERROR_HANDLE_INVALID;
    }
    sp::ReqSaveSpaceFB req = {};
    req.asyncRequestId = SpatialProxy::Get().NextAsyncRequestId();
    req.spaceId = remote->GetRemoteId();
    req.location = static_cast<uint32_t>(info->location);
    req.persistenceMode = static_cast<uint32_t>(info->persistenceMode);
    sp::RespResult resp = {};
    if (!Call(sp::Op::SaveSpaceFB, &req, sizeof(req), resp))
    {
        return kProxyFailure;
    }
    spdlog::info("SpatialEntity: xrSaveSpaceFB -> {} (request {})", resp.result, req.asyncRequestId);
    if (XR_SUCCEEDED(resp.result))
    {
        *requestId = req.asyncRequestId;
    }
    return static_cast<XrResult>(resp.result);
}

XRAPI_ATTR XrResult XRAPI_CALL OxrEraseSpaceFB(XrSession session, const XrSpaceEraseInfoFB* info, XrAsyncRequestIdFB* requestId)
{
    if (SessionFromHandle(session) == nullptr || info == nullptr || requestId == nullptr || info->type != XR_TYPE_SPACE_ERASE_INFO_FB)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    Space* remote = RemoteSpaceFromHandle(info->space);
    if (remote == nullptr)
    {
        return XR_ERROR_HANDLE_INVALID;
    }
    sp::ReqEraseSpaceFB req = {};
    req.asyncRequestId = SpatialProxy::Get().NextAsyncRequestId();
    req.spaceId = remote->GetRemoteId();
    req.location = static_cast<uint32_t>(info->location);
    sp::RespResult resp = {};
    if (!Call(sp::Op::EraseSpaceFB, &req, sizeof(req), resp))
    {
        return kProxyFailure;
    }
    if (XR_SUCCEEDED(resp.result))
    {
        *requestId = req.asyncRequestId;
    }
    return static_cast<XrResult>(resp.result);
}

// ─── XR_META_spatial_entity_persistence ──────────────────────────────────────

XRAPI_ATTR XrResult XRAPI_CALL OxrSaveSpacesMETA(XrSession session, const XrSpacesSaveInfoMETA* info, XrAsyncRequestIdFB* requestId)
{
    if (SessionFromHandle(session) == nullptr || info == nullptr || requestId == nullptr || info->type != XR_TYPE_SPACES_SAVE_INFO_META)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    std::vector<uint8_t> payload;
    sp::ReqSpaceList req = {};
    req.asyncRequestId = SpatialProxy::Get().NextAsyncRequestId();
    req.spaceCount = info->spaceCount;
    Append(payload, req);
    for (uint32_t i = 0; i < info->spaceCount; ++i)
    {
        Space* remote = RemoteSpaceFromHandle(info->spaces[i]);
        if (remote == nullptr)
        {
            return XR_ERROR_HANDLE_INVALID;
        }
        Append(payload, remote->GetRemoteId());
    }
    sp::RespResult resp = {};
    if (!Call(sp::Op::SaveSpacesMETA, payload.data(), payload.size(), resp))
    {
        return kProxyFailure;
    }
    spdlog::info("SpatialEntity: xrSaveSpacesMETA({} spaces) -> {} (request {})", info->spaceCount, resp.result, req.asyncRequestId);
    if (XR_SUCCEEDED(resp.result))
    {
        *requestId = req.asyncRequestId;
    }
    return static_cast<XrResult>(resp.result);
}

XRAPI_ATTR XrResult XRAPI_CALL OxrEraseSpacesMETA(XrSession session, const XrSpacesEraseInfoMETA* info, XrAsyncRequestIdFB* requestId)
{
    if (SessionFromHandle(session) == nullptr || info == nullptr || requestId == nullptr || info->type != XR_TYPE_SPACES_ERASE_INFO_META)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    std::vector<uint8_t> payload;
    sp::ReqEraseSpacesMETA req = {};
    req.asyncRequestId = SpatialProxy::Get().NextAsyncRequestId();
    req.spaceCount = info->spaceCount;
    req.uuidCount = info->uuidCount;
    Append(payload, req);
    for (uint32_t i = 0; i < info->spaceCount; ++i)
    {
        Space* remote = RemoteSpaceFromHandle(info->spaces[i]);
        if (remote == nullptr)
        {
            return XR_ERROR_HANDLE_INVALID;
        }
        Append(payload, remote->GetRemoteId());
    }
    for (uint32_t i = 0; i < info->uuidCount; ++i)
    {
        Append(payload, ToWire(info->uuids[i]));
    }
    sp::RespResult resp = {};
    if (!Call(sp::Op::EraseSpacesMETA, payload.data(), payload.size(), resp))
    {
        return kProxyFailure;
    }
    spdlog::info("SpatialEntity: xrEraseSpacesMETA({} spaces, {} uuids) -> {}", info->spaceCount, info->uuidCount, resp.result);
    if (XR_SUCCEEDED(resp.result))
    {
        *requestId = req.asyncRequestId;
    }
    return static_cast<XrResult>(resp.result);
}

// ─── XR_FB_spatial_entity_query ──────────────────────────────────────────────

XRAPI_ATTR XrResult XRAPI_CALL OxrQuerySpacesFB(XrSession session, const XrSpaceQueryInfoBaseHeaderFB* baseInfo,
                                                XrAsyncRequestIdFB* requestId)
{
    if (SessionFromHandle(session) == nullptr || baseInfo == nullptr || requestId == nullptr)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    if (baseInfo->type != XR_TYPE_SPACE_QUERY_INFO_FB)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    const auto* info = reinterpret_cast<const XrSpaceQueryInfoFB*>(baseInfo);
    std::vector<uint8_t> payload;
    sp::ReqQuerySpacesFB req = {};
    req.asyncRequestId = SpatialProxy::Get().NextAsyncRequestId();
    req.timeoutNs = info->timeout;
    req.maxResultCount = info->maxResultCount;
    req.filterKind = sp::FILTER_NONE;
    std::vector<sp::Uuid> uuids;
    if (info->filter != nullptr)
    {
        if (info->filter->type == XR_TYPE_SPACE_UUID_FILTER_INFO_FB)
        {
            const auto* f = reinterpret_cast<const XrSpaceUuidFilterInfoFB*>(info->filter);
            req.filterKind = sp::FILTER_UUIDS;
            req.uuidCount = f->uuidCount;
            for (uint32_t i = 0; i < f->uuidCount; ++i)
            {
                uuids.push_back(ToWire(f->uuids[i]));
            }
        }
        else if (info->filter->type == XR_TYPE_SPACE_COMPONENT_FILTER_INFO_FB)
        {
            const auto* f = reinterpret_cast<const XrSpaceComponentFilterInfoFB*>(info->filter);
            req.filterKind = sp::FILTER_COMPONENT;
            req.componentType = static_cast<uint32_t>(f->componentType);
        }
        for (const auto* next = reinterpret_cast<const XrBaseInStructure*>(info->filter->next); next != nullptr;
             next = next->next)
        {
            if (next->type == XR_TYPE_SPACE_STORAGE_LOCATION_FILTER_INFO_FB)
            {
                req.storageLocation = static_cast<uint32_t>(
                    reinterpret_cast<const XrSpaceStorageLocationFilterInfoFB*>(next)->location);
            }
        }
    }
    Append(payload, req);
    for (const auto& uuid : uuids)
    {
        Append(payload, uuid);
    }
    sp::RespResult resp = {};
    if (!Call(sp::Op::QuerySpacesFB, payload.data(), payload.size(), resp))
    {
        return kProxyFailure;
    }
    spdlog::info("SpatialEntity: xrQuerySpacesFB(filter {}, location {}, max {}) -> {} (request {})",
                 req.filterKind, req.storageLocation, req.maxResultCount, resp.result, req.asyncRequestId);
    if (XR_SUCCEEDED(resp.result))
    {
        *requestId = req.asyncRequestId;
    }
    return static_cast<XrResult>(resp.result);
}

XRAPI_ATTR XrResult XRAPI_CALL OxrRetrieveSpaceQueryResultsFB(XrSession session, XrAsyncRequestIdFB requestId,
                                                              XrSpaceQueryResultsFB* results)
{
    if (SessionFromHandle(session) == nullptr || results == nullptr || results->type != XR_TYPE_SPACE_QUERY_RESULTS_FB)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    std::vector<XrSpaceQueryResultFB> list;
    {
        std::lock_guard<std::mutex> lock(gMutex);
        auto it = gQueryResults.find(requestId);
        if (it == gQueryResults.end())
        {
            return XR_ERROR_VALIDATION_FAILURE;
        }
        for (const auto& entry : it->second)
        {
            list.push_back({entry.space, entry.uuid});
        }
        if (results->resultCapacityInput > 0)
        {
            gQueryResults.erase(it); // results are retrieved once
        }
    }
    return FillArray(results->resultCapacityInput, &results->resultCountOutput, results->results, list);
}

// ─── XR_META_spatial_entity_discovery ────────────────────────────────────────

XRAPI_ATTR XrResult XRAPI_CALL OxrDiscoverSpacesMETA(XrSession session, const XrSpaceDiscoveryInfoMETA* info,
                                                     XrAsyncRequestIdFB* requestId)
{
    if (SessionFromHandle(session) == nullptr || info == nullptr || requestId == nullptr || info->type != XR_TYPE_SPACE_DISCOVERY_INFO_META)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    std::vector<uint8_t> payload;
    sp::ReqDiscoverSpacesMETA req = {};
    req.asyncRequestId = SpatialProxy::Get().NextAsyncRequestId();
    req.filterCount = 0;
    Append(payload, req);
    for (uint32_t i = 0; i < info->filterCount; ++i)
    {
        const XrSpaceFilterBaseHeaderMETA* filter = info->filters[i];
        if (filter == nullptr)
        {
            continue;
        }
        sp::DiscoveryFilter wire = {};
        if (filter->type == XR_TYPE_SPACE_FILTER_UUID_META)
        {
            const auto* f = reinterpret_cast<const XrSpaceFilterUuidMETA*>(filter);
            wire.kind = sp::FILTER_UUIDS;
            wire.uuidCount = f->uuidCount;
            Append(payload, wire);
            for (uint32_t u = 0; u < f->uuidCount; ++u)
            {
                Append(payload, ToWire(f->uuids[u]));
            }
        }
        else if (filter->type == XR_TYPE_SPACE_FILTER_COMPONENT_META)
        {
            const auto* f = reinterpret_cast<const XrSpaceFilterComponentMETA*>(filter);
            wire.kind = sp::FILTER_COMPONENT;
            wire.componentType = static_cast<uint32_t>(f->componentType);
            Append(payload, wire);
        }
        else
        {
            spdlog::warn("SpatialEntity: unsupported discovery filter type {}", static_cast<int>(filter->type));
            continue;
        }
        ++req.filterCount;
    }
    std::memcpy(payload.data(), &req, sizeof(req)); // patch the accepted filter count
    sp::RespResult resp = {};
    if (!Call(sp::Op::DiscoverSpacesMETA, payload.data(), payload.size(), resp))
    {
        return kProxyFailure;
    }
    spdlog::info("SpatialEntity: xrDiscoverSpacesMETA({} filters) -> {} (request {})", req.filterCount, resp.result, req.asyncRequestId);
    if (XR_SUCCEEDED(resp.result))
    {
        *requestId = req.asyncRequestId;
    }
    return static_cast<XrResult>(resp.result);
}

XRAPI_ATTR XrResult XRAPI_CALL OxrRetrieveSpaceDiscoveryResultsMETA(XrSession session, XrAsyncRequestIdFB requestId,
                                                                    XrSpaceDiscoveryResultsMETA* results)
{
    if (SessionFromHandle(session) == nullptr || results == nullptr || results->type != XR_TYPE_SPACE_DISCOVERY_RESULTS_META)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    std::vector<XrSpaceDiscoveryResultMETA> list;
    {
        std::lock_guard<std::mutex> lock(gMutex);
        auto it = gDiscoveryResults.find(requestId);
        if (it == gDiscoveryResults.end())
        {
            return XR_ERROR_VALIDATION_FAILURE;
        }
        for (const auto& entry : it->second)
        {
            list.push_back({entry.space, entry.uuid});
        }
        if (results->resultCapacityInput > 0)
        {
            gDiscoveryResults.erase(it);
        }
    }
    return FillArray(results->resultCapacityInput, &results->resultCountOutput, results->results, list);
}

// ─── XR_FB_spatial_entity_container / XR_FB_scene ────────────────────────────

XRAPI_ATTR XrResult XRAPI_CALL OxrGetSpaceContainerFB(XrSession session, XrSpace space, XrSpaceContainerFB* container)
{
    Space* remote = RemoteSpaceFromHandle(space);
    if (SessionFromHandle(session) == nullptr || remote == nullptr)
    {
        return XR_ERROR_HANDLE_INVALID;
    }
    if (container == nullptr || container->type != XR_TYPE_SPACE_CONTAINER_FB)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    sp::ReqSpace req = {remote->GetRemoteId()};
    auto response = SpatialProxy::Get().Call(sp::Op::GetSpaceContainer, &req, sizeof(req));
    if (!response)
    {
        return kProxyFailure;
    }
    size_t offset = 0;
    sp::RespContainer head = {};
    if (!ReadAt(*response, offset, head))
    {
        return kProxyFailure;
    }
    if (XR_FAILED(head.result))
    {
        return static_cast<XrResult>(head.result);
    }
    std::vector<XrUuidEXT> uuids;
    for (uint32_t i = 0; i < head.uuidCount; ++i)
    {
        sp::Uuid uuid = {};
        if (!ReadAt(*response, offset, uuid)) break;
        uuids.push_back(FromWire(uuid));
    }
    return FillArray(container->uuidCapacityInput, &container->uuidCountOutput, container->uuids, uuids);
}

XRAPI_ATTR XrResult XRAPI_CALL OxrGetSpaceBoundingBox2DFB(XrSession session, XrSpace space, XrRect2Df* rect)
{
    Space* remote = RemoteSpaceFromHandle(space);
    if (SessionFromHandle(session) == nullptr || remote == nullptr)
    {
        return XR_ERROR_HANDLE_INVALID;
    }
    if (rect == nullptr)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    sp::ReqSpace req = {remote->GetRemoteId()};
    sp::RespBoundingBox2D resp = {};
    if (!Call(sp::Op::GetSpaceBoundingBox2D, &req, sizeof(req), resp))
    {
        return kProxyFailure;
    }
    rect->offset = {resp.offsetX, resp.offsetY};
    rect->extent = {resp.extentWidth, resp.extentHeight};
    return static_cast<XrResult>(resp.result);
}

XRAPI_ATTR XrResult XRAPI_CALL OxrGetSpaceBoundingBox3DFB(XrSession session, XrSpace space, XrRect3DfFB* rect)
{
    Space* remote = RemoteSpaceFromHandle(space);
    if (SessionFromHandle(session) == nullptr || remote == nullptr)
    {
        return XR_ERROR_HANDLE_INVALID;
    }
    if (rect == nullptr)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    sp::ReqSpace req = {remote->GetRemoteId()};
    sp::RespBoundingBox3D resp = {};
    if (!Call(sp::Op::GetSpaceBoundingBox3D, &req, sizeof(req), resp))
    {
        return kProxyFailure;
    }
    rect->offset = {resp.offset.x, resp.offset.y, resp.offset.z};
    rect->extent = {resp.extent.x, resp.extent.y, resp.extent.z};
    return static_cast<XrResult>(resp.result);
}

XRAPI_ATTR XrResult XRAPI_CALL OxrGetSpaceSemanticLabelsFB(XrSession session, XrSpace space, XrSemanticLabelsFB* labels)
{
    Space* remote = RemoteSpaceFromHandle(space);
    if (SessionFromHandle(session) == nullptr || remote == nullptr)
    {
        return XR_ERROR_HANDLE_INVALID;
    }
    if (labels == nullptr || labels->type != XR_TYPE_SEMANTIC_LABELS_FB)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    std::vector<uint8_t> payload;
    sp::ReqSemanticLabels req = {};
    req.spaceId = remote->GetRemoteId();
    std::string recognized;
    for (const auto* next = reinterpret_cast<const XrBaseInStructure*>(labels->next); next != nullptr; next = next->next)
    {
        if (next->type == XR_TYPE_SEMANTIC_LABELS_SUPPORT_INFO_FB)
        {
            const auto* support = reinterpret_cast<const XrSemanticLabelsSupportInfoFB*>(next);
            req.supportFlags = support->flags;
            if (support->recognizedLabels != nullptr)
            {
                recognized = support->recognizedLabels;
            }
        }
    }
    req.labelsByteCount = static_cast<uint32_t>(recognized.size());
    Append(payload, req);
    payload.insert(payload.end(), recognized.begin(), recognized.end());
    sp::RespSemanticLabels resp = {};
    if (!Call(sp::Op::GetSpaceSemanticLabels, payload.data(), payload.size(), resp))
    {
        return kProxyFailure;
    }
    if (XR_FAILED(resp.result))
    {
        return static_cast<XrResult>(resp.result);
    }
    const uint32_t count = std::min<uint32_t>(resp.byteCount, sp::MAX_SEMANTIC_LABELS_BYTES);
    std::vector<char> text(resp.labels, resp.labels + count);
    text.push_back('\0');
    return FillArray(labels->bufferCapacityInput, &labels->bufferCountOutput, labels->buffer, text);
}

XRAPI_ATTR XrResult XRAPI_CALL OxrGetSpaceBoundary2DFB(XrSession session, XrSpace space, XrBoundary2DFB* boundary)
{
    Space* remote = RemoteSpaceFromHandle(space);
    if (SessionFromHandle(session) == nullptr || remote == nullptr)
    {
        return XR_ERROR_HANDLE_INVALID;
    }
    if (boundary == nullptr || boundary->type != XR_TYPE_BOUNDARY_2D_FB)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    sp::ReqSpace req = {remote->GetRemoteId()};
    auto response = SpatialProxy::Get().Call(sp::Op::GetSpaceBoundary2D, &req, sizeof(req));
    if (!response)
    {
        return kProxyFailure;
    }
    size_t offset = 0;
    sp::RespBoundary2D head = {};
    if (!ReadAt(*response, offset, head))
    {
        return kProxyFailure;
    }
    if (XR_FAILED(head.result))
    {
        return static_cast<XrResult>(head.result);
    }
    std::vector<XrVector2f> vertices;
    for (uint32_t i = 0; i < head.vertexCount; ++i)
    {
        XrVector2f v = {};
        if (!ReadAt(*response, offset, v)) break;
        vertices.push_back(v);
    }
    return FillArray(boundary->vertexCapacityInput, &boundary->vertexCountOutput, boundary->vertices, vertices);
}

XRAPI_ATTR XrResult XRAPI_CALL OxrGetSpaceRoomLayoutFB(XrSession session, XrSpace space, XrRoomLayoutFB* layout)
{
    Space* remote = RemoteSpaceFromHandle(space);
    if (SessionFromHandle(session) == nullptr || remote == nullptr)
    {
        return XR_ERROR_HANDLE_INVALID;
    }
    if (layout == nullptr || layout->type != XR_TYPE_ROOM_LAYOUT_FB)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    sp::ReqSpace req = {remote->GetRemoteId()};
    auto response = SpatialProxy::Get().Call(sp::Op::GetSpaceRoomLayout, &req, sizeof(req));
    if (!response)
    {
        return kProxyFailure;
    }
    size_t offset = 0;
    sp::RespRoomLayout head = {};
    if (!ReadAt(*response, offset, head))
    {
        return kProxyFailure;
    }
    if (XR_FAILED(head.result))
    {
        return static_cast<XrResult>(head.result);
    }
    layout->floorUuid = FromWire(head.floorUuid);
    layout->ceilingUuid = FromWire(head.ceilingUuid);
    std::vector<XrUuidEXT> walls;
    for (uint32_t i = 0; i < head.wallCount; ++i)
    {
        sp::Uuid uuid = {};
        if (!ReadAt(*response, offset, uuid)) break;
        walls.push_back(FromWire(uuid));
    }
    return FillArray(layout->wallUuidCapacityInput, &layout->wallUuidCountOutput, layout->wallUuids, walls);
}

// ─── XR_META_spatial_entity_mesh ─────────────────────────────────────────────

XRAPI_ATTR XrResult XRAPI_CALL OxrGetSpaceTriangleMeshMETA(XrSpace space, const XrSpaceTriangleMeshGetInfoMETA* getInfo,
                                                           XrSpaceTriangleMeshMETA* mesh)
{
    Space* remote = RemoteSpaceFromHandle(space);
    if (remote == nullptr)
    {
        return XR_ERROR_HANDLE_INVALID;
    }
    if (mesh == nullptr || mesh->type != XR_TYPE_SPACE_TRIANGLE_MESH_META ||
        (getInfo != nullptr && getInfo->type != XR_TYPE_SPACE_TRIANGLE_MESH_GET_INFO_META))
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    const uint64_t remoteId = remote->GetRemoteId();
    std::vector<uint8_t> response;
    {
        // The two-call idiom asks twice: serve the second call from the cache.
        std::lock_guard<std::mutex> lock(gMutex);
        auto it = gMeshCache.find(remoteId);
        if (it != gMeshCache.end() && std::chrono::steady_clock::now() - it->second.at < kMeshCacheTtl)
        {
            response = it->second.bytes;
        }
    }
    if (response.empty())
    {
        sp::ReqSpace req = {remoteId};
        auto fetched = SpatialProxy::Get().Call(sp::Op::GetSpaceTriangleMesh, &req, sizeof(req), std::chrono::milliseconds(10000));
        if (!fetched)
        {
            return kProxyFailure;
        }
        response = std::move(*fetched);
        std::lock_guard<std::mutex> lock(gMutex);
        gMeshCache[remoteId] = {std::chrono::steady_clock::now(), response};
    }
    size_t offset = 0;
    sp::RespTriangleMesh head = {};
    if (!ReadAt(response, offset, head))
    {
        return kProxyFailure;
    }
    if (XR_FAILED(head.result))
    {
        return static_cast<XrResult>(head.result);
    }
    const size_t verticesBytes = static_cast<size_t>(head.vertexCount) * sizeof(XrVector3f);
    const size_t indicesBytes = static_cast<size_t>(head.indexCount) * sizeof(uint32_t);
    if (response.size() < offset + verticesBytes + indicesBytes)
    {
        return kProxyFailure;
    }
    mesh->vertexCountOutput = head.vertexCount;
    mesh->indexCountOutput = head.indexCount;
    if (mesh->vertexCapacityInput == 0 && mesh->indexCapacityInput == 0)
    {
        return XR_SUCCESS;
    }
    if (mesh->vertexCapacityInput < head.vertexCount || mesh->indexCapacityInput < head.indexCount)
    {
        return XR_ERROR_SIZE_INSUFFICIENT;
    }
    if (mesh->vertices == nullptr || mesh->indices == nullptr)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    std::memcpy(mesh->vertices, response.data() + offset, verticesBytes);
    std::memcpy(mesh->indices, response.data() + offset + verticesBytes, indicesBytes);
    return XR_SUCCESS;
}

// ─── XR_FB_scene_capture ─────────────────────────────────────────────────────

XRAPI_ATTR XrResult XRAPI_CALL OxrRequestSceneCaptureFB(XrSession session, const XrSceneCaptureRequestInfoFB* info,
                                                        XrAsyncRequestIdFB* requestId)
{
    if (SessionFromHandle(session) == nullptr || info == nullptr || requestId == nullptr || info->type != XR_TYPE_SCENE_CAPTURE_REQUEST_INFO_FB)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    std::vector<uint8_t> payload;
    sp::ReqSceneCapture req = {};
    req.asyncRequestId = SpatialProxy::Get().NextAsyncRequestId();
    req.requestByteCount = info->request != nullptr ? info->requestByteCount : 0;
    Append(payload, req);
    if (req.requestByteCount > 0)
    {
        payload.insert(payload.end(), info->request, info->request + req.requestByteCount);
    }
    sp::RespResult resp = {};
    if (!Call(sp::Op::RequestSceneCapture, payload.data(), payload.size(), resp))
    {
        return kProxyFailure;
    }
    spdlog::info("SpatialEntity: xrRequestSceneCaptureFB -> {} (request {})", resp.result, req.asyncRequestId);
    if (XR_SUCCEEDED(resp.result))
    {
        *requestId = req.asyncRequestId;
    }
    return static_cast<XrResult>(resp.result);
}

} // namespace

namespace oxrsys::spatial_entity
{

std::vector<std::pair<const char*, uint32_t>> Extensions()
{
    return {
        {XR_FB_SPATIAL_ENTITY_EXTENSION_NAME, XR_FB_spatial_entity_SPEC_VERSION},
        {XR_FB_SPATIAL_ENTITY_STORAGE_EXTENSION_NAME, XR_FB_spatial_entity_storage_SPEC_VERSION},
        {XR_FB_SPATIAL_ENTITY_QUERY_EXTENSION_NAME, XR_FB_spatial_entity_query_SPEC_VERSION},
        {XR_FB_SPATIAL_ENTITY_CONTAINER_EXTENSION_NAME, XR_FB_spatial_entity_container_SPEC_VERSION},
        {XR_META_SPATIAL_ENTITY_PERSISTENCE_EXTENSION_NAME, XR_META_spatial_entity_persistence_SPEC_VERSION},
        {XR_META_SPATIAL_ENTITY_DISCOVERY_EXTENSION_NAME, XR_META_spatial_entity_discovery_SPEC_VERSION},
        {XR_FB_SCENE_EXTENSION_NAME, XR_FB_scene_SPEC_VERSION},
        {XR_FB_SCENE_CAPTURE_EXTENSION_NAME, XR_FB_scene_capture_SPEC_VERSION},
        {XR_META_SPATIAL_ENTITY_MESH_EXTENSION_NAME, XR_META_spatial_entity_mesh_SPEC_VERSION},
    };
}

const char* ExtensionForFunction(const char* name)
{
    struct Entry { const char* function; const char* extension; };
    static const Entry kEntries[] = {
        {"xrCreateSpatialAnchorFB", XR_FB_SPATIAL_ENTITY_EXTENSION_NAME},
        {"xrGetSpaceUuidFB", XR_FB_SPATIAL_ENTITY_EXTENSION_NAME},
        {"xrEnumerateSpaceSupportedComponentsFB", XR_FB_SPATIAL_ENTITY_EXTENSION_NAME},
        {"xrSetSpaceComponentStatusFB", XR_FB_SPATIAL_ENTITY_EXTENSION_NAME},
        {"xrGetSpaceComponentStatusFB", XR_FB_SPATIAL_ENTITY_EXTENSION_NAME},
        {"xrSaveSpaceFB", XR_FB_SPATIAL_ENTITY_STORAGE_EXTENSION_NAME},
        {"xrEraseSpaceFB", XR_FB_SPATIAL_ENTITY_STORAGE_EXTENSION_NAME},
        {"xrQuerySpacesFB", XR_FB_SPATIAL_ENTITY_QUERY_EXTENSION_NAME},
        {"xrRetrieveSpaceQueryResultsFB", XR_FB_SPATIAL_ENTITY_QUERY_EXTENSION_NAME},
        {"xrSaveSpacesMETA", XR_META_SPATIAL_ENTITY_PERSISTENCE_EXTENSION_NAME},
        {"xrEraseSpacesMETA", XR_META_SPATIAL_ENTITY_PERSISTENCE_EXTENSION_NAME},
        {"xrDiscoverSpacesMETA", XR_META_SPATIAL_ENTITY_DISCOVERY_EXTENSION_NAME},
        {"xrRetrieveSpaceDiscoveryResultsMETA", XR_META_SPATIAL_ENTITY_DISCOVERY_EXTENSION_NAME},
        {"xrGetSpaceContainerFB", XR_FB_SPATIAL_ENTITY_CONTAINER_EXTENSION_NAME},
        {"xrGetSpaceBoundingBox2DFB", XR_FB_SCENE_EXTENSION_NAME},
        {"xrGetSpaceBoundingBox3DFB", XR_FB_SCENE_EXTENSION_NAME},
        {"xrGetSpaceSemanticLabelsFB", XR_FB_SCENE_EXTENSION_NAME},
        {"xrGetSpaceBoundary2DFB", XR_FB_SCENE_EXTENSION_NAME},
        {"xrGetSpaceRoomLayoutFB", XR_FB_SCENE_EXTENSION_NAME},
        {"xrGetSpaceTriangleMeshMETA", XR_META_SPATIAL_ENTITY_MESH_EXTENSION_NAME},
        {"xrRequestSceneCaptureFB", XR_FB_SCENE_CAPTURE_EXTENSION_NAME},
    };
    if (name == nullptr)
    {
        return nullptr;
    }
    for (const auto& entry : kEntries)
    {
        if (std::strcmp(entry.function, name) == 0)
        {
            return entry.extension;
        }
    }
    return nullptr;
}

bool GetProcAddr(const char* name, PFN_xrVoidFunction* function)
{
#define SPATIAL_DISPATCH(fn, impl)                                   \
    if (std::strcmp(name, #fn) == 0)                                 \
    {                                                                \
        *function = reinterpret_cast<PFN_xrVoidFunction>(impl);      \
        return true;                                                 \
    }
    SPATIAL_DISPATCH(xrCreateSpatialAnchorFB, OxrCreateSpatialAnchorFB)
    SPATIAL_DISPATCH(xrGetSpaceUuidFB, OxrGetSpaceUuidFB)
    SPATIAL_DISPATCH(xrEnumerateSpaceSupportedComponentsFB, OxrEnumerateSpaceSupportedComponentsFB)
    SPATIAL_DISPATCH(xrSetSpaceComponentStatusFB, OxrSetSpaceComponentStatusFB)
    SPATIAL_DISPATCH(xrGetSpaceComponentStatusFB, OxrGetSpaceComponentStatusFB)
    SPATIAL_DISPATCH(xrSaveSpaceFB, OxrSaveSpaceFB)
    SPATIAL_DISPATCH(xrEraseSpaceFB, OxrEraseSpaceFB)
    SPATIAL_DISPATCH(xrQuerySpacesFB, OxrQuerySpacesFB)
    SPATIAL_DISPATCH(xrRetrieveSpaceQueryResultsFB, OxrRetrieveSpaceQueryResultsFB)
    SPATIAL_DISPATCH(xrSaveSpacesMETA, OxrSaveSpacesMETA)
    SPATIAL_DISPATCH(xrEraseSpacesMETA, OxrEraseSpacesMETA)
    SPATIAL_DISPATCH(xrDiscoverSpacesMETA, OxrDiscoverSpacesMETA)
    SPATIAL_DISPATCH(xrRetrieveSpaceDiscoveryResultsMETA, OxrRetrieveSpaceDiscoveryResultsMETA)
    SPATIAL_DISPATCH(xrGetSpaceContainerFB, OxrGetSpaceContainerFB)
    SPATIAL_DISPATCH(xrGetSpaceBoundingBox2DFB, OxrGetSpaceBoundingBox2DFB)
    SPATIAL_DISPATCH(xrGetSpaceBoundingBox3DFB, OxrGetSpaceBoundingBox3DFB)
    SPATIAL_DISPATCH(xrGetSpaceSemanticLabelsFB, OxrGetSpaceSemanticLabelsFB)
    SPATIAL_DISPATCH(xrGetSpaceBoundary2DFB, OxrGetSpaceBoundary2DFB)
    SPATIAL_DISPATCH(xrGetSpaceRoomLayoutFB, OxrGetSpaceRoomLayoutFB)
    SPATIAL_DISPATCH(xrGetSpaceTriangleMeshMETA, OxrGetSpaceTriangleMeshMETA)
    SPATIAL_DISPATCH(xrRequestSceneCaptureFB, OxrRequestSceneCaptureFB)
#undef SPATIAL_DISPATCH
    return false;
}

void DrainEvents(Instance* instance, Session* session)
{
    if (instance == nullptr)
    {
        return;
    }
    auto pending = SpatialProxy::Get().TakeEvents();
    if (pending.empty())
    {
        return;
    }
    const XrSession sessionHandle = session != nullptr ? reinterpret_cast<XrSession>(session->GetHandle()) : XR_NULL_HANDLE;
    for (auto& event : pending)
    {
        XrEventDataBuffer buffer = {};
        switch (event.op)
        {
            case sp::Op::EvtAnchorCreateComplete:
            {
                sp::EvtAnchorCreateComplete e = {};
                if (event.payload.size() < sizeof(e)) break;
                std::memcpy(&e, event.payload.data(), sizeof(e));
                auto* out = reinterpret_cast<XrEventDataSpatialAnchorCreateCompleteFB*>(&buffer);
                out->type = XR_TYPE_EVENT_DATA_SPATIAL_ANCHOR_CREATE_COMPLETE_FB;
                out->requestId = e.asyncRequestId;
                out->result = static_cast<XrResult>(e.result);
                out->uuid = FromWire(e.uuid);
                out->space = XR_NULL_HANDLE;
                if (XR_SUCCEEDED(e.result))
                {
                    Space* space = EnsureRemoteSpace(session, e.spaceId);
                    out->space = space != nullptr ? reinterpret_cast<XrSpace>(space->GetHandle()) : XR_NULL_HANDLE;
                }
                spdlog::info("SpatialEntity: anchor create complete result={} uuid={} (request {})", e.result, UuidText(out->uuid), e.asyncRequestId);
                instance->PushEvent(buffer);
                break;
            }
            case sp::Op::EvtSetStatusComplete:
            {
                sp::EvtSetStatusComplete e = {};
                if (event.payload.size() < sizeof(e)) break;
                std::memcpy(&e, event.payload.data(), sizeof(e));
                auto* out = reinterpret_cast<XrEventDataSpaceSetStatusCompleteFB*>(&buffer);
                out->type = XR_TYPE_EVENT_DATA_SPACE_SET_STATUS_COMPLETE_FB;
                out->requestId = e.asyncRequestId;
                out->result = static_cast<XrResult>(e.result);
                out->space = SpaceHandleForRemoteId(e.spaceId);
                out->uuid = FromWire(e.uuid);
                out->componentType = static_cast<XrSpaceComponentTypeFB>(e.componentType);
                out->enabled = e.enabled ? XR_TRUE : XR_FALSE;
                InvalidateStatus(e.spaceId, e.componentType);
                instance->PushEvent(buffer);
                break;
            }
            case sp::Op::EvtSaveCompleteFB:
            case sp::Op::EvtEraseCompleteFB:
            {
                sp::EvtSpaceStorageComplete e = {};
                if (event.payload.size() < sizeof(e)) break;
                std::memcpy(&e, event.payload.data(), sizeof(e));
                if (event.op == sp::Op::EvtSaveCompleteFB)
                {
                    auto* out = reinterpret_cast<XrEventDataSpaceSaveCompleteFB*>(&buffer);
                    out->type = XR_TYPE_EVENT_DATA_SPACE_SAVE_COMPLETE_FB;
                    out->requestId = e.asyncRequestId;
                    out->result = static_cast<XrResult>(e.result);
                    out->space = SpaceHandleForRemoteId(e.spaceId);
                    out->uuid = FromWire(e.uuid);
                    out->location = static_cast<XrSpaceStorageLocationFB>(e.location);
                }
                else
                {
                    auto* out = reinterpret_cast<XrEventDataSpaceEraseCompleteFB*>(&buffer);
                    out->type = XR_TYPE_EVENT_DATA_SPACE_ERASE_COMPLETE_FB;
                    out->requestId = e.asyncRequestId;
                    out->result = static_cast<XrResult>(e.result);
                    out->space = SpaceHandleForRemoteId(e.spaceId);
                    out->uuid = FromWire(e.uuid);
                    out->location = static_cast<XrSpaceStorageLocationFB>(e.location);
                }
                spdlog::info("SpatialEntity: {} complete result={} (request {})",
                             event.op == sp::Op::EvtSaveCompleteFB ? "save" : "erase", e.result, e.asyncRequestId);
                instance->PushEvent(buffer);
                break;
            }
            case sp::Op::EvtSaveSpacesResultMETA:
            case sp::Op::EvtEraseSpacesResultMETA:
            case sp::Op::EvtQueryCompleteFB:
            case sp::Op::EvtDiscoveryCompleteMETA:
            case sp::Op::EvtSceneCaptureComplete:
            {
                sp::EvtRequestResult e = {};
                if (event.payload.size() < sizeof(e)) break;
                std::memcpy(&e, event.payload.data(), sizeof(e));
                // These five event structs share the layout {type, next, requestId, result}.
                auto* out = reinterpret_cast<XrEventDataSpacesSaveResultMETA*>(&buffer);
                switch (event.op)
                {
                    case sp::Op::EvtSaveSpacesResultMETA: out->type = XR_TYPE_EVENT_DATA_SPACES_SAVE_RESULT_META; break;
                    case sp::Op::EvtEraseSpacesResultMETA: out->type = XR_TYPE_EVENT_DATA_SPACES_ERASE_RESULT_META; break;
                    case sp::Op::EvtQueryCompleteFB: out->type = XR_TYPE_EVENT_DATA_SPACE_QUERY_COMPLETE_FB; break;
                    case sp::Op::EvtDiscoveryCompleteMETA: out->type = XR_TYPE_EVENT_DATA_SPACE_DISCOVERY_COMPLETE_META; break;
                    default: out->type = XR_TYPE_EVENT_DATA_SCENE_CAPTURE_COMPLETE_FB; break;
                }
                out->requestId = e.asyncRequestId;
                out->result = static_cast<XrResult>(e.result);
                spdlog::info("SpatialEntity: async request {} complete (op {:#x}) result={}", e.asyncRequestId, static_cast<int>(event.op), e.result);
                instance->PushEvent(buffer);
                break;
            }
            case sp::Op::EvtQueryResultsFB:
            case sp::Op::EvtDiscoveryResultsMETA:
            {
                size_t offset = 0;
                sp::EvtSpaceList head = {};
                if (!ReadAt(event.payload, offset, head)) break;
                std::vector<SpaceEntry> entries;
                for (uint32_t i = 0; i < head.count; ++i)
                {
                    sp::SpaceListEntry entry = {};
                    if (!ReadAt(event.payload, offset, entry)) break;
                    Space* space = EnsureRemoteSpace(session, entry.spaceId);
                    if (space == nullptr) continue;
                    entries.push_back({reinterpret_cast<XrSpace>(space->GetHandle()), FromWire(entry.uuid)});
                }
                {
                    std::lock_guard<std::mutex> lock(gMutex);
                    if (event.op == sp::Op::EvtQueryResultsFB)
                    {
                        gQueryResults[head.asyncRequestId] = std::move(entries);
                        auto* out = reinterpret_cast<XrEventDataSpaceQueryResultsAvailableFB*>(&buffer);
                        out->type = XR_TYPE_EVENT_DATA_SPACE_QUERY_RESULTS_AVAILABLE_FB;
                        out->requestId = head.asyncRequestId;
                    }
                    else
                    {
                        gDiscoveryResults[head.asyncRequestId] = std::move(entries);
                        auto* out = reinterpret_cast<XrEventDataSpaceDiscoveryResultsAvailableMETA*>(&buffer);
                        out->type = XR_TYPE_EVENT_DATA_SPACE_DISCOVERY_RESULTS_AVAILABLE_META;
                        out->requestId = head.asyncRequestId;
                    }
                }
                spdlog::info("SpatialEntity: {} results available: {} spaces (request {})",
                             event.op == sp::Op::EvtQueryResultsFB ? "query" : "discovery", head.count, head.asyncRequestId);
                instance->PushEvent(buffer);
                break;
            }
            default:
                spdlog::warn("SpatialEntity: unknown event op {:#x}", static_cast<int>(event.op));
                break;
        }
    }
    (void)sessionHandle;
}

void OnSpaceDestroyed(Space* space)
{
    if (space == nullptr || space->GetType() != Space::Type::Remote)
    {
        return;
    }
    const uint64_t remoteId = space->GetRemoteId();
    {
        std::lock_guard<std::mutex> lock(gMutex);
        gRemoteSpaces.erase(remoteId);
        ForgetSpaceCaches(remoteId);
    }
    SpatialProxy::Get().ForgetSpace(remoteId);
    sp::ReqSpace req = {remoteId};
    SpatialProxy::Get().Notify(sp::Op::DestroySpace, &req, sizeof(req));
}

void OnSessionDestroyed()
{
    std::lock_guard<std::mutex> lock(gMutex);
    gRemoteSpaces.clear();
    gQueryResults.clear();
    gDiscoveryResults.clear();
    gMeshCache.clear();
    gUuidCache.clear();
    gComponentsCache.clear();
    gStatusCache.clear();
}

} // namespace oxrsys::spatial_entity
