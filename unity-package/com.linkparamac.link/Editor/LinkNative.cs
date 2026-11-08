// SPDX-License-Identifier: MIT
// Link para Mac: acesso às funções C do runtime OXRSys já carregado no processo do Unity (dlsym RTLD_DEFAULT).
// Fora do Play o runtime não está carregado: só o sucesso fica guardado.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LinkParaMac.EditorTools
{
    public static class LinkNative
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

        // Sessões: tracking do headset (cabeça, mãos, controles, microgestos) gravado e repetido pelo runtime.
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

        // Profundidade gravada no lugar da do headset (o raycast oficial do runtime responde pela sessão).
        public delegate void DepthReplayModeDelegate(int on);
        public delegate void DepthInjectDelegate(ushort[] pixels, uint count, ref LinkDepth.DepthInfo info);
        public static void DepthReplayMode(bool on) => Get<DepthReplayModeDelegate>("oxrsys_depth_replay_mode")?.Invoke(on ? 1 : 0);
        public static void DepthInject(ushort[] pixels, LinkDepth.DepthInfo info) =>
            Get<DepthInjectDelegate>("oxrsys_depth_inject")?.Invoke(pixels, (uint)pixels.Length, ref info);

        // Microfone do Quest (anel de 2 s no runtime, mono).
        public delegate int MicPullDelegate([Out] float[] dst, int maxSamples, out int sampleRate);
        public static int MicPull(float[] dst, out int sampleRate)
        {
            sampleRate = 0;
            var f = Get<MicPullDelegate>("oxrsys_mic_pull");
            return f == null ? 0 : f(dst, dst.Length, out sampleRate);
        }

        // Estado do headset (HUD).
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
