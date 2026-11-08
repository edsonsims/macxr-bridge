// SPDX-License-Identifier: MIT
// Link para Mac: live feed of the (relayed) Quest camera in the Editor.
// - shows the frame on a quad in front of you (to check orientation/quality);
// - injects into PassthroughCameraAccess everything detection uses: texture, IsPlaying, intrinsics,
//   resolution and camera pose (GetCameraPose = head pose ∘ lens offset, using the same formula
//   MRUK uses on the headset);
// - asks the app (LinkHooks.WireDetection) to wire up its real detection — in Hello World: the passthrough source
//   in LabelManager, the mock turned off and the fallback raycast (LinkRaycast).
// Static class driven by EditorApplication.update (a MonoBehaviour in the Editor folder cannot be
// added with AddComponent at runtime).

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
        public static string TestImagePath; // when set, injects this fixed image instead of the live feed
        public static string LastWiringReport = "";
        public static float Threshold = -1f; // <0 = leave untouched; otherwise a threshold for this session only (agent.minConfidence + provider.scoreThreshold)
        static object _origMinConf, _origScoreThr;

        // Extrinsics of the Quest 3 left passthrough camera (Camera2 id=50: LENS_POSE_TRANSLATION/ROTATION).
        // TODO(runtime): send them in FrameInfo along with the frame (requires rebuilding the runtime + relaunching Unity).
        static readonly Vector3 LensTranslation = new Vector3(-0.03224482f, -0.01726034f, -0.06312903f);
        static readonly Vector4 LensRotation = new Vector4(-0.99526715f, -0.00131198f, -0.00368363f, 0.09709802f);

        // Rotation of the left RGB camera relative to the head (CenterEye), measured by aligning the edges of the
        // environment depth (which comes in the view pose = head) with the camera image (2 depth frames
        // x 2 camera frames, link-para-mac/tools/align-camera-depth.py): ~2° down, 3° right,
        // 2° clockwise roll (±1°). Camera2's LENS_POSE_ROTATION is relative to the gyroscope (not the head) and
        // gave 11° down: ~20 cm of vertical error on a label at 1.5 m.
        // "camera-show ... lens=camera2" switches back to the old formula for comparison.
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
            _autoDone = true; // configured by hand: auto won't touch it again during this Play
            Enabled = a != "off";
            ShowQuad = !a.Contains("noquad") && Enabled;
            FlipVertical = a.Contains("flipv");
            FlipHorizontal = a.Contains("fliph");
            DriveDetection = a.Contains("detect");
            LinkRaycast.FixedDistance = a.Contains("fixed") ? 1.2f : 0f;
            UseCamera2LensRotation = a.Contains("lens=camera2");
            LinkRaycast.Hits = LinkRaycast.Misses = 0; LinkRaycast.LastSource = "-"; // counters per configuration
            Threshold = -1f;
            foreach (var part in a.Split(' '))
                if (part.StartsWith("thr=") && float.TryParse(part.Substring(4), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var thr)) Threshold = thr;
            TestImagePath = null;
            if (a.Contains("test"))
            {
                TestImagePath = Path.Combine(Directory.GetCurrentDirectory(), ".link", "test.png");
                if (!File.Exists(TestImagePath)) return "test image does not exist: " + TestImagePath;
            }
            if (!Enabled) return "live feed off";
            string report = $"{(TestImagePath != null ? "TEST feed (" + Path.GetFileName(TestImagePath) + ")" : "live feed")} " +
                            $"{(ShowQuad ? "with quad" : "no quad")}; detect={DriveDetection} fixed={LinkRaycast.FixedDistance > 0}";
            if (DriveDetection) report += " | " + WireDetection();
            return report;
        }

        // Automatically turns on the real camera + detection (no quad) in Play when the Quest camera channel connects.
        // To disable: EditorPrefs "LinkParaMac.AutoCamera" = false, or "camera-show off" (lasts until the end of Play).
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
            Debug.Log("[LinkCameraFeed] Quest camera connected: enabling real detection (auto) → " + Configure("detect noquad"));
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
                    // Approximate left camera intrinsics for 640x480 (center crop of the 1280x1280 sensor).
                    fx = fy = 431.4f * w / 640f; cx = 320.6f * w / 640f; cy = 239.8f * h / 480f;
                }
                else
                {
                    // Several readers pull from the camera (HUD, compositor, recording): each keeps the last frame it saw.
                    if (!LinkCameraSource.TryGetLatestTexture(out tex, out var info) || tex == null || info.frameId == _lastFrameId) return;
                    _lastFrameId = info.frameId;
                    w = (int)info.width; h = (int)info.height;
                    fx = info.fx; fy = info.fy; cx = info.cx; cy = info.cy;
                    // Old clients scaled fy wrong (stretching instead of cropping); pixels are square.
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
            Debug.Log("[LinkCameraFeed] PassthroughCameraAccess found; injecting real frames");
            return true;
        }

        static void Inject(Texture2D tex, int w, int h, float fx, float fy, float cx, float cy)
        {
            if (!ResolvePca()) return;
            _fTexture?.SetValue(_pca, tex);
            _fIsPlaying?.SetValue(_pca, (bool?)true);
            _fHasPermission?.SetValue(_pca, true);
            _fTimestamp?.SetValue(_pca, DateTime.UtcNow.Ticks); // any value > 0; the pose comes from the hook below
            _setResolution?.Invoke(_pca, new object[] { new Vector2Int(w, h) });
            // Intrinsics already in frame space: SensorResolution = current resolution → crop = whole frame.
            var intrinsics = new PassthroughCameraAccess.CameraIntrinsics
            {
                FocalLength = new Vector2(fx, fy),
                PrincipalPoint = new Vector2(cx, cy),
                SensorResolution = new Vector2Int(w, h),
                // Translation: Camera2 extrinsics (a few cm). Rotation: calibrated against depth (see above).
                LensOffset = new Pose(new Vector3(LensTranslation.x, LensTranslation.y, -LensTranslation.z),
                    UseCamera2LensRotation
                        ? Quaternion.Inverse(new Quaternion(-LensRotation.x, -LensRotation.y, LensRotation.z, LensRotation.w)) * Quaternion.Euler(180, 0, 0)
                        : HeadFromCameraCalibrated),
            };
            _setIntrinsics?.Invoke(_pca, new object[] { intrinsics });
            HookHeadPose();
        }

        // GetCameraPose() asks native MRUK for the head pose (GetHeadsetPoseAtTime), which has no camera in the Editor.
        // We swap the delegate for one that returns the CenterEyeAnchor (in OpenXR convention, since the PCA applies FlipZ).
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
            if (_fHeadPoseDelegate == null) { Debug.LogWarning("[LinkCameraFeed] MRUKNativeFuncs.GetHeadsetPoseAtTime not found"); _headPoseHooked = true; return; }
            _originalHeadPoseDelegate = _fHeadPoseDelegate.GetValue(null);
            var method = typeof(LinkCameraFeed).GetMethod(nameof(HeadPoseAtTime), BindingFlags.Static | BindingFlags.NonPublic);
            _fHeadPoseDelegate.SetValue(null, Delegate.CreateDelegate(_fHeadPoseDelegate.FieldType, method));
            _headPoseHooked = true;
            Debug.Log("[LinkCameraFeed] GetCameraPose hooked to the Editor head pose");
        }

        // In the Editor, PassthroughCameraAccess.Update() pulls a CPU image from native MRUK
        // (CameraAcquireLatestCpuImage — the Editor's own camera, at a different resolution) and calls
        // LoadRawTextureData on the SAME texture we inject: YOLO was receiving striped garbage. We null out the
        // delegate while the feed is on (the PCA then returns early in Update and doesn't touch the texture).
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
            _originalCpuImage = current; // MRUK may reload the native functions; we keep the most recent one
            _fCpuImage.SetValue(null, null);
            Debug.Log("[LinkCameraFeed] native MRUK CPU image disabled (no longer overwrites the texture)");
        }

        static void HeadPoseAtTime(long time, ref Vector3 position, ref Quaternion orientation)
        {
            Vector3 p; Quaternion r;
            if (LinkSession.TryGetReplayHead(out var recorded)) { p = recorded.position; r = recorded.rotation; } // recorded session
            else
            {
                var head = Head();
                if (head == null) { position = Vector3.zero; orientation = Quaternion.identity; return; }
                p = head.position; r = head.rotation;
            }
            position = new Vector3(p.x, p.y, -p.z);           // FlipZ: the PCA undoes it
            orientation = new Quaternion(-r.x, -r.y, r.z, r.w);
        }

        static string WireDetection()
        {
            try
            {
                if (!ResolvePca()) return "PassthroughCameraAccess not found";
                string msg = LinkHooks.WireDetection != null ? LinkHooks.WireDetection() : "the app does not wire up its own detection (LinkHooks.WireDetection)";
                if (Threshold >= 0f) msg += "; " + ApplyThreshold(Threshold);
                LastWiringReport = msg;
                Debug.Log("[LinkCameraFeed] " + msg);
                return msg;
            }
            catch (Exception e) { return "failed to wire up detection: " + e.Message; }
        }

        static string ApplyThreshold(float thr)
        {
            var agent = UnityEngine.Object.FindAnyObjectByType<Meta.XR.BuildingBlocks.AIBlocks.ObjectDetectionAgent>(FindObjectsInactive.Include);
            if (agent == null) return "no agent; ";
            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var fMin = agent.GetType().GetField("minConfidence", F);
            var provider = agent.GetType().GetField("providerAsset", F)?.GetValue(agent);
            var fThr = provider?.GetType().GetField("scoreThreshold", F);
            if (_origMinConf == null) _origMinConf = fMin?.GetValue(agent);
            if (_origScoreThr == null) _origScoreThr = fThr?.GetValue(provider);
            fMin?.SetValue(agent, thr);
            fThr?.SetValue(provider, thr);
            LinkHooks.ApplyAppThreshold?.Invoke(thr); // the app's own cutoff (in Hello World, LabelManager.minScore)
            return $"threshold {thr} (session; agent, provider and app); ";
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
            if (_pca != null) { _fIsPlaying?.SetValue(_pca, null); _fTexture?.SetValue(_pca, null); } // stops the agent too
            _pca = null; _head = null; _lastFrameId = 0;
            if (_testTexture != null) { UnityEngine.Object.DestroyImmediate(_testTexture); _testTexture = null; }
            Enabled = false;
        }
    }
}
