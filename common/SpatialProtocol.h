// SPDX-License-Identifier: MPL-2.0
//
// Link para Mac: wire format of the "spatial" channel (TcpRecordType::Spatial, port 9948).
//
// The macOS runtime does not own a tracking stack, so every spatial feature the headset has
// (XR_FB_spatial_entity anchors and their persistence, XR_FB_scene room data, scene capture,
// XR_META_spatial_entity_mesh) is proxied: the runtime turns the app's OpenXR call into a
// request, the Quest client performs it against the headset's own OpenXR runtime and answers.
// Asynchronous OpenXR requests complete through events the client forwards; anchor poses are
// streamed by the client in its STAGE space, which is the runtime's world frame.
//
// All structs are plain little-endian POD, 8-byte aligned, shared by runtime and client.
// Variable-sized messages are a fixed header followed by arrays whose counts are in the header.

#pragma once

#include <cstddef>
#include <cstdint>

namespace oxr
{
namespace protocol
{
namespace spatial
{

constexpr uint16_t SPATIAL_VERSION = 1;
constexpr uint32_t UUID_SIZE = 16;
constexpr uint32_t MAX_COMPONENT_TYPES = 32;
constexpr uint32_t MAX_SEMANTIC_LABELS_BYTES = 256;

enum class MsgKind : uint16_t
{
    Request = 1,  // runtime -> client, answered by exactly one Response with the same requestId
    Response = 2, // client -> runtime
    Event = 3,    // client -> runtime, unsolicited (OpenXR async completion events)
    Poses = 4,    // client -> runtime, periodic poses of locatable spaces
    Hello = 5,    // client -> runtime, once after connecting
};

enum class Op : uint16_t
{
    None = 0,
    Hello = 0x0001,

    // XR_FB_spatial_entity
    CreateSpatialAnchor = 0x0010,       // ReqCreateSpatialAnchor -> RespResult ; EvtAnchorCreateComplete
    GetSpaceUuid = 0x0011,              // ReqSpace -> RespUuid
    EnumerateSpaceComponents = 0x0012,  // ReqSpace -> RespComponents
    SetSpaceComponentStatus = 0x0013,   // ReqSetComponentStatus -> RespResult ; EvtSetStatusComplete
    GetSpaceComponentStatus = 0x0014,   // ReqGetComponentStatus -> RespComponentStatus
    DestroySpace = 0x0015,              // ReqSpace -> RespResult

    // XR_FB_spatial_entity_storage / XR_META_spatial_entity_persistence
    SaveSpaceFB = 0x0020,     // ReqSaveSpaceFB -> RespResult ; EvtSaveCompleteFB
    EraseSpaceFB = 0x0021,    // ReqEraseSpaceFB -> RespResult ; EvtEraseCompleteFB
    SaveSpacesMETA = 0x0022,  // ReqSpaceList -> RespResult ; EvtSaveSpacesResultMETA
    EraseSpacesMETA = 0x0023, // ReqEraseSpacesMETA -> RespResult ; EvtEraseSpacesResultMETA

    // XR_FB_spatial_entity_query / XR_META_spatial_entity_discovery
    QuerySpacesFB = 0x0030,      // ReqQuerySpacesFB -> RespResult ; EvtQueryResultsFB, EvtQueryCompleteFB
    DiscoverSpacesMETA = 0x0031, // ReqDiscoverSpacesMETA -> RespResult ; EvtDiscoveryResultsMETA, EvtDiscoveryCompleteMETA

    // XR_FB_spatial_entity_container / XR_FB_scene / XR_META_spatial_entity_mesh / XR_FB_scene_capture
    GetSpaceContainer = 0x0040,     // ReqSpace -> RespContainer
    GetSpaceBoundingBox2D = 0x0041, // ReqSpace -> RespBoundingBox2D
    GetSpaceBoundingBox3D = 0x0042, // ReqSpace -> RespBoundingBox3D
    GetSpaceSemanticLabels = 0x0043,// ReqSemanticLabels -> RespSemanticLabels
    GetSpaceBoundary2D = 0x0044,    // ReqSpace -> RespBoundary2D
    GetSpaceRoomLayout = 0x0045,    // ReqSpace -> RespRoomLayout
    GetSpaceTriangleMesh = 0x0046,  // ReqSpace -> RespTriangleMesh
    RequestSceneCapture = 0x0047,   // ReqSceneCapture -> RespResult ; EvtSceneCaptureComplete

    // Client -> runtime
    SpacePoses = 0x0050, // SpacePosesHeader + SpacePose[count]

    // Events (client -> runtime), payloads below
    EvtAnchorCreateComplete = 0x0060,
    EvtSetStatusComplete = 0x0061,
    EvtSaveCompleteFB = 0x0062,
    EvtEraseCompleteFB = 0x0063,
    EvtSaveSpacesResultMETA = 0x0064,
    EvtEraseSpacesResultMETA = 0x0065,
    EvtQueryResultsFB = 0x0066,       // EvtSpaceList
    EvtQueryCompleteFB = 0x0067,      // EvtRequestResult
    EvtDiscoveryResultsMETA = 0x0068, // EvtSpaceList
    EvtDiscoveryCompleteMETA = 0x0069,// EvtRequestResult
    EvtSceneCaptureComplete = 0x006A, // EvtRequestResult
};

struct MsgHeader
{
    uint16_t version = SPATIAL_VERSION;
    MsgKind kind = MsgKind::Request;
    Op op = Op::None;
    uint16_t reserved = 0;
    uint32_t requestId = 0;   // Request/Response correlation (runtime-chosen)
    uint32_t payloadSize = 0; // bytes after this header
};
static_assert(sizeof(MsgHeader) == 16, "MsgHeader layout");

struct Vec3 { float x, y, z; };
struct Quat { float x, y, z, w; };
struct Pose { Quat orientation; Vec3 position; };
struct Uuid { uint8_t data[UUID_SIZE]; };

// ─── Hello ──────────────────────────────────────────────────────────────────

enum HelloFeature : uint32_t
{
    FEATURE_ANCHORS = 0x1,          // XR_FB_spatial_entity + storage/query
    FEATURE_PERSISTENCE_META = 0x2, // XR_META_spatial_entity_persistence + discovery
    FEATURE_SCENE = 0x4,            // XR_FB_scene + container
    FEATURE_SCENE_CAPTURE = 0x8,
    FEATURE_MESH = 0x10,            // XR_META_spatial_entity_mesh
};

struct Hello
{
    uint32_t features = 0;
    uint32_t scenePermissionGranted = 0; // com.oculus.permission.USE_SCENE
};

// ─── Requests ────────────────────────────────────────────────────────────────

struct ReqCreateSpatialAnchor
{
    uint64_t asyncRequestId; // the XrAsyncRequestIdFB the runtime returned to the app
    Pose poseInStage;        // anchor pose in the client's STAGE space (= runtime world frame)
};

struct ReqSpace
{
    uint64_t spaceId; // client XrSpace handle value
};

struct ReqSetComponentStatus
{
    uint64_t asyncRequestId;
    uint64_t spaceId;
    uint32_t componentType; // XrSpaceComponentTypeFB
    uint32_t enabled;
    int64_t timeoutNs;      // XrDuration
};

struct ReqGetComponentStatus
{
    uint64_t spaceId;
    uint32_t componentType;
    uint32_t reserved = 0;
};

struct ReqSaveSpaceFB
{
    uint64_t asyncRequestId;
    uint64_t spaceId;
    uint32_t location;        // XrSpaceStorageLocationFB
    uint32_t persistenceMode; // XrSpacePersistenceModeFB
};

struct ReqEraseSpaceFB
{
    uint64_t asyncRequestId;
    uint64_t spaceId;
    uint32_t location;
    uint32_t reserved = 0;
};

struct ReqSpaceList // SaveSpacesMETA
{
    uint64_t asyncRequestId;
    uint32_t spaceCount;
    uint32_t reserved = 0;
    // followed by uint64_t spaceIds[spaceCount]
};

struct ReqEraseSpacesMETA
{
    uint64_t asyncRequestId;
    uint32_t spaceCount;
    uint32_t uuidCount;
    // followed by uint64_t spaceIds[spaceCount], then Uuid uuids[uuidCount]
};

enum FilterKind : uint32_t
{
    FILTER_NONE = 0,
    FILTER_UUIDS = 1,
    FILTER_COMPONENT = 2,
};

struct ReqQuerySpacesFB
{
    uint64_t asyncRequestId;
    int64_t timeoutNs;
    uint32_t maxResultCount;
    uint32_t storageLocation; // XrSpaceStorageLocationFB (XrSpaceStorageLocationFilterInfoFB), 0 = none
    uint32_t filterKind;      // FilterKind
    uint32_t componentType;   // when FILTER_COMPONENT
    uint32_t uuidCount;       // when FILTER_UUIDS
    uint32_t reserved = 0;
    // followed by Uuid uuids[uuidCount]
};

struct DiscoveryFilter
{
    uint32_t kind;          // FilterKind (UUIDS or COMPONENT)
    uint32_t componentType; // when COMPONENT
    uint32_t uuidCount;     // when UUIDS
    uint32_t reserved = 0;
    // followed by Uuid uuids[uuidCount]
};

struct ReqDiscoverSpacesMETA
{
    uint64_t asyncRequestId;
    uint32_t filterCount;
    uint32_t reserved = 0;
    // followed by filterCount x (DiscoveryFilter + its uuids)
};

struct ReqSemanticLabels
{
    uint64_t spaceId;
    uint64_t supportFlags;    // XrSemanticLabelsSupportFlagsFB from the app's XrSemanticLabelsSupportInfoFB (0 = none chained)
    uint32_t labelsByteCount; // recognizedLabels (without terminator); 0 = none
    uint32_t reserved = 0;
    // followed by char recognizedLabels[labelsByteCount]
};

struct ReqSceneCapture
{
    uint64_t asyncRequestId;
    uint32_t requestByteCount;
    uint32_t reserved = 0;
    // followed by char request[requestByteCount]
};

// ─── Responses ───────────────────────────────────────────────────────────────

struct RespResult
{
    int32_t result; // XrResult of the headset call
    uint32_t reserved = 0;
};

struct RespUuid
{
    int32_t result;
    uint32_t reserved = 0;
    Uuid uuid;
};

struct RespComponents
{
    int32_t result;
    uint32_t count;
    uint32_t types[MAX_COMPONENT_TYPES];
};

struct RespComponentStatus
{
    int32_t result;
    uint32_t enabled;
    uint32_t changePending;
    uint32_t reserved = 0;
};

struct RespContainer
{
    int32_t result;
    uint32_t uuidCount;
    // followed by Uuid uuids[uuidCount]
};

struct RespBoundingBox2D
{
    int32_t result;
    uint32_t reserved = 0;
    float offsetX, offsetY, extentWidth, extentHeight;
};

struct RespBoundingBox3D
{
    int32_t result;
    uint32_t reserved = 0;
    Vec3 offset;
    Vec3 extent;
};

struct RespSemanticLabels
{
    int32_t result;
    uint32_t byteCount; // without terminator
    char labels[MAX_SEMANTIC_LABELS_BYTES];
};

struct RespBoundary2D
{
    int32_t result;
    uint32_t vertexCount;
    // followed by float xy[vertexCount * 2]
};

struct RespRoomLayout
{
    int32_t result;
    uint32_t wallCount;
    Uuid floorUuid;
    Uuid ceilingUuid;
    // followed by Uuid walls[wallCount]
};

struct RespTriangleMesh
{
    int32_t result;
    uint32_t vertexCount;
    uint32_t indexCount;
    uint32_t reserved = 0;
    // followed by Vec3 vertices[vertexCount], then uint32_t indices[indexCount]
};

// ─── Poses ───────────────────────────────────────────────────────────────────

struct SpacePosesHeader
{
    uint32_t count;
    uint32_t reserved = 0;
    int64_t timeNs; // client XrTime the poses were located at
    // followed by SpacePose[count]
};

struct SpacePose
{
    uint64_t spaceId;
    uint64_t locationFlags; // XrSpaceLocationFlags
    Pose poseInStage;
};

// ─── Events ──────────────────────────────────────────────────────────────────

struct EvtAnchorCreateComplete
{
    uint64_t asyncRequestId;
    int32_t result;
    uint32_t reserved = 0;
    uint64_t spaceId; // new client XrSpace (0 on failure)
    Uuid uuid;
};

struct EvtSetStatusComplete
{
    uint64_t asyncRequestId;
    int32_t result;
    uint32_t componentType;
    uint64_t spaceId;
    Uuid uuid;
    uint32_t enabled;
    uint32_t reserved = 0;
};

struct EvtSpaceStorageComplete // SaveCompleteFB / EraseCompleteFB
{
    uint64_t asyncRequestId;
    int32_t result;
    uint32_t location;
    uint64_t spaceId;
    Uuid uuid;
};

struct EvtRequestResult // SaveSpacesResultMETA, EraseSpacesResultMETA, QueryCompleteFB, DiscoveryCompleteMETA, SceneCaptureComplete
{
    uint64_t asyncRequestId;
    int32_t result;
    uint32_t reserved = 0;
};

struct SpaceListEntry
{
    uint64_t spaceId;
    Uuid uuid;
};

struct EvtSpaceList // QueryResultsFB, DiscoveryResultsMETA
{
    uint64_t asyncRequestId;
    int32_t result; // of the retrieve call
    uint32_t count;
    // followed by SpaceListEntry entries[count]
};

static_assert(sizeof(Pose) == 28, "Pose layout");
static_assert(sizeof(SpacePose) == 48, "SpacePose layout (28 + 4 padding)");
static_assert(sizeof(ReqCreateSpatialAnchor) == 40, "ReqCreateSpatialAnchor layout");
static_assert(sizeof(EvtAnchorCreateComplete) == 40, "EvtAnchorCreateComplete layout");
static_assert(sizeof(EvtSetStatusComplete) == 48, "EvtSetStatusComplete layout");
static_assert(sizeof(EvtSpaceStorageComplete) == 40, "EvtSpaceStorageComplete layout");
static_assert(sizeof(SpaceListEntry) == 24, "SpaceListEntry layout");
static_assert(sizeof(RespRoomLayout) == 40, "RespRoomLayout layout");

} // namespace spatial
} // namespace protocol
} // namespace oxr
