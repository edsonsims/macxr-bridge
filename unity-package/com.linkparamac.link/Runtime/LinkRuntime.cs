// SPDX-License-Identifier: MIT
// Link para Mac: no Play do Editor com o runtime OXRSys (Quest ligado ao Mac), o som do app vai pro fone do Quest
// (LinkAudioTap no AudioListener). Sem o OXRSys (Editor puro, XR Simulator, Link do Windows) nada muda.
#if UNITY_EDITOR
using UnityEngine;

namespace LinkParaMac
{
    public static class LinkRuntime
    {
        /// <summary>True quando o runtime OpenXR ativo é o OXRSys (Link para Mac).</summary>
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
