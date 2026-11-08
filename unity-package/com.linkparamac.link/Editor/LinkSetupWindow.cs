// SPDX-License-Identifier: MIT
// Link para Mac: instalação e diagnóstico (Window > Link para Mac > Instalação e diagnóstico).
// Confere cada peça e oferece o botão que resolve: runtime compilado, este Unity usando o runtime (sem precisar
// abrir o Unity por script), XR do projeto, app no Quest, microfone do Quest, ffmpeg. "Instalar tudo" roda
// link-para-mac/scripts/install.sh no Terminal (instalação em um passo).
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
    // Este Unity passa a usar o runtime do Link sem ser aberto por script: XR_RUNTIME_JSON no processo antes do Play.
    // (Classe à parte: o construtor estático de uma EditorWindow roda na desserialização, onde EditorPrefs é proibido.)
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
            UnityEngine.Debug.Log("[LinkParaMac] este Unity usa o runtime do Link: " + LinkSetupWindow.RuntimeJson);
        }
    }

    public class LinkSetupWindow : EditorWindow
    {
        [MenuItem("Window/Link para Mac/Instalação e diagnóstico", priority = 1)]
        public static void Open() => GetWindow<LinkSetupWindow>("Link: instalação");

        static string Root => LinkPanel.LinkRoot;
        public static string RuntimeJson => Path.Combine(Root, "oxrsys", "build", "macos-arm64", "runtime", "oxrsys-runtime.json");
        static string RuntimeDylib => Path.Combine(Root, "oxrsys", "build", "macos-arm64", "runtime", "liboxrsys-runtime.dylib");
        static string Apk => Path.Combine(Root, "oxrsys", "clients", "android-vr", "app", "build", "outputs", "apk", "release", "app-release.apk");
        const string Ffmpeg = "/opt/homebrew/bin/ffmpeg";

        static string _adbState = "(conferindo…)", _adbAction = "";
        static bool _adbBusy;
        static double _nextAdbCheck;

        static string Adb()
        {
            foreach (var p in new[] { "/opt/homebrew/bin/adb", Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", "Library/Android/sdk/platform-tools/adb") })
                if (File.Exists(p)) return p;
            return null;
        }

        // adb sempre fora da thread principal (nunca trava o Editor).
        static void RunAdb(string args, Action<string> done)
        {
            string adb = Adb();
            if (adb == null) { done("adb não encontrado (instale o Android SDK platform-tools)"); return; }
            _adbBusy = true;
            new Thread(() =>
            {
                string output;
                try
                {
                    var p = Process.Start(new ProcessStartInfo(adb, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true });
                    output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } output += " (tempo esgotado)"; }
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
                if (line == null) { _adbState = "nenhum Quest no cabo"; return; }
                if (line.Contains("unauthorized")) { _adbState = "Quest no cabo, mas a depuração USB não foi aceita (aceite no headset com \"Sempre permitir\")"; return; }
                RunAdb("shell pm list packages net.demonixis.oxrsys.android", pkg =>
                    _adbState = pkg.Contains("net.demonixis.oxrsys.android") ? "Quest no cabo, app OXRSys instalado" : "Quest no cabo, app OXRSys NÃO instalado");
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
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget per) || per == null) { detail = "XR Plug-in Management sem configuração"; return false; }
            var gs = per.SettingsForBuildTarget(BuildTargetGroup.Standalone);
            bool ok = gs != null && gs.Manager != null && gs.Manager.activeLoaders.Any(l => l is OpenXRLoader);
            detail = ok ? "OpenXR ativo no macOS (Standalone)" : "OpenXR não está ativo no macOS (Standalone)";
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
            EditorGUILayout.LabelField("Link para Mac — instalação e diagnóstico", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Unity Play Mode no Quest 3 a partir do Mac (como o Quest Link do Windows), com câmera, profundidade, sala, " +
                                    "áudio, microfone, microgestos, gravação, sessões e espectador.", MessageType.None);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Pasta do Link para Mac", GUILayout.Width(150));
                EditorGUILayout.SelectableLabel(Root, GUILayout.Height(18));
                if (GUILayout.Button("Escolher…", GUILayout.Width(90)))
                {
                    string p = EditorUtility.OpenFolderPanel("Pasta link-para-mac", Root, "");
                    if (!string.IsNullOrEmpty(p)) LinkPanel.LinkRoot = p;
                }
            }
            if (GUILayout.Button("Instalar tudo (Terminal: scripts/install.sh)", GUILayout.Height(28)))
                Terminal($"zsh '{Path.Combine(Root, "scripts", "install.sh")}'");
            EditorGUILayout.Space();

            Row(Directory.Exists(Path.Combine(Root, "scripts")), "Pasta", Directory.Exists(Path.Combine(Root, "scripts")) ? "encontrada" : "não encontrada — escolha a pasta link-para-mac");
            bool runtime = File.Exists(RuntimeDylib) && File.Exists(RuntimeJson);
            Row(runtime, "Runtime OpenXR do Mac", runtime ? "compilado" : "não compilado", "Compilar (Terminal)", () => Terminal($"zsh '{Path.Combine(Root, "scripts", "10-build-runtime.sh")}'"));
            string env = Environment.GetEnvironmentVariable("XR_RUNTIME_JSON") ?? "";
            bool usesRuntime = env == RuntimeJson || (env.Contains("oxrsys") && File.Exists(env));
            Row(usesRuntime, "Este Unity usa o runtime", usesRuntime ? env : "XR_RUNTIME_JSON não aponta pro Link", "Usar neste Unity", () => { LinkRuntimeEnv.Use(); EditorPrefs.SetBool(LinkRuntimeEnv.AutoEnvKey, true); });
            bool xr = XrConfigured(out var xrDetail);
            Row(xr, "Projeto", xrDetail, "Configurar XR", () => UnityEngine.Debug.Log("[LinkSetup] " + LinkBridge.ConfigureStandaloneXR()));
            bool apk = File.Exists(Apk);
            Row(_adbState.Contains("instalado") && !_adbState.Contains("NÃO"), "Quest", _adbState + (_adbAction != "" ? " · " + _adbAction : ""),
                apk ? "Instalar app no Quest" : "Compilar app (Terminal)", () =>
                {
                    if (!apk) { Terminal($"zsh '{Path.Combine(Root, "scripts", "30-build-quest-client.sh")}'"); return; }
                    _adbAction = "instalando…";
                    RunAdb($"install -r \"{Apk}\"", r => { _adbAction = r.Contains("Success") ? "app instalado" : r; RunAdb("shell pm grant net.demonixis.oxrsys.android android.permission.RECORD_AUDIO", _ => { }); CheckQuest(); });
                });
            bool mic = Microphone.devices.Contains(LinkMic.DeviceName);
            Row(mic, "Microfone do Quest", mic ? "\"" + LinkMic.DeviceName + "\" instalado" : "dispositivo não instalado", "Instalar (pede a senha)",
                () => Terminal($"zsh '{Path.Combine(Root, "scripts", "65-install-mic-driver.sh")}'"));
            bool ffmpeg = File.Exists(Ffmpeg);
            Row(ffmpeg, "ffmpeg", ffmpeg ? "presente (gravações)" : "ausente — gravação de vídeo não funciona", "brew install ffmpeg", () => Terminal("brew install ffmpeg"));
            EditorGUILayout.Space();
            if (GUILayout.Button("Abrir o painel do Link")) LinkPanel.Open();
            EditorGUILayout.EndScrollView();
        }
    }
}
