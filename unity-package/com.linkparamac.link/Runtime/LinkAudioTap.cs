// SPDX-License-Identifier: MIT
// Link para Mac: o som do app sai no fone do Quest, como no Quest Link. Fica no mesmo GameObject do
// AudioListener e recebe a mixagem final (OnAudioFilterRead); manda pro runtime OXRSys
// (oxrsys_audio_push), que repassa ao headset pelo canal da câmera. Enquanto o headset recebe, o Mac fica
// mudo (EditorPrefs "LinkParaMac.AudioToMac" = true toca nos dois).
#if UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace LinkParaMac
{
    [AddComponentMenu("")]
    public sealed class LinkAudioTap : MonoBehaviour
    {
        [DllImport("/usr/lib/libSystem.B.dylib")] static extern IntPtr dlsym(IntPtr handle, string symbol);
        delegate int PushDelegate([In] float[] samples, int frames, int channels, int sampleRate);
        static readonly IntPtr RTLD_DEFAULT = new IntPtr(-2);
        static PushDelegate _push;
        static bool _resolved;

        public static volatile bool HeadsetAttached;
        public static volatile int Callbacks;
        public static volatile int SentCallbacks;
        public static bool AlsoPlayOnMac;
        // Gravação de realidade mista (LinkRecorder): cópias da mixagem enquanto não for null.
        public static System.Collections.Concurrent.ConcurrentQueue<float[]> RecordQueue;
        public static volatile int LastChannels;
        int _sampleRate;

        public static bool Resolve()
        {
            if (_resolved) return true;
            IntPtr p = dlsym(RTLD_DEFAULT, "oxrsys_audio_push"); // o runtime só carrega no Play: não guardar a falha
            if (p == IntPtr.Zero) return false;
            _resolved = true;
            _push = Marshal.GetDelegateForFunctionPointer<PushDelegate>(p);
            return true;
        }

        void Awake()
        {
            _sampleRate = AudioSettings.outputSampleRate; // só pode ser lido na thread principal
            AlsoPlayOnMac = UnityEditor.EditorPrefs.GetBool("LinkParaMac.AudioToMac", false);
            Callbacks = SentCallbacks = 0;
            TryResolve();
        }

        float _nextResolve;

        // O runtime OpenXR pode ainda não ter exportado os símbolos no primeiro frame: tenta de novo a cada segundo.
        void Update()
        {
            if (_push != null || Time.unscaledTime < _nextResolve) return;
            _nextResolve = Time.unscaledTime + 1f;
            TryResolve();
        }

        void TryResolve()
        {
            if (!Resolve()) return;
            Debug.Log($"[LinkAudio] som do app vai pro headset ({_sampleRate} Hz); Mac {(AlsoPlayOnMac ? "também toca" : "fica mudo enquanto o headset recebe")}");
            enabled = true;
        }

        void OnAudioFilterRead(float[] data, int channels)
        {
            Callbacks++;
            LastChannels = channels;
            var record = RecordQueue;
            if (record != null) record.Enqueue((float[])data.Clone());
            if (_push == null || channels <= 0) return;
            bool attached = _push(data, data.Length / channels, channels, _sampleRate) != 0;
            HeadsetAttached = attached;
            if (!attached) return;
            SentCallbacks++;
            if (!AlsoPlayOnMac) Array.Clear(data, 0, data.Length);
        }

        /// <summary>Bipe de teste (880 Hz, 0,4 s) tocado pelo app: confere o som no headset e nas gravações.</summary>
        public static string Beep()
        {
            var listener = FindAnyObjectByType<AudioListener>();
            if (listener == null) return "sem AudioListener na cena";
            int rate = AudioSettings.outputSampleRate, n = rate * 2 / 5;
            var samples = new float[n];
            int fade = rate / 100;
            for (int i = 0; i < n; i++)
            {
                float env = Mathf.Min(1f, Mathf.Min(i, n - 1 - i) / (float)fade);
                samples[i] = 0.4f * env * Mathf.Sin(2f * Mathf.PI * 880f * i / rate);
            }
            var clip = AudioClip.Create("Link beep", n, 1, rate, false);
            clip.SetData(samples, 0);
            // GameObject próprio: uma AudioSource no mesmo objeto do listener pegaria o OnAudioFilterRead só pra ela.
            var go = new GameObject("Link beep");
            go.transform.position = listener.transform.position;
            var src = go.AddComponent<AudioSource>();
            src.spatialBlend = 0f;
            src.clip = clip;
            src.Play();
            Destroy(go, 1f);
            return "bipe 880 Hz 0,4 s (" + (HeadsetAttached ? "indo pro headset" : "headset não recebe áudio agora") + ")";
        }

        public static string Status()
        {
            if (!Resolve()) return "Áudio: runtime sem oxrsys_audio_push (recompile o runtime e reinicie o Unity)";
            var tap = FindAnyObjectByType<LinkAudioTap>();
            return $"Áudio: tap={(tap != null ? tap.gameObject.name : "ausente")} headset={(HeadsetAttached ? "recebendo" : "não conectado")} " +
                   $"blocos={Callbacks} enviados={SentCallbacks} Mac={(AlsoPlayOnMac ? "também toca" : "mudo")}";
        }
    }
}
#endif
