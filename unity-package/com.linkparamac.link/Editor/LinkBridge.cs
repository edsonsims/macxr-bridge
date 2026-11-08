// Link para Mac — ponte de comandos para o Unity Editor.
// Lê arquivos .cmd em <projeto>/.link/cmd/, executa no Editor e grava a resposta em <projeto>/.link/out/<nome>.out.
// Espelha o Console em <projeto>/.link/unity.log e o estado em <projeto>/.link/status.json.
// Comandos: status | play | stop | refresh | menu <Caminho/Do/Menu> | scene <Assets/...unity> | runtime <manifest.json>
//           | env <VAR> | xr-standalone | hierarchy | screenshot <arquivo.png> | sh <comando zsh> | shbg <comando zsh> | clearlog
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;

namespace LinkParaMac.EditorTools
{
    [InitializeOnLoad]
    public static class LinkBridge
    {
        static readonly string ProjectDir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        static readonly string Root = Path.Combine(ProjectDir, ".link");
        static readonly string CmdDir = Path.Combine(Root, "cmd");
        static readonly string OutDir = Path.Combine(Root, "out");
        static readonly string LogFile = Path.Combine(Root, "unity.log");
        static readonly string StatusFile = Path.Combine(Root, "status.json");
        static readonly object Gate = new object();
        static double _nextTick;

        static LinkBridge()
        {
            Directory.CreateDirectory(CmdDir);
            Directory.CreateDirectory(OutDir);
            Application.logMessageReceivedThreaded += OnLog;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += s => { Append($"[LinkBridge] playMode: {s}"); WriteStatus(); };
            Append($"[LinkBridge] carregado {DateTime.Now:HH:mm:ss} — Unity {Application.unityVersion}");
            // Recompilar scripts NO MEIO do Play recarrega o domínio com o OpenXR/OVRPlugin vivo e derruba o Unity
            // (crash em xrPollEvent → OVRPlugin, 8/nov). Com o Link: recompila só ao sair do Play.
            // (Preferences > General > Script Changes While Playing; 0 = Recompile And Continue Playing)
            if (EditorPrefs.GetInt("ScriptCompilationDuringPlay", 0) == 0)
            {
                EditorPrefs.SetInt("ScriptCompilationDuringPlay", 1); // Recompile After Finished Playing
                Append("[LinkBridge] 'Script Changes While Playing' → Recompile After Finished Playing (evita crash do OVRPlugin)");
            }
            WriteStatus();
        }

        static void OnLog(string msg, string stack, LogType type)
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{type}] {msg}";
            if (type == LogType.Error || type == LogType.Exception) line += "\n" + stack;
            Append(line);
        }

        static void Append(string line)
        {
            lock (Gate) { try { File.AppendAllText(LogFile, line + "\n"); } catch { } }
        }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup < _nextTick) return;
            _nextTick = EditorApplication.timeSinceStartup + 0.5;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            string[] files;
            try { files = Directory.GetFiles(CmdDir, "*.cmd"); } catch { return; }
            if (files.Length == 0) return;
            Array.Sort(files, StringComparer.Ordinal);
            var file = files[0];
            var name = Path.GetFileNameWithoutExtension(file);
            string text;
            try { text = File.ReadAllText(file).Trim(); File.Delete(file); }
            catch (Exception e) { Append("[LinkBridge] não li o comando: " + e.Message); return; }
            string result;
            try { result = Run(text); } catch (Exception e) { result = "ERRO: " + e; }
            File.WriteAllText(Path.Combine(OutDir, name + ".out"), result ?? "");
            Append($"[LinkBridge] {name}: {text} → {(result ?? "").Split('\n')[0]}");
            WriteStatus();
        }

        static string Run(string text)
        {
            var sp = text.IndexOf(' ');
            var cmd = sp < 0 ? text : text.Substring(0, sp);
            var arg = sp < 0 ? "" : text.Substring(sp + 1).Trim();
            switch (cmd)
            {
                case "status": WriteStatus(); return File.ReadAllText(StatusFile);
                case "play": EditorApplication.EnterPlaymode(); return "entrando em Play";
                case "stop": EditorApplication.ExitPlaymode(); return "saindo de Play";
                case "refresh": AssetDatabase.Refresh(); return "refresh pedido";
                case "menu": return EditorApplication.ExecuteMenuItem(arg) ? "menu ok: " + arg : "menu não encontrado: " + arg;
                case "scene": return "cena aberta: " + EditorSceneManager.OpenScene(arg).name;
                case "runtime":
                    Environment.SetEnvironmentVariable("XR_RUNTIME_JSON", arg);
                    return "XR_RUNTIME_JSON=" + arg + (File.Exists(arg) ? "" : "  (AVISO: arquivo não existe)");
                case "env": return Environment.GetEnvironmentVariable(arg) ?? "(vazio)";
                case "xr-standalone": return ConfigureStandaloneXR();
                case "hierarchy": return Hierarchy();
                case "pos": return Pos(arg);
                case "find": return Find(arg);
                case "dump": return Dump(arg);
                case "props": return Props(arg);
                case "enable": return Enable(arg);
                case "hands": return Hands();
                case "anchor": return AnchorTestTools.Create(arg);
                case "anchors": return AnchorTestTools.List();
                case "anchor-save": return AnchorTestTools.Save(arg);
                case "anchor-load": return AnchorTestTools.Load(arg);
                case "anchor-erase": return AnchorTestTools.Erase(arg);
                case "room-load": return RoomTestTools.Load(arg);
                case "room": return RoomTestTools.Describe();
                case "room-mesh": return RoomTestTools.Mesh();
                case "room-draw": return RoomTestTools.Draw();
                case "room-capture": return RoomTestTools.Capture();
                case "room-fetch": return RoomTestTools.Fetch(arg);
                case "camera": return LinkCameraSource.Capture(arg);
                case "camera-show": return LinkCameraFeed.Configure(arg);
                case "detect-status": return LinkDetectionDiag.Status();
                case "yolo-test": return LinkDetectionDiag.YoloTest(arg);
                case "agent-test": return LinkDetectionDiag.AgentTest();
                case "depth": return LinkDepth.Capture();
                case "micro": return LinkMicrogestures.Status();
                case "debug": return LinkDebugOverlay.Command(arg);
                case "record": return LinkRecorder.Command(arg);
                case "session": return LinkSession.Command(arg);
                case "mic": return LinkMic.Command(arg);
                case "stream": return LinkStream.Command(arg);
                case "hud": return LinkHud.Command(arg);
                case "spectator": LinkSpectatorWindow.Open(); return "janela do espectador aberta";
                case "audio":
                    if (arg == "beep") return LinkParaMac.LinkAudioTap.Beep();
                    if (arg == "mac on" || arg == "mac off")
                    {
                        UnityEditor.EditorPrefs.SetBool("LinkParaMac.AudioToMac", arg == "mac on");
                        LinkParaMac.LinkAudioTap.AlsoPlayOnMac = arg == "mac on";
                    }
                    return LinkParaMac.LinkAudioTap.Status();
                case "winshot": return WindowShot(arg);
                case "screenshot": ScreenCapture.CaptureScreenshot(arg); return "screenshot pedido: " + arg + " (só funciona em Play)";
                case "sh": return Shell(arg, false);
                case "shbg": return Shell(arg, true);
                case "clearlog": File.WriteAllText(LogFile, ""); return "log limpo";
                default: return "comando desconhecido: " + cmd;
            }
        }

        // ---- XR no Standalone (Editor do Mac): OpenXR loader + Meta XR Feature + perfis de controle/mãos ----
        // Foto de uma janela do Editor (pelo título) em .link/winshot.png — lê os pixels da tela onde ela está.
        static string WindowShot(string title)
        {
            var win = Resources.FindObjectsOfTypeAll<EditorWindow>().FirstOrDefault(w => w.titleContent.text == title);
            if (win == null) return "janela não encontrada: " + title + " (abertas: " + string.Join(", ", Resources.FindObjectsOfTypeAll<EditorWindow>().Select(w => w.titleContent.text)) + ")";
            win.Focus();
            win.Repaint();
            Rect r = win.position;
            var pixels = UnityEditorInternal.InternalEditorUtility.ReadScreenPixel(r.position, (int)r.width, (int)r.height);
            var tex = new Texture2D((int)r.width, (int)r.height, TextureFormat.RGBA32, false);
            tex.SetPixels(pixels);
            tex.Apply();
            string path = Path.Combine(Directory.GetCurrentDirectory(), ".link", "winshot.png");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            return $"{title} {r} → {path}";
        }

        internal static string ConfigureStandaloneXR()
        {
            var sb = new StringBuilder();
            var tg = BuildTargetGroup.Standalone;
            XRGeneralSettingsPerBuildTarget per;
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out per) || per == null)
                return "ERRO: XRGeneralSettingsPerBuildTarget não encontrado (abra Project Settings > XR Plug-in Management uma vez)";

            if (!per.HasSettingsForBuildTarget(tg))
            {
                Invoke(per, "CreateDefaultSettingsForBuildTarget", tg);
                sb.AppendLine("criado XRGeneralSettings para Standalone");
            }
            var gs = per.SettingsForBuildTarget(tg);
            if (gs == null) return sb + "ERRO: não consegui criar as settings do Standalone";
            if (gs.Manager == null)
            {
                Invoke(per, "CreateDefaultManagerSettingsForBuildTarget", tg);
                gs = per.SettingsForBuildTarget(tg);
            }
            if (gs.Manager == null) return sb + "ERRO: sem XRManagerSettings para Standalone";
            gs.InitManagerOnStart = true;

            var loaderType = typeof(OpenXRLoader).FullName;
            if (gs.Manager.activeLoaders.Any(l => l != null && l.GetType().FullName == loaderType))
                sb.AppendLine("OpenXRLoader já ativo no Standalone");
            else
                sb.AppendLine(XRPackageMetadataStore.AssignLoader(gs.Manager, loaderType, tg)
                    ? "OpenXRLoader atribuído ao Standalone" : "FALHA ao atribuir OpenXRLoader");
            EditorUtility.SetDirty(gs); EditorUtility.SetDirty(gs.Manager); EditorUtility.SetDirty(per);

            var oxr = OpenXRSettings.GetSettingsForBuildTargetGroup(tg);
            if (oxr == null) sb.AppendLine("AVISO: OpenXRSettings do Standalone não encontrado");
            else
            {
                var want = new HashSet<string> {
                    "com.meta.openxr.feature.metaxr",                    // Meta XR Feature (OVRPlugin via OpenXR)
                    "com.unity.openxr.feature.input.oculustouch",        // Oculus Touch
                    "com.unity.openxr.feature.input.metaquestplus",      // Touch Plus (Quest 3)
                    "com.unity.openxr.feature.input.handinteraction",    // Hand Interaction Profile
                };
                foreach (var f in oxr.GetFeatures())
                {
                    if (f == null) continue;
                    var so = new SerializedObject(f);
                    var id = so.FindProperty("featureIdInternal")?.stringValue ?? f.GetType().Name;
                    var ui = so.FindProperty("nameUi")?.stringValue ?? f.GetType().Name;
                    if (!want.Contains(id)) continue;
                    if (f.enabled) { sb.AppendLine("feature já ligada: " + ui); continue; }
                    f.enabled = true; EditorUtility.SetDirty(f);
                    sb.AppendLine("feature ligada: " + ui + " (" + id + ")");
                }
                EditorUtility.SetDirty(oxr);
            }
            PlayerSettings.runInBackground = true;
            AssetDatabase.SaveAssets();
            sb.AppendLine("runInBackground = true; assets salvos");
            return sb.ToString();
        }

        static void Invoke(object target, string method, params object[] args)
        {
            var m = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null) throw new MissingMethodException(target.GetType().Name, method);
            m.Invoke(target, args);
        }

        // ---- estado ----
        static void WriteStatus()
        {
            var sb = new StringBuilder("{\n");
            void Kv(string k, object v, bool last = false)
            {
                string s = v == null ? "null" : v is bool b ? (b ? "true" : "false") : "\"" + v.ToString().Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ") + "\"";
                sb.Append("  \"").Append(k).Append("\": ").Append(s).Append(last ? "\n" : ",\n");
            }
            Kv("time", DateTime.Now.ToString("HH:mm:ss"));
            Kv("unity", Application.unityVersion);
            Kv("isPlaying", EditorApplication.isPlaying);
            Kv("isPaused", EditorApplication.isPaused);
            Kv("isCompiling", EditorApplication.isCompiling);
            Kv("activeBuildTarget", EditorUserBuildSettings.activeBuildTarget.ToString());
            Kv("scene", SceneManager.GetActiveScene().path);
            Kv("XR_RUNTIME_JSON", Environment.GetEnvironmentVariable("XR_RUNTIME_JSON"));
            string loader = null; try { loader = XRGeneralSettings.Instance?.Manager?.activeLoader?.name; } catch { }
            Kv("xrActiveLoader", loader);
            Kv("xrDeviceActive", XRSettings.isDeviceActive);
            Kv("xrLoadedDevice", XRSettings.loadedDeviceName);
            string rt = null, rtv = null; try { rt = OpenXRRuntime.name; rtv = OpenXRRuntime.version; } catch { }
            Kv("openxrRuntime", rt); Kv("openxrRuntimeVersion", rtv);
            Kv("ovrPluginVersion", Ovr("version"));
            var init = Ovr("initialized");
            Kv("ovrPluginInitialized", init);
            // Só consulta mãos/passthrough com o OVRPlugin inicializado (senão ele loga erro no Console).
            Kv("ovrHandTrackingEnabled", init == "True" ? Ovr("GetHandTrackingEnabled") : "(não inicializado)");
            Kv("ovrPassthroughSupported", init == "True" ? Ovr("IsInsightPassthroughSupported") : "(não inicializado)", true);
            sb.Append("}\n");
            try { File.WriteAllText(StatusFile, sb.ToString()); } catch { }
        }

        static string Ovr(string member)
        {
            try
            {
                var t = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("OVRPlugin")).FirstOrDefault(x => x != null);
                if (t == null) return "(OVRPlugin não carregado)";
                var p = t.GetProperty(member, BindingFlags.Static | BindingFlags.Public);
                if (p != null) return p.GetValue(null)?.ToString();
                var m = t.GetMethod(member, BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (m != null) return m.Invoke(null, null)?.ToString();
                return "(sem " + member + ")";
            }
            catch (Exception e) { return "erro: " + e.GetBaseException().Message; }
        }

        // Posição/rotação no mundo de um GameObject pelo nome (ou caminho "Pai/Filho").
        static string Pos(string name)
        {
            var go = GameObject.Find(name);
            if (go == null) return "não achei: " + name;
            var t = go.transform;
            return $"{name}: pos=({t.position.x:F3}, {t.position.y:F3}, {t.position.z:F3}) euler=({t.eulerAngles.x:F1}, {t.eulerAngles.y:F1}, {t.eulerAngles.z:F1}) active={go.activeInHierarchy}";
        }

        // dump <caminho/do/GameObject> — campos serializados (Inspector) de cada componente, via SerializedObject.
        static string Dump(string path)
        {
            var go = GameObject.Find(path) ?? Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(g => g.scene.IsValid() && (g.name == path || FullPath(g.transform) == path));
            if (go == null) return "não achei: " + path;
            var sb = new StringBuilder(FullPath(go.transform) + (go.activeInHierarchy ? "" : " (off)") + "\n");
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null || c is Transform) continue;
                sb.AppendLine("## " + c.GetType().Name + (c is Behaviour b && !b.enabled ? " (disabled)" : ""));
                var so = new SerializedObject(c);
                var it = so.GetIterator();
                bool enterChildren = true;
                while (it.NextVisible(enterChildren))
                {
                    enterChildren = false;
                    if (it.name == "m_Script") continue;
                    string v;
                    switch (it.propertyType)
                    {
                        case SerializedPropertyType.ObjectReference:
                            v = it.objectReferenceValue == null ? "null" : it.objectReferenceValue is Component rc ? FullPath(rc.transform) + " <" + rc.GetType().Name + ">" : it.objectReferenceValue.name + " <" + it.objectReferenceValue.GetType().Name + ">"; break;
                        case SerializedPropertyType.Boolean: v = it.boolValue.ToString(); break;
                        case SerializedPropertyType.Float: v = it.floatValue.ToString("F3"); break;
                        case SerializedPropertyType.Integer: v = it.intValue.ToString(); break;
                        case SerializedPropertyType.Enum: v = it.enumValueIndex >= 0 && it.enumValueIndex < it.enumDisplayNames.Length ? it.enumDisplayNames[it.enumValueIndex] : it.enumValueIndex.ToString(); break;
                        case SerializedPropertyType.String: v = it.stringValue; break;
                        case SerializedPropertyType.Color: v = it.colorValue.ToString(); break;
                        case SerializedPropertyType.Vector3: v = it.vector3Value.ToString("F3"); break;
                        case SerializedPropertyType.Generic:
                            if (it.isArray) { v = "[" + it.arraySize + "]"; for (int i = 0; i < Math.Min(it.arraySize, 8); i++) { var e = it.GetArrayElementAtIndex(i); v += " " + (e.propertyType == SerializedPropertyType.ObjectReference ? (e.objectReferenceValue == null ? "null" : e.objectReferenceValue is Component ec ? FullPath(ec.transform) + " <" + ec.GetType().Name + ">" : e.objectReferenceValue.name) : e.propertyType.ToString()); } }
                            else v = "{...}";
                            break;
                        default: v = it.propertyType.ToString(); break;
                    }
                    sb.AppendLine("  " + it.name + " = " + v);
                }
            }
            return sb.ToString();
        }

        static string FullPath(Transform t) { var p = t.name; for (var x = t.parent; x != null; x = x.parent) p = x.name + "/" + p; return p; }

        // find <trecho do nome> — lista objetos da cena (qualquer profundidade) com esse trecho no nome e seus componentes.
        static string Find(string needle)
        {
            var sb = new StringBuilder();
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (!go.scene.IsValid() || go.hideFlags != HideFlags.None) continue;
                if (go.name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var path = go.name; for (var t = go.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
                var comps = go.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name);
                sb.AppendLine((go.activeInHierarchy ? "" : "(off) ") + path + "  [" + string.Join(", ", comps) + "]");
            }
            return sb.Length == 0 ? "nada com '" + needle + "'" : sb.ToString();
        }

        // props <GameObject> <TipoDoComponente> [prop1,prop2,...] — lê propriedades/campos por reflexão.
        static string Props(string arg)
        {
            var parts = arg.Contains("|") ? arg.Split('|') : arg.Split(' ');
            if (parts.Length < 2) return "uso: props <GameObject> <Tipo> [p1,p2]  (use | como separador se o nome tiver espaço)";
            var go = GameObject.Find(parts[0]);
            if (go == null) return "não achei: " + parts[0];
            var comp = go.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().Name == parts[1]);
            if (comp == null) return "sem componente " + parts[1] + " em " + parts[0] + " (tem: " + string.Join(", ", go.GetComponents<Component>().Select(c => c.GetType().Name)) + ")";
            var t = comp.GetType();
            var names = parts.Length >= 3 ? parts[2].Split(',') : t.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.GetIndexParameters().Length == 0).Select(p => p.Name).ToArray();
            var sb = new StringBuilder($"{parts[0]}.{parts[1]}:\n");
            foreach (var n in names)
            {
                try
                {
                    var pi = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var fi = pi == null ? t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
                    object v = pi != null ? pi.GetValue(comp) : fi != null ? fi.GetValue(comp) : "(não existe)";
                    sb.AppendLine($"  {n} = {v}");
                }
                catch (Exception e) { sb.AppendLine($"  {n} = erro: {e.GetBaseException().Message}"); }
            }
            return sb.ToString();
        }

        // enable <GameObject> <Tipo|self> <true|false> — liga/desliga um componente (Behaviour) ou o GameObject.
        static string Enable(string arg)
        {
            var parts = arg.Contains("|") ? arg.Split('|') : arg.Split(' ');
            if (parts.Length != 3) return "uso: enable <GameObject> <Tipo|self> <true|false>  (use | como separador se o nome tiver espaço)";
            var go = GameObject.Find(parts[0]) ?? Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(g => g.name == parts[0] && g.scene.IsValid());
            if (go == null) return "não achei: " + parts[0];
            bool on = parts[2] == "true";
            if (parts[1] == "self") { go.SetActive(on); return $"{parts[0]} active={on}"; }
            var b = go.GetComponents<Behaviour>().FirstOrDefault(c => c != null && c.GetType().Name == parts[1]);
            if (b == null) return "sem Behaviour " + parts[1];
            b.enabled = on;
            return $"{parts[0]}.{parts[1]}.enabled={on}";
        }

        // Estado das mãos (OVRHand) via reflexão, para não depender do assembly da Meta em tempo de compilação.
        static string Hands()
        {
            var sb = new StringBuilder();
            var ovrHandType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("OVRHand")).FirstOrDefault(x => x != null);
            if (ovrHandType == null) return "OVRHand não carregado";
            var hands = UnityEngine.Object.FindObjectsByType(ovrHandType, FindObjectsSortMode.None);
            sb.AppendLine($"OVRHand na cena: {hands.Length}");
            foreach (var h in hands)
            {
                var c = (Component)h;
                string Get(string prop) { try { return ovrHandType.GetProperty(prop)?.GetValue(h)?.ToString() ?? "?"; } catch (Exception e) { return "erro:" + e.GetBaseException().Message; } }
                sb.AppendLine($"  {c.gameObject.name}: IsTracked={Get("IsTracked")} IsDataValid={Get("IsDataValid")} IsDataHighConfidence={Get("IsDataHighConfidence")} HandConfidence={Get("HandConfidence")} IsPointerPoseValid={Get("IsPointerPoseValid")} pos=({c.transform.position.x:F2},{c.transform.position.y:F2},{c.transform.position.z:F2})");
            }
            sb.AppendLine("OVRPlugin.GetHandTrackingEnabled=" + Ovr("GetHandTrackingEnabled"));
            var cam = Camera.main;
            if (cam != null) sb.AppendLine($"Camera.main '{cam.name}' pos=({cam.transform.position.x:F3},{cam.transform.position.y:F3},{cam.transform.position.z:F3})");
            return sb.ToString();
        }

        static string Hierarchy()
        {
            var sb = new StringBuilder();
            var scene = SceneManager.GetActiveScene();
            sb.AppendLine("cena: " + scene.name + (EditorApplication.isPlaying ? " (Play)" : ""));
            foreach (var go in scene.GetRootGameObjects()) Dump(go.transform, 0, sb);
            return sb.ToString();
        }

        static void Dump(Transform t, int depth, StringBuilder sb)
        {
            if (depth > 3) return;
            var comps = t.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name);
            sb.Append(new string(' ', depth * 2)).Append(t.gameObject.activeInHierarchy ? "" : "(off) ").Append(t.name)
              .Append("  [").Append(string.Join(", ", comps)).AppendLine("]");
            for (int i = 0; i < t.childCount; i++) Dump(t.GetChild(i), depth + 1, sb);
        }

        // ---- shell (zsh de login, mesmo PATH do Terminal) ----
        static string Shell(string command, bool background)
        {
            var id = DateTime.Now.ToString("HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var script = Path.Combine(Root, "sh_" + id + ".zsh");
            var outFile = Path.Combine(OutDir, "sh_" + id + ".log");
            File.WriteAllText(script, (background ? $"exec > \"{outFile}\" 2>&1\n" : "") + command + "\n");
            var psi = new ProcessStartInfo("/bin/zsh", "-l \"" + script + "\"")
            {
                RedirectStandardOutput = !background, RedirectStandardError = !background,
                StandardOutputEncoding = background ? null : Encoding.UTF8, StandardErrorEncoding = background ? null : Encoding.UTF8,
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = ProjectDir,
            };
            var p = Process.Start(psi);
            if (background) return "rodando em segundo plano; saída em " + outFile;
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } return "TIMEOUT 120 s\n" + so.Result + se.Result; }
            try { File.Delete(script); } catch { }
            return $"exit={p.ExitCode}\n{so.Result}" + (se.Result.Length > 0 ? "\n[stderr]\n" + se.Result : "");
        }
    }
}
