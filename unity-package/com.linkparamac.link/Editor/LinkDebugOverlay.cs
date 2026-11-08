// SPDX-License-Identifier: MIT
// Link para Mac: painel de depuração no headset (comando "debug on|off|depth"). Desenha na cena (então aparece
// no Quest pelo stream):
//  - o que o YOLO viu: um raio da câmera até onde a etiqueta pousou (verde) ou até 2 m quando nada foi
//    atingido (vermelho), com "classe nota · distância";
//  - a profundidade real (opcional): uma grade de pontos onde o sensor do Quest vê superfícies.
// Nada disso é do app: some ao sair do Play ou com "debug off".
using System.Collections.Generic;
using System.Linq;
using Meta.XR;
using Meta.XR.BuildingBlocks.AIBlocks;
using UnityEditor;
using UnityEngine;

namespace LinkParaMac.EditorTools
{
    [InitializeOnLoad]
    public static class LinkDebugOverlay
    {
        public static bool Enabled { get; private set; }
        public static bool ShowDepth { get; private set; }

        static GameObject _root;
        static ObjectDetectionAgent _agent;
        static readonly List<GameObject> _rays = new List<GameObject>();
        static readonly List<GameObject> _dots = new List<GameObject>();
        static List<BoxData> _lastBoxes = new List<BoxData>();
        static List<(string label, Vector3 position)> _lastPlaced = new List<(string, Vector3)>();
        static bool _dirty;
        static double _nextDepth;
        static Material _lineMat, _dotMat;
        static Font _font;
        static int _batches;

        static LinkDebugOverlay()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += s => { if (s == PlayModeStateChange.ExitingPlayMode) Teardown(false); };
        }

        public static string Command(string arg)
        {
            string a = (arg ?? "").Trim();
            if (!EditorApplication.isPlaying) return "só em Play Mode";
            if (a == "off") { Teardown(true); return "painel de depuração desligado"; }
            if (a == "on" || a == "depth" || a == "nodepth")
            {
                Enabled = true;
                if (a == "depth") ShowDepth = true;
                if (a == "nodepth") ShowDepth = false;
                Hook();
                return $"painel de depuração ligado (raios do YOLO{(ShowDepth ? " + pontos de profundidade" : "")})";
            }
            return Status();
        }

        public static string Status() =>
            $"Depuração: {(Enabled ? "ligada" : "desligada")} profundidade={(ShowDepth ? "sim" : "não")} lotes={_batches} " +
            $"raios={_rays.Count} pontos={_dots.Count}";

        static void Hook()
        {
            var agent = Object.FindAnyObjectByType<ObjectDetectionAgent>(FindObjectsInactive.Include);
            if (agent != null && agent != _agent)
            {
                agent.OnDetectionResponseReceived.AddListener(OnBoxes);
                _agent = agent;
            }
            LinkHooks.PlacedBatch -= OnPlaced;
            LinkHooks.PlacedBatch += OnPlaced;
            if (_root == null) _root = new GameObject("Link Debug (Editor)");
            if (_lineMat == null) _lineMat = new Material(Shader.Find("Sprites/Default"));
            if (_dotMat == null)
            {
                var probe = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                _dotMat = new Material(probe.GetComponent<Renderer>().sharedMaterial) { color = new Color(0.2f, 0.8f, 1f) };
                Object.DestroyImmediate(probe);
            }
            if (_font == null) _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        static void OnBoxes(List<BoxData> boxes)
        {
            if (!Enabled) return;
            _lastBoxes = boxes.ToList();
            _batches++;
            _dirty = true;
        }

        static void OnPlaced(IReadOnlyList<(string label, Vector3 position)> placed)
        {
            if (!Enabled) return;
            _lastPlaced = placed.ToList();
            _dirty = true;
        }

        static void Tick()
        {
            if (!Enabled || !EditorApplication.isPlaying) return;
            if (_root == null) Hook();
            if (_dirty) { _dirty = false; RedrawRays(); }
            if (ShowDepth && EditorApplication.timeSinceStartup >= _nextDepth)
            {
                _nextDepth = EditorApplication.timeSinceStartup + 0.5;
                RedrawDepth();
            }
        }

        static void RedrawRays()
        {
            foreach (var go in _rays) if (go != null) Object.Destroy(go);
            _rays.Clear();
            var pca = Object.FindAnyObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include);
            if (pca == null || !pca.IsPlaying) return;
            var tex = pca.GetTexture();
            if (tex == null) return;
            float w = tex.width, h = tex.height;
            Pose cam = pca.GetCameraPose();
            foreach (var b in _lastBoxes.Take(12))
            {
                float cx = (b.position.x + b.scale.x) * 0.5f / w, cy = (b.position.y + b.scale.y) * 0.5f / h;
                Ray ray = pca.ViewportPointToRay(new Vector2(cx, 1f - cy), cam);
                string cls = b.label ?? "?";
                string name = cls.Contains(' ') ? cls.Substring(0, cls.LastIndexOf(' ')) : cls;
                // A colocada mais perto do raio com a mesma classe (o source usa vários raios dentro da caixa).
                Vector3? hit = null;
                float bestAngle = 12f;
                foreach (var p in _lastPlaced)
                {
                    if (p.label != name) continue;
                    float angle = Vector3.Angle(ray.direction, p.position - ray.origin);
                    if (angle < bestAngle) { bestAngle = angle; hit = p.position; }
                }
                Vector3 end = hit ?? ray.GetPoint(2f);
                Color color = hit.HasValue ? new Color(0.2f, 1f, 0.3f) : new Color(1f, 0.25f, 0.2f);
                var go = new GameObject("ray " + cls);
                go.transform.SetParent(_root.transform, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.sharedMaterial = _lineMat;
                lr.positionCount = 2;
                lr.SetPosition(0, ray.origin + ray.direction * 0.15f); // começa à frente dos olhos
                lr.SetPosition(1, end);
                lr.startWidth = 0.002f; lr.endWidth = 0.006f;
                lr.startColor = lr.endColor = color;
                var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Object.Destroy(dot.GetComponent<Collider>());
                dot.transform.SetParent(go.transform, false);
                dot.transform.position = end;
                dot.transform.localScale = Vector3.one * 0.025f;
                dot.GetComponent<Renderer>().sharedMaterial = new Material(_dotMat) { color = color };
                var text = new GameObject("text");
                text.transform.SetParent(go.transform, false);
                text.transform.position = end + Vector3.up * 0.05f;
                text.transform.rotation = Quaternion.LookRotation(end - ray.origin, Vector3.up);
                var tm = text.AddComponent<TextMesh>();
                tm.font = _font;
                tm.GetComponent<MeshRenderer>().sharedMaterial = _font.material;
                tm.text = $"{cls} · {(hit.HasValue ? Vector3.Distance(ray.origin, end).ToString("F2") + " m" : "sem acerto")}";
                tm.fontSize = 48; tm.characterSize = 0.0025f; tm.anchor = TextAnchor.LowerCenter; tm.color = color;
                _rays.Add(go);
            }
        }

        static void RedrawDepth()
        {
            var points = LinkDepth.SampleWorldPoints(24, 18);
            while (_dots.Count < points.Count)
            {
                var d = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.Destroy(d.GetComponent<Collider>());
                d.transform.SetParent(_root.transform, false);
                d.GetComponent<Renderer>().sharedMaterial = _dotMat;
                _dots.Add(d);
            }
            for (int i = 0; i < _dots.Count; i++)
            {
                bool on = i < points.Count;
                if (_dots[i].activeSelf != on) _dots[i].SetActive(on);
                if (!on) continue;
                _dots[i].transform.position = points[i];
                // Tamanho que acompanha a distância: perto não vira parede de cubos, longe continua visível.
                float dist = Camera.main != null ? Vector3.Distance(Camera.main.transform.position, points[i]) : 1f;
                _dots[i].transform.localScale = Vector3.one * Mathf.Clamp(0.004f * dist, 0.002f, 0.02f);
            }
        }

        static void Teardown(bool keepHooks)
        {
            Enabled = false;
            ShowDepth = false;
            if (_root != null) Object.Destroy(_root);
            _root = null;
            _rays.Clear();
            _dots.Clear();
            if (!keepHooks) { _agent = null; LinkHooks.PlacedBatch -= OnPlaced; }
        }
    }
}
