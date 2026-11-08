// SPDX-License-Identifier: MIT
// Link para Mac: ponto de apoio das sessões gravadas (LinkSession, no Editor). Fica no OVRCameraRig e roda
// depois de tudo (LateUpdate + onBeforeRender):
//  - gravando: avisa o Editor a cada quadro pra guardar a pose da cabeça;
//  - reproduzindo: move o TrackingSpace pra que a cabeça (CenterEyeAnchor) fique na pose gravada, seja
//    qual for a pose que o rastreamento informa (headset parado, na mesa, ou desligado).
#if UNITY_EDITOR
using System;
using UnityEngine;

namespace LinkParaMac
{
    [AddComponentMenu("")]
    [DefaultExecutionOrder(32000)]
    public sealed class LinkSessionRig : MonoBehaviour
    {
        public static Action Late;                 // gravação
        public static Func<Pose?> TargetHead;      // reprodução (null = não mexe)

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
            // cabeça no mundo = TrackingSpace * pose local da cabeça → TrackingSpace = gravada * inversa(local)
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
