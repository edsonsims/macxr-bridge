// SPDX-License-Identifier: MPL-2.0
#include "PassthroughFB.h"
#include "Runtime.h"
#include <algorithm>

PassthroughFB::PassthroughFB(Session* session, XrPassthroughFlagsFB flags)
    : session_(session), running_((flags & XR_PASSTHROUGH_IS_RUNNING_AT_CREATION_BIT_FB) != 0)
{
    Runtime::Get().RegisterHandle(handle_, this);
}

PassthroughFB::~PassthroughFB()
{
    Runtime::Get().RemoveHandle(handle_);
}

PassthroughLayerFB::PassthroughLayerFB(Session* session, PassthroughFB* passthrough,
                                       XrPassthroughFlagsFB flags, XrPassthroughLayerPurposeFB purpose)
    : session_(session),
      passthrough_(passthrough),
      purpose_(purpose),
      running_((flags & XR_PASSTHROUGH_IS_RUNNING_AT_CREATION_BIT_FB) != 0)
{
    Runtime::Get().RegisterHandle(handle_, this);
}

PassthroughLayerFB::~PassthroughLayerFB()
{
    Runtime::Get().RemoveHandle(handle_);
}

void PassthroughLayerFB::SetStyle(const XrPassthroughStyleFB& style)
{
    opacity_ = std::clamp(style.textureOpacityFactor, 0.0f, 1.0f);
    edgeColor_ = style.edgeColor;
}
