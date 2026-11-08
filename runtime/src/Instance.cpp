// SPDX-License-Identifier: MPL-2.0

#include "Instance.h"
#include "EnvironmentRaycast.h"
#include "MinorExtensions.h"
#include "MicRelay.h"
#include "Config.h"
#include "Runtime.h"
#include <algorithm>
#include <cstring>
#include <spdlog/spdlog.h>

Instance::Instance(XrVersion apiVersion, const std::vector<std::string>& enabledExtensions)
    : apiVersion_(apiVersion),
      enabledExtensions_(enabledExtensions),
      passthroughBlendModeEnabled_([] {
          const ConfigValues values = Config::Get().GetValues();
          return values.passthroughEnabled && values.appAlphaBlendPassthrough;
      }())
{
    Runtime::Get().RegisterHandle(handle_, this);
    Runtime::Get().SetInstance(this);
    MicRelay::Get().OnInstanceCreated(); // Link para Mac: the Quest microphone is the default input while the app runs
    spdlog::info("OXRSys: Instance created");
}

Instance::~Instance()
{
    MicRelay::Get().OnInstanceDestroyed();
    Runtime::Get().SetInstance(nullptr);
    Runtime::Get().RemoveHandle(handle_);
    spdlog::info("OXRSys: Instance destroyed");
}

XrResult Instance::GetSystem(const XrSystemGetInfo* getInfo, XrSystemId* systemId)
{
    if (getInfo == nullptr || systemId == nullptr)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }

    if (getInfo->formFactor != XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY)
    {
        return XR_ERROR_FORM_FACTOR_UNSUPPORTED;
    }

    systemRequested_ = true;
    *systemId = 1;
    return XR_SUCCESS;
}

XrResult Instance::GetSystemProperties(XrSystemId systemId, XrSystemProperties* properties)
{
    if (systemId != 1 || !systemRequested_)
    {
        return XR_ERROR_SYSTEM_INVALID;
    }
    if (properties == nullptr)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }

    properties->type = XR_TYPE_SYSTEM_PROPERTIES;
    properties->systemId = 1;
    properties->vendorId = 0;
    // Link para Mac: OVRPlugin (Meta XR SDK) refuses runtimes whose name lacks "Meta"/"Oculus"
    // ("Non-Oculus OpenXR runtime is not supported") and reads the system name to pick the headset model.
    std::strncpy(properties->systemName, "Meta Quest 3", XR_MAX_SYSTEM_NAME_SIZE);
    properties->graphicsProperties.maxSwapchainImageWidth = 4096;
    properties->graphicsProperties.maxSwapchainImageHeight = 4096;
    properties->graphicsProperties.maxLayerCount = 16;
    properties->trackingProperties.orientationTracking = XR_TRUE;
    properties->trackingProperties.positionTracking = XR_TRUE;

    // Walk the next chain to fill extension structs
    XrBaseOutStructure* next = reinterpret_cast<XrBaseOutStructure*>(properties->next);
    while (next)
    {
        if (next->type == XR_TYPE_SYSTEM_HAND_TRACKING_PROPERTIES_EXT)
        {
            auto* handTrackingProps = reinterpret_cast<XrSystemHandTrackingPropertiesEXT*>(next);
            handTrackingProps->supportsHandTracking =
                IsExtensionEnabled(XR_EXT_HAND_TRACKING_EXTENSION_NAME) ? XR_TRUE : XR_FALSE;
        }
#ifdef XR_FB_passthrough
        else if (next->type == XR_TYPE_SYSTEM_PASSTHROUGH_PROPERTIES_FB)
        {
            auto* props = reinterpret_cast<XrSystemPassthroughPropertiesFB*>(next);
            props->supportsPassthrough = IsExtensionEnabled(XR_FB_PASSTHROUGH_EXTENSION_NAME) ? XR_TRUE : XR_FALSE;
        }
        else if (next->type == XR_TYPE_SYSTEM_PASSTHROUGH_PROPERTIES2_FB)
        {
            auto* props = reinterpret_cast<XrSystemPassthroughProperties2FB*>(next);
            props->capabilities = IsExtensionEnabled(XR_FB_PASSTHROUGH_EXTENSION_NAME)
                ? (XR_PASSTHROUGH_CAPABILITY_BIT_FB | XR_PASSTHROUGH_CAPABILITY_COLOR_BIT_FB)
                : 0;
        }
#endif
        else if (oxrsys::minor_extensions::FillSystemProperties(next, IsExtensionEnabled(XR_FB_COLOR_SPACE_EXTENSION_NAME)))
        {
            // Link para Mac: XrSystemColorSpacePropertiesFB (MinorExtensions.cpp).
        }
        else if (oxrsys::environment_raycast::FillSystemProperties(
                     next, IsExtensionEnabled(oxrsys::environment_raycast::RaycastExtensionName())))
        {
            // Link para Mac: XrSystemEnvironmentRaycastPropertiesMETA (EnvironmentRaycast.cpp).
        }
        else if (next->type == XR_TYPE_SYSTEM_SPATIAL_ENTITY_PROPERTIES_FB)
        {
            // Link para Mac: anchors are proxied to the headset (SpatialEntity.cpp).
            auto* props = reinterpret_cast<XrSystemSpatialEntityPropertiesFB*>(next);
            props->supportsSpatialEntity = IsExtensionEnabled(XR_FB_SPATIAL_ENTITY_EXTENSION_NAME) ? XR_TRUE : XR_FALSE;
        }
#ifdef XR_META_headset_id
        else if (next->type == XR_TYPE_SYSTEM_HEADSET_ID_PROPERTIES_META &&
                 IsExtensionEnabled(XR_META_HEADSET_ID_EXTENSION_NAME))
        {
            // Stable, runtime-specific UUID ("OXRSys headset" v1); real per-device ids can come from the client later.
            static const uint8_t kHeadsetId[XR_UUID_SIZE] = {0x6f, 0x78, 0x72, 0x73, 0x79, 0x73, 0x00, 0x01,
                                                             0x4c, 0x69, 0x6e, 0x6b, 0x4d, 0x61, 0x63, 0x01};
            auto* headsetId = reinterpret_cast<XrSystemHeadsetIdPropertiesMETA*>(next);
            std::memcpy(headsetId->id.data, kHeadsetId, XR_UUID_SIZE);
        }
#endif
        next = next->next;
    }

    return XR_SUCCESS;
}

XrResult Instance::GetInstanceProperties(XrInstanceProperties* properties)
{
    if (properties == nullptr)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }

    properties->type = XR_TYPE_INSTANCE_PROPERTIES;
    properties->runtimeVersion =
        XR_MAKE_VERSION(OXRSYS_VERSION_MAJOR, OXRSYS_VERSION_MINOR, OXRSYS_VERSION_PATCH);
    std::strncpy(properties->runtimeName, "OXRSys Runtime for Meta Quest", XR_MAX_RUNTIME_NAME_SIZE);

    return XR_SUCCESS;
}

XrResult Instance::EnumerateViewConfigurations(XrSystemId systemId,
                                                uint32_t viewConfigurationTypeCapacityInput,
                                                uint32_t* viewConfigurationTypeCountOutput,
                                                XrViewConfigurationType* viewConfigurationTypes)
{
    if (systemId != 1)
    {
        return XR_ERROR_SYSTEM_INVALID;
    }
    if (viewConfigurationTypeCountOutput == nullptr ||
        (viewConfigurationTypeCapacityInput > 0 && viewConfigurationTypes == nullptr))
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }

    *viewConfigurationTypeCountOutput = 1;
    if (viewConfigurationTypeCapacityInput == 0)
    {
        return XR_SUCCESS;
    }
    if (viewConfigurationTypeCapacityInput < 1)
    {
        return XR_ERROR_SIZE_INSUFFICIENT;
    }

    viewConfigurationTypes[0] = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
    return XR_SUCCESS;
}

XrResult Instance::GetViewConfigurationProperties(XrSystemId systemId,
                                                    XrViewConfigurationType viewConfigurationType,
                                                    XrViewConfigurationProperties* configurationProperties)
{
    if (systemId != 1)
    {
        return XR_ERROR_SYSTEM_INVALID;
    }
    if (!IsViewConfigurationTypeSupported(viewConfigurationType))
    {
        return XR_ERROR_VIEW_CONFIGURATION_TYPE_UNSUPPORTED;
    }
    if (configurationProperties == nullptr)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }

    configurationProperties->type = XR_TYPE_VIEW_CONFIGURATION_PROPERTIES;
    configurationProperties->viewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
    configurationProperties->fovMutable = XR_FALSE;
    return XR_SUCCESS;
}

XrResult Instance::EnumerateViewConfigurationViews(XrSystemId systemId,
                                                     XrViewConfigurationType viewConfigurationType,
                                                     uint32_t viewCapacityInput,
                                                     uint32_t* viewCountOutput,
                                                     XrViewConfigurationView* views)
{
    if (systemId != 1)
    {
        return XR_ERROR_SYSTEM_INVALID;
    }
    if (!IsViewConfigurationTypeSupported(viewConfigurationType))
    {
        return XR_ERROR_VIEW_CONFIGURATION_TYPE_UNSUPPORTED;
    }
    if (viewCountOutput == nullptr || (viewCapacityInput > 0 && views == nullptr))
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }

    *viewCountOutput = 2;
    if (viewCapacityInput == 0)
    {
        return XR_SUCCESS;
    }
    if (viewCapacityInput < 2)
    {
        return XR_ERROR_SIZE_INSUFFICIENT;
    }

    uint32_t recommendedWidth = 0;
    uint32_t recommendedHeight = 0;
    RenderBaseEyeResolution(recommendedWidth, recommendedHeight);

    for (uint32_t i = 0; i < 2; i++)
    {
        views[i].type = XR_TYPE_VIEW_CONFIGURATION_VIEW;
        views[i].next = nullptr;
        views[i].recommendedImageRectWidth = recommendedWidth;
        views[i].maxImageRectWidth = 4096;
        views[i].recommendedImageRectHeight = recommendedHeight;
        views[i].maxImageRectHeight = 4096;
        views[i].recommendedSwapchainSampleCount = 1;
        views[i].maxSwapchainSampleCount = 1;
    }
    return XR_SUCCESS;
}

XrResult Instance::EnumerateEnvironmentBlendModes(XrSystemId systemId,
                                                    XrViewConfigurationType viewConfigurationType,
                                                    uint32_t environmentBlendModeCapacityInput,
                                                    uint32_t* environmentBlendModeCountOutput,
                                                    XrEnvironmentBlendMode* environmentBlendModes)
{
    if (systemId != 1)
    {
        return XR_ERROR_SYSTEM_INVALID;
    }
    if (!IsViewConfigurationTypeSupported(viewConfigurationType))
    {
        return XR_ERROR_VIEW_CONFIGURATION_TYPE_UNSUPPORTED;
    }
    if (environmentBlendModeCountOutput == nullptr ||
        (environmentBlendModeCapacityInput > 0 && environmentBlendModes == nullptr))
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }

    const bool alphaBlendEnabled = SupportsPassthroughBlendMode();
    const uint32_t supportedModeCount = alphaBlendEnabled ? 2u : 1u;

    *environmentBlendModeCountOutput = supportedModeCount;
    if (environmentBlendModeCapacityInput == 0)
    {
        return XR_SUCCESS;
    }
    if (environmentBlendModeCapacityInput < supportedModeCount)
    {
        return XR_ERROR_SIZE_INSUFFICIENT;
    }

    environmentBlendModes[0] = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
    if (alphaBlendEnabled)
    {
        environmentBlendModes[1] = XR_ENVIRONMENT_BLEND_MODE_ALPHA_BLEND;
    }
    return XR_SUCCESS;
}

void Instance::PushEvent(const XrEventDataBuffer& event)
{
    std::lock_guard lock(eventMutex_);
    eventQueue_.push_back(event);
}

XrResult Instance::PollEvent(XrEventDataBuffer* eventData)
{
    std::lock_guard lock(eventMutex_);
    if (eventQueue_.empty())
    {
        return XR_EVENT_UNAVAILABLE;
    }

    *eventData = eventQueue_.front();
    eventQueue_.pop_front();
    return XR_SUCCESS;
}

void Instance::RemoveEventsForSession(XrSession session)
{
    std::lock_guard lock(eventMutex_);
    std::erase_if(eventQueue_, [session](const XrEventDataBuffer& event) {
        if (event.type == XR_TYPE_EVENT_DATA_REFERENCE_SPACE_CHANGE_PENDING)
        {
            const auto* change = reinterpret_cast<const XrEventDataReferenceSpaceChangePending*>(&event);
            return change->session == session;
        }
        if (event.type != XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED)
        {
            return false;
        }

        const auto* stateChanged = reinterpret_cast<const XrEventDataSessionStateChanged*>(&event);
        return stateChanged->session == session;
    });
}

bool Instance::IsExtensionEnabled(const char* extensionName) const
{
    for (const auto& ext : enabledExtensions_)
    {
        if (ext == extensionName)
        {
            return true;
        }
    }
    return false;
}

bool Instance::SupportsLocalFloor() const
{
#ifdef XR_EXT_local_floor
    if (IsExtensionEnabled(XR_EXT_LOCAL_FLOOR_EXTENSION_NAME))
    {
        return true;
    }
#endif
    return apiVersion_ >= XR_MAKE_VERSION(1, 1, 0);
}

bool Instance::IsSystemIdValid(XrSystemId systemId) const
{
    return systemRequested_ && systemId == 1;
}

void Instance::MarkMetalGraphicsRequirementsQueried()
{
    metalGraphicsRequirementsQueried_ = true;
}

bool Instance::HasQueriedMetalGraphicsRequirements() const
{
    return metalGraphicsRequirementsQueried_;
}

void Instance::MarkVulkanGraphicsRequirementsQueried()
{
    vulkanGraphicsRequirementsQueried_ = true;
}

bool Instance::HasQueriedVulkanGraphicsRequirements() const
{
    return vulkanGraphicsRequirementsQueried_;
}

bool Instance::IsViewConfigurationTypeSupported(XrViewConfigurationType viewConfigurationType) const
{
    return viewConfigurationType == XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
}

void Instance::SetDebugUtilsObjectName(XrObjectType objectType, uint64_t objectHandle, const char* objectName)
{
    std::lock_guard lock(debugUtilsMutex_);

    DebugUtilsObjectKey key = {objectType, objectHandle};
    if (objectName == nullptr || objectName[0] == '\0')
    {
        debugUtilsObjectNames_.erase(key);
        return;
    }

    debugUtilsObjectNames_[key] = objectName;
}

std::string Instance::GetDebugUtilsObjectName(XrObjectType objectType, uint64_t objectHandle) const
{
    std::lock_guard lock(debugUtilsMutex_);

    DebugUtilsObjectKey key = {objectType, objectHandle};
    auto it = debugUtilsObjectNames_.find(key);
    if (it == debugUtilsObjectNames_.end())
    {
        return {};
    }

    return it->second;
}
