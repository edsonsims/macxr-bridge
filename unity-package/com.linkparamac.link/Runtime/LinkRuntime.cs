// SPDX-License-Identifier: MIT
// Link para Mac: in Editor Play Mode with the OXRSys runtime (Quest connected to the Mac), the app's sound goes to the
// Quest headset (LinkAudioTap on the AudioListener). Without OXRSys (plain Editor, XR Simulator, Windows Link) nothing changes.
#if UNITY_EDITOR
using UnityEngine;

namespace LinkParaMac
{
    public static class LinkRuntime
    {
        /// <summary>True when the active OpenXR runtime is OXRSys (Link para Mac).</summary>
        public static bool IsLinkParaMac()
        {
            string json = System.Environment.GetEnvironmentVariable("XR_RUNTIME_JSON") ?? "";
            if (json.IndexOf("oxrsys", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            string name = UnityEngine.XR.OpenXR.OpenXRRuntime.name ?? "";
            return name.IndexOf("OXRSys", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void OnPlay()
        {
            if (!Application.isEditor || !IsLinkParaMac()) return;
            var listener = Object.FindAnyObjectByType<AudioListener>();
            if (listener != null && listener.GetComponent<LinkAudioTap>() == null) listener.gameObject.AddComponent<LinkAudioTap>();
        }
    }
}
#endif
