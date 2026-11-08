// SPDX-License-Identifier: MIT
// Link para Mac: detection diagnostics in the Editor. Hooks the YOLO output (ObjectDetectionAgent) and the
// output of the app's source (LinkHooks.PlacedBatch) and reports each stage: camera → provider → 2D
// boxes → 3D projection → labels.

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
            Debug.Log($"[LinkDetect] YOLO {boxes.Count} box(es): {_lastBoxes}");
        }

        static void OnPlaced(IReadOnlyList<(string label, Vector3 position)> objs)
        {
            _placedBatches++;
            _placedTotal += objs.Count;
            _lastPlacedTime = Time.time;
            _lastPlaced = string.Join(", ", objs.Take(8).Select(o => $"{o.label}@({o.position.x:F2},{o.position.y:F2},{o.position.z:F2})"));
            Debug.Log($"[LinkDetect] placed {objs.Count}: {_lastPlaced}");
        }

        static object Get(object o, string field)
        {
            if (o == null) return null;
            var f = o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return f?.GetValue(o);
        }

        static string _yoloTest = "-";
        static string _agentTest = "-";

        /// <summary>Pauses the source, waits for the model to be free and runs ONE agent inference, awaiting the result,
        /// to surface exceptions the agent normally swallows (it fires with "_ = RunDetection()").</summary>
        public static string AgentTest()
        {
            if (!Application.isPlaying) return "Play Mode only";
            Hook();
            if (_hookedAgent == null) return "agent missing";
            LinkHooks.PauseAppDetection?.Invoke(true);
            _agentTest = "running…";
            RunAgentOnce(_hookedAgent);
            return "running 1 agent inference (source paused)… (see detect-status)";
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
                sb.Append($"agent._cam {(agentCam == null ? "null" : agentCam == pca ? "= scene PCA" : "≠ scene PCA!")} ");
                sb.Append($"tex={(tex != null ? tex.GetType().Name + " " + tex.width + "x" + tex.height : "null")} ");
                sb.Append($"provider.thr={fThr?.GetValue(provider)} minConf={Get(agent, "minConfidence")}; ");
                if (tex != null) SaveTexture(tex, "agent-tex.png");
                var m = typeof(ObjectDetectionAgent).GetMethod("RunDetection", BindingFlags.Instance | BindingFlags.NonPublic);
                var start = Time.realtimeSinceStartup;
                await (System.Threading.Tasks.Task)m.Invoke(agent, null);
                sb.Append($"RunDetection {(Time.realtimeSinceStartup - start) * 1000f:F0} ms without exception, new batches={_boxBatches - before}; ");
                if (provider != null && tex != null)
                {
                    object orig = fThr?.GetValue(provider);
                    try
                    {
                        fThr?.SetValue(provider, 0.1f);
                        var preds = await provider.DetectAsync(tex);
                        sb.Append($"direct(thr 0.1): {(preds?.Length ?? 0)}: " + (preds == null ? "null" : string.Join(", ", preds.OrderByDescending(p => p.score).Take(6)
                            .Select(p => $"{p.label} {p.score:F2} [{string.Join(",", p.box.Select(v => v.ToString("F0")))}]"))));
                    }
                    finally { fThr?.SetValue(provider, orig); }
                }
                _agentTest = sb.ToString();
            }
            catch (Exception e) { _agentTest = sb + " EXCEPTION: " + e.GetBaseException().GetType().Name + ": " + e.GetBaseException().Message; Debug.LogWarning("[LinkDetect] agent-test " + e); }
            finally { LinkHooks.PauseAppDetection?.Invoke(false); Debug.Log("[LinkDetect] agent-test: " + _agentTest); }
        }

        /// <summary>Reads the texture back from the GPU and saves it to .link/&lt;name&gt; (to see what the model receives).</summary>
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

        /// <summary>Runs the provider (YOLO) directly on an image, with a low threshold, to see what the model sees.
        /// Usage: yolo-test [thr=0.1] [cpu]   (image: .link/test.png)</summary>
        public static string YoloTest(string arg)
        {
            if (!Application.isPlaying) return "Play Mode only";
            Hook();
            var agent = _hookedAgent;
            var provider = Get(agent, "providerAsset") as UnityInferenceEngineProvider;
            if (provider == null) return "provider missing";
            float thr = 0.1f;
            foreach (var part in (arg ?? "").Split(' '))
                if (part.StartsWith("thr=") && float.TryParse(part.Substring(4), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t)) thr = t;
            string path = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), ".link", "test.png");
            if (!System.IO.File.Exists(path)) return "no .link/test.png";
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.LoadImage(System.IO.File.ReadAllBytes(path));
            RunYolo(provider, tex, thr);
            return $"running YOLO on test.png ({tex.width}x{tex.height}) with threshold {thr}… (see detect-status)";
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
                _yoloTest = $"{(preds?.Length ?? 0)} prediction(s) in {ms:F0} ms (backend={backendField?.GetValue(provider)}): " +
                            (preds == null ? "null" : string.Join(", ", preds.OrderByDescending(p => p.score).Take(10)
                                .Select(p => $"{p.label} {p.score:F2} [{string.Join(",", p.box.Select(v => v.ToString("F0")))}]")));
                Debug.Log("[LinkDetect] yolo-test: " + _yoloTest);
            }
            catch (Exception e) { _yoloTest = "error: " + e.Message; Debug.LogWarning("[LinkDetect] yolo-test " + e); }
            finally { f?.SetValue(provider, original); UnityEngine.Object.Destroy(tex); }
        }

        public static string Status()
        {
            Hook();
            var sb = new StringBuilder();
            var pca = UnityEngine.Object.FindAnyObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include);
            if (pca == null) sb.AppendLine("PassthroughCameraAccess: MISSING");
            else
            {
                var tex = Get(pca, "_texture") as Texture;
                sb.AppendLine($"PCA: active={pca.isActiveAndEnabled} IsPlaying={pca.IsPlaying} hasPermission={Get(pca, "_hasPermission")} tex={(tex != null ? tex.width + "x" + tex.height : "null")}");
            }

            var agent = _hookedAgent;
            if (agent == null) sb.AppendLine("ObjectDetectionAgent: MISSING");
            else
            {
                var provider = Get(agent, "providerAsset");
                object worker = Get(provider, "_worker"), input = Get(provider, "_input");
                sb.AppendLine($"Agent: active={agent.isActiveAndEnabled} busy={Get(agent, "_busy")} everyN={Get(agent, "detectEveryNFrames")} minConf={Get(agent, "minConfidence")} cam={(Get(agent, "_cam") != null ? "ok" : "null")}");
                sb.AppendLine($"Provider: {(provider != null ? provider.GetType().Name : "null")} worker={(worker != null ? "ready" : "null")} input={(input != null ? "ready" : "null")}");
            }

            sb.AppendLine(LinkHooks.AppStatus != null ? LinkHooks.AppStatus() : "App: no integration (LinkHooks)");

            var erm = UnityEngine.Object.FindAnyObjectByType<EnvironmentRaycastManager>(FindObjectsInactive.Include);
            bool scenePermission = UnityEngine.Android.Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission);
            sb.AppendLine($"EnvironmentRaycastManager.IsSupported={EnvironmentRaycastManager.IsSupported} " +
                          $"component={(erm == null ? "missing" : (erm.isActiveAndEnabled ? "active" : "inactive"))} " +
                          $"IsReady={(erm != null && (bool)(typeof(EnvironmentRaycastManager).GetProperty("IsReady", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(erm) ?? false))} " +
                          $"scenePermission(Editor)={scenePermission}");
            var mruk = MRUK.Instance;
            sb.AppendLine($"MRUK: {(mruk == null ? "missing" : mruk.Rooms.Count + " room(s)")}");
            sb.AppendLine($"YOLO: {_boxBatches} batch(es), {_boxesTotal} box(es); last {(_lastBoxTime < 0 ? "-" : (Time.time - _lastBoxTime).ToString("F1") + "s")} ago: {_lastBoxes}");
            sb.AppendLine($"Placed: {_placedBatches} batch(es), {_placedTotal} obj; last {(_lastPlacedTime < 0 ? "-" : (Time.time - _lastPlacedTime).ToString("F1") + "s")} ago: {_lastPlaced}");
            sb.AppendLine($"yolo-test: {_yoloTest}");
            sb.AppendLine($"agent-test: {_agentTest}");
            sb.AppendLine(LinkDepth.Status());
            sb.AppendLine($"Fallback raycast: hits={LinkRaycast.Hits} misses={LinkRaycast.Misses} last={LinkRaycast.LastSource}");
            sb.AppendLine($"Feed: enabled={LinkCameraFeed.Enabled} detect={LinkCameraFeed.DriveDetection} camera={(LinkCameraSource.IsConnected() ? "connected" : "disconnected")}");
            return sb.ToString();
        }
    }
}
