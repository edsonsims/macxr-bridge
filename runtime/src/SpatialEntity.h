// SPDX-License-Identifier: MPL-2.0
//
// Link para Mac: XR_FB_spatial_entity (+ storage, query, container), XR_META_spatial_entity_persistence
// and _discovery, XR_FB_scene, XR_FB_scene_capture and XR_META_spatial_entity_mesh, implemented by
// proxying every call to the headset's OpenXR runtime through SpatialProxy.

#pragma once

#include <openxr/openxr.h>

#include <utility>
#include <vector>

class Instance;
class Session;
class Space;

namespace oxrsys::spatial_entity
{

// Extensions (name, spec version) this module implements, to be advertised by the instance.
std::vector<std::pair<const char*, uint32_t>> Extensions();

// Extension owning a function name, or nullptr when it is not one of ours.
const char* ExtensionForFunction(const char* functionName);

// Resolves one of our functions; true when name matched (function may still be null when
// compiled without the extension headers).
bool GetProcAddr(const char* name, PFN_xrVoidFunction* function);

// Converts the headset's forwarded events into OpenXR events on the instance queue. Runs on
// the app thread (from xrPollEvent) so it can create Space objects on the session.
void DrainEvents(Instance* instance, Session* session);

// Called by Session when a space is destroyed by the app (remote spaces tell the headset).
void OnSpaceDestroyed(Space* space);

// Called when the session goes away: drop every mapping.
void OnSessionDestroyed();

} // namespace oxrsys::spatial_entity
