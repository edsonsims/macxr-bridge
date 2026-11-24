// Reference example: the BridgeHooks of the Lumi app (working title "Hello World"), the app MacXR Bridge was built with.
// It uses that app's own types (LabelManager, PassthroughDetectionSource, MockDetectionSource…), so it won't compile
// in another project: read it as a pattern. See docs/connect-your-app.md.
//
// SPDX-License-Identifier: MIT
// Hello World ↔ MacXR Bridge: the part of the bridge that knows the app. The rest of the bridge (com.macxrbridge.xr
// package) is generic and calls these hooks (BridgeHooks):
//  - WireDetection: the real camera arrived → passthrough source in the LabelManager, mock off, fallback raycast;
//  - ApplyAppThreshold: the LabelManager cutoff (minScore);
//  - IgnoreCollider: the fallback raycast doesn't land on labels or on the mock room;
//  - PlacedBatch: each batch of placed labels (session tests, debugging);
//  - DetectNow/HoldDetection/DetectionStarted: the lockstep test controls the detection pace;
//  - PauseAppDetection / AppStatus: diagnostics.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HelloWorld.Labels;
using UnityEditor;
using UnityEngine;

namespace MacXRBridge.EditorTools
{
    [InitializeOnLoad]
    static class HelloWorldBridgeHooks
    {
        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static PassthroughDetectionSource _source;
        static readonly FieldInfo NextDetection = typeof(PassthroughDetectionSource).GetField("_nextDetection", F);

        static HelloWorldBridgeHooks()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += s => { if (s == PlayModeStateChange.ExitingPlayMode) _source = null; };

            BridgeHooks.WireDetection = WireDetection;
            BridgeHooks.UnwireDetection = () =>
            {
                if (PassthroughDetectionSource.FallbackRaycast == (PassthroughDetectionSource.FallbackRaycastDelegate)BridgeRaycast.Raycast)
                    PassthroughDetectionSource.FallbackRaycast = null;
            };
            BridgeHooks.ApplyAppThreshold = thr =>
            {
                // The LabelManager has its own cutoff (minScore, 0.45 in the scene); it's a scene object, so it reverts on its own when leaving Play.
                var labels = UnityEngine.Object.FindAnyObjectByType<LabelManager>(FindObjectsInactive.Include);
                var fMin = labels?.GetType().GetField("minScore", F);
                if (fMin != null && thr < (float)fMin.GetValue(labels)) fMin.SetValue(labels, thr);
            };
            BridgeHooks.IgnoreCollider = c => c.GetComponentInParent<LivingLabel>() != null || c.GetComponentInParent<MockObject>() != null;
            BridgeHooks.PauseAppDetection = pause => { if (Source() != null) _source.enabled = !pause; };
            BridgeHooks.AppStatus = () =>
            {
                var s = Source();
                if (s == null) return "PassthroughDetectionSource: MISSING";
                object Get(string f) => typeof(PassthroughDetectionSource).GetField(f, F)?.GetValue(s);
                return $"Source: active={s.isActiveAndEnabled} driving={Get("_driving")} readySince={Get("_readySince")} hasCapture={Get("_hasCapture")} next={Get("_nextDetection")} t={Time.time:F1}";
            };
            // The source schedules the next detection in _nextDetection (Time.time); 0 = right away, MaxValue = hold.
            BridgeHooks.DetectNow = () => { if (Source() != null) NextDetection?.SetValue(_source, 0f); };
            BridgeHooks.HoldDetection = () => { if (Source() != null) NextDetection?.SetValue(_source, float.MaxValue); };
            BridgeHooks.DetectionStarted = () =>
            {
                if (Source() == null || NextDetection == null) return false;
                float next = (float)NextDetection.GetValue(_source);
                return next > 0f && next < float.MaxValue;
            };
        }

        static PassthroughDetectionSource Source()
        {
            if (_source == null && EditorApplication.isPlaying)
                _source = UnityEngine.Object.FindAnyObjectByType<PassthroughDetectionSource>(FindObjectsInactive.Include);
            return _source;
        }

        static bool _hooked;

        static void Tick()
        {
            if (!EditorApplication.isPlaying) { _hooked = false; return; }
            if (_hooked || Source() == null) return;
            _hooked = true;
            _source.Detected += placed => BridgeHooks.ReportPlacedBatch(placed.Select(p => (p.Label, p.Position)).ToList());
        }

        static string WireDetection()
        {
            var source = Source();
            var mock = UnityEngine.Object.FindAnyObjectByType<MockDetectionSource>(FindObjectsInactive.Include);
            var labels = UnityEngine.Object.FindAnyObjectByType<LabelManager>(FindObjectsInactive.Include);
            if (source == null) return "PassthroughDetectionSource not found";
            if (!source.gameObject.activeSelf) source.gameObject.SetActive(true);

            string msg = "";
            if (labels != null)
            {
                var serialized = typeof(LabelManager).GetField("detectionSources", F)?.GetValue(labels) as System.Collections.IList;
                if (serialized != null && !serialized.Contains(source)) serialized.Add(source);
                var sources = typeof(LabelManager).GetField("_sources", F)?.GetValue(labels) as System.Collections.IList;
                if (sources != null && !sources.Contains(source))
                {
                    sources.Add(source);
                    var onDetected = typeof(LabelManager).GetMethod("OnDetected", F);
                    var handler = (Action<IReadOnlyList<DetectedObject>>)Delegate.CreateDelegate(typeof(Action<IReadOnlyList<DetectedObject>>), labels, onDetected);
                    source.Detected += handler;
                    msg += "real source subscribed to the LabelManager; ";
                }
                else msg += "real source already subscribed; ";
            }
            if (mock != null) { mock.enabled = false; msg += "mock off; "; }
            PassthroughDetectionSource.FallbackRaycast = BridgeRaycast.Raycast;
            msg += "fallback raycast = depth/MRUK room" + (BridgeRaycast.FixedDistance > 0 ? " + fixed distance" : "");
            return msg;
        }
    }
}
