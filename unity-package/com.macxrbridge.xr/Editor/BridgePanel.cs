// SPDX-License-Identifier: MIT
// MacXR Bridge: Unity panel (Window > MacXR Bridge > Panel). Everything the bridge commands do, as buttons:
// stream and headset status, microphone, room, debugging, HUD, spectator, video recording, sessions (record,
// replay with a timeline, tests).
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MacXRBridge.EditorTools
{
    public class BridgePanel : EditorWindow
    {
        [MenuItem("Window/MacXR Bridge/Panel", priority = 0)]
        public static void Open() => GetWindow<BridgePanel>("MacXR Bridge");

        string _videoName = "demo", _sessionName = "sessao", _lastAction = "";
        int _sessionIndex;
        bool _loop, _debugDepth = true;
        Vector2 _scroll;
        double _nextRepaint;
        static readonly double[] Speeds = { 0.25, 0.5, 1, 2 };

        public static string BridgeRoot
        {
            get
            {
                string fallback = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "macxr-bridge"));
                return EditorPrefs.GetString("MacXRBridge.Root", fallback);
            }
            set => EditorPrefs.SetString("MacXRBridge.Root", value);
        }

        void OnEnable() => EditorApplication.update += Poll;
        void OnDisable() => EditorApplication.update -= Poll;

        void Poll()
        {
            if (EditorApplication.timeSinceStartup < _nextRepaint) return;
            _nextRepaint = EditorApplication.timeSinceStartup + 0.25;
            Repaint();
        }

        static GUIStyle _mono;
        static GUIStyle Mono => _mono ??= new GUIStyle(EditorStyles.label) { font = EditorStyles.miniFont, wordWrap = true, richText = false };

        void Act(string result) { _lastAction = result ?? ""; }

        public static void RunScript(string script)
        {
            string path = Path.Combine(BridgeRoot, "scripts", script);
            if (!File.Exists(path)) { UnityEngine.Debug.LogWarning("[BridgePanel] script not found: " + path); return; }
            // In the background (adb must never block the Editor).
            Process.Start(new ProcessStartInfo("/bin/zsh", $"\"{path}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = false });
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            bool playing = EditorApplication.isPlaying;

            EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
            if (!playing)
            {
                EditorGUILayout.HelpBox("Enter Play Mode with the Quest connected by cable (or on the same Wi-Fi) and the OXRSys app open on the headset.", MessageType.Info);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Play")) EditorApplication.EnterPlaymode();
                    if (GUILayout.Button("Setup & Diagnostics")) BridgeSetupWindow.Open();
                }
            }
            else
            {
                BridgeHud.Refresh();
                EditorGUILayout.LabelField(BridgeHud.Text, Mono);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Reconnect the Quest")) { RunScript("80-reconnect-quest.sh"); Act("reconnecting the Quest (a few seconds)…"); }
                    if (GUILayout.Button(BridgeHud.Visible ? "Headset HUD: on" : "Headset HUD: off")) Act(BridgeHud.Command(BridgeHud.Visible ? "off" : "on"));
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Quest Microphone", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(BridgeMic.Status(), Mono);
            using (new EditorGUI.DisabledScope(!playing))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Test (speak into the headset, 3 s)")) Act(BridgeMic.Command("test"));
                if (GUILayout.Button("Default input = Quest")) Act(BridgeMic.Command("default on"));
                if (GUILayout.Button("Restore previous")) Act(BridgeMic.Command("default off"));
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Real world", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(!playing))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Load the room (MRUK)")) Act(RoomTestTools.Load(""));
                    if (GUILayout.Button("Test beep")) Act(MacXRBridge.BridgeAudioTap.Beep());
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool dbg = BridgeDebugOverlay.Enabled;
                    if (GUILayout.Button(dbg ? "Headset debug view: on" : "Headset debug view: off"))
                        Act(BridgeDebugOverlay.Command(dbg ? "off" : _debugDepth ? "depth" : "on"));
                    bool depth = GUILayout.Toggle(_debugDepth, "with depth points", GUILayout.Width(190));
                    if (depth != _debugDepth) { _debugDepth = depth; if (dbg) Act(BridgeDebugOverlay.Command(depth ? "depth" : "nodepth")); }
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Mixed reality video", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(!playing))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (!BridgeRecorder.Recording)
                    {
                        _videoName = EditorGUILayout.TextField(_videoName);
                        if (GUILayout.Button("Record", GUILayout.Width(80))) Act(BridgeRecorder.Start(_videoName));
                    }
                    else if (GUILayout.Button($"Stop recording ({BridgeRecorder.Seconds:0} s)")) Act(BridgeRecorder.Stop());
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Spectator (window)")) BridgeSpectatorWindow.Open();
                    if (GUILayout.Button(BridgeStream.Running ? "Stop streaming" : "Stream on local network"))
                        Act(BridgeStream.Running ? BridgeStream.Command("off") : BridgeStream.Start(BridgeStream.DefaultPort));
                    if (GUILayout.Button("Open folder", GUILayout.Width(90)))
                    { Directory.CreateDirectory("Recordings"); EditorUtility.RevealInFinder(Path.GetFullPath("Recordings")); }
                }
                if (BridgeStream.Running) EditorGUILayout.SelectableLabel(BridgeStream.Url + $"   ({BridgeStream.Clients} watching)", GUILayout.Height(18));
            }
            EditorGUILayout.LabelField(BridgeRecorder.Status(), Mono);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Sessions (record with the headset, replay without it)", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(!playing))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (!BridgeSession.Recording)
                    {
                        _sessionName = EditorGUILayout.TextField(_sessionName);
                        using (new EditorGUI.DisabledScope(BridgeSession.Replaying))
                            if (GUILayout.Button("Record session", GUILayout.Width(110))) Act(BridgeSession.Command("rec " + _sessionName));
                    }
                    else if (GUILayout.Button($"Stop session ({BridgeSession.RecordingSeconds:0} s)")) Act(BridgeSession.Command("stop"));
                }
                string[] sessions = Directory.Exists("Sessions")
                    ? new DirectoryInfo("Sessions").GetDirectories().Where(d => File.Exists(Path.Combine(d.FullName, "camera.csv"))).Select(d => d.Name).OrderByDescending(n => n).ToArray()
                    : Array.Empty<string>();
                if (sessions.Length > 0)
                {
                    _sessionIndex = Mathf.Clamp(_sessionIndex, 0, sessions.Length - 1);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        _sessionIndex = EditorGUILayout.Popup(_sessionIndex, sessions);
                        _loop = GUILayout.Toggle(_loop, "loop", GUILayout.Width(45));
                        if (!BridgeSession.Replaying)
                        {
                            if (GUILayout.Button("Replay", GUILayout.Width(70))) Act(BridgeSession.Command("play " + sessions[_sessionIndex] + (_loop ? " loop" : "")));
                        }
                        else if (GUILayout.Button("Stop", GUILayout.Width(70))) Act(BridgeSession.Command("stop"));
                    }
                    if (BridgeSession.Replaying && BridgeSession.Duration > 0)
                    {
                        double t = BridgeSession.CurrentTime;
                        double nt = EditorGUILayout.Slider((float)t, 0f, (float)BridgeSession.Duration);
                        if (Math.Abs(nt - t) > 0.05) BridgeSession.Seek(nt);
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button("◀ frame")) Act(BridgeSession.Step(-1));
                            if (GUILayout.Button(BridgeSession.Paused ? "▶ resume" : "❚❚ pause")) Act(BridgeSession.Pause(!BridgeSession.Paused));
                            if (GUILayout.Button("frame ▶")) Act(BridgeSession.Step(1));
                            int si = Array.IndexOf(Speeds, BridgeSession.Speed);
                            int ns = EditorGUILayout.Popup(si < 0 ? 2 : si, new[] { "0.25×", "0.5×", "1×", "2×" }, GUILayout.Width(60));
                            if (ns != si && ns >= 0) Act(BridgeSession.SetSpeed(Speeds[ns]));
                        }
                        EditorGUILayout.LabelField($"{t:0.00} / {BridgeSession.Duration:0.0} s · frame {BridgeSession.CameraFrameIndex + 1}/{BridgeSession.CameraFrameCount}" +
                                                   (BridgeSession.TapeActive ? " · recorded tracking (hands, controllers)" : ""), EditorStyles.miniLabel);
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    using (new EditorGUI.DisabledScope(BridgeSession.Recording || BridgeSessionTests.Running))
                    {
                        if (GUILayout.Button("Record expected (baseline)")) Act(BridgeSession.Command("baseline " + sessions[_sessionIndex]));
                        if (GUILayout.Button("Test this one")) Act(BridgeSession.Command("test " + sessions[_sessionIndex]));
                        if (GUILayout.Button("Test all")) Act(BridgeSession.Command("test all"));
                    }
                    if (BridgeSessionTests.LastReport != "-") EditorGUILayout.LabelField(BridgeSessionTests.LastReport, Mono);
                }
                EditorGUILayout.LabelField(BridgeSession.Status(), Mono);
            }

            if (!string.IsNullOrEmpty(_lastAction))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(_lastAction, MessageType.None);
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
