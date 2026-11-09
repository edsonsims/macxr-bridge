// SPDX-License-Identifier: MIT
// MacXR Bridge: checks the microgestures (XR_META_hand_tracking_microgestures) arriving from the Quest through
// OVRPlugin, the way an app reads them (OVRPlugin.GetHandTrackingState → Microgesture). "micro" command.
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MacXRBridge.EditorTools
{
    [InitializeOnLoad]
    public static class BridgeMicrogestures
    {
        static readonly List<string> _events = new List<string>();
        static int _count;
        static OVRPlugin.MicrogestureType _lastLeft, _lastRight;

        static BridgeMicrogestures()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += s => { if (s == PlayModeStateChange.EnteredPlayMode) { _events.Clear(); _count = 0; } };
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying || !OVRPlugin.initialized) return;
            Poll(OVRPlugin.Hand.HandLeft, "left", ref _lastLeft);
            Poll(OVRPlugin.Hand.HandRight, "right", ref _lastRight);
        }

        static void Poll(OVRPlugin.Hand hand, string name, ref OVRPlugin.MicrogestureType last)
        {
            var state = new OVRPlugin.HandTrackingState();
            if (!OVRPlugin.GetHandTrackingState(OVRPlugin.Step.Render, hand, ref state)) return;
            var g = state.Microgesture;
            if (g != last && g != OVRPlugin.MicrogestureType.NoGesture && g != OVRPlugin.MicrogestureType.Invalid)
            {
                _count++;
                string e = $"{System.DateTime.Now:HH:mm:ss.f} {name} hand: {g}";
                _events.Add(e);
                if (_events.Count > 12) _events.RemoveAt(0);
                Debug.Log("[BridgeMicro] " + e);
            }
            last = g;
        }

        public static string Status()
        {
            return $"Microgestures: {_count} received" + (_events.Count > 0 ? "\n  " + string.Join("\n  ", _events) : " — swipe your thumb along your index finger (hand relaxed, turned sideways)");
        }
    }
}
