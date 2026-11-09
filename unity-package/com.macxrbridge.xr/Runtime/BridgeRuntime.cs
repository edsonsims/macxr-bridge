// SPDX-License-Identifier: MIT
// MacXR Bridge: in Editor Play Mode with the OXRSys runtime (Quest connected to the Mac), the app's sound goes to the
// Quest headset (BridgeAudioTap on the AudioListener). Without OXRSys (plain Editor, XR Simulator, Windows Quest Link) nothing changes.
#if UNITY_EDITOR
using UnityEngine;

namespace MacXRBridge
{
    public static class BridgeRuntime
    {
        /// <summary>True when the active OpenXR runtime is OXRSys (MacXR Bridge).</summary>
        public static bool IsMacXRBridge()
        {
            string json = System.Environment.GetEnvironmentVariable("XR_RUNTIME_JSON") ?? "";
            if (json.IndexOf("oxrsys", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            string name = UnityEngine.XR.OpenXR.OpenXRRuntime.name ?? "";
            return name.IndexOf("OXRSys", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void OnPlay()
        {
            if (!Application.isEditor || !IsMacXRBridge()) return;
            var listener = Object.FindAnyObjectByType<AudioListener>();
            if (listener != null && listener.GetComponent<BridgeAudioTap>() == null) listener.gameObject.AddComponent<BridgeAudioTap>();
        }
    }
}
#endif
