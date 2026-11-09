// SPDX-License-Identifier: MIT
// MacXR Bridge: access to the C functions of the OXRSys runtime already loaded in the Unity process (dlsym RTLD_DEFAULT).
// Outside Play the runtime isn't loaded: only successful lookups are cached.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MacXRBridge.EditorTools
{
    public static class BridgeNative
    {
        [DllImport("/usr/lib/libSystem.B.dylib")] static extern IntPtr dlsym(IntPtr handle, string symbol);
        static readonly IntPtr RTLD_DEFAULT = new IntPtr(-2);
        static readonly Dictionary<string, Delegate> Cache = new Dictionary<string, Delegate>();

        public static T Get<T>(string symbol) where T : Delegate
        {
            if (Cache.TryGetValue(symbol, out var cached)) return (T)cached;
            IntPtr p = dlsym(RTLD_DEFAULT, symbol);
            if (p == IntPtr.Zero) return null;
            var d = Marshal.GetDelegateForFunctionPointer<T>(p);
            Cache[symbol] = d;
            return d;
        }

        // Sessions: headset tracking (head, hands, controllers, microgestures) recorded and replayed by the runtime.
        public delegate int PathDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        public delegate int IntDelegate();
        public delegate void VoidDelegate();
        public delegate void ClockDelegate(double t, double speed);

        public static bool TapeRecordStart(string path) => Get<PathDelegate>("oxrsys_tracking_record_start")?.Invoke(path) == 1;
        public static int TapeRecordStop() => Get<IntDelegate>("oxrsys_tracking_record_stop")?.Invoke() ?? 0;
        public static int TapeReplayStart(string path) => Get<PathDelegate>("oxrsys_tracking_replay_start")?.Invoke(path) ?? 0;
        public static void TapeReplayClock(double t, double speed) => Get<ClockDelegate>("oxrsys_tracking_replay_clock")?.Invoke(t, speed);
        public static void TapeReplayStop() => Get<VoidDelegate>("oxrsys_tracking_replay_stop")?.Invoke();
        public static bool TapeSupported => Get<PathDelegate>("oxrsys_tracking_record_start") != null;

        // The runtime's LOCAL reference (x y z qx qy qz qw): a replay uses the one its session was recorded with.
        public delegate int FloatsDelegate([In, Out] float[] values);
        public static float[] LocalReferenceGet()
        {
            var v = new float[7];
            return Get<FloatsDelegate>("oxrsys_local_reference_get")?.Invoke(v) == 1 ? v : null;
        }
        public static bool LocalReferenceSet(float[] v7) => v7 != null && v7.Length == 7 && Get<FloatsDelegate>("oxrsys_local_reference_set")?.Invoke(v7) == 1;
        public static bool LocalReferenceFromTape() => Get<IntDelegate>("oxrsys_local_reference_from_tape")?.Invoke() == 1;

        // Recorded depth in place of the headset's (the runtime's official raycast answers from the session).
        public delegate void DepthReplayModeDelegate(int on);
        public delegate void DepthInjectDelegate(ushort[] pixels, uint count, ref BridgeDepth.DepthInfo info);
        public static void DepthReplayMode(bool on) => Get<DepthReplayModeDelegate>("oxrsys_depth_replay_mode")?.Invoke(on ? 1 : 0);
        public static void DepthInject(ushort[] pixels, BridgeDepth.DepthInfo info) =>
            Get<DepthInjectDelegate>("oxrsys_depth_inject")?.Invoke(pixels, (uint)pixels.Length, ref info);

        // Quest microphone (2 s ring buffer in the runtime, mono).
        public delegate int MicPullDelegate([Out] float[] dst, int maxSamples, out int sampleRate);
        public static int MicPull(float[] dst, out int sampleRate)
        {
            sampleRate = 0;
            var f = Get<MicPullDelegate>("oxrsys_mic_pull");
            return f == null ? 0 : f(dst, dst.Length, out sampleRate);
        }

        // Headset state (HUD).
        [StructLayout(LayoutKind.Sequential)]
        public struct ClientStats
        {
            public uint size;
            public float batteryPercent, batteryTemperatureC;
            public uint charging;
            public float displayFps, videoFps;
            public uint micActive, transport;
            public uint r0, r1, r2, r3, r4, r5;
        }
        public delegate int ClientStatsDelegate(out ClientStats stats, out float ageSeconds);
        public static bool TryGetClientStats(out ClientStats stats, out float ageSeconds)
        {
            stats = default; ageSeconds = 0;
            var f = Get<ClientStatsDelegate>("oxrsys_client_stats_get");
            return f != null && f(out stats, out ageSeconds) == 1;
        }

        public delegate IntPtr StatusPathDelegate();
        public static string RuntimeStatusPath()
        {
            var f = Get<StatusPathDelegate>("oxrsys_runtime_status_path");
            return f == null ? null : Marshal.PtrToStringUTF8(f());
        }
    }
}
