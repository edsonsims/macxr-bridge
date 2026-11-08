// SPDX-License-Identifier: MIT
// Link para Mac: diagnóstico da detecção no Editor. Engancha a saída do YOLO (ObjectDetectionAgent) e a
// saída da fonte do app (LinkHooks.PlacedBatch) e reporta cada etapa: câmera → provider → caixas
// 2D → projeção 3D → etiquetas.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Meta.XR;
using Meta.XR.BuildingBlocks.AIBlocks;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace LinkParaMac.EditorTools
{
    public static class LinkDetectionDiag
    {
        static ObjectDetectionAgent _hookedAgent;
        static int _boxBatches, _boxesTotal, _placedBatches, _placedTotal;
        static string _lastBoxes = "-", _lastPlaced = "-";
        static float _lastBoxTime = -1f, _lastPlacedTime = -1f;

        static void Hook()
        {
            var agent = UnityEngine.Object.FindAnyObjectByType<ObjectDetectionAgent>(FindObjectsInactive.Include);
            if (agent != null && agent != _hookedAgent)
            {
                agent.OnDetectionResponseReceived.AddListener(OnBoxes);
                _hookedAgent = agent;
            }
            LinkHooks.PlacedBatch -= OnPlaced;
            LinkHooks.PlacedBatch += OnPlaced;
        }

        static void OnBoxes(List<BoxData> boxes)
        {
            _boxBatches++;
            _boxesTotal += boxes.Count;
            _lastBoxTime = Time.time;
            _lastBoxes = string.Join(", ", boxes.Take(8).Select(b => $"{b.label} [{b.position.x:F0},{b.position.y:F0}-{b.scale.x:F0},{b.scale.y:F0}]"));
            Debug.Log($"[LinkDetect] YOLO {boxes.Count} caixa(s): {_lastBoxes}");
        }

        static void OnPlaced(IReadOnlyList<(string label, Vector3 position)> objs)
        {
            _placedBatches++;
            _placedTotal += objs.Count;
            _lastPlacedTime = Time.time;
            _lastPlaced = string.Join(", ", objs.Take(8).Select(o => $"{o.label}@({o.position.x:F2},{o.position.y:F2},{o.position.z:F2})"));
            Debug.Log($"[LinkDetect] colocadas {objs.Count}: {_lastPlaced}");
        }

        static object Get(object o, string field)
        {
            if (o == null) return null;
            var f = o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return f?.GetValue(o);
        }

        static string _yoloTest = "-";
        static string _agentTest = "-";

        /// <summary>Pausa a fonte, espera o modelo ficar livre e roda UMA inferência do agent esperando o resultado,
        /// pra ver exceções que o agent normalmente engole (ele dispara com "_ = RunDetection()").</summary>
        public static string AgentTest()
        {
            if (!Application.isPlaying) return "só em Play Mode";
            Hook();
            if (_hookedAgent == null) return "agent ausente";
            LinkHooks.PauseAppDetection?.Invoke(true);
            _agentTest = "rodando…";
            RunAgentOnce(_hookedAgent);
            return "rodando 1 inferência do agent (fonte pausada)… (veja detect-status)";
        }

        static async void RunAgentOnce(ObjectDetectionAgent agent)
        {
            int before = _boxBatches;
            var sb = new StringBuilder();
            try
            {
                for (int i = 0; i < 200 && (bool)Get(agent, "_busy"); i++) await System.Threading.Tasks.Task.Delay(50);
                var pca = UnityEngine.Object.FindAnyObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include);
                var agentCam = Get(agent, "_cam") as PassthroughCameraAccess;
                var tex = agentCam != null ? agentCam.GetTexture() : null;
                var provider = Get(agent, "providerAsset") as UnityInferenceEngineProvider;
                var fThr = typeof(UnityInferenceEngineProvider).GetField("scoreThreshold", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                sb.Append($"agent._cam {(agentCam == null ? "null" : agentCam == pca ? "= PCA da cena" : "≠ PCA da cena!")} ");
                sb.Append($"tex={(tex != null ? tex.GetType().Name + " " + tex.width + "x" + tex.height : "null")} ");
                sb.Append($"provider.thr={fThr?.GetValue(provider)} minConf={Get(agent, "minConfidence")}; ");
                if (tex != null) SaveTexture(tex, "agent-tex.png");
                var m = typeof(ObjectDetectionAgent).GetMethod("RunDetection", BindingFlags.Instance | BindingFlags.NonPublic);
                var start = Time.realtimeSinceStartup;
                await (System.Threading.Tasks.Task)m.Invoke(agent, null);
                sb.Append($"RunDetection {(Time.realtimeSinceStartup - start) * 1000f:F0} ms sem exceção, lotes novos={_boxBatches - before}; ");
                if (provider != null && tex != null)
                {
                    object orig = fThr?.GetValue(provider);
                    try
                    {
                        fThr?.SetValue(provider, 0.1f);
                        var preds = await provider.DetectAsync(tex);
                        sb.Append($"direto(thr 0.1): {(preds?.Length ?? 0)}: " + (preds == null ? "null" : string.Join(", ", preds.OrderByDescending(p => p.score).Take(6)
                            .Select(p => $"{p.label} {p.score:F2} [{string.Join(",", p.box.Select(v => v.ToString("F0")))}]"))));
                    }
                    finally { fThr?.SetValue(provider, orig); }
                }
                _agentTest = sb.ToString();
            }
            catch (Exception e) { _agentTest = sb + " EXCEÇÃO: " + e.GetBaseException().GetType().Name + ": " + e.GetBaseException().Message; Debug.LogWarning("[LinkDetect] agent-test " + e); }
            finally { LinkHooks.PauseAppDetection?.Invoke(false); Debug.Log("[LinkDetect] agent-test: " + _agentTest); }
        }

        /// <summary>Lê a textura da GPU e salva em .link/&lt;name&gt; (pra ver o que o modelo recebe).</summary>
        static void SaveTexture(Texture tex, string name)
        {
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var t2 = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
            t2.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
            t2.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), ".link", name), t2.EncodeToPNG());
            UnityEngine.Object.Destroy(t2);
        }

        /// <summary>Roda o provider (YOLO) direto numa imagem, com limiar baixo, pra ver o que o modelo enxerga.
        /// Uso: yolo-test [thr=0.1] [cpu]   (imagem: .link/test.png)</summary>
        public static string YoloTest(string arg)
        {
            if (!Application.isPlaying) return "só em Play Mode";
            Hook();
            var agent = _hookedAgent;
            var provider = Get(agent, "providerAsset") as UnityInferenceEngineProvider;
            if (provider == null) return "provider ausente";
            float thr = 0.1f;
            foreach (var part in (arg ?? "").Split(' '))
                if (part.StartsWith("thr=") && float.TryParse(part.Substring(4), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t)) thr = t;
            string path = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), ".link", "test.png");
            if (!System.IO.File.Exists(path)) return "sem .link/test.png";
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.LoadImage(System.IO.File.ReadAllBytes(path));
            RunYolo(provider, tex, thr);
            return $"rodando YOLO em test.png ({tex.width}x{tex.height}) com limiar {thr}… (veja detect-status)";
        }

        static async void RunYolo(UnityInferenceEngineProvider provider, Texture2D tex, float thr)
        {
            var f = typeof(UnityInferenceEngineProvider).GetField("scoreThreshold", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var backendField = typeof(UnityInferenceEngineProvider).GetField("backend", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            object original = f?.GetValue(provider);
            try
            {
                f?.SetValue(provider, thr);
                var start = Time.realtimeSinceStartup;
                var preds = await provider.DetectAsync(tex);
                var ms = (Time.realtimeSinceStartup - start) * 1000f;
                _yoloTest = $"{(preds?.Length ?? 0)} predição(ões) em {ms:F0} ms (backend={backendField?.GetValue(provider)}): " +
                            (preds == null ? "null" : string.Join(", ", preds.OrderByDescending(p => p.score).Take(10)
                                .Select(p => $"{p.label} {p.score:F2} [{string.Join(",", p.box.Select(v => v.ToString("F0")))}]")));
                Debug.Log("[LinkDetect] yolo-test: " + _yoloTest);
            }
            catch (Exception e) { _yoloTest = "erro: " + e.Message; Debug.LogWarning("[LinkDetect] yolo-test " + e); }
            finally { f?.SetValue(provider, original); UnityEngine.Object.Destroy(tex); }
        }

        public static string Status()
        {
            Hook();
            var sb = new StringBuilder();
            var pca = UnityEngine.Object.FindAnyObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include);
            if (pca == null) sb.AppendLine("PassthroughCameraAccess: AUSENTE");
            else
            {
                var tex = Get(pca, "_texture") as Texture;
                sb.AppendLine($"PCA: active={pca.isActiveAndEnabled} IsPlaying={pca.IsPlaying} hasPermission={Get(pca, "_hasPermission")} tex={(tex != null ? tex.width + "x" + tex.height : "null")}");
            }

            var agent = _hookedAgent;
            if (agent == null) sb.AppendLine("ObjectDetectionAgent: AUSENTE");
            else
            {
                var provider = Get(agent, "providerAsset");
                object worker = Get(provider, "_worker"), input = Get(provider, "_input");
                sb.AppendLine($"Agent: active={agent.isActiveAndEnabled} busy={Get(agent, "_busy")} everyN={Get(agent, "detectEveryNFrames")} minConf={Get(agent, "minConfidence")} cam={(Get(agent, "_cam") != null ? "ok" : "null")}");
                sb.AppendLine($"Provider: {(provider != null ? provider.GetType().Name : "null")} worker={(worker != null ? "pronto" : "null")} input={(input != null ? "pronto" : "null")}");
            }

            sb.AppendLine(LinkHooks.AppStatus != null ? LinkHooks.AppStatus() : "App: sem integração (LinkHooks)");

            var erm = UnityEngine.Object.FindAnyObjectByType<EnvironmentRaycastManager>(FindObjectsInactive.Include);
            bool scenePermission = UnityEngine.Android.Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission);
            sb.AppendLine($"EnvironmentRaycastManager.IsSupported={EnvironmentRaycastManager.IsSupported} " +
                          $"componente={(erm == null ? "ausente" : (erm.isActiveAndEnabled ? "ativo" : "inativo"))} " +
                          $"IsReady={(erm != null && (bool)(typeof(EnvironmentRaycastManager).GetProperty("IsReady", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(erm) ?? false))} " +
                          $"permissãoScene(Editor)={scenePermission}");
            var mruk = MRUK.Instance;
            sb.AppendLine($"MRUK: {(mruk == null ? "ausente" : mruk.Rooms.Count + " sala(s)")}");
            sb.AppendLine($"YOLO: {_boxBatches} lote(s), {_boxesTotal} caixa(s); último há {(_lastBoxTime < 0 ? "-" : (Time.time - _lastBoxTime).ToString("F1") + "s")}: {_lastBoxes}");
            sb.AppendLine($"Colocadas: {_placedBatches} lote(s), {_placedTotal} obj; último há {(_lastPlacedTime < 0 ? "-" : (Time.time - _lastPlacedTime).ToString("F1") + "s")}: {_lastPlaced}");
            sb.AppendLine($"yolo-test: {_yoloTest}");
            sb.AppendLine($"agent-test: {_agentTest}");
            sb.AppendLine(LinkDepth.Status());
            sb.AppendLine($"Raycast reserva: hits={LinkRaycast.Hits} misses={LinkRaycast.Misses} último={LinkRaycast.LastSource}");
            sb.AppendLine($"Feed: enabled={LinkCameraFeed.Enabled} detect={LinkCameraFeed.DriveDetection} camera={(LinkCameraSource.IsConnected() ? "conectada" : "desconectada")}");
            return sb.ToString();
        }
    }
}
