// SPDX-License-Identifier: MIT
// MacXR Bridge: head-locked performance HUD ("hud on|off" command; also in the Unity panel).
// Shows the whole path: stream transport, bitrate and latency (the runtime's runtime_status.json), what the
// headset reports about itself (fps, battery, temperature — the client's Stats message), Unity on the Mac and the
// bridge channels (camera, depth, microphone). On-screen reminder: the Unity fps here is the Mac's, not the Quest's.
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace MacXRBridge.EditorTools
{
    [InitializeOnLoad]
    public static class BridgeHud
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

        static BridgeHud()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += s => { if (s == PlayModeStateChange.ExitingPlayMode) Hide(); };
        }

        public static string Command(string arg)
        {
            string a = (arg ?? "").Trim();
            if (a == "on") { if (!EditorApplication.isPlaying) return "Play Mode only"; Visible = true; return "HUD on (in the headset, below your view)"; }
            if (a == "off") { Hide(); return "HUD off"; }
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
            if (BridgeCameraSource.TryGetLatestTexture(out _, out var info) && info.frameId != _camLastId) { _camLastId = info.frameId; _camFrames++; }
            double now = EditorApplication.timeSinceStartup;
            if (now - _camWindowStart >= 1.0)
            {
                double dt = now - _camWindowStart;
                _camFps = (float)(_camFrames / dt);
                _depthHz = (float)((BridgeDepth.FrameCount - _depthFrames0) / dt);
                _camFrames = 0; _depthFrames0 = BridgeDepth.FrameCount; _camWindowStart = now;
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
                    string path = BridgeNative.RuntimeStatusPath();
                    _statusJson = !string.IsNullOrEmpty(path) && File.Exists(path) ? File.ReadAllText(path) : "";
                }
                catch { _statusJson = ""; }
            }
            var sb = new StringBuilder();
            string transport = Str("transport");
            transport = transport == "usb_adb" ? "USB" : transport == "wifi" ? "Wi-Fi" : transport == "usb_ncm" ? "USB (network)" : transport == "" ? "disconnected" : transport;
            sb.AppendLine($"MACXR BRIDGE   {transport} · {F(Num("refresh_rate_hz"), "0")} Hz · {F(Num("current_bitrate_mbps"), "0")} Mb/s · {Str("video_codec")}");
            sb.AppendLine($"Latency   Mac {F(Num("server_pipeline"), "0")} ms + Quest {F(Num("client_pipeline"), "0")} ms · decode {F(Num("client_decode"), "0.0")} ms · encode {F(Num("total_avg"), "0.0")} ms");
            if (BridgeNative.TryGetClientStats(out var cs, out float age) && age < 5f)
            {
                string battery = cs.batteryPercent >= 0 ? $"{cs.batteryPercent:0}%{(cs.charging != 0 ? " (charging)" : "")}" : "–";
                string temp = cs.batteryTemperatureC > -100 ? $"{cs.batteryTemperatureC:0} °C" : "–";
                sb.AppendLine($"Headset   {cs.displayFps:0} fps · video {cs.videoFps:0} fps · battery {battery} · {temp}");
            }
            else sb.AppendLine("Headset   (no client data — old Quest app or disconnected)");
            sb.AppendLine($"Unity     {1f / _dtAvg:0} fps ({_dtAvg * 1000f:0.0} ms)  ← on the Mac, not Quest performance");
            string mic = "–";
            if (BridgeMic.TryGetStatus(out var ms) && ms.lastAgeMs >= 0 && ms.lastAgeMs < 1000)
                mic = new string('|', Mathf.Clamp(Mathf.RoundToInt(ms.level * 60f), 0, 10)).PadRight(10, '.');
            sb.Append($"Camera    {_camFps:0} fps · depth {_depthHz:0} Hz · mic [{mic}]");
            if (BridgeSession.Replaying) sb.Append($"\nREPLAYING {BridgeSession.ReplayName} {BridgeSession.CurrentTime:0.0}/{BridgeSession.Duration:0.0} s{(BridgeSession.Paused ? " (paused)" : "")}");
            if (BridgeSession.Recording) sb.Append($"\n● RECORDING SESSION {BridgeSession.RecordingSeconds:0} s");
            if (BridgeRecorder.Recording) sb.Append($"\n● RECORDING VIDEO {BridgeRecorder.Seconds:0} s");
            Text = sb.ToString();
        }

        static void Draw()
        {
            var rig = UnityEngine.Object.FindAnyObjectByType<OVRCameraRig>();
            Transform head = rig != null ? rig.centerEyeAnchor : (Camera.main != null ? Camera.main.transform : null);
            if (head == null) return;
            if (_root == null)
            {
                _root = new GameObject("MacXR HUD (Editor)");
                var bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
                UnityEngine.Object.Destroy(bg.GetComponent<Collider>());
                bg.name = "background";
                bg.transform.SetParent(_root.transform, false);
                bg.transform.localPosition = new Vector3(0, 0, 0.002f);
                bg.transform.localScale = new Vector3(0.62f, 0.17f, 1f);
                var mat = new Material(Shader.Find("Sprites/Default")) { color = new Color(0f, 0f, 0f, 0.65f) };
                bg.GetComponent<Renderer>().sharedMaterial = mat;
                var textGo = new GameObject("text");
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
