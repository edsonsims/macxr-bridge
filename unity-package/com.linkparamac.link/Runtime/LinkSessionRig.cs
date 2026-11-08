// SPDX-License-Identifier: MIT
// Link para Mac: anchor point for recorded sessions (LinkSession, in the Editor). Lives on the OVRCameraRig and runs
// after everything else (LateUpdate + onBeforeRender):
//  - recording: notifies the Editor every frame so it can store the head pose;
//  - replaying: moves the TrackingSpace so the head (CenterEyeAnchor) sits at the recorded pose, whatever
//    pose tracking reports (headset still, lying on the desk, or switched off).
#if UNITY_EDITOR
using System;
using UnityEngine;

namespace LinkParaMac
{
    [AddComponentMenu("")]
    [DefaultExecutionOrder(32000)]
    public sealed class LinkSessionRig : MonoBehaviour
    {
        public static Action Late;                 // recording
        public static Func<Pose?> TargetHead;      // replay (null = leave as is)

        OVRCameraRig _rig;
        bool _moved;
        Vector3 _origPos;
        Quaternion _origRot;

        void OnEnable()
        {
            _rig = GetComponent<OVRCameraRig>();
            Application.onBeforeRender += Apply;
        }

        void OnDisable()
        {
            Application.onBeforeRender -= Apply;
            Restore();
        }

        void LateUpdate()
        {
            Apply();
            Late?.Invoke();
        }

        void Apply()
        {
            if (_rig == null || _rig.trackingSpace == null) return;
            var target = TargetHead?.Invoke();
            if (target == null) { Restore(); return; }
            Transform ts = _rig.trackingSpace, eye = _rig.centerEyeAnchor;
            if (!_moved) { _origPos = ts.localPosition; _origRot = ts.localRotation; _moved = true; }
            // head in world = TrackingSpace * local head pose → TrackingSpace = recorded * inverse(local)
            Quaternion rot = target.Value.rotation * Quaternion.Inverse(eye.localRotation);
            ts.SetPositionAndRotation(target.Value.position - rot * eye.localPosition, rot);
        }

        void Restore()
        {
            if (!_moved || _rig == null || _rig.trackingSpace == null) return;
            _rig.trackingSpace.localPosition = _origPos;
            _rig.trackingSpace.localRotation = _origRot;
            _moved = false;
        }
    }
}
#endif
