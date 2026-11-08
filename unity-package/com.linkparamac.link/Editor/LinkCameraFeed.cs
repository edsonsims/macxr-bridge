// SPDX-License-Identifier: MIT
// Link para Mac: feed ao vivo da câmera do Quest (relayada) no Editor.
// - mostra o quadro num quad à frente (conferir orientação/qualidade);
// - injeta no PassthroughCameraAccess tudo que a detecção usa: textura, IsPlaying, intrínsecos,
//   resolução e pose da câmera (GetCameraPose = pose da cabeça ∘ offset da lente, com a mesma fórmula
//   que o MRUK usa no headset);
// - pede ao app (LinkHooks.WireDetection) que ligue a detecção real dele — no Hello World: a fonte de passthrough
//   no LabelManager, o mock desligado e o raycast de reserva (LinkRaycast).
// Classe estática movida por EditorApplication.update (MonoBehaviour na pasta Editor não pode ser
// AddComponent em runtime).

using System;
using System.IO;
using System.Reflection;
using Meta.XR;
using UnityEditor;
using UnityEngine;

namespace LinkParaMac.EditorTools
{
    [InitializeOnLoad]
    public static class LinkCameraFeed
    {
        public static bool Enabled;
        public static bool ShowQuad = true;
        public static bool FlipVertical;
        public static bool FlipHorizontal;
        public static bool DriveDetection;
        public static string TestImagePath; // quando definido, injeta essa imagem fixa em vez do feed ao vivo
        public static string LastWiringReport = "";
        public static float Threshold = -1f; // <0 = não mexe; senão limiar só nesta sessão (agent.minConfidence + provider.scoreThreshold)
        static object _origMinConf, _origScoreThr;

        // Extrínsecos da câmera de passthrough esquerda do Quest 3 (Camera2 id=50: LENS_POSE_TRANSLATION/ROTATION).
        // TODO(runtime): mandar no FrameInfo junto com o quadro (exige rebuild do runtime + relançar o Unity).
        static readonly Vector3 LensTranslation = new Vector3(-0.03224482f, -0.01726034f, -0.06312903f);
        static readonly Vector4 LensRotation = new Vector4(-0.99526715f, -0.00131198f, -0.00368363f, 0.09709802f);

        // Rotação da câmera RGB esquerda em relação à cabeça (CenterEye), medida alinhando as bordas da
        // profundidade do ambiente (que vem na pose da visão = cabeça) com a imagem da câmera (2 quadros de
        // profundidade x 2 da câmera, link-para-mac/tools/align-camera-depth.py): ~2° pra baixo, 3° pra direita,
        // 2° de rolagem horária (±1°). O LENS_POSE_ROTATION do Camera2 é relativo ao giroscópio (não à cabeça) e
        // dava 11° pra baixo: ~20 cm de erro vertical numa etiqueta a 1,5 m.
        // "camera-show ... lens=camera2" volta à fórmula antiga pra comparar.
        static readonly Quaternion HeadFromCameraCalibrated = new Quaternion(0.01699f, 0.02647f, -0.01790f, 0.99934f);
        public static bool UseCamera2LensRotation;

        static uint _lastFrameId;
        static GameObject _quadGO;
        static Renderer _quad;
        static Texture2D _testTexture;
        static PassthroughCameraAccess _pca;
        static FieldInfo _fTexture, _fIsPlaying, _fHasPermission, _fTimestamp;
        static MethodInfo _setIntrinsics, _setResolution;
        static FieldInfo _fHeadPoseDelegate;
        static object _originalHeadPoseDelegate;
        static bool _headPoseHooked;
        static Transform _head;
        static FieldInfo _fCpuImage;
        static object _originalCpuImage;

        static LinkCameraFeed()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += s =>
            {
                if (s == PlayModeStateChange.ExitingPlayMode) Cleanup();
                if (s == PlayModeStateChange.EnteredPlayMode || s == PlayModeStateChange.EnteredEditMode) { _autoDone = false; _nextAutoCheck = 0; }
            };
        }

        public static string Configure(string arg)
        {
            string a = arg ?? "";
            Cleanup();
            _autoDone = true; // configurado à mão: o auto não mexe mais neste Play
            Enabled = a != "off";
            ShowQuad = !a.Contains("noquad") && Enabled;
            FlipVertical = a.Contains("flipv");
            FlipHorizontal = a.Contains("fliph");
            DriveDetection = a.Contains("detect");
            LinkRaycast.FixedDistance = a.Contains("fixed") ? 1.2f : 0f;
            UseCamera2LensRotation = a.Contains("lens=camera2");
            LinkRaycast.Hits = LinkRaycast.Misses = 0; LinkRaycast.LastSource = "-"; // contadores por configuração
            Threshold = -1f;
            foreach (var part in a.Split(' '))
                if (part.StartsWith("thr=") && float.TryParse(part.Substring(4), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var thr)) Threshold = thr;
            TestImagePath = null;
            if (a.Contains("test"))
            {
                TestImagePath = Path.Combine(Directory.GetCurrentDirectory(), ".link", "test.png");
                if (!File.Exists(TestImagePath)) return "imagem de teste não existe: " + TestImagePath;
            }
            if (!Enabled) return "feed ao vivo desligado";
            string report = $"feed {(TestImagePath != null ? "de TESTE (" + Path.GetFileName(TestImagePath) + ")" : "ao vivo")} " +
                            $"{(ShowQuad ? "com quad" : "sem quad")}; detect={DriveDetection} fixed={LinkRaycast.FixedDistance > 0}";
            if (DriveDetection) report += " | " + WireDetection();
            return report;
        }

        // Liga sozinho a câmera real + detecção (sem quad) no Play quando o canal da câmera do Quest conecta.
        // Desligar: EditorPrefs "LinkParaMac.AutoCamera" = false, ou "camera-show off" (vale até o fim do Play).
        static bool _autoDone;
        static double _nextAutoCheck;

        static void AutoStart()
        {
            if (_autoDone || Enabled || !EditorApplication.isPlaying) return;
            if (EditorApplication.timeSinceStartup < _nextAutoCheck) return;
            _nextAutoCheck = EditorApplication.timeSinceStartup + 1.0;
            if (!EditorPrefs.GetBool("LinkParaMac.AutoCamera", true)) { _autoDone = true; return; }
            if (!LinkCameraSource.IsConnected()) return;
            if (UnityEngine.Object.FindAnyObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include) == null) return;
            _autoDone = true;
            Debug.Log("[LinkCameraFeed] câmera do Quest conectada: ligando a detecção real (auto) → " + Configure("detect noquad"));
        }

        static void Tick()
        {
            AutoStart();
            if (!Enabled || !EditorApplication.isPlaying) return;
            try
            {
                Texture2D tex;
                int w, h;
                float fx, fy, cx, cy;
                if (TestImagePath != null)
                {
                    if (_testTexture == null)
                    {
                        _testTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        _testTexture.LoadImage(File.ReadAllBytes(TestImagePath));
                    }
                    tex = _testTexture; w = tex.width; h = tex.height;
                    // Intrínsecos aproximados da câmera esquerda pra 640x480 (corte central do sensor 1280x1280).
                    fx = fy = 431.4f * w / 640f; cx = 320.6f * w / 640f; cy = 239.8f * h / 480f;
                }
                else
                {
                    // Vários leitores puxam a câmera (HUD, compositor, gravação): cada um guarda o último quadro que viu.
                    if (!LinkCameraSource.TryGetLatestTexture(out tex, out var info) || tex == null || info.frameId == _lastFrameId) return;
                    _lastFrameId = info.frameId;
                    w = (int)info.width; h = (int)info.height;
                    fx = info.fx; fy = info.fy; cx = info.cx; cy = info.cy;
                    // Clientes antigos escalavam fy errado (esticando em vez de recortar); pixels são quadrados.
                    if (fy < fx * 0.9f) fy = fx;
                }
                SuppressNativeCpuImage();
                if (ShowQuad) UpdateQuad(tex);
                Inject(tex, w, h, fx, fy, cx, cy);
            }
            catch (Exception e) { Debug.LogWarning("[LinkCameraFeed] " + e); }
        }

        static Transform Head()
        {
            if (_head != null) return _head;
            var rig = UnityEngine.Object.FindAnyObjectByType<OVRCameraRig>();
            _head = rig != null ? rig.centerEyeAnchor : (Camera.main != null ? Camera.main.transform : null);
            return _head;
        }

        static void UpdateQuad(Texture2D tex)
        {
            if (_quadGO == null)
            {
                _quadGO = GameObject.CreatePrimitive(PrimitiveType.Quad);
                _quadGO.name = "Link Camera Quad";
                var probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var mat = new Material(probe.GetComponent<Renderer>().sharedMaterial);
                UnityEngine.Object.DestroyImmediate(probe);
                _quad = _quadGO.GetComponent<Renderer>();
                _quad.sharedMaterial = mat;
                var col = _quadGO.GetComponent<Collider>(); if (col != null) UnityEngine.Object.DestroyImmediate(col);
            }
            _quad.sharedMaterial.mainTexture = tex;
            float aspect = (float)tex.width / tex.height;
            _quadGO.transform.localScale = new Vector3(0.5f * aspect * (FlipHorizontal ? -1f : 1f), 0.5f * (FlipVertical ? -1f : 1f), 1f);
            var head = Head();
            if (head != null)
            {
                _quadGO.transform.position = head.position + head.forward * 1.0f;
                _quadGO.transform.rotation = Quaternion.LookRotation(_quadGO.transform.position - head.position, head.up);
            }
        }

        static bool ResolvePca()
        {
            if (_pca != null) return true;
            _pca = UnityEngine.Object.FindAnyObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include);
            if (_pca == null) return false;
            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var t = typeof(PassthroughCameraAccess);
            _fTexture = t.GetField("_texture", F);
            _fIsPlaying = t.GetField("_isPlaying", F);
            _fHasPermission = t.GetField("_hasPermission", F);
            _fTimestamp = t.GetField("_timestampNsMonotonic", F);
            _setIntrinsics = t.GetProperty("Intrinsics", F)?.GetSetMethod(true);
            _setResolution = t.GetProperty("CurrentResolution", F)?.GetSetMethod(true);
            if (!_pca.gameObject.activeSelf) _pca.gameObject.SetActive(true);
            Debug.Log("[LinkCameraFeed] PassthroughCameraAccess encontrado; injetando quadros reais");
            return true;
        }

        static void Inject(Texture2D tex, int w, int h, float fx, float fy, float cx, float cy)
        {
            if (!ResolvePca()) return;
            _fTexture?.SetValue(_pca, tex);
            _fIsPlaying?.SetValue(_pca, (bool?)true);
            _fHasPermission?.SetValue(_pca, true);
            _fTimestamp?.SetValue(_pca, DateTime.UtcNow.Ticks); // qualquer valor > 0; a pose vem do hook abaixo
            _setResolution?.Invoke(_pca, new object[] { new Vector2Int(w, h) });
            // Intrínsecos já no espaço do quadro: SensorResolution = resolução atual → recorte = quadro inteiro.
            var intrinsics = new PassthroughCameraAccess.CameraIntrinsics
            {
                FocalLength = new Vector2(fx, fy),
                PrincipalPoint = new Vector2(cx, cy),
                SensorResolution = new Vector2Int(w, h),
                // Translação: extrínsecos do Camera2 (poucos cm). Rotação: calibrada contra a profundidade (ver acima).
                LensOffset = new Pose(new Vector3(LensTranslation.x, LensTranslation.y, -LensTranslation.z),
                    UseCamera2LensRotation
                        ? Quaternion.Inverse(new Quaternion(-LensRotation.x, -LensRotation.y, LensRotation.z, LensRotation.w)) * Quaternion.Euler(180, 0, 0)
                        : HeadFromCameraCalibrated),
            };
            _setIntrinsics?.Invoke(_pca, new object[] { intrinsics });
            HookHeadPose();
        }

        // GetCameraPose() pede a pose da cabeça ao MRUK nativo (GetHeadsetPoseAtTime), que no Editor não tem câmera.
        // Trocamos o delegate por um que devolve o CenterEyeAnchor (em convenção OpenXR, pois o PCA aplica FlipZ).
        static void HookHeadPose()
        {
            if (_headPoseHooked) return;
            Type funcs = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                funcs = asm.GetType("Meta.XR.MRUtilityKit.MRUKNativeFuncs");
                if (funcs != null) break;
            }
            _fHeadPoseDelegate = funcs?.GetField("GetHeadsetPoseAtTime", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (_fHeadPoseDelegate == null) { Debug.LogWarning("[LinkCameraFeed] MRUKNativeFuncs.GetHeadsetPoseAtTime não encontrado"); _headPoseHooked = true; return; }
            _originalHeadPoseDelegate = _fHeadPoseDelegate.GetValue(null);
            var method = typeof(LinkCameraFeed).GetMethod(nameof(HeadPoseAtTime), BindingFlags.Static | BindingFlags.NonPublic);
            _fHeadPoseDelegate.SetValue(null, Delegate.CreateDelegate(_fHeadPoseDelegate.FieldType, method));
            _headPoseHooked = true;
            Debug.Log("[LinkCameraFeed] GetCameraPose ligado à pose da cabeça do Editor");
        }

        // No Editor, PassthroughCameraAccess.Update() puxa uma imagem de CPU do MRUK nativo
        // (CameraAcquireLatestCpuImage — uma câmera própria do Editor, em outra resolução) e faz
        // LoadRawTextureData na MESMA textura que injetamos: o YOLO recebia lixo listrado. Anulamos o
        // delegate enquanto o feed está ligado (o PCA então sai cedo no Update e não toca na textura).
        static void SuppressNativeCpuImage()
        {
            if (_fCpuImage == null)
            {
                Type funcs = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    funcs = asm.GetType("Meta.XR.MRUtilityKit.MRUKNativeFuncs");
                    if (funcs != null) break;
                }
                _fCpuImage = funcs?.GetField("CameraAcquireLatestCpuImage", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (_fCpuImage == null) return;
            }
            var current = _fCpuImage.GetValue(null);
            if (current == null) return;
            _originalCpuImage = current; // o MRUK pode recarregar as funções nativas; guardamos a mais recente
            _fCpuImage.SetValue(null, null);
            Debug.Log("[LinkCameraFeed] imagem de CPU do MRUK nativo desligada (não sobrescreve mais a textura)");
        }

        static void HeadPoseAtTime(long time, ref Vector3 position, ref Quaternion orientation)
        {
            Vector3 p; Quaternion r;
            if (LinkSession.TryGetReplayHead(out var recorded)) { p = recorded.position; r = recorded.rotation; } // sessão gravada
            else
            {
                var head = Head();
                if (head == null) { position = Vector3.zero; orientation = Quaternion.identity; return; }
                p = head.position; r = head.rotation;
            }
            position = new Vector3(p.x, p.y, -p.z);           // FlipZ: o PCA desfaz
            orientation = new Quaternion(-r.x, -r.y, r.z, r.w);
        }

        static string WireDetection()
        {
            try
            {
                if (!ResolvePca()) return "PassthroughCameraAccess não encontrado";
                string msg = LinkHooks.WireDetection != null ? LinkHooks.WireDetection() : "o app não liga a própria detecção (LinkHooks.WireDetection)";
                if (Threshold >= 0f) msg += "; " + ApplyThreshold(Threshold);
                LastWiringReport = msg;
                Debug.Log("[LinkCameraFeed] " + msg);
                return msg;
            }
            catch (Exception e) { return "falha ao ligar detecção: " + e.Message; }
        }

        static string ApplyThreshold(float thr)
        {
            var agent = UnityEngine.Object.FindAnyObjectByType<Meta.XR.BuildingBlocks.AIBlocks.ObjectDetectionAgent>(FindObjectsInactive.Include);
            if (agent == null) return "sem agent; ";
            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var fMin = agent.GetType().GetField("minConfidence", F);
            var provider = agent.GetType().GetField("providerAsset", F)?.GetValue(agent);
            var fThr = provider?.GetType().GetField("scoreThreshold", F);
            if (_origMinConf == null) _origMinConf = fMin?.GetValue(agent);
            if (_origScoreThr == null) _origScoreThr = fThr?.GetValue(provider);
            fMin?.SetValue(agent, thr);
            fThr?.SetValue(provider, thr);
            LinkHooks.ApplyAppThreshold?.Invoke(thr); // o corte do próprio app (no Hello World, LabelManager.minScore)
            return $"limiar {thr} (sessão; agent, provider e app); ";
        }

        static void RestoreThreshold()
        {
            var agent = UnityEngine.Object.FindAnyObjectByType<Meta.XR.BuildingBlocks.AIBlocks.ObjectDetectionAgent>(FindObjectsInactive.Include);
            if (agent == null) { _origMinConf = _origScoreThr = null; return; }
            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var provider = agent.GetType().GetField("providerAsset", F)?.GetValue(agent);
            if (_origMinConf != null) agent.GetType().GetField("minConfidence", F)?.SetValue(agent, _origMinConf);
            if (_origScoreThr != null) provider?.GetType().GetField("scoreThreshold", F)?.SetValue(provider, _origScoreThr);
            _origMinConf = _origScoreThr = null;
        }

        static void Cleanup()
        {
            RestoreThreshold();
            if (_quadGO != null) { UnityEngine.Object.DestroyImmediate(_quadGO); _quadGO = null; _quad = null; }
            if (_headPoseHooked && _fHeadPoseDelegate != null) _fHeadPoseDelegate.SetValue(null, _originalHeadPoseDelegate);
            _headPoseHooked = false; _fHeadPoseDelegate = null; _originalHeadPoseDelegate = null;
            if (_fCpuImage != null && _originalCpuImage != null && _fCpuImage.GetValue(null) == null) _fCpuImage.SetValue(null, _originalCpuImage);
            _originalCpuImage = null;
            LinkHooks.UnwireDetection?.Invoke();
            if (_pca != null) { _fIsPlaying?.SetValue(_pca, null); _fTexture?.SetValue(_pca, null); } // para o agent também
            _pca = null; _head = null; _lastFrameId = 0;
            if (_testTexture != null) { UnityEngine.Object.DestroyImmediate(_testTexture); _testTexture = null; }
            Enabled = false;
        }
    }
}
