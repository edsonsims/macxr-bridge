// SPDX-License-Identifier: MPL-2.0
//
// MacXR Bridge: XR_META_environment_depth on the headset, relayed to the macOS runtime.
//
// The Quest computes a depth map of the real room (the same one the Depth API / EnvironmentRaycast
// use on device). This class acquires it a few times per second, reads the left eye back to the CPU
// (a small GL pass packs the depth into RGBA8, then glReadPixels) and hands it to a worker thread that
// sends it over the camera channel. On the Mac, the Editor ray-casts against it, so detected objects
// get a real 3D position instead of a fixed distance.

#pragma once

#include "CameraProtocol.h"

#include <GLES3/gl3.h>
#include <openxr/openxr.h>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

class EnvironmentDepth
{
public:
    EnvironmentDepth() = default;
    ~EnvironmentDepth();

    static const char* ExtensionName();

    // Diagnostics: the 5th image after each provider start is also written to <dir>/depth_dump.bin
    // (DepthHeader + pixels), so the conventions can be checked offline with adb pull.
    void SetDumpDirectory(const char* dir) { dumpDir_ = dir != nullptr ? dir : ""; }

    // After the session exists. Resolves the extension functions; the provider is created lazily.
    void Initialize(XrInstance instance, XrSession session, bool extensionEnabled);
    // Before the session is destroyed (GL context current).
    void Shutdown();

    // XR/GL thread, once per frame after xrLocateViews. Rate-limited; does nothing unless the camera
    // channel is attached and the scene permission was granted.
    void Update(XrSpace space, XrTime displayTime, const XrPosef& headPose, bool scenePermissionGranted);

private:
    bool CreateProvider();
    void DestroyProvider();
    bool CreateGlResources(uint32_t width, uint32_t height);
    void DestroyGlResources();
    bool ReadBack(GLuint depthTexture, std::vector<uint16_t>& out);
    void SenderThread();

    bool extensionEnabled_ = false;
    bool failed_ = false; // permanent failure (unsupported): stop trying
    XrInstance instance_ = XR_NULL_HANDLE;
    XrSession session_ = XR_NULL_HANDLE;

    PFN_xrCreateEnvironmentDepthProviderMETA pfnCreateProvider_ = nullptr;
    PFN_xrDestroyEnvironmentDepthProviderMETA pfnDestroyProvider_ = nullptr;
    PFN_xrStartEnvironmentDepthProviderMETA pfnStartProvider_ = nullptr;
    PFN_xrStopEnvironmentDepthProviderMETA pfnStopProvider_ = nullptr;
    PFN_xrCreateEnvironmentDepthSwapchainMETA pfnCreateSwapchain_ = nullptr;
    PFN_xrDestroyEnvironmentDepthSwapchainMETA pfnDestroySwapchain_ = nullptr;
    PFN_xrEnumerateEnvironmentDepthSwapchainImagesMETA pfnEnumerateImages_ = nullptr;
    PFN_xrGetEnvironmentDepthSwapchainStateMETA pfnGetSwapchainState_ = nullptr;
    PFN_xrAcquireEnvironmentDepthImageMETA pfnAcquireImage_ = nullptr;
    PFN_xrSetEnvironmentDepthHandRemovalMETA pfnSetHandRemoval_ = nullptr;

    XrEnvironmentDepthProviderMETA provider_ = XR_NULL_HANDLE;
    XrEnvironmentDepthSwapchainMETA swapchain_ = XR_NULL_HANDLE;
    std::vector<GLuint> depthTextures_;
    uint32_t width_ = 0;
    uint32_t height_ = 0;
    bool running_ = false;
    uint32_t acquireFailures_ = 0;
    uint32_t framesSent_ = 0;
    uint32_t framesSinceStart_ = 0;
    std::string dumpDir_;
    std::chrono::steady_clock::time_point lastSend_{};
    std::chrono::steady_clock::time_point nextProviderAttempt_{};

    GLuint program_ = 0;
    GLint layerUniform_ = -1;
    GLint samplerUniform_ = -1;
    GLuint fbo_ = 0;
    GLuint colorTexture_ = 0;
    std::vector<uint8_t> rgba_;

    // Worker: the newest depth image waits here; older unsent ones are replaced.
    std::thread sender_;
    std::mutex pendingMutex_;
    std::condition_variable pendingCv_;
    bool hasPending_ = false;
    bool stopSender_ = false;
    oxr::protocol::camera::DepthHeader pendingHeader_ = {};
    std::vector<uint16_t> pendingPixels_;
};
