// SPDX-License-Identifier: MPL-2.0

#include "SpatialPermission.h"

#include <jni.h>

#include <atomic>

namespace
{
std::atomic<bool> gSceneGranted{false};
}

namespace oxr::spatial_permission
{
void SetSceneGranted(bool granted)
{
    gSceneGranted.store(granted);
}

bool IsSceneGranted()
{
    return gSceneGranted.load();
}
} // namespace oxr::spatial_permission

extern "C" JNIEXPORT void JNICALL
Java_com_oculus_NativeActivity_nativeSetScenePermission(JNIEnv*, jclass, jboolean granted)
{
    oxr::spatial_permission::SetSceneGranted(granted == JNI_TRUE);
}
