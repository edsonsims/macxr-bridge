// SPDX-License-Identifier: MIT
// Link para Mac: in-headset debug overlay ("debug on|off|depth" command). Draws into the scene (so it shows up
// on the Quest through the stream):
//  - what YOLO saw: a ray from the camera to where the label landed (green), or out to 2 m when nothing was
//    hit (red), with "class score · distance";
//  - real depth (optional): a grid of points where the Quest's sensor sees surfaces.
// None of this belongs to the app: it goes away when leaving Play or with "debug off".
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
            if (!EditorApplication.isPlaying) return "Play Mode only";
            if (a == "off") { Teardown(true); return "debug overlay off"; }
            if (a == "on" || a == "depth" || a == "nodepth")
            {
                Enabled = true;
                if (a == "depth") ShowDepth = true;
                if (a == "nodepth") ShowDepth = false;
                Hook();
                return $"debug overlay on (YOLO rays{(ShowDepth ? " + depth points" : "")})";
            }
            return Status();
        }

        public static string Status() =>
            $"Debug: {(Enabled ? "on" : "off")} depth={(ShowDepth ? "yes" : "no")} batches={_batches} " +
            $"rays={_rays.Count} points={_dots.Count}";

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
                // The placement of the same class closest to the ray (the source casts several rays inside the box).
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
                lr.SetPosition(0, ray.origin + ray.direction * 0.15f); // starts in front of the eyes
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
                tm.text = $"{cls} · {(hit.HasValue ? Vector3.Distance(ray.origin, end).ToString("F2") + " m" : "no hit")}";
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
                // Size scales with distance: up close it doesn't turn into a wall of cubes, far away it stays visible.
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
