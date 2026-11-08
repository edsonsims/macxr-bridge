// SPDX-License-Identifier: MIT
// Link para Mac: real environment depth (XR_META_environment_depth) relayed from the Quest.
// The client reads the left-eye depth map (320x320, uint16) 5x per second and sends it over the
// camera channel; the Mac runtime keeps the latest one and exports oxrsys_depth_get_latest. Here we do the
// same thing EnvironmentRaycastManager does on the headset: a world ray becomes points in the depth
// camera's view and we compare them against the measured depth.
//
// Spaces: poses arrive in the client's STAGE (OpenXR, right-handed, -Z forward). We convert with FlipZ and
// bring them into the Unity world with T = Editor head * inverse(client head), estimated while the head is
// still (T is constant: it only changes if someone recenters).

using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

namespace LinkParaMac.EditorTools
{
    [InitializeOnLoad]
    public static class LinkDepth
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct DepthInfo
        {
            public uint width, height, valid, frameId;
            public float nearZ, farZ;
            public float hqx, hqy, hqz, hqw, hpx, hpy, hpz; // headPose (client STAGE)
            public float vqx, vqy, vqz, vqw, vpx, vpy, vpz; // viewPose (client STAGE)
            public float angleLeft, angleRight, angleUp, angleDown;
            public long displayTimeNs;
        }

        [DllImport("/usr/lib/libSystem.B.dylib")] static extern IntPtr dlopen(string path, int mode);
        [DllImport("/usr/lib/libSystem.B.dylib")] static extern IntPtr dlsym(IntPtr handle, string symbol);
        delegate int GetLatestDepthDelegate(IntPtr dst, uint dstCount, uint lastFrameId, out DepthInfo info);
        static readonly IntPtr RTLD_DEFAULT = new IntPtr(-2);

        static GetLatestDepthDelegate _get;
        static bool _resolved;
        static ushort[] _depth = Array.Empty<ushort>();
        static DepthInfo _info;
        static uint _lastId;
        static double _lastFrameTime = -1;
        static int _frames;

        // Unity world ← client STAGE (already converted to Unity).
        static bool _hasT;
        static Vector3 _tPos;
        static Quaternion _tRot = Quaternion.identity;
        static int _tSamples;
        static Pose _prevClientHead, _prevEditorHead;
        static bool _hasPrev;
        static Pose _viewWorld; // depth view pose in the Unity world (latest frame)

        public static int Hits, Misses, OutOfView;
        public static int FrameCount => _frames;
        /// <summary>Each new frame (with the view pose already in the Unity world): for recording sessions.</summary>
        public static event Action<DepthInfo, ushort[], Pose> FrameLoaded;
        /// <summary>Session replay: doesn't pull from the runtime; frames arrive via InjectReplay.</summary>
        public static bool ReplayActive;
        public static string LastResult = "-";
        public static bool Enabled = true;

        static LinkDepth()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += s =>
            {
                if (s != PlayModeStateChange.ExitingPlayMode) return;
                _hasT = false; _tSamples = 0; _hasPrev = false; _lastId = 0; _lastFrameTime = -1; _frames = 0;
                Hits = Misses = OutOfView = 0; LastResult = "-";
            };
        }

        static bool Resolve()
        {
            if (_resolved) return true;
            IntPtr p = dlsym(RTLD_DEFAULT, "oxrsys_depth_get_latest"); // the runtime only loads in Play: don't cache the failure
            if (p == IntPtr.Zero)
            {
                string json = Environment.GetEnvironmentVariable("XR_RUNTIME_JSON");
                if (!string.IsNullOrEmpty(json))
                {
                    IntPtr handle = dlopen(Path.Combine(Path.GetDirectoryName(json), "liboxrsys-runtime.dylib"), 0x10 | 0x2);
                    if (handle != IntPtr.Zero) p = dlsym(handle, "oxrsys_depth_get_latest");
                }
            }
            if (p == IntPtr.Zero) return false; // outside Play or an old runtime (no depth)
            _get = Marshal.GetDelegateForFunctionPointer<GetLatestDepthDelegate>(p);
            _resolved = true;
            return true;
        }

        public static bool RuntimeHasDepth => Resolve();
        public static bool IsFresh => _lastFrameTime > 0 && EditorApplication.timeSinceStartup - _lastFrameTime < 2.0;

        static void Tick()
        {
            if (!Enabled || !EditorApplication.isPlaying || ReplayActive || !Resolve()) return;
            try { Pull(); } catch (Exception e) { Debug.LogWarning("[LinkDepth] " + e.Message); }
        }

        static void Pull()
        {
            if (_get(IntPtr.Zero, 0, _lastId, out var probe) == 0) return;
            int need = (int)(probe.width * probe.height);
            if (need <= 0) return;
            if (_depth.Length != need) _depth = new ushort[need];
            var handle = GCHandle.Alloc(_depth, GCHandleType.Pinned);
            int ok;
            DepthInfo info;
            try { ok = _get(handle.AddrOfPinnedObject(), (uint)need, _lastId, out info); }
            finally { handle.Free(); }
            if (ok == 0) return;
            _info = info;
            _lastId = info.frameId;
            _lastFrameTime = EditorApplication.timeSinceStartup;
            _frames++;
            UpdateTransform();
            if (_hasT) FrameLoaded?.Invoke(_info, _depth, _viewWorld);
        }

        /// <summary>Frame from a recorded session: the view pose already comes in the Unity world.</summary>
        public static void InjectReplay(DepthInfo info, ushort[] data, Pose viewWorld)
        {
            _info = info;
            _depth = data;
            _viewWorld = viewWorld;
            _hasT = true;
            _lastFrameTime = EditorApplication.timeSinceStartup;
            _frames++;
        }

        public static void EndReplay()
        {
            ReplayActive = false;
            _hasT = false; _tSamples = 0; _hasPrev = false; _lastId = 0; _lastFrameTime = -1;
            _depth = Array.Empty<ushort>();
        }

        static Pose ToUnity(float qx, float qy, float qz, float qw, float px, float py, float pz) =>
            new Pose(new Vector3(px, py, -pz), new Quaternion(-qx, -qy, qz, qw));

        static Transform Head()
        {
            var rig = UnityEngine.Object.FindAnyObjectByType<OVRCameraRig>();
            return rig != null ? rig.centerEyeAnchor : (Camera.main != null ? Camera.main.transform : null);
        }

        // T = Editor head * inverse(client head). Only accepts samples with the head still (between two
        // depth frames, 200 ms) because the Editor pose is "now" while the client's is from the frame.
        static void UpdateTransform()
        {
            var head = Head();
            if (head == null) return;
            Pose client = ToUnity(_info.hqx, _info.hqy, _info.hqz, _info.hqw, _info.hpx, _info.hpy, _info.hpz);
            Pose editor = new Pose(head.position, head.rotation);
            bool still = _hasPrev &&
                         Vector3.Distance(client.position, _prevClientHead.position) < 0.01f &&
                         Quaternion.Angle(client.rotation, _prevClientHead.rotation) < 1.0f &&
                         Vector3.Distance(editor.position, _prevEditorHead.position) < 0.01f &&
                         Quaternion.Angle(editor.rotation, _prevEditorHead.rotation) < 1.0f;
            _prevClientHead = client; _prevEditorHead = editor; _hasPrev = true;
            if (!_hasT || still)
            {
                Quaternion rot = editor.rotation * Quaternion.Inverse(client.rotation);
                Vector3 pos = editor.position - rot * client.position;
                if (!_hasT || _tSamples < 3) { _tRot = rot; _tPos = pos; }
                else { _tRot = Quaternion.Slerp(_tRot, rot, 0.2f); _tPos = Vector3.Lerp(_tPos, pos, 0.2f); }
                _hasT = true;
                if (still) _tSamples++;
            }
            Pose view = ToUnity(_info.vqx, _info.vqy, _info.vqz, _info.vqw, _info.vpx, _info.vpy, _info.vpz);
            _viewWorld = new Pose(_tPos + _tRot * view.position, _tRot * view.rotation);
        }

        /// <summary>Linear distance (along the view axis) at pixel (i, j), row 0 = bottom. NaN when there is no data.</summary>
        static float LinearDepth(int i, int j)
        {
            int w = (int)_info.width, h = (int)_info.height;
            if (i < 0 || j < 0 || i >= w || j >= h) return float.NaN;
            float d = _depth[j * w + i] / 65535f;
            if (d <= 0f || d >= 0.99999f) return float.NaN;
            float n = _info.nearZ, f = _info.farZ;
            if (float.IsInfinity(f) || f <= n) return n / (1f - d);
            float ndc = 2f * d - 1f;
            return 2f * f * n / ((f + n) - ndc * (f - n));
        }

        static bool ToPixel(Vector3 local, out float u, out float v)
        {
            u = v = 0f;
            if (local.z <= 0.01f) return false;
            float tl = Mathf.Tan(_info.angleLeft), tr = Mathf.Tan(_info.angleRight);
            float tu = Mathf.Tan(_info.angleUp), td = Mathf.Tan(_info.angleDown);
            u = (local.x / local.z - tl) / (tr - tl);
            v = (local.y / local.z - td) / (tu - td);
            return u >= 0f && u < 1f && v >= 0f && v < 1f;
        }

        static Vector3 PixelToLocal(int i, int j, float z)
        {
            float tl = Mathf.Tan(_info.angleLeft), tr = Mathf.Tan(_info.angleRight);
            float tu = Mathf.Tan(_info.angleUp), td = Mathf.Tan(_info.angleDown);
            float tx = tl + (i + 0.5f) / _info.width * (tr - tl);
            float ty = td + (j + 0.5f) / _info.height * (tu - td);
            return new Vector3(tx * z, ty * z, z);
        }

        /// <summary>Grid of world points where the depth sensor sees surfaces (up to 6 m), for the debug panel.</summary>
        public static System.Collections.Generic.List<Vector3> SampleWorldPoints(int cols, int rows)
        {
            var list = new System.Collections.Generic.List<Vector3>(cols * rows);
            if (!IsFresh || !_hasT || _depth.Length == 0) return list;
            int w = (int)_info.width, h = (int)_info.height;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int i = (int)((c + 0.5f) / cols * w), j = (int)((r + 0.5f) / rows * h);
                    float z = LinearDepth(i, j);
                    if (float.IsNaN(z) || z > 6f) continue;
                    list.Add(_viewWorld.position + _viewWorld.rotation * PixelToLocal(i, j, z));
                }
            return list;
        }

        /// <summary>Raycast against the real depth (same idea as EnvironmentRaycastManager on the headset).</summary>
        public static bool Raycast(Ray ray, float maxDistance, out Vector3 point, out Vector3 normal)
        {
            point = default; normal = Vector3.up;
            if (!Enabled || !IsFresh || !_hasT || _depth.Length == 0) return false;
            Quaternion inv = Quaternion.Inverse(_viewWorld.rotation);
            Vector3 o = inv * (ray.origin - _viewWorld.position);
            Vector3 dir = inv * ray.direction;
            const float step = 0.02f;
            float prevT = 0f;
            bool everInView = false;
            for (float t = 0.05f; t <= maxDistance; t += step)
            {
                Vector3 p = o + dir * t;
                if (!ToPixel(p, out float u, out float v)) { prevT = t; continue; }
                everInView = true;
                float z = LinearDepth((int)(u * _info.width), (int)(v * _info.height));
                if (float.IsNaN(z) || p.z < z) { prevT = t; continue; }
                // Crossed the surface between prevT and t: refine by bisection.
                float a = prevT, b = t;
                for (int k = 0; k < 6; k++)
                {
                    float m = (a + b) * 0.5f;
                    Vector3 q = o + dir * m;
                    if (ToPixel(q, out float qu, out float qv))
                    {
                        float qz = LinearDepth((int)(qu * _info.width), (int)(qv * _info.height));
                        if (!float.IsNaN(qz) && q.z >= qz) b = m; else a = m;
                    }
                    else a = m;
                }
                Vector3 hitLocal = o + dir * b;
                point = _viewWorld.position + _viewWorld.rotation * hitLocal;
                normal = NormalAt(hitLocal, -dir);
                Hits++;
                LastResult = $"hit {b:F2} m";
                return true;
            }
            if (!everInView) OutOfView++;
            Misses++;
            LastResult = everInView ? "miss (nothing within " + maxDistance.ToString("F1") + " m)" : "outside the depth field of view";
            return false;
        }

        static Vector3 NormalAt(Vector3 local, Vector3 towardViewer)
        {
            if (!ToPixel(local, out float u, out float v)) return _viewWorld.rotation * towardViewer;
            int i = (int)(u * _info.width), j = (int)(v * _info.height);
            const int r = 3;
            float z0 = LinearDepth(i, j), zx = LinearDepth(i + r, j), zy = LinearDepth(i, j + r);
            if (float.IsNaN(z0) || float.IsNaN(zx) || float.IsNaN(zy)) return _viewWorld.rotation * towardViewer;
            Vector3 p0 = PixelToLocal(i, j, z0), px = PixelToLocal(i + r, j, zx), py = PixelToLocal(i, j + r, zy);
            Vector3 n = Vector3.Cross(py - p0, px - p0).normalized;
            if (Vector3.Dot(n, towardViewer) < 0f) n = -n;
            return _viewWorld.rotation * n;
        }

        public static string Status()
        {
            if (!Resolve()) return "Depth: the loaded runtime does not export oxrsys_depth_get_latest (rebuild the runtime and restart Unity)";
            string age = _lastFrameTime > 0 ? $"{EditorApplication.timeSinceStartup - _lastFrameTime:F1}s" : "-";
            return $"Depth: frames={_frames} last {age} ago {_info.width}x{_info.height} near={_info.nearZ:F2} far={_info.farZ} " +
                   $"T={(_hasT ? $"ok ({_tSamples} still samples)" : "none")} hits={Hits} misses={Misses} outOfView={OutOfView} last={LastResult}";
        }

        /// <summary>Saves .link/depth.png (gray: near = bright, 0–4 m; red = no data) and tests a ray straight ahead.</summary>
        public static string Capture()
        {
            if (!Resolve()) return Status();
            if (_depth.Length == 0) return Status() + " — no frames yet";
            int w = (int)_info.width, h = (int)_info.height;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color32[w * h];
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    float z = LinearDepth(i, j);
                    if (float.IsNaN(z)) { px[(h - 1 - j) * w + i] = new Color32(160, 0, 0, 255); continue; }
                    byte g = (byte)Mathf.Clamp(255f * (1f - z / 4f), 0f, 255f);
                    px[(h - 1 - j) * w + i] = new Color32(g, g, g, 255); // EncodeToPNG puts row 0 at the top: we flip
                }
            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes(Path.Combine(Directory.GetCurrentDirectory(), ".link", "depth.png"), tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            string center = $"center={LinearDepth(w / 2, h / 2):F2} m";
            string ray = "-";
            var head = Head();
            if (head != null)
                ray = Raycast(new Ray(head.position, head.forward), 6f, out var p, out var n)
                    ? $"head ray: {Vector3.Distance(head.position, p):F2} m at {p:F2} normal {n:F2}"
                    : "head ray: " + LastResult;
            return Status() + "\n" + center + "; " + ray + "\nimage: .link/depth.png";
        }
    }
}
