// SPDX-License-Identifier: MIT
// Link para Mac: painel de desempenho preso à cabeça (comando "hud on|off"; também no painel do Unity).
// Mostra o caminho todo: transporte, taxa de bits e latência do stream (runtime_status.json do runtime), o que o
// headset diz dele mesmo (fps, bateria, temperatura — mensagem Stats do cliente), o Unity no Mac e os canais do
// Link (câmera, profundidade, microfone). Lembrete na tela: o fps do Unity aqui é do Mac, não do Quest.
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace LinkParaMac.EditorTools
{
    [InitializeOnLoad]
    public static class LinkHud
    {
        public static bool Visible { get; private set; }
        public static string Text { get; private set; } = "";

        static GameObject _root;
        static TextMesh _text;
        static double _nextUpdate, _nextStatusRead;
        static float _dtAvg = 1f / 72f;
        static uint _camLastId;
        static int _camFrames, _depthFrames0;
        static double _camWindowStart;
        static float _camFps, _depthHz;
        static string _statusJson = "";

        static LinkHud()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += s => { if (s == PlayModeStateChange.ExitingPlayMode) Hide(); };
        }

        public static string Command(string arg)
        {
            string a = (arg ?? "").Trim();
            if (a == "on") { if (!EditorApplication.isPlaying) return "só em Play Mode"; Visible = true; return "HUD ligado (no headset, embaixo da visão)"; }
            if (a == "off") { Hide(); return "HUD desligado"; }
            Refresh();
            return Text;
        }

        static void Hide()
        {
            Visible = false;
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _text = null;
        }

        static void Tick()
        {
            if (!EditorApplication.isPlaying) return;
            _dtAvg = Mathf.Lerp(_dtAvg, Mathf.Max(1e-4f, Time.unscaledDeltaTime), 0.05f);
            if (LinkCameraSource.TryGetLatestTexture(out _, out var info) && info.frameId != _camLastId) { _camLastId = info.frameId; _camFrames++; }
            double now = EditorApplication.timeSinceStartup;
            if (now - _camWindowStart >= 1.0)
            {
                double dt = now - _camWindowStart;
                _camFps = (float)(_camFrames / dt);
                _depthHz = (float)((LinkDepth.FrameCount - _depthFrames0) / dt);
                _camFrames = 0; _depthFrames0 = LinkDepth.FrameCount; _camWindowStart = now;
            }
            if (now < _nextUpdate) return;
            _nextUpdate = now + 0.5;
            Refresh();
            if (Visible) Draw();
        }

        static double Num(string key)
        {
            var m = Regex.Match(_statusJson, "\"" + key + "\":\\s*(-?[0-9.eE+-]+)");
            return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
        }

        static string Str(string key)
        {
            var m = Regex.Match(_statusJson, "\"" + key + "\":\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        static string F(double v, string fmt) => double.IsNaN(v) ? "–" : v.ToString(fmt, CultureInfo.InvariantCulture);

        public static void Refresh()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now >= _nextStatusRead)
            {
                _nextStatusRead = now + 1.0;
                try
                {
                    string path = LinkNative.RuntimeStatusPath();
                    _statusJson = !string.IsNullOrEmpty(path) && File.Exists(path) ? File.ReadAllText(path) : "";
                }
                catch { _statusJson = ""; }
            }
            var sb = new StringBuilder();
            string transport = Str("transport");
            transport = transport == "usb_adb" ? "USB" : transport == "wifi" ? "Wi-Fi" : transport == "usb_ncm" ? "USB (rede)" : transport == "" ? "desconectado" : transport;
            sb.AppendLine($"LINK PARA MAC   {transport} · {F(Num("refresh_rate_hz"), "0")} Hz · {F(Num("current_bitrate_mbps"), "0")} Mb/s · {Str("video_codec")}");
            sb.AppendLine($"Latência  Mac {F(Num("server_pipeline"), "0")} ms + Quest {F(Num("client_pipeline"), "0")} ms · decodificação {F(Num("client_decode"), "0.0")} ms · codificação {F(Num("total_avg"), "0.0")} ms");
            if (LinkNative.TryGetClientStats(out var cs, out float age) && age < 5f)
            {
                string battery = cs.batteryPercent >= 0 ? $"{cs.batteryPercent:0}%{(cs.charging != 0 ? " (carregando)" : "")}" : "–";
                string temp = cs.batteryTemperatureC > -100 ? $"{cs.batteryTemperatureC:0} °C" : "–";
                sb.AppendLine($"Headset   {cs.displayFps:0} fps · vídeo {cs.videoFps:0} fps · bateria {battery} · {temp}");
            }
            else sb.AppendLine("Headset   (sem dados do cliente — app do Quest antigo ou desconectado)");
            sb.AppendLine($"Unity     {1f / _dtAvg:0} fps ({_dtAvg * 1000f:0.0} ms)  ← no Mac, não é o desempenho do Quest");
            string mic = "–";
            if (LinkMic.TryGetStatus(out var ms) && ms.lastAgeMs >= 0 && ms.lastAgeMs < 1000)
                mic = new string('|', Mathf.Clamp(Mathf.RoundToInt(ms.level * 60f), 0, 10)).PadRight(10, '.');
            sb.Append($"Câmera    {_camFps:0} fps · profundidade {_depthHz:0} Hz · mic [{mic}]");
            if (LinkSession.Replaying) sb.Append($"\nREPETINDO {LinkSession.ReplayName} {LinkSession.CurrentTime:0.0}/{LinkSession.Duration:0.0} s{(LinkSession.Paused ? " (pausada)" : "")}");
            if (LinkSession.Recording) sb.Append($"\n● GRAVANDO SESSÃO {LinkSession.RecordingSeconds:0} s");
            if (LinkRecorder.Recording) sb.Append($"\n● GRAVANDO VÍDEO {LinkRecorder.Seconds:0} s");
            Text = sb.ToString();
        }

        static void Draw()
        {
            var rig = UnityEngine.Object.FindAnyObjectByType<OVRCameraRig>();
            Transform head = rig != null ? rig.centerEyeAnchor : (Camera.main != null ? Camera.main.transform : null);
            if (head == null) return;
            if (_root == null)
            {
                _root = new GameObject("Link HUD (Editor)");
                var bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
                UnityEngine.Object.Destroy(bg.GetComponent<Collider>());
                bg.name = "fundo";
                bg.transform.SetParent(_root.transform, false);
                bg.transform.localPosition = new Vector3(0, 0, 0.002f);
                bg.transform.localScale = new Vector3(0.62f, 0.17f, 1f);
                var mat = new Material(Shader.Find("Sprites/Default")) { color = new Color(0f, 0f, 0f, 0.65f) };
                bg.GetComponent<Renderer>().sharedMaterial = mat;
                var textGo = new GameObject("texto");
                textGo.transform.SetParent(_root.transform, false);
                _text = textGo.AddComponent<TextMesh>();
                var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                _text.font = font;
                _text.GetComponent<MeshRenderer>().sharedMaterial = font.material;
                _text.fontSize = 48;
                _text.characterSize = 0.0019f;
                _text.anchor = TextAnchor.MiddleCenter;
                _text.alignment = TextAlignment.Left;
                _text.color = new Color(0.85f, 1f, 0.9f);
            }
            _root.transform.SetParent(head, false);
            _root.transform.localPosition = new Vector3(0f, -0.24f, 0.85f);
            _root.transform.localRotation = Quaternion.Euler(15f, 0f, 0f);
            _text.text = Text;
        }
    }
}
