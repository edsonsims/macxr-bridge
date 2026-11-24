// SPDX-License-Identifier: MPL-2.0

#include "MinorExtensions.h"

#include "Runtime.h"
#include "Session.h"

#include <spdlog/spdlog.h>

#include <cmath>
#include <cstring>
#include <iterator>
#include <mutex>
#include <unordered_map>

namespace
{
// Quest 3 panel rates.
constexpr float kRefreshRates[] = {72.0f, 80.0f, 90.0f, 120.0f};
constexpr XrColorSpaceFB kColorSpaces[] = {XR_COLOR_SPACE_REC2020_FB, XR_COLOR_SPACE_REC709_FB, XR_COLOR_SPACE_RIFT_CV1_FB,
                                           XR_COLOR_SPACE_RIFT_S_FB,  XR_COLOR_SPACE_QUEST_FB,  XR_COLOR_SPACE_P3_FB,
                                           XR_COLOR_SPACE_ADOBE_RGB_FB};

std::mutex gMutex;
std::unordered_map<Session*, float> gRequestedRate;
std::unordered_map<Session*, XrColorSpaceFB> gColorSpace;

Session* SessionFromHandle(XrSession session)
{
    return Runtime::Get().FromHandle<Session>(reinterpret_cast<uint64_t>(session));
}

template <typename T>
XrResult FillList(uint32_t capacity, uint32_t* countOutput, T* out, const T* values, uint32_t count)
{
    if (countOutput == nullptr) return XR_ERROR_VALIDATION_FAILURE;
    *countOutput = count;
    if (capacity == 0) return XR_SUCCESS;
    if (capacity < count || out == nullptr) return XR_ERROR_SIZE_INSUFFICIENT;
    std::memcpy(out, values, sizeof(T) * count);
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL OxrEnumerateDisplayRefreshRatesFB(XrSession session, uint32_t capacity, uint32_t* count,
                                                                 float* rates)
{
    if (SessionFromHandle(session) == nullptr) return XR_ERROR_HANDLE_INVALID;
    return FillList(capacity, count, rates, kRefreshRates, static_cast<uint32_t>(std::size(kRefreshRates)));
}

XRAPI_ATTR XrResult XRAPI_CALL OxrGetDisplayRefreshRateFB(XrSession session, float* rate)
{
    Session* sess = SessionFromHandle(session);
    if (sess == nullptr) return XR_ERROR_HANDLE_INVALID;
    if (rate == nullptr) return XR_ERROR_VALIDATION_FAILURE;
    // The rate the stream actually runs at (negotiated with the headset).
    *rate = static_cast<float>(sess->GetDisplayRefreshRateHz());
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL OxrRequestDisplayRefreshRateFB(XrSession session, float rate)
{
    Session* sess = SessionFromHandle(session);
    if (sess == nullptr) return XR_ERROR_HANDLE_INVALID;
    if (rate != 0.0f)
    {
        bool supported = false;
        for (float r : kRefreshRates) supported = supported || std::fabs(r - rate) < 0.5f;
        if (!supported) return XR_ERROR_DISPLAY_REFRESH_RATE_UNSUPPORTED_FB;
    }
    std::lock_guard<std::mutex> lock(gMutex);
    gRequestedRate[sess] = rate;
    // The headset picks the stream rate when it connects (oxrsys-runtime.toml); the request is recorded,
    // not forwarded, so no XrEventDataDisplayRefreshRateChangedFB is raised.
    spdlog::info("MinorExtensions: app requested {} Hz (stream runs at {} Hz)", rate, sess->GetDisplayRefreshRateHz());
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL OxrEnumerateColorSpacesFB(XrSession session, uint32_t capacity, uint32_t* count,
                                                         XrColorSpaceFB* colorSpaces)
{
    if (SessionFromHandle(session) == nullptr) return XR_ERROR_HANDLE_INVALID;
    return FillList(capacity, count, colorSpaces, kColorSpaces, static_cast<uint32_t>(std::size(kColorSpaces)));
}

XRAPI_ATTR XrResult XRAPI_CALL OxrSetColorSpaceFB(XrSession session, const XrColorSpaceFB colorSpace)
{
    Session* sess = SessionFromHandle(session);
    if (sess == nullptr) return XR_ERROR_HANDLE_INVALID;
    bool supported = false;
    for (XrColorSpaceFB c : kColorSpaces) supported = supported || c == colorSpace;
    if (!supported) return XR_ERROR_COLOR_SPACE_UNSUPPORTED_FB;
    std::lock_guard<std::mutex> lock(gMutex);
    gColorSpace[sess] = colorSpace;
    spdlog::info("MinorExtensions: app color space {}", static_cast<int>(colorSpace));
    return XR_SUCCESS;
}

XRAPI_ATTR XrResult XRAPI_CALL OxrGetPassthroughPreferencesMETA(XrSession session, XrPassthroughPreferencesMETA* preferences)
{
    if (SessionFromHandle(session) == nullptr) return XR_ERROR_HANDLE_INVALID;
    if (preferences == nullptr || preferences->type != XR_TYPE_PASSTHROUGH_PREFERENCES_META) return XR_ERROR_VALIDATION_FAILURE;
    preferences->flags = 0; // "start apps in passthrough" is a headset setting the stream does not carry
    return XR_SUCCESS;
}
} // namespace

namespace oxrsys::minor_extensions
{

std::vector<std::pair<const char*, uint32_t>> Extensions()
{
    return {
        {XR_FB_DISPLAY_REFRESH_RATE_EXTENSION_NAME, XR_FB_display_refresh_rate_SPEC_VERSION},
        {XR_FB_COLOR_SPACE_EXTENSION_NAME, XR_FB_color_space_SPEC_VERSION},
        {XR_META_PASSTHROUGH_PREFERENCES_EXTENSION_NAME, XR_META_passthrough_preferences_SPEC_VERSION},
    };
}

const char* ExtensionForFunction(const char* name)
{
    if (name == nullptr) return nullptr;
    if (std::strcmp(name, "xrEnumerateDisplayRefreshRatesFB") == 0 || std::strcmp(name, "xrGetDisplayRefreshRateFB") == 0 ||
        std::strcmp(name, "xrRequestDisplayRefreshRateFB") == 0)
        return XR_FB_DISPLAY_REFRESH_RATE_EXTENSION_NAME;
    if (std::strcmp(name, "xrEnumerateColorSpacesFB") == 0 || std::strcmp(name, "xrSetColorSpaceFB") == 0)
        return XR_FB_COLOR_SPACE_EXTENSION_NAME;
    if (std::strcmp(name, "xrGetPassthroughPreferencesMETA") == 0) return XR_META_PASSTHROUGH_PREFERENCES_EXTENSION_NAME;
    return nullptr;
}

bool GetProcAddr(const char* name, PFN_xrVoidFunction* function)
{
#define MINOR_DISPATCH(fn, impl)                                \
    if (std::strcmp(name, #fn) == 0)                            \
    {                                                           \
        *function = reinterpret_cast<PFN_xrVoidFunction>(impl); \
        return true;                                            \
    }
    MINOR_DISPATCH(xrEnumerateDisplayRefreshRatesFB, OxrEnumerateDisplayRefreshRatesFB)
    MINOR_DISPATCH(xrGetDisplayRefreshRateFB, OxrGetDisplayRefreshRateFB)
    MINOR_DISPATCH(xrRequestDisplayRefreshRateFB, OxrRequestDisplayRefreshRateFB)
    MINOR_DISPATCH(xrEnumerateColorSpacesFB, OxrEnumerateColorSpacesFB)
    MINOR_DISPATCH(xrSetColorSpaceFB, OxrSetColorSpaceFB)
    MINOR_DISPATCH(xrGetPassthroughPreferencesMETA, OxrGetPassthroughPreferencesMETA)
#undef MINOR_DISPATCH
    return false;
}

bool FillSystemProperties(XrBaseOutStructure* next, bool colorSpaceEnabled)
{
    if (next == nullptr || next->type != XR_TYPE_SYSTEM_COLOR_SPACE_PROPERTIES_FB) return false;
    reinterpret_cast<XrSystemColorSpacePropertiesFB*>(next)->colorSpace =
        colorSpaceEnabled ? XR_COLOR_SPACE_REC2020_FB : XR_COLOR_SPACE_UNMANAGED_FB;
    return true;
}

} // namespace oxrsys::minor_extensions
