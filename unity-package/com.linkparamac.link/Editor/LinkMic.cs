// SPDX-License-Identifier: MIT
// Link para Mac: microfone do Quest no Mac (comando "mic", "mic test", "mic default on|off").
// O cliente manda o mic do headset pelo Link; o runtime entrega ao dispositivo "Quest 3 Microphone (Link para Mac)"
// (driver em link-para-mac/mic-driver), que fica como entrada padrão do Mac enquanto o app roda. Assim o
// Microphone do Unity (e qualquer app) grava a voz do headset, como o "Headset Microphone" do Quest Link no Windows.
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
            if (s == IntPtr.Zero || d == IntPtr.Zero) return false; // runtime não carregado (fora do Play) ou antigo
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
                if (!Resolve()) return "runtime sem oxrsys_mic_set_default (entre em Play)";
                return _setDefault(a == "default on" ? 1 : 0) != 0 ? "entrada padrão: " + (a == "default on" ? "Quest 3 Microphone" : "a anterior") : "falhou (driver instalado?)";
            }
            return Status();
        }

        public static string Status()
        {
            string devices = string.Join(", ", Microphone.devices);
            if (!TryGetStatus(out var s))
                return $"Mic: runtime sem estado (fora do Play?). Dispositivo no Mac: {(QuestDevicePresent() ? "sim" : "NÃO — instale link-para-mac/mic-driver")}\n  entradas: {devices}";
            string age = s.lastAgeMs < 0 ? "nunca" : $"{s.lastAgeMs:F0} ms";
            return $"Mic do Quest: {s.packets} pacotes, último há {age}, nível {s.level:F3}, {s.sampleRate} Hz; " +
                   $"driver {(s.driverInstalled != 0 ? "instalado" : "NÃO instalado")}, entrada padrão {(s.isDefaultInput != 0 ? "= Quest" : "≠ Quest")}\n" +
                   $"  teste: {_testResult}\n  entradas: {devices}";
        }

        // Grava 3 s pelo Microphone do Unity no dispositivo do Quest e mede o nível (o mesmo caminho do app).
        static string StartTest()
        {
            if (!QuestDevicePresent()) return "dispositivo \"" + DeviceName + "\" não existe (instale o driver)";
            if (_testClip != null) return "teste em andamento";
            _testClip = Microphone.Start(DeviceName, false, 3, 48000);
            _testEnd = EditorApplication.timeSinceStartup + 3.2;
            _testResult = "gravando 3 s… fale no headset";
            return "gravando 3 s pelo Microphone do Unity em \"" + DeviceName + "\": fale no headset (resultado em 'mic')";
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
            _testResult = $"{data.Length} amostras, RMS {rms:F4}, pico {peak:F3} ({(peak > 0.02f ? "voz/som captado" : "silêncio")})";
            Debug.Log("[LinkMic] teste: " + _testResult);
            UnityEngine.Object.DestroyImmediate(_testClip);
            _testClip = null;
        }
    }
}
