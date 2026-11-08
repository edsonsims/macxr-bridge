// SPDX-License-Identifier: MIT
// Link para Mac: confere os microgestos (XR_META_hand_tracking_microgestures) que chegam do Quest pelo
// OVRPlugin, do jeito que um app lê (OVRPlugin.GetHandTrackingState → Microgesture). Comando "micro".
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace LinkParaMac.EditorTools
{
    [InitializeOnLoad]
    public static class LinkMicrogestures
    {
        static readonly List<string> _events = new List<string>();
        static int _count;
        static OVRPlugin.MicrogestureType _lastLeft, _lastRight;

        static LinkMicrogestures()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += s => { if (s == PlayModeStateChange.EnteredPlayMode) { _events.Clear(); _count = 0; } };
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying || !OVRPlugin.initialized) return;
            Poll(OVRPlugin.Hand.HandLeft, "esquerda", ref _lastLeft);
            Poll(OVRPlugin.Hand.HandRight, "direita", ref _lastRight);
        }

        static void Poll(OVRPlugin.Hand hand, string name, ref OVRPlugin.MicrogestureType last)
        {
            var state = new OVRPlugin.HandTrackingState();
            if (!OVRPlugin.GetHandTrackingState(OVRPlugin.Step.Render, hand, ref state)) return;
            var g = state.Microgesture;
            if (g != last && g != OVRPlugin.MicrogestureType.NoGesture && g != OVRPlugin.MicrogestureType.Invalid)
            {
                _count++;
                string e = $"{System.DateTime.Now:HH:mm:ss.f} mão {name}: {g}";
                _events.Add(e);
                if (_events.Count > 12) _events.RemoveAt(0);
                Debug.Log("[LinkMicro] " + e);
            }
            last = g;
        }

        public static string Status()
        {
            return $"Microgestos: {_count} recebido(s)" + (_events.Count > 0 ? "\n  " + string.Join("\n  ", _events) : " — faça um deslize do polegar sobre o indicador (mão relaxada, de lado)");
        }
    }
}
