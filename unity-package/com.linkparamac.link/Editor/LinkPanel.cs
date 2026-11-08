// SPDX-License-Identifier: MIT
// Link para Mac: Unity panel (Window > Link para Mac > Panel). Everything the bridge commands do, as buttons:
// stream and headset status, microphone, room, debugging, HUD, spectator, video recording, sessions (record,
// replay with a timeline, tests).
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LinkParaMac.EditorTools
{
    public class LinkPanel : EditorWindow
    {
        [MenuItem("Window/Link para Mac/Panel", priority = 0)]
        public static void Open() => GetWindow<LinkPanel>("Link para Mac");

        string _videoName = "demo", _sessionName = "sessao", _lastAction = "";
        int _sessionIndex;
        bool _loop, _debugDepth = true;
        Vector2 _scroll;
        double _nextRepaint;
        static readonly double[] Speeds = { 0.25, 0.5, 1, 2 };

        public static string LinkRoot
        {
            get
            {
                string fallback = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "link-para-mac"));
                return EditorPrefs.GetString("LinkParaMac.Root", fallback);
            }
            set => EditorPrefs.SetString("LinkParaMac.Root", value);
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
            string path = Path.Combine(LinkRoot, "scripts", script);
            if (!File.Exists(path)) { UnityEngine.Debug.LogWarning("[LinkPanel] script not found: " + path); return; }
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
                    if (GUILayout.Button("Setup & Diagnostics")) LinkSetupWindow.Open();
                }
            }
            else
            {
                LinkHud.Refresh();
                EditorGUILayout.LabelField(LinkHud.Text, Mono);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Reconnect the Quest")) { RunScript("80-reconnect-quest.sh"); Act("reconnecting the Quest (a few seconds)…"); }
                    if (GUILayout.Button(LinkHud.Visible ? "Headset HUD: on" : "Headset HUD: off")) Act(LinkHud.Command(LinkHud.Visible ? "off" : "on"));
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Quest Microphone", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(LinkMic.Status(), Mono);
            using (new EditorGUI.DisabledScope(!playing))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Test (speak into the headset, 3 s)")) Act(LinkMic.Command("test"));
                if (GUILayout.Button("Default input = Quest")) Act(LinkMic.Command("default on"));
                if (GUILayout.Button("Restore previous")) Act(LinkMic.Command("default off"));
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Real world", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(!playing))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Load the room (MRUK)")) Act(RoomTestTools.Load(""));
                    if (GUILayout.Button("Test beep")) Act(LinkParaMac.LinkAudioTap.Beep());
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool dbg = LinkDebugOverlay.Enabled;
                    if (GUILayout.Button(dbg ? "Headset debug view: on" : "Headset debug view: off"))
                        Act(LinkDebugOverlay.Command(dbg ? "off" : _debugDepth ? "depth" : "on"));
                    bool depth = GUILayout.Toggle(_debugDepth, "with depth points", GUILayout.Width(190));
                    if (depth != _debugDepth) { _debugDepth = depth; if (dbg) Act(LinkDebugOverlay.Command(depth ? "depth" : "nodepth")); }
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Mixed reality video", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(!playing))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (!LinkRecorder.Recording)
                    {
                        _videoName = EditorGUILayout.TextField(_videoName);
                        if (GUILayout.Button("Record", GUILayout.Width(80))) Act(LinkRecorder.Start(_videoName));
                    }
                    else if (GUILayout.Button($"Stop recording ({LinkRecorder.Seconds:0} s)")) Act(LinkRecorder.Stop());
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Spectator (window)")) LinkSpectatorWindow.Open();
                    if (GUILayout.Button(LinkStream.Running ? "Stop streaming" : "Stream on local network"))
                        Act(LinkStream.Running ? LinkStream.Command("off") : LinkStream.Start(LinkStream.DefaultPort));
                    if (GUILayout.Button("Open folder", GUILayout.Width(90)))
                    { Directory.CreateDirectory("Recordings"); EditorUtility.RevealInFinder(Path.GetFullPath("Recordings")); }
                }
                if (LinkStream.Running) EditorGUILayout.SelectableLabel(LinkStream.Url + $"   ({LinkStream.Clients} watching)", GUILayout.Height(18));
            }
            EditorGUILayout.LabelField(LinkRecorder.Status(), Mono);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Sessions (record with the headset, replay without it)", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(!playing))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (!LinkSession.Recording)
                    {
                        _sessionName = EditorGUILayout.TextField(_sessionName);
                        using (new EditorGUI.DisabledScope(LinkSession.Replaying))
                            if (GUILayout.Button("Record session", GUILayout.Width(110))) Act(LinkSession.Command("rec " + _sessionName));
                    }
                    else if (GUILayout.Button($"Stop session ({LinkSession.RecordingSeconds:0} s)")) Act(LinkSession.Command("stop"));
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
                        if (!LinkSession.Replaying)
                        {
                            if (GUILayout.Button("Replay", GUILayout.Width(70))) Act(LinkSession.Command("play " + sessions[_sessionIndex] + (_loop ? " loop" : "")));
                        }
                        else if (GUILayout.Button("Stop", GUILayout.Width(70))) Act(LinkSession.Command("stop"));
                    }
                    if (LinkSession.Replaying && LinkSession.Duration > 0)
                    {
                        double t = LinkSession.CurrentTime;
                        double nt = EditorGUILayout.Slider((float)t, 0f, (float)LinkSession.Duration);
                        if (Math.Abs(nt - t) > 0.05) LinkSession.Seek(nt);
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button("◀ frame")) Act(LinkSession.Step(-1));
                            if (GUILayout.Button(LinkSession.Paused ? "▶ resume" : "❚❚ pause")) Act(LinkSession.Pause(!LinkSession.Paused));
                            if (GUILayout.Button("frame ▶")) Act(LinkSession.Step(1));
                            int si = Array.IndexOf(Speeds, LinkSession.Speed);
                            int ns = EditorGUILayout.Popup(si < 0 ? 2 : si, new[] { "0.25×", "0.5×", "1×", "2×" }, GUILayout.Width(60));
                            if (ns != si && ns >= 0) Act(LinkSession.SetSpeed(Speeds[ns]));
                        }
                        EditorGUILayout.LabelField($"{t:0.00} / {LinkSession.Duration:0.0} s · frame {LinkSession.CameraFrameIndex + 1}/{LinkSession.CameraFrameCount}" +
                                                   (LinkSession.TapeActive ? " · recorded tracking (hands, controllers)" : ""), EditorStyles.miniLabel);
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    using (new EditorGUI.DisabledScope(LinkSession.Recording || LinkSessionTests.Running))
                    {
                        if (GUILayout.Button("Record expected (baseline)")) Act(LinkSession.Command("baseline " + sessions[_sessionIndex]));
                        if (GUILayout.Button("Test this one")) Act(LinkSession.Command("test " + sessions[_sessionIndex]));
                        if (GUILayout.Button("Test all")) Act(LinkSession.Command("test all"));
                    }
                    if (LinkSessionTests.LastReport != "-") EditorGUILayout.LabelField(LinkSessionTests.LastReport, Mono);
                }
                EditorGUILayout.LabelField(LinkSession.Status(), Mono);
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
