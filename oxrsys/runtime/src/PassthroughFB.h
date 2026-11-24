// SPDX-License-Identifier: MPL-2.0
// MacXR Bridge: XR_FB_passthrough objects. The headset client owns the real passthrough layer;
// the runtime only tracks the app's intent (running/paused, opacity, edge color) and relays it.
#pragma once

#include <openxr/openxr.h>
#include <cstdint>

class Session;

class PassthroughFB
{
public:
    PassthroughFB(Session* session, XrPassthroughFlagsFB flags);
    ~PassthroughFB();

    uint64_t GetHandle() const { return handle_; }
    Session* GetSession() const { return session_; }
    bool IsRunning() const { return running_; }
    void SetRunning(bool running) { running_ = running; }

private:
    Session* session_;
    uint64_t handle_ = 0;
    bool running_ = false;
};

class PassthroughLayerFB
{
public:
    PassthroughLayerFB(Session* session, PassthroughFB* passthrough, XrPassthroughFlagsFB flags,
                       XrPassthroughLayerPurposeFB purpose);
    ~PassthroughLayerFB();

    uint64_t GetHandle() const { return handle_; }
    Session* GetSession() const { return session_; }
    PassthroughFB* GetPassthrough() const { return passthrough_; }
    XrPassthroughLayerPurposeFB GetPurpose() const { return purpose_; }
    bool IsRunning() const { return running_; }
    void SetRunning(bool running) { running_ = running; }
    void SetStyle(const XrPassthroughStyleFB& style);
    float GetOpacity() const { return opacity_; }
    const XrColor4f& GetEdgeColor() const { return edgeColor_; }

    // Visible to the user only when both the parent passthrough and the layer are running.
    bool IsVisible() const { return running_ && passthrough_ != nullptr && passthrough_->IsRunning(); }

private:
    Session* session_;
    PassthroughFB* passthrough_;
    uint64_t handle_ = 0;
    XrPassthroughLayerPurposeFB purpose_;
    bool running_ = false;
    float opacity_ = 1.0f;
    XrColor4f edgeColor_ = {0.0f, 0.0f, 0.0f, 0.0f};
};
