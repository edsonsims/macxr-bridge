// SPDX-License-Identifier: MPL-2.0
//
// MacXR Bridge: XR_META_environment_raycast (+ the XR_EXT_future plumbing it needs), answered on the Mac
// from the environment depth the headset relays over the camera channel (CameraRelay). This is what MRUK's
// EnvironmentRaycastManager uses on device, so apps ray-cast against the real room in Play Mode unchanged.

#pragma once

#include <openxr/openxr.h>

#include <cstdint>
#include <utility>
#include <vector>

namespace oxrsys::environment_raycast
{

// Extensions (name, spec version) this module implements, to be advertised by the instance.
std::vector<std::pair<const char*, uint32_t>> Extensions();

// Extension owning a function name, or nullptr when it is not one of ours.
const char* ExtensionForFunction(const char* functionName);

// Resolves one of our functions; true when the name matched.
bool GetProcAddr(const char* name, PFN_xrVoidFunction* function);

// Fills XrSystemEnvironmentRaycastPropertiesMETA when it appears in an xrGetSystemProperties chain.
// Returns true when the structure was ours.
bool FillSystemProperties(XrBaseOutStructure* next, bool extensionEnabled);

// Name of the raycast extension (for IsExtensionEnabled checks).
const char* RaycastExtensionName();

// Called when the session goes away: raycasters and futures die with it.
void OnSessionDestroyed();

} // namespace oxrsys::environment_raycast
