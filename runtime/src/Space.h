// SPDX-License-Identifier: MPL-2.0

#pragma once

#include <openxr/openxr.h>
#include <cstdint>

class Session;

class Space
{
public:
    enum class Type
    {
        Reference,
        Action,
        Remote, // Link para Mac: a space that lives in the headset's runtime (spatial anchor, scene entity)
    };

    // Reference space constructor
    Space(Session* session, Type type, XrReferenceSpaceType refType, const XrPosef& poseInSpace);

    // Action space constructor
    Space(Session* session, XrAction action, XrPath subactionPath, const XrPosef& poseInSpace);

    // Remote space constructor: remoteId is the client's XrSpace handle value, whose pose the
    // client streams in its STAGE space (the runtime's world frame).
    Space(Session* session, uint64_t remoteId);

    ~Space();

    uint64_t GetHandle() const
    {
        return handle_;
    }

    Session* GetSession() const
    {
        return session_;
    }

    Type GetType() const
    {
        return type_;
    }

    XrReferenceSpaceType GetReferenceSpaceType() const
    {
        return referenceSpaceType_;
    }

    const XrPosef& GetPoseInSpace() const
    {
        return poseInSpace_;
    }

    XrPath GetSubactionPath() const
    {
        return subactionPath_;
    }

    XrAction GetAction() const
    {
        return action_;
    }

    uint64_t GetRemoteId() const
    {
        return remoteId_;
    }

    XrResult LocateSpace(Space* baseSpace, XrTime time, XrSpaceLocation* location);
    XrSpaceLocation LocateWorldPose(const XrPosef& pose) const;
    // World-frame pose of a pose expressed in this space (inverse of LocateWorldPose).
    // Returns false when this space has no valid pose (e.g. a remote space not located yet).
    bool ToWorldPose(const XrPosef& poseInSpace, XrPosef* worldPose) const;

private:
    uint64_t handle_ = 0;
    Session* session_;
    Type type_;
    XrReferenceSpaceType referenceSpaceType_ = XR_REFERENCE_SPACE_TYPE_LOCAL;
    XrPosef poseInSpace_;
    XrAction action_ = XR_NULL_HANDLE;
    XrPath subactionPath_ = XR_NULL_PATH;
    uint64_t remoteId_ = 0;
};
