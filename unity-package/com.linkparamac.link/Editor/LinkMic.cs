// SPDX-License-Identifier: MIT
// Link para Mac: Quest microphone on the Mac ("mic", "mic test", "mic default on|off" commands).
// The client sends the headset mic over the Link; the runtime delivers it to the "Quest 3 Microphone (Link para Mac)"
// device (driver in link-para-mac/mic-driver), which becomes the Mac's default input while the app runs. That way
// Unity's Microphone (and any app) records the headset voice, like Quest Link's "Headset Microphone" on Windows.
using System;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

namespace LinkParaMac.EditorTools
{
    [InitializeOnLoad]
    public static class LinkMic
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MicStatus
        {
            public uint packets, sampleRate;
            public float lastAgeMs, level;
            public uint driverInstalled, isDefaultInput;
            public uint reserved0, reserved1;
        }

        public const string DeviceName = "Quest 3 Microphone (Link para Mac)";

        [DllImport("/usr/lib/libSystem.B.dylib")] static extern IntPtr dlsym(IntPtr handle, string symbol);
        static readonly IntPtr RTLD_DEFAULT = new IntPtr(-2);
        delegate int StatusDelegate(out MicStatus status);
        delegate int SetDefaultDelegate(int useQuestMic);
        static StatusDelegate _status;
        static SetDefaultDelegate _setDefault;

        static AudioClip _testClip;
        static double _testEnd;
        static string _testResult = "-";

        static LinkMic()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static bool Resolve()
        {
            if (_status != null) return true;
            IntPtr s = dlsym(RTLD_DEFAULT, "oxrsys_mic_status"), d = dlsym(RTLD_DEFAULT, "oxrsys_mic_set_default");
            if (s == IntPtr.Zero || d == IntPtr.Zero) return false; // runtime not loaded (outside Play) or too old
            _status = Marshal.GetDelegateForFunctionPointer<StatusDelegate>(s);
            _setDefault = Marshal.GetDelegateForFunctionPointer<SetDefaultDelegate>(d);
            return true;
        }

        public static bool TryGetStatus(out MicStatus status)
        {
            status = default;
            return Resolve() && _status(out status) != 0;
        }

        public static bool QuestDevicePresent()
        {
            foreach (var d in Microphone.devices) if (d == DeviceName) return true;
            return false;
        }

        public static string Command(string arg)
        {
            string a = (arg ?? "").Trim();
            if (a == "test") return StartTest();
            if (a == "default on" || a == "default off")
            {
                if (!Resolve()) return "runtime has no oxrsys_mic_set_default (enter Play Mode)";
                return _setDefault(a == "default on" ? 1 : 0) != 0 ? "default input: " + (a == "default on" ? "Quest 3 Microphone" : "the previous one") : "failed (is the driver installed?)";
            }
            return Status();
        }

        public static string Status()
        {
            string devices = string.Join(", ", Microphone.devices);
            if (!TryGetStatus(out var s))
                return $"Mic: no runtime status (outside Play?). Device on the Mac: {(QuestDevicePresent() ? "yes" : "NO — install link-para-mac/mic-driver")}\n  inputs: {devices}";
            string age = s.lastAgeMs < 0 ? "never" : $"{s.lastAgeMs:F0} ms ago";
            return $"Quest mic: {s.packets} packets, last {age}, level {s.level:F3}, {s.sampleRate} Hz; " +
                   $"driver {(s.driverInstalled != 0 ? "installed" : "NOT installed")}, default input {(s.isDefaultInput != 0 ? "= Quest" : "≠ Quest")}\n" +
                   $"  test: {_testResult}\n  inputs: {devices}";
        }

        // Records 3 s through Unity's Microphone on the Quest device and measures the level (the same path the app uses).
        static string StartTest()
        {
            if (!QuestDevicePresent()) return "device \"" + DeviceName + "\" not found (install the driver)";
            if (_testClip != null) return "test already running";
            _testClip = Microphone.Start(DeviceName, false, 3, 48000);
            _testEnd = EditorApplication.timeSinceStartup + 3.2;
            _testResult = "recording 3 s… speak into the headset";
            return "recording 3 s with Unity's Microphone from \"" + DeviceName + "\": speak into the headset (result in 'mic')";
        }

        static void Tick()
        {
            if (_testClip == null || EditorApplication.timeSinceStartup < _testEnd) return;
            Microphone.End(DeviceName);
            var data = new float[_testClip.samples * _testClip.channels];
            _testClip.GetData(data, 0);
            double sum = 0; float peak = 0;
            foreach (var v in data) { sum += v * v; peak = Mathf.Max(peak, Mathf.Abs(v)); }
            float rms = (float)Math.Sqrt(sum / Math.Max(1, data.Length));
            _testResult = $"{data.Length} samples, RMS {rms:F4}, peak {peak:F3} ({(peak > 0.02f ? "voice/sound captured" : "silence")})";
            Debug.Log("[LinkMic] test: " + _testResult);
            UnityEngine.Object.DestroyImmediate(_testClip);
            _testClip = null;
        }
    }
}
