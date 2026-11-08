// SPDX-License-Identifier: MIT
// Link para Mac: painel do Unity (Window > Link para Mac > Painel). Tudo que os comandos da ponte fazem, com botões:
// estado do stream e do headset, microfone, sala, depuração, HUD, espectador, gravação de vídeo, sessões (gravar,
// repetir com linha do tempo, testes).
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
        [MenuItem("Window/Link para Mac/Painel", priority = 0)]
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
            if (!File.Exists(path)) { UnityEngine.Debug.LogWarning("[LinkPanel] script não encontrado: " + path); return; }
            // Em segundo plano (adb nunca pode travar o Editor).
            Process.Start(new ProcessStartInfo("/bin/zsh", $"\"{path}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = false });
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            bool playing = EditorApplication.isPlaying;

            EditorGUILayout.LabelField("Estado", EditorStyles.boldLabel);
            if (!playing)
            {
                EditorGUILayout.HelpBox("Entre em Play com o Quest ligado no cabo (ou no mesmo Wi-Fi) e o app OXRSys aberto no headset.", MessageType.Info);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Play")) EditorApplication.EnterPlaymode();
                    if (GUILayout.Button("Instalação e diagnóstico")) LinkSetupWindow.Open();
                }
            }
            else
            {
                LinkHud.Refresh();
                EditorGUILayout.LabelField(LinkHud.Text, Mono);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Reconectar o Quest")) { RunScript("80-reconnect-quest.sh"); Act("reconectando o Quest (alguns segundos)…"); }
                    if (GUILayout.Button(LinkHud.Visible ? "HUD no headset: ligado" : "HUD no headset: desligado")) Act(LinkHud.Command(LinkHud.Visible ? "off" : "on"));
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Microfone do Quest", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(LinkMic.Status(), Mono);
            using (new EditorGUI.DisabledScope(!playing))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Testar (fale no headset 3 s)")) Act(LinkMic.Command("test"));
                if (GUILayout.Button("Entrada padrão = Quest")) Act(LinkMic.Command("default on"));
                if (GUILayout.Button("Voltar a anterior")) Act(LinkMic.Command("default off"));
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Mundo real", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(!playing))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Carregar a sala (MRUK)")) Act(RoomTestTools.Load(""));
                    if (GUILayout.Button("Bipe de teste")) Act(LinkParaMac.LinkAudioTap.Beep());
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool dbg = LinkDebugOverlay.Enabled;
                    if (GUILayout.Button(dbg ? "Depuração no headset: ligada" : "Depuração no headset: desligada"))
                        Act(LinkDebugOverlay.Command(dbg ? "off" : _debugDepth ? "depth" : "on"));
                    bool depth = GUILayout.Toggle(_debugDepth, "com pontos da profundidade", GUILayout.Width(190));
                    if (depth != _debugDepth) { _debugDepth = depth; if (dbg) Act(LinkDebugOverlay.Command(depth ? "depth" : "nodepth")); }
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Vídeo de realidade mista", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(!playing))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (!LinkRecorder.Recording)
                    {
                        _videoName = EditorGUILayout.TextField(_videoName);
                        if (GUILayout.Button("Gravar", GUILayout.Width(80))) Act(LinkRecorder.Start(_videoName));
                    }
                    else if (GUILayout.Button($"Parar a gravação ({LinkRecorder.Seconds:0} s)")) Act(LinkRecorder.Stop());
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Espectador (janela)")) LinkSpectatorWindow.Open();
                    if (GUILayout.Button(LinkStream.Running ? "Parar a transmissão" : "Transmitir na rede local"))
                        Act(LinkStream.Running ? LinkStream.Command("off") : LinkStream.Start(LinkStream.DefaultPort));
                    if (GUILayout.Button("Abrir pasta", GUILayout.Width(90)))
                    { Directory.CreateDirectory("Recordings"); EditorUtility.RevealInFinder(Path.GetFullPath("Recordings")); }
                }
                if (LinkStream.Running) EditorGUILayout.SelectableLabel(LinkStream.Url + $"   ({LinkStream.Clients} assistindo)", GUILayout.Height(18));
            }
            EditorGUILayout.LabelField(LinkRecorder.Status(), Mono);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Sessões (gravar com o headset, repetir sem ele)", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(!playing))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (!LinkSession.Recording)
                    {
                        _sessionName = EditorGUILayout.TextField(_sessionName);
                        using (new EditorGUI.DisabledScope(LinkSession.Replaying))
                            if (GUILayout.Button("Gravar sessão", GUILayout.Width(110))) Act(LinkSession.Command("rec " + _sessionName));
                    }
                    else if (GUILayout.Button($"Parar a sessão ({LinkSession.RecordingSeconds:0} s)")) Act(LinkSession.Command("stop"));
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
                            if (GUILayout.Button("Repetir", GUILayout.Width(70))) Act(LinkSession.Command("play " + sessions[_sessionIndex] + (_loop ? " loop" : "")));
                        }
                        else if (GUILayout.Button("Parar", GUILayout.Width(70))) Act(LinkSession.Command("stop"));
                    }
                    if (LinkSession.Replaying && LinkSession.Duration > 0)
                    {
                        double t = LinkSession.CurrentTime;
                        double nt = EditorGUILayout.Slider((float)t, 0f, (float)LinkSession.Duration);
                        if (Math.Abs(nt - t) > 0.05) LinkSession.Seek(nt);
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button("◀ quadro")) Act(LinkSession.Step(-1));
                            if (GUILayout.Button(LinkSession.Paused ? "▶ continuar" : "❚❚ pausar")) Act(LinkSession.Pause(!LinkSession.Paused));
                            if (GUILayout.Button("quadro ▶")) Act(LinkSession.Step(1));
                            int si = Array.IndexOf(Speeds, LinkSession.Speed);
                            int ns = EditorGUILayout.Popup(si < 0 ? 2 : si, new[] { "0,25×", "0,5×", "1×", "2×" }, GUILayout.Width(60));
                            if (ns != si && ns >= 0) Act(LinkSession.SetSpeed(Speeds[ns]));
                        }
                        EditorGUILayout.LabelField($"{t:0.00} / {LinkSession.Duration:0.0} s · quadro {LinkSession.CameraFrameIndex + 1}/{LinkSession.CameraFrameCount}" +
                                                   (LinkSession.TapeActive ? " · tracking gravado (mãos, controles)" : ""), EditorStyles.miniLabel);
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    using (new EditorGUI.DisabledScope(LinkSession.Recording || LinkSessionTests.Running))
                    {
                        if (GUILayout.Button("Gravar esperado (baseline)")) Act(LinkSession.Command("baseline " + sessions[_sessionIndex]));
                        if (GUILayout.Button("Testar esta")) Act(LinkSession.Command("test " + sessions[_sessionIndex]));
                        if (GUILayout.Button("Testar todas")) Act(LinkSession.Command("test all"));
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
