// SPDX-License-Identifier: MIT
// Link para Mac: imagem de realidade mista (câmera real do Quest + app) — usada pela gravação (record), pelo
// espectador ao vivo (janela e stream na rede) e pelo que mais pedir. A cada quadro novo da câmera: o fundo é o
// quadro; por cima, o app renderizado por uma câmera virtual NA POSE e com os INTRÍNSECOS da câmera real
// (projeção fora do eixo pelo ponto principal), então o virtual cai em cima do real como no headset.
using System;
using Meta.XR;
using UnityEditor;
using UnityEngine;

namespace LinkParaMac.EditorTools
{
    [InitializeOnLoad]
    public static class LinkComposite
    {
        /// <summary>Cada imagem nova (thread principal). A RenderTexture é reaproveitada: leia ou copie na hora.</summary>
        public static event Action<RenderTexture, LinkCameraSource.FrameInfo> Composited;
        public static RenderTexture Output => _rtOut;
        public static int Frames { get; private set; }
        public static bool Active => _users > 0;

        static int _users;
        static Camera _cam;
        static RenderTexture _rtApp, _rtOut;
        static Material _over;
        static uint _lastFrameId;

        static LinkComposite()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += s => { if (s == PlayModeStateChange.ExitingPlayMode) Release(); };
        }

        public static void AddUser() => _users++;
        public static void RemoveUser() { _users = Math.Max(0, _users - 1); if (_users == 0) Release(); }

        static void Release()
        {
            if (_cam != null) UnityEngine.Object.Destroy(_cam.gameObject);
            _cam = null;
            if (_rtApp != null) { _rtApp.Release(); UnityEngine.Object.DestroyImmediate(_rtApp); _rtApp = null; }
            if (_rtOut != null) { _rtOut.Release(); UnityEngine.Object.DestroyImmediate(_rtOut); _rtOut = null; }
            _lastFrameId = 0;
        }

        static void Ensure(int w, int h)
        {
            if (_rtOut != null && _rtOut.width == w && _rtOut.height == h && _cam != null) return;
            Release();
            _rtApp = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "Link Composite App" };
            _rtOut = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = "Link Composite" };
            if (_over == null) _over = new Material(Shader.Find("Sprites/Default")); // blend alpha "por cima"
            var go = new GameObject("Link Composite Camera (Editor)") { hideFlags = HideFlags.DontSave };
            _cam = go.AddComponent<Camera>();
            _cam.enabled = false;
            _cam.stereoTargetEye = StereoTargetEyeMask.None;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0, 0, 0, 0);
            _cam.targetTexture = _rtApp;
            _cam.nearClipPlane = 0.05f;
            _cam.farClipPlane = 100f;
            var main = Camera.main;
            if (main != null) _cam.cullingMask = main.cullingMask;
        }

        static void Tick()
        {
            if (_users == 0 || !EditorApplication.isPlaying) return;
            try
            {
                if (!LinkCameraSource.TryGetLatestTexture(out var tex, out var info) || info.frameId == _lastFrameId) return;
                var pca = UnityEngine.Object.FindAnyObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include);
                if (pca == null || !pca.IsPlaying) return;
                _lastFrameId = info.frameId;
                int w = tex.width, h = tex.height;
                Ensure(w, h);

                Pose pose = pca.GetCameraPose();
                _cam.transform.SetPositionAndRotation(pose.position, pose.rotation);
                var intr = pca.Intrinsics;
                float n = _cam.nearClipPlane, f = _cam.farClipPlane;
                float fx = intr.FocalLength.x, fy = intr.FocalLength.y, cx = intr.PrincipalPoint.x, cy = intr.PrincipalPoint.y;
                // Intrínsecos no espaço do quadro (o feed injeta SensorResolution = quadro).
                float sx = (float)w / Mathf.Max(1, intr.SensorResolution.x), sy = (float)h / Mathf.Max(1, intr.SensorResolution.y);
                fx *= sx; cx *= sx; fy *= sy; cy *= sy;
                _cam.projectionMatrix = Matrix4x4.Frustum(-cx * n / fx, (w - cx) * n / fx, -cy * n / fy, (h - cy) * n / fy, n, f);
                _cam.Render();

                Graphics.Blit(tex, _rtOut);           // fundo: a câmera real
                Graphics.Blit(_rtApp, _rtOut, _over); // por cima: o app (alpha)
                Frames++;
                Composited?.Invoke(_rtOut, info);
            }
            catch (Exception e) { Debug.LogWarning("[LinkComposite] " + e.Message); }
        }
    }
}
