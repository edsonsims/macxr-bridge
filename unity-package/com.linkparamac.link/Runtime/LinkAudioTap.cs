// SPDX-License-Identifier: MIT
// Link para Mac: the app's sound plays in the Quest headset, as with Quest Link. Lives on the same GameObject as the
// AudioListener and receives the final mix (OnAudioFilterRead); sends it to the OXRSys runtime
// (oxrsys_audio_push), which relays it to the headset over the camera channel. While the headset is receiving, the
// Mac stays muted (EditorPrefs "LinkParaMac.AudioToMac" = true plays on both).
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
        // Mixed reality recording (LinkRecorder): copies of the mix while this is not null.
        public static System.Collections.Concurrent.ConcurrentQueue<float[]> RecordQueue;
        public static volatile int LastChannels;
        int _sampleRate;

        public static bool Resolve()
        {
            if (_resolved) return true;
            IntPtr p = dlsym(RTLD_DEFAULT, "oxrsys_audio_push"); // the runtime only loads in Play: don't cache the failure
            if (p == IntPtr.Zero) return false;
            _resolved = true;
            _push = Marshal.GetDelegateForFunctionPointer<PushDelegate>(p);
            return true;
        }

        void Awake()
        {
            _sampleRate = AudioSettings.outputSampleRate; // can only be read on the main thread
            AlsoPlayOnMac = UnityEditor.EditorPrefs.GetBool("LinkParaMac.AudioToMac", false);
            Callbacks = SentCallbacks = 0;
            TryResolve();
        }

        float _nextResolve;

        // The OpenXR runtime may not have exported the symbols yet on the first frame: retry every second.
        void Update()
        {
            if (_push != null || Time.unscaledTime < _nextResolve) return;
            _nextResolve = Time.unscaledTime + 1f;
            TryResolve();
        }

        void TryResolve()
        {
            if (!Resolve()) return;
            Debug.Log($"[LinkAudio] app audio goes to the headset ({_sampleRate} Hz); Mac {(AlsoPlayOnMac ? "plays too" : "stays muted while the headset receives")}");
            enabled = true;
        }

        // Diagnostics: loudest sample and non-finite samples (NaN/Inf from a plugin) since the last Status().
        static float _peak;
        static int _nonFinite;

        void OnAudioFilterRead(float[] data, int channels)
        {
            Callbacks++;
            LastChannels = channels;
            for (int i = 0; i < data.Length; i++)
            {
                float v = data[i];
                if (float.IsNaN(v) || float.IsInfinity(v)) { _nonFinite++; data[i] = 0f; continue; } // never send NaN to the headset
                if (v > _peak) _peak = v; else if (-v > _peak) _peak = -v;
            }
            var record = RecordQueue;
            if (record != null) record.Enqueue((float[])data.Clone());
            if (_push == null || channels <= 0) return;
            bool attached = _push(data, data.Length / channels, channels, _sampleRate) != 0;
            HeadsetAttached = attached;
            if (!attached) return;
            SentCallbacks++;
            if (!AlsoPlayOnMac) Array.Clear(data, 0, data.Length);
        }

        /// <summary>Test beep (880 Hz, 0.4 s) played by the app: checks the sound in the headset and in recordings.</summary>
        public static string Beep()
        {
            var listener = FindAnyObjectByType<AudioListener>();
            if (listener == null) return "no AudioListener in the scene";
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
            // Separate GameObject: an AudioSource on the listener's object would get OnAudioFilterRead for itself only.
            var go = new GameObject("Link beep");
            go.transform.position = listener.transform.position;
            var src = go.AddComponent<AudioSource>();
            src.spatialBlend = 0f;
            src.clip = clip;
            src.Play();
            Destroy(go, 1f);
            return "beep 880 Hz 0.4 s (" + (HeadsetAttached ? "going to the headset" : "headset isn't receiving audio right now") + ")";
        }

        public static string Status()
        {
            if (!Resolve()) return "Audio: runtime has no oxrsys_audio_push (rebuild the runtime and restart Unity)";
            var tap = FindAnyObjectByType<LinkAudioTap>();
            return $"Audio: tap={(tap != null ? tap.gameObject.name : "missing")} headset={(HeadsetAttached ? "receiving" : "not connected")} " +
                   $"blocks={Callbacks} sent={SentCallbacks} Mac={(AlsoPlayOnMac ? "plays too" : "muted")} " +
                   $"peak={PeakAndReset():F3} nonFinite={_nonFinite}";
        }

        static float PeakAndReset() { float p = _peak; _peak = 0f; return p; }
    }
}
#endif
