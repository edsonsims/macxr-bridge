// SPDX-License-Identifier: MPL-2.0

#include "HandTracker.h"
#include "Session.h"
#include "Runtime.h"
#include "InputManager.h"
#include "Space.h"
#include <algorithm>
#include <cmath>
#include <glm/glm.hpp>
#include <glm/gtc/quaternion.hpp>

namespace
{

XrHandJointVelocitiesEXT* FindJointVelocities(XrHandJointLocationsEXT* locations)
{
    auto* next = reinterpret_cast<XrBaseOutStructure*>(locations->next);
    while (next != nullptr)
    {
        if (next->type == XR_TYPE_HAND_JOINT_VELOCITIES_EXT)
        {
            return reinterpret_cast<XrHandJointVelocitiesEXT*>(next);
        }
        next = next->next;
    }

    return nullptr;
}

void ClearJointLocations(XrHandJointLocationsEXT* locations)
{
    if (locations == nullptr || locations->jointLocations == nullptr)
    {
        return;
    }

    for (uint32_t i = 0; i < locations->jointCount; ++i)
    {
        locations->jointLocations[i].locationFlags = 0;
        locations->jointLocations[i].pose.orientation = {0.0f, 0.0f, 0.0f, 1.0f};
        locations->jointLocations[i].pose.position = {0.0f, 0.0f, 0.0f};
        locations->jointLocations[i].radius = 0.0f;
    }
}

void ClearJointVelocities(XrHandJointVelocitiesEXT* velocities)
{
    if (velocities == nullptr || velocities->jointVelocities == nullptr)
    {
        return;
    }

    for (uint32_t i = 0; i < velocities->jointCount; ++i)
    {
        velocities->jointVelocities[i].velocityFlags = 0;
        velocities->jointVelocities[i].linearVelocity = {0.0f, 0.0f, 0.0f};
        velocities->jointVelocities[i].angularVelocity = {0.0f, 0.0f, 0.0f};
    }
}

} // namespace

HandTracker::HandTracker(Session* session, XrHandEXT hand)
    : session_(session), hand_(hand)
{
    Runtime::Get().RegisterHandle(handle_, this);
}

HandTracker::~HandTracker()
{
    Runtime::Get().RemoveHandle(handle_);
}

XrResult HandTracker::LocateHandJoints(XrSpace baseSpace, XrTime /*time*/,
                                        XrHandJointLocationsEXT* locations)
{
    if (locations == nullptr)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }

    XrHandJointVelocitiesEXT* velocities = FindJointVelocities(locations);
    if (locations->jointCount != XR_HAND_JOINT_COUNT_EXT || locations->jointLocations == nullptr)
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }
    if (velocities != nullptr &&
        (velocities->jointCount != XR_HAND_JOINT_COUNT_EXT || velocities->jointVelocities == nullptr))
    {
        return XR_ERROR_VALIDATION_FAILURE;
    }

    const InputManager& inputManager = session_->GetInputManager();

    InputManager::Hand hand = (hand_ == XR_HAND_LEFT_EXT)
                                  ? InputManager::Hand::Left
                                  : InputManager::Hand::Right;

    if (!inputManager.IsHandTrackingActive(hand))
    {
        locations->isActive = XR_FALSE;
        ClearJointLocations(locations);
        ClearJointVelocities(velocities);
        return XR_SUCCESS;
    }

    locations->isActive = XR_TRUE;
    inputManager.GetHandJointLocations(hand, locations->jointLocations, locations->jointCount);
    ClearJointVelocities(velocities);
    // Meta aim/scale structs are computed in the runtime's world frame (same as the head pose)...
    XrHandTrackingAimStateFB* aim = FillMetaHandExtensions(locations, inputManager.GetHeadPose(), hand_ == XR_HAND_RIGHT_EXT);

    // ...then everything is expressed in the app's base space (OpenXR requires joint poses relative to
    // baseSpace; the app space is usually LOCAL_FLOOR, offset and yawed from the client's STAGE frame).
    auto* base = Runtime::Get().FromHandle<Space>(reinterpret_cast<uint64_t>(baseSpace));
    if (base != nullptr && base->GetSession() == session_)
    {
        for (uint32_t i = 0; i < locations->jointCount; ++i)
        {
            if (locations->jointLocations[i].locationFlags != 0)
            {
                locations->jointLocations[i].pose = base->LocateWorldPose(locations->jointLocations[i].pose).pose;
            }
        }
        if (aim != nullptr && (aim->status & XR_HAND_TRACKING_AIM_VALID_BIT_FB) != 0)
        {
            aim->aimPose = base->LocateWorldPose(aim->aimPose).pose;
        }
    }

    return XR_SUCCESS;
}

// MacXR Bridge: fill the Meta (FB) structs that OVRPlugin chains to xrLocateHandJointsEXT.
// - XrHandTrackingScaleFB (from XR_FB_hand_tracking_mesh): hand scale 1.0 (ISDK needs > 0).
// - XrHandTrackingAimStateFB (XR_FB_hand_tracking_aim): pinch strengths from thumb-to-fingertip distance
//   and an aim pose that points from the shoulder through the palm (Meta's "pointer pose").
XrHandTrackingAimStateFB* HandTracker::FillMetaHandExtensions(XrHandJointLocationsEXT* locations, const XrPosef& headPose, bool rightHand)
{
    XrHandTrackingAimStateFB* aimOut = nullptr;
    for (auto* next = reinterpret_cast<XrBaseOutStructure*>(locations->next); next != nullptr;
         next = next->next)
    {
#ifdef XR_FB_hand_tracking_mesh
        if (next->type == XR_TYPE_HAND_TRACKING_SCALE_FB)
        {
            auto* scale = reinterpret_cast<XrHandTrackingScaleFB*>(next);
            scale->sensorOutput = 1.0f;
            scale->currentOutput = (scale->overrideHandScale == XR_TRUE && scale->overrideValueInput > 0.0f)
                                       ? scale->overrideValueInput : 1.0f;
            continue;
        }
#endif
#ifdef XR_FB_hand_tracking_aim
        if (next->type == XR_TYPE_HAND_TRACKING_AIM_STATE_FB)
        {
            auto* aim = reinterpret_cast<XrHandTrackingAimStateFB*>(next);
            aimOut = aim;
            aim->status = 0;
            aim->aimPose.orientation = {0.0f, 0.0f, 0.0f, 1.0f};
            aim->aimPose.position = {0.0f, 0.0f, 0.0f};
            aim->pinchStrengthIndex = aim->pinchStrengthMiddle = aim->pinchStrengthRing = aim->pinchStrengthLittle = 0.0f;
            if (locations->isActive != XR_TRUE)
            {
                continue;
            }
            const XrHandJointLocationEXT* j = locations->jointLocations;
            auto pinch = [&](XrHandJointEXT tip) {
                const auto& a = j[XR_HAND_JOINT_THUMB_TIP_EXT].pose.position;
                const auto& b = j[tip].pose.position;
                const float d = std::sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y) + (a.z - b.z) * (a.z - b.z));
                // 0 at >= 5 cm apart, 1 at <= 1.5 cm (fingertip radii touching).
                return std::clamp((0.05f - d) / 0.035f, 0.0f, 1.0f);
            };
            aim->pinchStrengthIndex = pinch(XR_HAND_JOINT_INDEX_TIP_EXT);
            aim->pinchStrengthMiddle = pinch(XR_HAND_JOINT_MIDDLE_TIP_EXT);
            aim->pinchStrengthRing = pinch(XR_HAND_JOINT_RING_TIP_EXT);
            aim->pinchStrengthLittle = pinch(XR_HAND_JOINT_LITTLE_TIP_EXT);

            // Aim ray: from an approximate shoulder (head, offset down and to the side) through the palm.
            const glm::vec3 headPos(headPose.position.x, headPose.position.y, headPose.position.z);
            const glm::quat headRot(headPose.orientation.w, headPose.orientation.x, headPose.orientation.y, headPose.orientation.z);
            const glm::vec3 shoulder = headPos + headRot * glm::vec3(rightHand ? 0.17f : -0.17f, -0.25f, 0.05f);
            const auto& palm = j[XR_HAND_JOINT_PALM_EXT].pose.position;
            const glm::vec3 palmPos(palm.x, palm.y, palm.z);
            glm::vec3 dir = palmPos - shoulder;
            if (glm::length(dir) < 1e-4f)
            {
                continue;
            }
            dir = glm::normalize(dir);
            const glm::quat rot = glm::quatLookAt(dir, glm::vec3(0.0f, 1.0f, 0.0f));  // OpenXR aims along -Z
            aim->aimPose.position = palm;
            aim->aimPose.orientation = {rot.x, rot.y, rot.z, rot.w};
            aim->status = XR_HAND_TRACKING_AIM_COMPUTED_BIT_FB | XR_HAND_TRACKING_AIM_VALID_BIT_FB;
            if (aim->pinchStrengthIndex > 0.9f) aim->status |= XR_HAND_TRACKING_AIM_INDEX_PINCHING_BIT_FB;
            if (aim->pinchStrengthMiddle > 0.9f) aim->status |= XR_HAND_TRACKING_AIM_MIDDLE_PINCHING_BIT_FB;
            if (aim->pinchStrengthRing > 0.9f) aim->status |= XR_HAND_TRACKING_AIM_RING_PINCHING_BIT_FB;
            if (aim->pinchStrengthLittle > 0.9f) aim->status |= XR_HAND_TRACKING_AIM_LITTLE_PINCHING_BIT_FB;
            if (rightHand) aim->status |= XR_HAND_TRACKING_AIM_DOMINANT_HAND_BIT_FB;
            continue;
        }
#endif
    }
    return aimOut;
}
