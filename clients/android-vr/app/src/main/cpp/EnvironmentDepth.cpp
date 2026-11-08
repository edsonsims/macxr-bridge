// SPDX-License-Identifier: MPL-2.0

#include "EnvironmentDepth.h"

#include "CameraSender.h"

#include <EGL/egl.h>
#include <android/log.h>
#include <jni.h>
#include <openxr/openxr_platform.h>

#include <cstring>

#define LOG_TAG "OXRSys-Depth"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)

namespace cam = oxr::protocol::camera;

namespace
{
constexpr auto kSendInterval = std::chrono::milliseconds(200); // 5 Hz is plenty for ray casts
constexpr auto kProviderRetry = std::chrono::seconds(5);

// Full-screen triangle; each fragment fetches one depth texel of layer uLayer and packs d*65535 into RG.
const char* kVertexShader = R"(#version 300 es
void main()
{
    vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}
)";

const char* kFragmentShader = R"(#version 300 es
precision highp float;
precision highp int;
precision highp sampler2DArray;
uniform sampler2DArray uDepth;
uniform int uLayer;
out vec4 outColor;
void main()
{
    float d = texelFetch(uDepth, ivec3(int(gl_FragCoord.x), int(gl_FragCoord.y), uLayer), 0).r;
    uint v = uint(clamp(d, 0.0, 1.0) * 65535.0 + 0.5);
    outColor = vec4(float(v & 255u) / 255.0, float(v >> 8u) / 255.0, 0.0, 1.0);
}
)";

GLuint Compile(GLenum type, const char* source)
{
    GLuint shader = glCreateShader(type);
    glShaderSource(shader, 1, &source, nullptr);
    glCompileShader(shader);
    GLint ok = GL_FALSE;
    glGetShaderiv(shader, GL_COMPILE_STATUS, &ok);
    if (!ok)
    {
        char log[512] = {};
        glGetShaderInfoLog(shader, sizeof(log), nullptr, log);
        LOGW("Depth shader compile failed: %s", log);
        glDeleteShader(shader);
        return 0;
    }
    return shader;
}

cam::Pose ToWire(const XrPosef& p)
{
    cam::Pose out = {};
    out.orientation = {p.orientation.x, p.orientation.y, p.orientation.z, p.orientation.w};
    out.position = {p.position.x, p.position.y, p.position.z};
    return out;
}
} // namespace

const char* EnvironmentDepth::ExtensionName()
{
    return XR_META_ENVIRONMENT_DEPTH_EXTENSION_NAME;
}

EnvironmentDepth::~EnvironmentDepth()
{
    {
        std::lock_guard<std::mutex> lock(pendingMutex_);
        stopSender_ = true;
    }
    pendingCv_.notify_all();
    if (sender_.joinable()) sender_.join();
}

void EnvironmentDepth::Initialize(XrInstance instance, XrSession session, bool extensionEnabled)
{
    instance_ = instance;
    session_ = session;
    extensionEnabled_ = extensionEnabled;
    if (!extensionEnabled_)
    {
        LOGI("XR_META_environment_depth not enabled; depth relay off");
        return;
    }
    auto load = [this](const char* name, auto& fn) {
        xrGetInstanceProcAddr(instance_, name, reinterpret_cast<PFN_xrVoidFunction*>(&fn));
        return fn != nullptr;
    };
    bool ok = load("xrCreateEnvironmentDepthProviderMETA", pfnCreateProvider_) &&
              load("xrDestroyEnvironmentDepthProviderMETA", pfnDestroyProvider_) &&
              load("xrStartEnvironmentDepthProviderMETA", pfnStartProvider_) &&
              load("xrStopEnvironmentDepthProviderMETA", pfnStopProvider_) &&
              load("xrCreateEnvironmentDepthSwapchainMETA", pfnCreateSwapchain_) &&
              load("xrDestroyEnvironmentDepthSwapchainMETA", pfnDestroySwapchain_) &&
              load("xrEnumerateEnvironmentDepthSwapchainImagesMETA", pfnEnumerateImages_) &&
              load("xrGetEnvironmentDepthSwapchainStateMETA", pfnGetSwapchainState_) &&
              load("xrAcquireEnvironmentDepthImageMETA", pfnAcquireImage_);
    load("xrSetEnvironmentDepthHandRemovalMETA", pfnSetHandRemoval_); // optional
    if (!ok)
    {
        LOGW("XR_META_environment_depth functions missing; depth relay off");
        failed_ = true;
        return;
    }
    if (!sender_.joinable())
    {
        stopSender_ = false;
        sender_ = std::thread(&EnvironmentDepth::SenderThread, this);
    }
    LOGI("Environment depth relay ready (provider starts when the camera channel attaches)");
}

bool EnvironmentDepth::CreateProvider()
{
    XrEnvironmentDepthProviderCreateInfoMETA providerInfo = {XR_TYPE_ENVIRONMENT_DEPTH_PROVIDER_CREATE_INFO_META};
    XrResult result = pfnCreateProvider_(session_, &providerInfo, &provider_);
    if (XR_FAILED(result))
    {
        LOGW("xrCreateEnvironmentDepthProviderMETA failed: %d", result);
        provider_ = XR_NULL_HANDLE;
        if (result == XR_ERROR_FEATURE_UNSUPPORTED || result == XR_ERROR_FUNCTION_UNSUPPORTED) failed_ = true;
        return false;
    }
    XrEnvironmentDepthSwapchainCreateInfoMETA swapchainInfo = {XR_TYPE_ENVIRONMENT_DEPTH_SWAPCHAIN_CREATE_INFO_META};
    result = pfnCreateSwapchain_(provider_, &swapchainInfo, &swapchain_);
    if (XR_FAILED(result))
    {
        LOGW("xrCreateEnvironmentDepthSwapchainMETA failed: %d", result);
        DestroyProvider();
        return false;
    }
    XrEnvironmentDepthSwapchainStateMETA state = {XR_TYPE_ENVIRONMENT_DEPTH_SWAPCHAIN_STATE_META};
    pfnGetSwapchainState_(swapchain_, &state);
    uint32_t count = 0;
    pfnEnumerateImages_(swapchain_, 0, &count, nullptr);
    std::vector<XrSwapchainImageOpenGLESKHR> images(count, {XR_TYPE_SWAPCHAIN_IMAGE_OPENGL_ES_KHR});
    result = pfnEnumerateImages_(swapchain_, count, &count,
                                 reinterpret_cast<XrSwapchainImageBaseHeader*>(images.data()));
    if (XR_FAILED(result) || count == 0 || state.width == 0 || state.height == 0)
    {
        LOGW("Depth swapchain images unavailable (%d, %u images, %ux%u)", result, count, state.width, state.height);
        DestroyProvider();
        return false;
    }
    depthTextures_.clear();
    for (const auto& image : images) depthTextures_.push_back(image.image);
    if (!CreateGlResources(state.width, state.height))
    {
        DestroyProvider();
        failed_ = true;
        return false;
    }
    if (pfnSetHandRemoval_ != nullptr)
    {
        XrEnvironmentDepthHandRemovalSetInfoMETA handRemoval = {XR_TYPE_ENVIRONMENT_DEPTH_HAND_REMOVAL_SET_INFO_META};
        handRemoval.enabled = XR_TRUE; // hands in front of the camera should not become "walls"
        pfnSetHandRemoval_(provider_, &handRemoval);
    }
    LOGI("Environment depth provider created: %ux%u, %u swapchain images", state.width, state.height, count);
    return true;
}

void EnvironmentDepth::DestroyProvider()
{
    if (provider_ != XR_NULL_HANDLE && running_) pfnStopProvider_(provider_);
    running_ = false;
    if (swapchain_ != XR_NULL_HANDLE) pfnDestroySwapchain_(swapchain_);
    swapchain_ = XR_NULL_HANDLE;
    if (provider_ != XR_NULL_HANDLE) pfnDestroyProvider_(provider_);
    provider_ = XR_NULL_HANDLE;
    depthTextures_.clear();
}

bool EnvironmentDepth::CreateGlResources(uint32_t width, uint32_t height)
{
    DestroyGlResources();
    width_ = width;
    height_ = height;
    GLuint vs = Compile(GL_VERTEX_SHADER, kVertexShader);
    GLuint fs = Compile(GL_FRAGMENT_SHADER, kFragmentShader);
    if (vs == 0 || fs == 0)
    {
        if (vs) glDeleteShader(vs);
        if (fs) glDeleteShader(fs);
        return false;
    }
    program_ = glCreateProgram();
    glAttachShader(program_, vs);
    glAttachShader(program_, fs);
    glLinkProgram(program_);
    glDeleteShader(vs);
    glDeleteShader(fs);
    GLint linked = GL_FALSE;
    glGetProgramiv(program_, GL_LINK_STATUS, &linked);
    if (!linked)
    {
        LOGW("Depth program link failed");
        glDeleteProgram(program_);
        program_ = 0;
        return false;
    }
    samplerUniform_ = glGetUniformLocation(program_, "uDepth");
    layerUniform_ = glGetUniformLocation(program_, "uLayer");

    GLint prevTexture = 0, prevFbo = 0;
    glGetIntegerv(GL_TEXTURE_BINDING_2D, &prevTexture);
    glGetIntegerv(GL_FRAMEBUFFER_BINDING, &prevFbo);
    glGenTextures(1, &colorTexture_);
    glBindTexture(GL_TEXTURE_2D, colorTexture_);
    glTexStorage2D(GL_TEXTURE_2D, 1, GL_RGBA8, static_cast<GLsizei>(width), static_cast<GLsizei>(height));
    glGenFramebuffers(1, &fbo_);
    glBindFramebuffer(GL_FRAMEBUFFER, fbo_);
    glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, colorTexture_, 0);
    const GLenum status = glCheckFramebufferStatus(GL_FRAMEBUFFER);
    glBindFramebuffer(GL_FRAMEBUFFER, static_cast<GLuint>(prevFbo));
    glBindTexture(GL_TEXTURE_2D, static_cast<GLuint>(prevTexture));
    if (status != GL_FRAMEBUFFER_COMPLETE)
    {
        LOGW("Depth readback framebuffer incomplete: 0x%x", status);
        DestroyGlResources();
        return false;
    }
    rgba_.resize(static_cast<size_t>(width) * height * 4);
    return true;
}

void EnvironmentDepth::DestroyGlResources()
{
    if (fbo_ != 0) glDeleteFramebuffers(1, &fbo_);
    if (colorTexture_ != 0) glDeleteTextures(1, &colorTexture_);
    if (program_ != 0) glDeleteProgram(program_);
    fbo_ = 0;
    colorTexture_ = 0;
    program_ = 0;
}

bool EnvironmentDepth::ReadBack(GLuint depthTexture, std::vector<uint16_t>& out)
{
    // Save the state this pass touches; the frame's own rendering continues right after.
    GLint prevDrawFbo = 0, prevReadFbo = 0, prevProgram = 0, prevVao = 0, prevActiveTexture = 0;
    GLint prevTextureArray = 0, prevPackBuffer = 0, prevPackAlignment = 4;
    GLint prevViewport[4] = {};
    GLboolean prevColorMask[4] = {GL_TRUE, GL_TRUE, GL_TRUE, GL_TRUE};
    glGetIntegerv(GL_DRAW_FRAMEBUFFER_BINDING, &prevDrawFbo);
    glGetIntegerv(GL_READ_FRAMEBUFFER_BINDING, &prevReadFbo);
    glGetIntegerv(GL_CURRENT_PROGRAM, &prevProgram);
    glGetIntegerv(GL_VERTEX_ARRAY_BINDING, &prevVao);
    glGetIntegerv(GL_ACTIVE_TEXTURE, &prevActiveTexture);
    glGetIntegerv(GL_PIXEL_PACK_BUFFER_BINDING, &prevPackBuffer);
    glGetIntegerv(GL_PACK_ALIGNMENT, &prevPackAlignment);
    glGetIntegerv(GL_VIEWPORT, prevViewport);
    glGetBooleanv(GL_COLOR_WRITEMASK, prevColorMask);
    const GLboolean depthTest = glIsEnabled(GL_DEPTH_TEST);
    const GLboolean blend = glIsEnabled(GL_BLEND);
    const GLboolean scissor = glIsEnabled(GL_SCISSOR_TEST);
    const GLboolean cull = glIsEnabled(GL_CULL_FACE);
    const GLboolean stencil = glIsEnabled(GL_STENCIL_TEST);
    glActiveTexture(GL_TEXTURE0);
    glGetIntegerv(GL_TEXTURE_BINDING_2D_ARRAY, &prevTextureArray);

    glBindFramebuffer(GL_FRAMEBUFFER, fbo_);
    glViewport(0, 0, static_cast<GLsizei>(width_), static_cast<GLsizei>(height_));
    glDisable(GL_DEPTH_TEST);
    glDisable(GL_BLEND);
    glDisable(GL_SCISSOR_TEST);
    glDisable(GL_CULL_FACE);
    glDisable(GL_STENCIL_TEST);
    glColorMask(GL_TRUE, GL_TRUE, GL_TRUE, GL_TRUE);
    glUseProgram(program_);
    glBindVertexArray(0);
    glBindTexture(GL_TEXTURE_2D_ARRAY, depthTexture);
    glTexParameteri(GL_TEXTURE_2D_ARRAY, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
    glTexParameteri(GL_TEXTURE_2D_ARRAY, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
    glTexParameteri(GL_TEXTURE_2D_ARRAY, GL_TEXTURE_COMPARE_MODE, GL_NONE);
    glUniform1i(samplerUniform_, 0);
    glUniform1i(layerUniform_, 0); // left eye
    glDrawArrays(GL_TRIANGLES, 0, 3);
    glBindBuffer(GL_PIXEL_PACK_BUFFER, 0);
    glPixelStorei(GL_PACK_ALIGNMENT, 4);
    glReadPixels(0, 0, static_cast<GLsizei>(width_), static_cast<GLsizei>(height_), GL_RGBA, GL_UNSIGNED_BYTE, rgba_.data());
    const GLenum error = glGetError();

    glBindTexture(GL_TEXTURE_2D_ARRAY, static_cast<GLuint>(prevTextureArray));
    glActiveTexture(static_cast<GLenum>(prevActiveTexture));
    glBindVertexArray(static_cast<GLuint>(prevVao));
    glUseProgram(static_cast<GLuint>(prevProgram));
    glColorMask(prevColorMask[0], prevColorMask[1], prevColorMask[2], prevColorMask[3]);
    if (depthTest) glEnable(GL_DEPTH_TEST);
    if (blend) glEnable(GL_BLEND);
    if (scissor) glEnable(GL_SCISSOR_TEST);
    if (cull) glEnable(GL_CULL_FACE);
    if (stencil) glEnable(GL_STENCIL_TEST);
    glViewport(prevViewport[0], prevViewport[1], prevViewport[2], prevViewport[3]);
    glPixelStorei(GL_PACK_ALIGNMENT, prevPackAlignment);
    glBindBuffer(GL_PIXEL_PACK_BUFFER, static_cast<GLuint>(prevPackBuffer));
    glBindFramebuffer(GL_DRAW_FRAMEBUFFER, static_cast<GLuint>(prevDrawFbo));
    glBindFramebuffer(GL_READ_FRAMEBUFFER, static_cast<GLuint>(prevReadFbo));

    if (error != GL_NO_ERROR)
    {
        LOGW("Depth readback GL error 0x%x", error);
        return false;
    }
    const size_t count = static_cast<size_t>(width_) * height_;
    out.resize(count);
    for (size_t i = 0; i < count; ++i)
    {
        out[i] = static_cast<uint16_t>(rgba_[i * 4] | (rgba_[i * 4 + 1] << 8));
    }
    return true;
}

void EnvironmentDepth::Update(XrSpace space, XrTime displayTime, const XrPosef& headPose, bool scenePermissionGranted)
{
    if (!extensionEnabled_ || failed_ || session_ == XR_NULL_HANDLE) return;

    const bool wanted = CameraSender::Get().IsAttached() && scenePermissionGranted;
    if (!wanted)
    {
        if (running_)
        {
            pfnStopProvider_(provider_);
            running_ = false;
            LOGI("Environment depth provider stopped (camera channel detached)");
        }
        return;
    }

    const auto now = std::chrono::steady_clock::now();
    if (provider_ == XR_NULL_HANDLE)
    {
        if (now < nextProviderAttempt_) return;
        if (!CreateProvider())
        {
            nextProviderAttempt_ = now + kProviderRetry;
            return;
        }
    }
    if (!running_)
    {
        const XrResult result = pfnStartProvider_(provider_);
        if (XR_FAILED(result))
        {
            LOGW("xrStartEnvironmentDepthProviderMETA failed: %d", result);
            DestroyProvider();
            nextProviderAttempt_ = now + kProviderRetry;
            return;
        }
        running_ = true;
        LOGI("Environment depth provider started");
    }

    if (now - lastSend_ < kSendInterval) return;
    {
        std::lock_guard<std::mutex> lock(pendingMutex_);
        if (hasPending_) return; // the sender is still busy with the previous image
    }

    XrEnvironmentDepthImageAcquireInfoMETA acquireInfo = {XR_TYPE_ENVIRONMENT_DEPTH_IMAGE_ACQUIRE_INFO_META};
    acquireInfo.space = space;
    acquireInfo.displayTime = displayTime;
    XrEnvironmentDepthImageMETA image = {XR_TYPE_ENVIRONMENT_DEPTH_IMAGE_META};
    image.views[0].type = XR_TYPE_ENVIRONMENT_DEPTH_IMAGE_VIEW_META;
    image.views[1].type = XR_TYPE_ENVIRONMENT_DEPTH_IMAGE_VIEW_META;
    const XrResult result = pfnAcquireImage_(provider_, &acquireInfo, &image);
    if (result != XR_SUCCESS) // XR_ENVIRONMENT_DEPTH_NOT_AVAILABLE_META while warming up
    {
        if (++acquireFailures_ <= 3 || acquireFailures_ % 300 == 0)
        {
            LOGI("Environment depth not available yet (%d, x%u)", result, acquireFailures_);
        }
        return;
    }
    if (image.swapchainIndex >= depthTextures_.size()) return;
    lastSend_ = now;

    std::vector<uint16_t> pixels;
    if (!ReadBack(depthTextures_[image.swapchainIndex], pixels)) return;

    cam::DepthHeader header = {};
    header.width = width_;
    header.height = height_;
    header.payloadSize = static_cast<uint32_t>(pixels.size() * sizeof(uint16_t));
    header.eye = 0;
    header.nearZ = image.nearZ;
    header.farZ = image.farZ;
    header.displayTimeNs = displayTime;
    header.headPose = ToWire(headPose);
    header.viewPose = ToWire(image.views[0].pose);
    header.fov = {image.views[0].fov.angleLeft, image.views[0].fov.angleRight,
                  image.views[0].fov.angleUp, image.views[0].fov.angleDown};
    {
        std::lock_guard<std::mutex> lock(pendingMutex_);
        pendingHeader_ = header;
        pendingPixels_.swap(pixels);
        hasPending_ = true;
    }
    pendingCv_.notify_one();
    if (++framesSent_ <= 3 || framesSent_ % 300 == 0)
    {
        LOGI("Depth #%u %ux%u near=%.3f far=%.3f fovL=%.3f fovR=%.3f fovU=%.3f fovD=%.3f",
             framesSent_, width_, height_, image.nearZ, image.farZ,
             header.fov.angleLeft, header.fov.angleRight, header.fov.angleUp, header.fov.angleDown);
    }
}

void EnvironmentDepth::SenderThread()
{
    std::vector<uint16_t> pixels;
    cam::DepthHeader header = {};
    for (;;)
    {
        {
            std::unique_lock<std::mutex> lock(pendingMutex_);
            pendingCv_.wait(lock, [this] { return hasPending_ || stopSender_; });
            if (stopSender_) return;
            header = pendingHeader_;
            pixels.swap(pendingPixels_);
        }
        CameraSender::Get().PushDepth(header, pixels.data());
        {
            std::lock_guard<std::mutex> lock(pendingMutex_);
            hasPending_ = false;
        }
    }
}

void EnvironmentDepth::Shutdown()
{
    if (provider_ != XR_NULL_HANDLE) DestroyProvider();
    DestroyGlResources();
    {
        std::lock_guard<std::mutex> lock(pendingMutex_);
        stopSender_ = true;
    }
    pendingCv_.notify_all();
    if (sender_.joinable()) sender_.join();
    session_ = XR_NULL_HANDLE;
}
