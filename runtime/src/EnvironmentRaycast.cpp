// SPDX-License-Identifier: MPL-2.0

#include "EnvironmentRaycast.h"

#include "CameraRelay.h"
#include "Runtime.h"
#include "Session.h"
#include "Space.h"

#include <glm/glm.hpp>
#include <glm/gtc/quaternion.hpp>
#include <spdlog/spdlog.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>
#include <limits>
#include <mutex>
#include <unordered_map>

// XR_META_environment_raycast became public in OpenXR 1.1.6x; the runtime still builds against 1.1.57, so
// carry the definitions (identical to the registry header) until the SDK is bumped.
#ifndef XR_META_environment_raycast
#define XR_META_environment_raycast 1
XR_DEFINE_HANDLE(XrEnvironmentRaycasterMETA)
#define XR_META_environment_raycast_SPEC_VERSION 1
#define XR_META_ENVIRONMENT_RAYCAST_EXTENSION_NAME "XR_META_environment_raycast"
typedef enum XrEnvironmentRaycastHitStatusMETA {
    XR_ENVIRONMENT_RAYCAST_HIT_STATUS_HIT_META = 1,
    XR_ENVIRONMENT_RAYCAST_HIT_STATUS_NO_HIT_META = 2,
    XR_ENVIRONMENT_RAYCAST_HIT_STATUS_HIT_POINT_OCCLUDED_META = 3,
    XR_ENVIRONMENT_RAYCAST_HIT_STATUS_HIT_POINT_OUTSIDE_OF_FOV_META = 4,
    XR_ENVIRONMENT_RAYCAST_HIT_STATUS_RAY_OCCLUDED_META = 5,
    XR_ENVIRONMENT_RAYCAST_HIT_STATUS_HIT_INVALID_ORIENTATION_META = 6,
    XR_ENVIRONMENT_RAYCAST_HIT_STATUS_MAX_ENUM_META = 0x7FFFFFFF
} XrEnvironmentRaycastHitStatusMETA;
typedef struct XrSystemEnvironmentRaycastPropertiesMETA {
    XrStructureType       type;
    void* XR_MAY_ALIAS    next;
    XrBool32              supportsEnvironmentRaycast;
} XrSystemEnvironmentRaycastPropertiesMETA;
typedef struct XrEnvironmentRaycasterCreateInfoMETA {
    XrStructureType             type;
    const void* XR_MAY_ALIAS    next;
} XrEnvironmentRaycasterCreateInfoMETA;
typedef struct XrEnvironmentRaycasterCreateCompletionMETA {
    XrStructureType               type;
    void* XR_MAY_ALIAS            next;
    XrResult                      futureResult;
    XrEnvironmentRaycasterMETA    environmentRaycaster;
} XrEnvironmentRaycasterCreateCompletionMETA;
typedef struct XR_MAY_ALIAS XrEnvironmentRaycastFilterBaseHeaderMETA {
    XrStructureType             type;
    const void* XR_MAY_ALIAS    next;
} XrEnvironmentRaycastFilterBaseHeaderMETA;
typedef struct XrEnvironmentRaycastHitGetInfoMETA {
    XrStructureType                                            type;
    const void* XR_MAY_ALIAS                                   next;
    XrSpace                                                    baseSpace;
    XrTime                                                     time;
    XrVector3f                                                 origin;
    XrVector3f                                                 direction;
    uint32_t                                                   filterCount;
    const XrEnvironmentRaycastFilterBaseHeaderMETA* const *    filters;
} XrEnvironmentRaycastHitGetInfoMETA;
typedef struct XrEnvironmentRaycastHitMETA {
    XrStructureType                      type;
    const void* XR_MAY_ALIAS             next;
    XrEnvironmentRaycastHitStatusMETA    status;
    XrPosef                              pose;
} XrEnvironmentRaycastHitMETA;
typedef struct XrEnvironmentRaycastFilterDistanceMETA {
    XrStructureType             type;
    const void* XR_MAY_ALIAS    next;
    float                       maxDistance;
} XrEnvironmentRaycastFilterDistanceMETA;
constexpr XrStructureType XR_TYPE_SYSTEM_ENVIRONMENT_RAYCAST_PROPERTIES_META = static_cast<XrStructureType>(1000592000);
constexpr XrStructureType XR_TYPE_ENVIRONMENT_RAYCASTER_CREATE_INFO_META = static_cast<XrStructureType>(1000592001);
constexpr XrStructureType XR_TYPE_ENVIRONMENT_RAYCASTER_CREATE_COMPLETION_META = static_cast<XrStructureType>(1000592002);
constexpr XrStructureType XR_TYPE_ENVIRONMENT_RAYCAST_HIT_GET_INFO_META = static_cast<XrStructureType>(1000592003);
constexpr XrStructureType XR_TYPE_ENVIRONMENT_RAYCAST_HIT_META = static_cast<XrStructureType>(1000592004);
constexpr XrStructureType XR_TYPE_ENVIRONMENT_RAYCAST_FILTER_DISTANCE_META = static_cast<XrStructureType>(1000592005);
#endif

namespace
{
using oxr::protocol::camera::DepthInfo;

constexpr int64_t kMaxDepthAgeNs = 2'000'000'000; // older depth means the headset stopped sending
constexpr float kMaxMarchDistance = 12.0f;        // the depth sensor does not see much farther

struct FutureEntry
{
    Session* session = nullptr;
};

std::mutex gMutex;
std::unordered_map<uint64_t, FutureEntry> gFutures;
std::unordered_map<uint64_t, Session*> gRaycasters;
std::atomic<uint64_t> gNextId{0x4c504d0000000001ull}; // "LPM": distinct from the runtime's handle ranges
std::atomic<uint32_t> gRaycastCount{0};

Session* SessionFromHandle(XrSession session)
{
    return Runtime::Get().FromHandle<Session>(reinterpret_cast<uint64_t>(session));
}

glm::vec3 ToGlm(const XrVector3f& v) { return {v.x, v.y, v.z}; }
glm::quat ToGlm(const oxr::protocol::camera::Quat& q) { return glm::quat(q.w, q.x, q.y, q.z); }
glm::vec3 ToGlm(const oxr::protocol::camera::Vec3& v) { return {v.x, v.y, v.z}; }
XrVector3f ToXr(const glm::vec3& v) { return {v.x, v.y, v.z}; }
XrQuaternionf ToXr(const glm::quat& q) { return {q.x, q.y, q.z, q.w}; }

// The latest relayed depth image, seen from the depth view (OpenXR view axes: +X right, +Y up, -Z forward),
// in the client's STAGE space = this runtime's world space.
struct DepthView
{
    DepthInfo info{};
    std::shared_ptr<const std::vector<uint16_t>> pixels;
    glm::quat rotation{1, 0, 0, 0};
    glm::quat inverseRotation{1, 0, 0, 0};
    glm::vec3 position{0};
    float tanLeft = 0, tanRight = 0, tanUp = 0, tanDown = 0;

    bool Project(const glm::vec3& world, int& i, int& j, float& viewZ) const
    {
        const glm::vec3 local = inverseRotation * (world - position);
        viewZ = -local.z;
        if (viewZ <= 0.02f) return false;
        const float u = (local.x / viewZ - tanLeft) / (tanRight - tanLeft);
        const float v = (local.y / viewZ - tanDown) / (tanUp - tanDown);
        if (!(u >= 0.0f && u < 1.0f && v >= 0.0f && v < 1.0f)) return false;
        i = static_cast<int>(u * info.width);
        j = static_cast<int>(v * info.height); // row 0 = bottom of the image
        return true;
    }

    // Distance along the view axis at pixel (i, j); NaN where the sensor has no data.
    float LinearDepth(int i, int j) const
    {
        if (i < 0 || j < 0 || i >= static_cast<int>(info.width) || j >= static_cast<int>(info.height))
            return std::numeric_limits<float>::quiet_NaN();
        const float d = (*pixels)[static_cast<size_t>(j) * info.width + i] / 65535.0f;
        if (d <= 0.0f || d >= 0.99999f) return std::numeric_limits<float>::quiet_NaN();
        const float n = info.nearZ, f = info.farZ;
        if (!std::isfinite(f) || f <= n) return n / (1.0f - d);
        const float ndc = 2.0f * d - 1.0f;
        return 2.0f * f * n / ((f + n) - ndc * (f - n));
    }

    glm::vec3 Unproject(int i, int j, float z) const
    {
        const float tx = tanLeft + (i + 0.5f) / info.width * (tanRight - tanLeft);
        const float ty = tanDown + (j + 0.5f) / info.height * (tanUp - tanDown);
        return position + rotation * glm::vec3(tx * z, ty * z, -z);
    }

    // Surface normal at a hit from neighbouring depth samples, facing `toward`. False when unknown.
    bool Normal(int i, int j, const glm::vec3& toward, glm::vec3& normal) const
    {
        for (int r : {3, -3, 2, -2})
        {
            const float z0 = LinearDepth(i, j), zx = LinearDepth(i + r, j), zy = LinearDepth(i, j + r);
            if (std::isnan(z0) || std::isnan(zx) || std::isnan(zy)) continue;
            const glm::vec3 p0 = Unproject(i, j, z0);
            glm::vec3 n = glm::cross(Unproject(i + r, j, zx) - p0, Unproject(i, j + r, zy) - p0);
            const float len = glm::length(n);
            if (len < 1e-8f) continue;
            n /= len;
            if (glm::dot(n, toward) < 0.0f) n = -n;
            normal = n;
            return true;
        }
        return false;
    }
};

bool LoadDepthView(DepthView& view)
{
    int64_t ageNs = 0;
    if (!CameraRelay::Get().GetDepthSnapshot(view.info, view.pixels, ageNs)) return false;
    if (ageNs > kMaxDepthAgeNs || !view.pixels || view.info.width == 0 || view.info.height == 0) return false;
    view.rotation = glm::normalize(ToGlm(view.info.viewPose.orientation));
    view.inverseRotation = glm::inverse(view.rotation);
    view.position = ToGlm(view.info.viewPose.position);
    view.tanLeft = std::tan(view.info.fov.angleLeft);
    view.tanRight = std::tan(view.info.fov.angleRight);
    view.tanUp = std::tan(view.info.fov.angleUp);
    view.tanDown = std::tan(view.info.fov.angleDown);
    return true;
}

// Orientation whose +Z is the surface normal and whose +Y points roughly back at the ray origin (spec).
glm::quat HitOrientation(const glm::vec3& normal, const glm::vec3& towardOrigin)
{
    const glm::vec3 z = normal;
    glm::vec3 y = towardOrigin - glm::dot(towardOrigin, z) * z;
    if (glm::length(y) < 1e-4f)
    {
        const glm::vec3 helper = std::fabs(z.y) < 0.9f ? glm::vec3(0, 1, 0) : glm::vec3(1, 0, 0);
        y = helper - glm::dot(helper, z) * z;
    }
    y = glm::normalize(y);
    const glm::vec3 x = glm::cross(y, z);
    return glm::normalize(glm::quat_cast(glm::mat3(x, y, z)));
}

XRAPI_ATTR XrResult XRAPI_CALL OxrPollFutureEXT(XrInstance, const XrFuturePollInfoEXT* pollInfo, XrFuturePollResultEXT* pollResult)
{
    if (pollInfo == nullptr || pollResult == nullptr || pollInfo->type != XR_TYPE_FUTURE_POLL_INFO_EXT ||
        pollResult->type != XR_TYPE_FUTURE_POLL_RESULT_EXT)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    std::lock_guard<std::mutex> lock(gMutex);
    if (gFutures.find(reinterpret_cast<uint64_t>(pollInfo->future)) == gFutures.end()) return XR_ERROR_FUTURE_INVALID_EXT;
    pollResult->state = XR_FUTURE_STATE_READY_EXT; // creating a raycaster needs nothing from the headset
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL OxrCancelFutureEXT(XrInstance, const XrFutureCancelInfoEXT* cancelInfo)
{
    if (cancelInfo == nullptr || cancelInfo->type != XR_TYPE_FUTURE_CANCEL_INFO_EXT) return XR_ERROR_VALIDATION_FAILURE;
    std::lock_guard<std::mutex> lock(gMutex);
    return gFutures.erase(reinterpret_cast<uint64_t>(cancelInfo->future)) > 0 ? XR_SUCCESS : XR_ERROR_FUTURE_INVALID_EXT;
}

XRAPI_ATTR XrResult XRAPI_CALL OxrCreateEnvironmentRaycasterAsyncMETA(XrSession session,
                                                                     const XrEnvironmentRaycasterCreateInfoMETA* info,
                                                                     XrFutureEXT* future)
{
    Session* sess = SessionFromHandle(session);
    if (sess == nullptr) return XR_ERROR_HANDLE_INVALID;
    if (info == nullptr || info->type != XR_TYPE_ENVIRONMENT_RAYCASTER_CREATE_INFO_META || future == nullptr)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    const uint64_t id = gNextId.fetch_add(1);
    {
        std::lock_guard<std::mutex> lock(gMutex);
        gFutures[id] = {sess};
    }
    *future = reinterpret_cast<XrFutureEXT>(id);
    spdlog::info("EnvironmentRaycast: xrCreateEnvironmentRaycasterAsyncMETA -> future {:#x}", id);
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL OxrCreateEnvironmentRaycasterCompleteMETA(XrSession session, XrFutureEXT future,
                                                                        XrEnvironmentRaycasterCreateCompletionMETA* completion)
{
    Session* sess = SessionFromHandle(session);
    if (sess == nullptr) return XR_ERROR_HANDLE_INVALID;
    if (completion == nullptr || completion->type != XR_TYPE_ENVIRONMENT_RAYCASTER_CREATE_COMPLETION_META)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    std::lock_guard<std::mutex> lock(gMutex);
    auto it = gFutures.find(reinterpret_cast<uint64_t>(future));
    if (it == gFutures.end() || it->second.session != sess) return XR_ERROR_FUTURE_INVALID_EXT;
    gFutures.erase(it);
    const uint64_t id = gNextId.fetch_add(1);
    gRaycasters[id] = sess;
    completion->futureResult = XR_SUCCESS;
    completion->environmentRaycaster = reinterpret_cast<XrEnvironmentRaycasterMETA>(id);
    spdlog::info("EnvironmentRaycast: raycaster {:#x} created (depth relayed from the headset)", id);
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL OxrDestroyEnvironmentRaycasterMETA(XrEnvironmentRaycasterMETA raycaster)
{
    std::lock_guard<std::mutex> lock(gMutex);
    return gRaycasters.erase(reinterpret_cast<uint64_t>(raycaster)) > 0 ? XR_SUCCESS : XR_ERROR_HANDLE_INVALID;
}

XRAPI_ATTR XrResult XRAPI_CALL OxrPerformEnvironmentRaycastMETA(XrEnvironmentRaycasterMETA raycaster,
                                                               const XrEnvironmentRaycastHitGetInfoMETA* info,
                                                               XrEnvironmentRaycastHitMETA* hit)
{
    Session* sess = nullptr;
    {
        std::lock_guard<std::mutex> lock(gMutex);
        auto it = gRaycasters.find(reinterpret_cast<uint64_t>(raycaster));
        if (it == gRaycasters.end()) return XR_ERROR_HANDLE_INVALID;
        sess = it->second;
    }
    if (info == nullptr || hit == nullptr || info->type != XR_TYPE_ENVIRONMENT_RAYCAST_HIT_GET_INFO_META ||
        hit->type != XR_TYPE_ENVIRONMENT_RAYCAST_HIT_META || (info->filterCount > 0 && info->filters == nullptr))
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    auto* base = Runtime::Get().FromHandle<Space>(reinterpret_cast<uint64_t>(info->baseSpace));
    if (base == nullptr || base->GetSession() != sess) return XR_ERROR_HANDLE_INVALID;
    if (info->time <= 0) return XR_ERROR_TIME_INVALID;

    float maxDistance = 100.0f;
    for (uint32_t f = 0; f < info->filterCount; ++f)
    {
        const auto* filter = info->filters[f];
        if (filter != nullptr && filter->type == XR_TYPE_ENVIRONMENT_RAYCAST_FILTER_DISTANCE_META)
        {
            maxDistance = reinterpret_cast<const XrEnvironmentRaycastFilterDistanceMETA*>(filter)->maxDistance;
        }
    }

    hit->status = XR_ENVIRONMENT_RAYCAST_HIT_STATUS_NO_HIT_META;
    hit->pose = {{0, 0, 0, 1}, {0, 0, 0}};

    // Ray in world (= client STAGE) space: transform the origin and a point one unit along the direction.
    XrPosef originPose = {{0, 0, 0, 1}, info->origin};
    XrPosef aheadPose = {{0, 0, 0, 1}, {info->origin.x + info->direction.x, info->origin.y + info->direction.y,
                                         info->origin.z + info->direction.z}};
    XrPosef worldOrigin = {}, worldAhead = {};
    if (!base->ToWorldPose(originPose, &worldOrigin) || !base->ToWorldPose(aheadPose, &worldAhead)) return XR_SUCCESS;
    const glm::vec3 origin = ToGlm(worldOrigin.position);
    glm::vec3 direction = ToGlm(worldAhead.position) - origin;
    if (glm::length(direction) < 1e-6f) return XR_ERROR_VALIDATION_FAILURE;
    direction = glm::normalize(direction);

    DepthView view;
    const uint32_t count = ++gRaycastCount;
    if (!LoadDepthView(view))
    {
        if (count <= 3 || count % 500 == 0) spdlog::info("EnvironmentRaycast: no fresh depth from the headset (raycast #{})", count);
        return XR_SUCCESS; // NO_HIT
    }

    const float limit = std::min(maxDistance > 0.0f ? maxDistance : 100.0f, kMaxMarchDistance);
    float previousT = 0.0f;
    bool everInView = false, lastInView = false, firstInView = true;
    for (float t = 0.02f; t <= limit; t += std::max(0.01f, 0.01f * t))
    {
        int i = 0, j = 0;
        float viewZ = 0.0f;
        const glm::vec3 p = origin + direction * t;
        lastInView = view.Project(p, i, j, viewZ);
        if (!lastInView) { previousT = t; continue; }
        everInView = true;
        const float surface = view.LinearDepth(i, j);
        if (std::isnan(surface) || viewZ < surface) { previousT = t; firstInView = false; continue; }
        if (firstInView && viewZ > surface + 0.05f)
        {
            hit->status = XR_ENVIRONMENT_RAYCAST_HIT_STATUS_RAY_OCCLUDED_META; // the origin is behind a surface
            return XR_SUCCESS;
        }
        // Crossed the surface between previousT and t: refine by bisection.
        float a = previousT, b = t;
        for (int k = 0; k < 8; ++k)
        {
            const float m = 0.5f * (a + b);
            int mi = 0, mj = 0;
            float mz = 0.0f;
            if (view.Project(origin + direction * m, mi, mj, mz))
            {
                const float ms = view.LinearDepth(mi, mj);
                if (!std::isnan(ms) && mz >= ms) b = m; else a = m;
            }
            else a = m;
        }
        const glm::vec3 point = origin + direction * b;
        view.Project(point, i, j, viewZ);
        glm::vec3 normal;
        const bool hasNormal = view.Normal(i, j, -direction, normal);
        const glm::quat worldRotation = hasNormal ? HitOrientation(normal, -direction) : glm::quat(1, 0, 0, 0);
        XrPosef worldHit = {ToXr(worldRotation), ToXr(point)};
        const XrSpaceLocation inBase = base->LocateWorldPose(worldHit);
        hit->pose = inBase.pose;
        hit->status = hasNormal ? XR_ENVIRONMENT_RAYCAST_HIT_STATUS_HIT_META
                                : XR_ENVIRONMENT_RAYCAST_HIT_STATUS_HIT_INVALID_ORIENTATION_META;
        if (count <= 3 || count % 500 == 0)
        {
            spdlog::info("EnvironmentRaycast: raycast #{} hit at {:.2f} m (depth #{})", count, b, view.info.frameId);
        }
        return XR_SUCCESS;
    }
    if (!everInView || !lastInView) hit->status = XR_ENVIRONMENT_RAYCAST_HIT_STATUS_HIT_POINT_OUTSIDE_OF_FOV_META;
    return XR_SUCCESS;
}
} // namespace

namespace oxrsys::environment_raycast
{

std::vector<std::pair<const char*, uint32_t>> Extensions()
{
    return {
        {XR_EXT_FUTURE_EXTENSION_NAME, XR_EXT_future_SPEC_VERSION},
        {XR_META_ENVIRONMENT_RAYCAST_EXTENSION_NAME, XR_META_environment_raycast_SPEC_VERSION},
    };
}

const char* RaycastExtensionName()
{
    return XR_META_ENVIRONMENT_RAYCAST_EXTENSION_NAME;
}

const char* ExtensionForFunction(const char* name)
{
    if (name == nullptr) return nullptr;
    if (std::strcmp(name, "xrPollFutureEXT") == 0 || std::strcmp(name, "xrCancelFutureEXT") == 0)
        return XR_EXT_FUTURE_EXTENSION_NAME;
    if (std::strcmp(name, "xrCreateEnvironmentRaycasterAsyncMETA") == 0 ||
        std::strcmp(name, "xrCreateEnvironmentRaycasterCompleteMETA") == 0 ||
        std::strcmp(name, "xrDestroyEnvironmentRaycasterMETA") == 0 ||
        std::strcmp(name, "xrPerformEnvironmentRaycastMETA") == 0)
        return XR_META_ENVIRONMENT_RAYCAST_EXTENSION_NAME;
    return nullptr;
}

bool GetProcAddr(const char* name, PFN_xrVoidFunction* function)
{
#define RAYCAST_DISPATCH(fn, impl)                              \
    if (std::strcmp(name, #fn) == 0)                            \
    {                                                           \
        *function = reinterpret_cast<PFN_xrVoidFunction>(impl); \
        return true;                                            \
    }
    RAYCAST_DISPATCH(xrPollFutureEXT, OxrPollFutureEXT)
    RAYCAST_DISPATCH(xrCancelFutureEXT, OxrCancelFutureEXT)
    RAYCAST_DISPATCH(xrCreateEnvironmentRaycasterAsyncMETA, OxrCreateEnvironmentRaycasterAsyncMETA)
    RAYCAST_DISPATCH(xrCreateEnvironmentRaycasterCompleteMETA, OxrCreateEnvironmentRaycasterCompleteMETA)
    RAYCAST_DISPATCH(xrDestroyEnvironmentRaycasterMETA, OxrDestroyEnvironmentRaycasterMETA)
    RAYCAST_DISPATCH(xrPerformEnvironmentRaycastMETA, OxrPerformEnvironmentRaycastMETA)
#undef RAYCAST_DISPATCH
    return false;
}

bool FillSystemProperties(XrBaseOutStructure* next, bool extensionEnabled)
{
    if (next == nullptr || next->type != XR_TYPE_SYSTEM_ENVIRONMENT_RAYCAST_PROPERTIES_META) return false;
    // Supported whenever the app enabled it; ray casts report NO_HIT until the headset relays depth.
    reinterpret_cast<XrSystemEnvironmentRaycastPropertiesMETA*>(next)->supportsEnvironmentRaycast =
        extensionEnabled ? XR_TRUE : XR_FALSE;
    return true;
}

void OnSessionDestroyed()
{
    std::lock_guard<std::mutex> lock(gMutex);
    gFutures.clear();
    gRaycasters.clear();
}

} // namespace oxrsys::environment_raycast
