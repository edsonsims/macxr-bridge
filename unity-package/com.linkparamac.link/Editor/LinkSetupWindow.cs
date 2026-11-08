// SPDX-License-Identifier: MIT
// Link para Mac: setup and diagnostics (Window > Link para Mac > Setup & Diagnostics).
// Checks each piece and offers the button that fixes it: compiled runtime, this Unity using the runtime (without having
// to launch Unity from a script), project XR, app on the Quest, Quest microphone, ffmpeg. "Install everything" runs
// link-para-mac/scripts/install.sh in Terminal (one-step install).
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEditor;
using UnityEditor.XR.Management;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

namespace LinkParaMac.EditorTools
{
    // This Unity uses the Link runtime without being launched from a script: XR_RUNTIME_JSON is set in the process before Play.
    // (Separate class: an EditorWindow's static constructor runs during deserialization, where EditorPrefs is not allowed.)
    [InitializeOnLoad]
    static class LinkRuntimeEnv
    {
        [DllImport("/usr/lib/libSystem.B.dylib")] static extern int setenv(string name, string value, int overwrite);
        public const string AutoEnvKey = "LinkParaMac.AutoRuntimeEnv";

        static LinkRuntimeEnv()
        {
            EditorApplication.delayCall += () =>
            {
                if (!EditorPrefs.GetBool(AutoEnvKey, true)) return;
                string current = Environment.GetEnvironmentVariable("XR_RUNTIME_JSON");
                if (string.IsNullOrEmpty(current) && File.Exists(LinkSetupWindow.RuntimeJson)) Use();
            };
        }

        public static void Use()
        {
            setenv("XR_RUNTIME_JSON", LinkSetupWindow.RuntimeJson, 1);
            Environment.SetEnvironmentVariable("XR_RUNTIME_JSON", LinkSetupWindow.RuntimeJson);
            UnityEngine.Debug.Log("[LinkParaMac] this Unity uses the Link runtime: " + LinkSetupWindow.RuntimeJson);
        }
    }

    public class LinkSetupWindow : EditorWindow
    {
        [MenuItem("Window/Link para Mac/Setup & Diagnostics", priority = 1)]
        public static void Open() => GetWindow<LinkSetupWindow>("Link: Setup");

        static string Root => LinkPanel.LinkRoot;
        public static string RuntimeJson => Path.Combine(Root, "oxrsys", "build", "macos-arm64", "runtime", "oxrsys-runtime.json");
        static string RuntimeDylib => Path.Combine(Root, "oxrsys", "build", "macos-arm64", "runtime", "liboxrsys-runtime.dylib");
        static string Apk => Path.Combine(Root, "oxrsys", "clients", "android-vr", "app", "build", "outputs", "apk", "release", "app-release.apk");
        const string Ffmpeg = "/opt/homebrew/bin/ffmpeg";

        static string _adbState = "(checking…)", _adbAction = "";
        static bool _adbBusy;
        static double _nextAdbCheck;

        static string Adb()
        {
            foreach (var p in new[] { "/opt/homebrew/bin/adb", Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", "Library/Android/sdk/platform-tools/adb") })
                if (File.Exists(p)) return p;
            return null;
        }

        // adb always runs off the main thread (never blocks the Editor).
        static void RunAdb(string args, Action<string> done)
        {
            string adb = Adb();
            if (adb == null) { done("adb not found (install the Android SDK platform-tools)"); return; }
            _adbBusy = true;
            new Thread(() =>
            {
                string output;
                try
                {
                    var p = Process.Start(new ProcessStartInfo(adb, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true });
                    output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } output += " (timed out)"; }
                }
                catch (Exception e) { output = e.Message; }
                _adbBusy = false;
                EditorApplication.delayCall += () => done(output.Trim());
            }) { IsBackground = true }.Start();
        }

        static void CheckQuest()
        {
            if (_adbBusy) return;
            RunAdb("devices", devices =>
            {
                var line = devices.Split('\n').Skip(1).FirstOrDefault(l => l.Trim().Length > 0);
                if (line == null) { _adbState = "no Quest connected by cable"; return; }
                if (line.Contains("unauthorized")) { _adbState = "Quest connected by cable, but USB debugging was not accepted (accept it on the headset with \"Always allow\")"; return; }
                RunAdb("shell pm list packages net.demonixis.oxrsys.android", pkg =>
                    _adbState = pkg.Contains("net.demonixis.oxrsys.android") ? "Quest connected by cable, OXRSys app installed" : "Quest connected by cable, OXRSys app NOT installed");
            });
        }

        static void Terminal(string command)
        {
            string escaped = command.Replace("\\", "\\\\").Replace("\"", "\\\"");
            Process.Start("/usr/bin/osascript", $"-e \"tell application \\\"Terminal\\\" to activate\" -e \"tell application \\\"Terminal\\\" to do script \\\"{escaped}\\\"\"");
        }

        static bool XrConfigured(out string detail)
        {
            detail = "";
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget per) || per == null) { detail = "XR Plug-in Management is not configured"; return false; }
            var gs = per.SettingsForBuildTarget(BuildTargetGroup.Standalone);
            bool ok = gs != null && gs.Manager != null && gs.Manager.activeLoaders.Any(l => l is OpenXRLoader);
            detail = ok ? "OpenXR active on macOS (Standalone)" : "OpenXR is not active on macOS (Standalone)";
            return ok;
        }

        Vector2 _scroll;

        void OnEnable() { _nextAdbCheck = 0; }

        void Update()
        {
            if (EditorApplication.timeSinceStartup < _nextAdbCheck) return;
            _nextAdbCheck = EditorApplication.timeSinceStartup + 5;
            CheckQuest();
            Repaint();
        }

        static void Row(bool ok, string title, string detail, string button = null, Action action = null)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var style = new GUIStyle(EditorStyles.label) { richText = true, wordWrap = true };
                GUILayout.Label(ok ? "<color=#3c3>✔</color>" : "<color=#e44>✖</color>", style, GUILayout.Width(18));
                GUILayout.Label($"<b>{title}</b> — {detail}", style);
                if (button != null && GUILayout.Button(button, GUILayout.Width(170))) action?.Invoke();
            }
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("Link para Mac — setup and diagnostics", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Unity Play Mode on Quest 3 from the Mac (like Quest Link on Windows), with camera, depth, room, " +
                                    "audio, microphone, microgestures, recording, sessions and spectator view.", MessageType.None);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Link para Mac folder", GUILayout.Width(150));
                EditorGUILayout.SelectableLabel(Root, GUILayout.Height(18));
                if (GUILayout.Button("Choose…", GUILayout.Width(90)))
                {
                    string p = EditorUtility.OpenFolderPanel("link-para-mac folder", Root, "");
                    if (!string.IsNullOrEmpty(p)) LinkPanel.LinkRoot = p;
                }
            }
            if (GUILayout.Button("Install everything (Terminal: scripts/install.sh)", GUILayout.Height(28)))
                Terminal($"zsh '{Path.Combine(Root, "scripts", "install.sh")}'");
            EditorGUILayout.Space();

            Row(Directory.Exists(Path.Combine(Root, "scripts")), "Folder", Directory.Exists(Path.Combine(Root, "scripts")) ? "found" : "not found — choose the link-para-mac folder");
            bool runtime = File.Exists(RuntimeDylib) && File.Exists(RuntimeJson);
            Row(runtime, "Mac OpenXR runtime", runtime ? "built" : "not built", "Build (Terminal)", () => Terminal($"zsh '{Path.Combine(Root, "scripts", "10-build-runtime.sh")}'"));
            string env = Environment.GetEnvironmentVariable("XR_RUNTIME_JSON") ?? "";
            bool usesRuntime = env == RuntimeJson || (env.Contains("oxrsys") && File.Exists(env));
            Row(usesRuntime, "This Unity uses the runtime", usesRuntime ? env : "XR_RUNTIME_JSON does not point to Link", "Use in this Unity", () => { LinkRuntimeEnv.Use(); EditorPrefs.SetBool(LinkRuntimeEnv.AutoEnvKey, true); });
            bool xr = XrConfigured(out var xrDetail);
            Row(xr, "Project", xrDetail, "Configure XR", () => UnityEngine.Debug.Log("[LinkSetup] " + LinkBridge.ConfigureStandaloneXR()));
            bool apk = File.Exists(Apk);
            Row(_adbState.Contains("app installed"), "Quest", _adbState + (_adbAction != "" ? " · " + _adbAction : ""),
                apk ? "Install app on Quest" : "Build app (Terminal)", () =>
                {
                    if (!apk) { Terminal($"zsh '{Path.Combine(Root, "scripts", "30-build-quest-client.sh")}'"); return; }
                    _adbAction = "installing…";
                    RunAdb($"install -r \"{Apk}\"", r => { _adbAction = r.Contains("Success") ? "app installed" : r; RunAdb("shell pm grant net.demonixis.oxrsys.android android.permission.RECORD_AUDIO", _ => { }); CheckQuest(); });
                });
            bool mic = Microphone.devices.Contains(LinkMic.DeviceName);
            Row(mic, "Quest Microphone", mic ? "\"" + LinkMic.DeviceName + "\" installed" : "device not installed", "Install (asks for password)",
                () => Terminal($"zsh '{Path.Combine(Root, "scripts", "65-install-mic-driver.sh")}'"));
            bool ffmpeg = File.Exists(Ffmpeg);
            Row(ffmpeg, "ffmpeg", ffmpeg ? "present (recordings)" : "missing — video recording will not work", "brew install ffmpeg", () => Terminal("brew install ffmpeg"));
            EditorGUILayout.Space();
            if (GUILayout.Button("Open the Link panel")) LinkPanel.Open();
            EditorGUILayout.EndScrollView();
        }
    }
}
