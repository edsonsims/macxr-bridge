// SPDX-License-Identifier: MPL-2.0
//
// Link para Mac: headset side of the spatial channel. Executes the macOS runtime's spatial
// requests (anchors, persistence, queries, scene data) against the Quest's own OpenXR runtime,
// forwards the async completion events and streams the poses of the resulting spaces in the
// client's STAGE space.

#pragma once

#include "SpatialProtocol.h"

#include <openxr/openxr.h>

#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <vector>

class SpatialBridge
{
public:
    SpatialBridge() = default;
    ~SpatialBridge();

    // Resolves the extension functions; call once after the session exists.
    void Initialize(XrInstance instance, XrSession session, XrSpace stageSpace,
                    const std::vector<const char*>& enabledExtensions);

    // Begins serving the runtime over the connected spatial socket (owned by the caller).
    void Start(int socket, bool scenePermissionGranted);
    // Stops serving; the caller closes the socket. Spaces created for the runtime are destroyed.
    void Stop();
    bool IsRunning() const { return running_.load(); }

    // Main (XR) thread hooks.
    void SetFrameTime(XrTime predictedDisplayTime) { frameTime_.store(predictedDisplayTime); }
    bool OnEvent(const XrEventDataBuffer& event); // true when the event was a spatial event
    void SendPosesIfDue();

    uint32_t GetFeatures() const { return features_; }

private:
    using Op = oxr::protocol::spatial::Op;
    using MsgKind = oxr::protocol::spatial::MsgKind;

    void ReaderThread(int socket);
    void HandleRequest(Op op, uint32_t requestId, const uint8_t* payload, size_t size);
    bool SendMsg(MsgKind kind, Op op, uint32_t requestId, const void* payload, size_t size);
    bool Respond(uint32_t requestId, const void* payload, size_t size);
    bool SendEvent(Op op, const void* payload, size_t size);

    XrSpace ResolveSpace(uint64_t spaceId);
    void TrackSpace(XrSpace space);
    void UntrackSpace(XrSpace space);
    void DestroyTrackedSpaces();
    uint64_t TakeAsyncRequestId(XrAsyncRequestIdFB clientId, bool erase);
    void MapAsyncRequest(XrAsyncRequestIdFB clientId, uint64_t runtimeId);

    XrInstance instance_ = XR_NULL_HANDLE;
    XrSession session_ = XR_NULL_HANDLE;
    XrSpace stageSpace_ = XR_NULL_HANDLE;
    uint32_t features_ = 0;
    bool scenePermission_ = false;

    std::atomic<bool> running_{false};
    std::atomic<int64_t> frameTime_{0};
    int socket_ = -1;
    std::thread reader_;
    std::mutex sendMutex_;

    std::mutex spacesMutex_;
    std::unordered_set<XrSpace> spaces_;
    int64_t lastPosesSentNs_ = 0;

    std::mutex requestsMutex_;
    std::unordered_map<XrAsyncRequestIdFB, uint64_t> asyncRequests_; // client id -> runtime id

    // XR_FB_spatial_entity
    PFN_xrCreateSpatialAnchorFB xrCreateSpatialAnchorFB_ = nullptr;
    PFN_xrGetSpaceUuidFB xrGetSpaceUuidFB_ = nullptr;
    PFN_xrEnumerateSpaceSupportedComponentsFB xrEnumerateSpaceSupportedComponentsFB_ = nullptr;
    PFN_xrSetSpaceComponentStatusFB xrSetSpaceComponentStatusFB_ = nullptr;
    PFN_xrGetSpaceComponentStatusFB xrGetSpaceComponentStatusFB_ = nullptr;
    // XR_FB_spatial_entity_storage
    PFN_xrSaveSpaceFB xrSaveSpaceFB_ = nullptr;
    PFN_xrEraseSpaceFB xrEraseSpaceFB_ = nullptr;
    // XR_FB_spatial_entity_query
    PFN_xrQuerySpacesFB xrQuerySpacesFB_ = nullptr;
    PFN_xrRetrieveSpaceQueryResultsFB xrRetrieveSpaceQueryResultsFB_ = nullptr;
    // XR_META_spatial_entity_persistence / discovery
    PFN_xrSaveSpacesMETA xrSaveSpacesMETA_ = nullptr;
    PFN_xrEraseSpacesMETA xrEraseSpacesMETA_ = nullptr;
    PFN_xrDiscoverSpacesMETA xrDiscoverSpacesMETA_ = nullptr;
    PFN_xrRetrieveSpaceDiscoveryResultsMETA xrRetrieveSpaceDiscoveryResultsMETA_ = nullptr;
    // XR_FB_spatial_entity_container / XR_FB_scene / XR_META_spatial_entity_mesh / XR_FB_scene_capture
    PFN_xrGetSpaceContainerFB xrGetSpaceContainerFB_ = nullptr;
    PFN_xrGetSpaceBoundingBox2DFB xrGetSpaceBoundingBox2DFB_ = nullptr;
    PFN_xrGetSpaceBoundingBox3DFB xrGetSpaceBoundingBox3DFB_ = nullptr;
    PFN_xrGetSpaceSemanticLabelsFB xrGetSpaceSemanticLabelsFB_ = nullptr;
    PFN_xrGetSpaceBoundary2DFB xrGetSpaceBoundary2DFB_ = nullptr;
    PFN_xrGetSpaceRoomLayoutFB xrGetSpaceRoomLayoutFB_ = nullptr;
    PFN_xrGetSpaceTriangleMeshMETA xrGetSpaceTriangleMeshMETA_ = nullptr;
    PFN_xrRequestSceneCaptureFB xrRequestSceneCaptureFB_ = nullptr;
};
