// SPDX-License-Identifier: MIT
// MacXR Bridge: extension points for app code. The bridge is generic (com.macxrbridge.xr package); what belongs to
// the app (turning on its real detection, where the label landed, what the fallback raycast should ignore) comes
// through a small bridge in the project itself (in Hello World: Assets/_Project/MacXRBridge/HelloWorldBridgeHooks.cs).
using System;
using System.Collections.Generic;
using UnityEngine;

namespace MacXRBridge.EditorTools
{
    public static class BridgeHooks
    {
        /// <summary>The app placed something in the world (label, position). Used by session tests and debugging.</summary>
        public static event Action<string, Vector3> Placed;
        /// <summary>A batch of placements (one detection response).</summary>
        public static event Action<IReadOnlyList<(string label, Vector3 position)>> PlacedBatch;

        public static void ReportPlaced(string label, Vector3 position) => Placed?.Invoke(label, position);
        public static void ReportPlacedBatch(IReadOnlyList<(string label, Vector3 position)> batch)
        {
            PlacedBatch?.Invoke(batch);
            foreach (var p in batch) Placed?.Invoke(p.label, p.position);
        }

        /// <summary>Turns on the app's REAL detection when the Quest camera arrives (turns off mocks, subscribes the source…).
        /// Returns a short report.</summary>
        public static Func<string> WireDetection;
        /// <summary>Undoes WireDetection (end of the feed / of Play).</summary>
        public static Action UnwireDetection;
        /// <summary>The app's own confidence threshold (the bridge already adjusts the agent/provider one).</summary>
        public static Action<float> ApplyAppThreshold;
        /// <summary>Colliders the fallback raycast should ignore (app labels, mock room…).</summary>
        public static Func<Collider, bool> IgnoreCollider;
        /// <summary>Diagnostics: pauses/resumes the app's detection source.</summary>
        public static Action<bool> PauseAppDetection;
        /// <summary>An extra line in detect-status about the app's source.</summary>
        public static Func<string> AppStatus;

        // Lockstep tests: the test decides when the app may start the next detection.
        /// <summary>Start a detection now (in the current frame).</summary>
        public static Action DetectNow;
        /// <summary>Don't start another one until the next DetectNow.</summary>
        public static Action HoldDetection;
        /// <summary>True if a detection started after the last DetectNow.</summary>
        public static Func<bool> DetectionStarted;
    }
}
