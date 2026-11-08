// SPDX-License-Identifier: MPL-2.0
//
// Link para Mac: small Meta extensions OVRPlugin asks for on Quest, so OVRManager behaves as it does over
// Quest Link: XR_FB_display_refresh_rate (rates of the Quest 3; the current one is the rate negotiated with
// the headset), XR_FB_color_space (Rec.2020 like Quest 3; the app's choice is accepted) and
// XR_META_passthrough_preferences (the system's "passthrough by default" flag; off).

#pragma once

#include <openxr/openxr.h>

#include <utility>
#include <vector>

namespace oxrsys::minor_extensions
{

std::vector<std::pair<const char*, uint32_t>> Extensions();
const char* ExtensionForFunction(const char* functionName);
bool GetProcAddr(const char* name, PFN_xrVoidFunction* function);

// Fills XrSystemColorSpacePropertiesFB; true when the structure was ours.
bool FillSystemProperties(XrBaseOutStructure* next, bool colorSpaceEnabled);

} // namespace oxrsys::minor_extensions
