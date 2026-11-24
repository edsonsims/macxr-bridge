// SPDX-License-Identifier: MPL-2.0
//
// MacXR Bridge: com.oculus.permission.USE_SCENE is a runtime permission the Java activity
// requests; the result is reported here for the spatial bridge.

#pragma once

namespace oxr::spatial_permission
{
void SetSceneGranted(bool granted);
bool IsSceneGranted();
} // namespace oxr::spatial_permission
