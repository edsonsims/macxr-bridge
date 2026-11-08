// SPDX-License-Identifier: MIT
// Link para Mac: record and replay sessions ("session" command).
//   session rec [name] [fps=N]   records: Quest camera frames (JPEG), depth, head pose and the room (MRUK)
//   session stop                 stops recording or replay
//   session play [name] [loop] [speed=X] [rig]
//                                replays in Play Mode WITHOUT the headset: the recorded camera, depth and tracking
//                                (head, hands, controllers, microgestures — through the runtime, as if they came from the Quest)
//                                replace the real ones, so YOLO, raycasts, labels and interactions run again
//                                on the same scene (handy for working on the app without putting the headset on for every test)
//   session pause | resume | seek <s|+s|-s> | step [n] | speed <x>   replay timeline
//   session list | session       list / status
// Folder: <project>/Sessions/<name>-<date>/ (cam/*.jpg, camera.csv, depth.bin + depth.csv, poses.csv, tracking.bin,
// room.json). Old sessions without tracking.bin only move the head (LinkSessionRig).
// Quest Link on Windows does not record or replay sessions.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Meta.XR.MRUtilityKit;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace LinkParaMac.EditorTools
{
    [InitializeOnLoad]
    public static class LinkSession
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static string Root => Path.Combine(Directory.GetCurrentDirectory(), "Sessions");
        static double Now => EditorApplication.timeSinceStartup;
        static string _last = "-";

        static LinkSession()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += s =>
            {
                if (s != PlayModeStateChange.ExitingPlayMode) return;
                if (Recording) StopRecording();
                if (Replaying) StopReplay("left Play Mode");
            };
        }

        public static string Command(string arg)
        {
            string a = (arg ?? "").Trim();
            string verb = a.Split(' ')[0];
            string rest = a.Length > verb.Length ? a.Substring(verb.Length).Trim() : "";
            switch (verb)
            {
                case "rec": case "record": return StartRecording(rest);
                case "stop": return Recording ? StopRecording() : Replaying ? StopReplay("stopped by hand") : Status();
                case "play": case "replay": return StartReplay(rest);
                case "pause": return Pause(true);
                case "resume": return Pause(false);
                case "seek": return SeekCommand(rest);
                case "step": return Step(int.TryParse(rest, out var n) ? n : 1);
                case "speed": return double.TryParse(rest, NumberStyles.Float, Inv, out var sp) ? SetSpeed(sp) : "usage: session speed 0.5";
                case "list": return List();
                case "test": case "baseline": return LinkSessionTests.Command(verb, rest);
                case "report": return LinkSessionTests.Running ? LinkSessionTests.Progress : LinkSessionTests.LastReport;
                default: return Status();
            }
        }

        public static string Status()
        {
            if (Recording)
                return $"Session: recording for {Now - _recStart:F0}s → {_recDir}\n" +
                       $"  camera {_camFrames} frames ({_jpgWritten} JPEG written, {_camDropped} dropped), " +
                       $"depth {_depthFrames}, poses {_poseCount}, room {(_roomSaved ? "yes" : "no")}, tracking {(_tapeOk ? "yes" : "no (old runtime)")}";
            if (Replaying)
                return $"Session: replaying {Path.GetFileName(_rDir)} {(_rRunning ? $"{ReplayTime():F2}/{_rDuration:F1}s" : "(loading the room)")} " +
                       $"{(_rPaused ? "PAUSED " : "")}{(_rLoop ? $"looping (pass {_rLoops}) " : "")}speed {_rSpeed:F2} " +
                       $"frame {_rCamIdx + 1}/{_rCams.Count} depth {_rDepthIdx + 1}/{_rDepth.Count} " +
                       $"tracking={(_rTape ? "recorded (runtime)" : _rRig ? "head only (rig)" : "live")}";
            return "Session: stopped. Last: " + _last;
        }

        static Transform Head()
        {
            var rig = UnityEngine.Object.FindAnyObjectByType<OVRCameraRig>();
            return rig != null ? rig.centerEyeAnchor : (Camera.main != null ? Camera.main.transform : null);
        }

        static OVRCameraRig EnsureRig()
        {
            var rig = UnityEngine.Object.FindAnyObjectByType<OVRCameraRig>();
            if (rig != null && rig.GetComponent<LinkSessionRig>() == null) rig.gameObject.AddComponent<LinkSessionRig>();
            return rig;
        }

        static void Tick()
        {
            if (Replaying) { try { ReplayTick(); } catch (Exception e) { Debug.LogWarning("[LinkSession] " + e); StopReplay("error: " + e.Message); } }
            else if (Recording) LinkCameraSource.TryGetLatestTexture(out _, out _); // pulls frames even when the feed is off
        }

        // =============================== recording ===============================
        public static bool Recording { get; private set; }
        static string _recDir;
        static double _recStart, _lastCamT;
        static float _minInterval;
        static StreamWriter _poses, _camCsv, _depthCsv;
        static FileStream _depthBin;
        static Thread _jpgThread;
        static BlockingCollection<(string path, byte[] rgba, int w, int h)> _jpgQueue;
        static readonly ConcurrentBag<byte[]> _pool = new ConcurrentBag<byte[]>();
        static int _camFrames, _camDropped, _depthFrames, _poseCount;
        static volatile int _jpgWritten;
        static bool _roomSaved, _tapeOk;
        static byte[] _depthBytes;
        public static string RecordingDir => _recDir;
        public static double RecordingSeconds => Recording ? Now - _recStart : 0;

        static string StartRecording(string args)
        {
            if (Recording) return Status();
            if (Replaying) return "stop the replay first (session stop)";
            if (!EditorApplication.isPlaying) return "Play Mode only";
            if (!LinkCameraSource.IsConnected()) return "the Quest camera is not connected";
            string name = "sessao";
            _minInterval = 0f;
            foreach (var part in args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith("fps=") && float.TryParse(part.Substring(4), NumberStyles.Float, Inv, out var fps) && fps > 0) _minInterval = 0.95f / fps;
                else name = part;
            }
            _recDir = Path.Combine(Root, name + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(Path.Combine(_recDir, "cam"));
            _poses = new StreamWriter(Path.Combine(_recDir, "poses.csv")) { NewLine = "\n" };
            _poses.WriteLine("t,px,py,pz,qx,qy,qz,qw");
            _camCsv = new StreamWriter(Path.Combine(_recDir, "camera.csv")) { NewLine = "\n" };
            _camCsv.WriteLine("t,file,w,h,fx,fy,cx,cy");
            _depthCsv = new StreamWriter(Path.Combine(_recDir, "depth.csv")) { NewLine = "\n" };
            // v (view) already in Unity world space; r* = raw poses in the client's STAGE space (for the runtime raycast during replay).
            _depthCsv.WriteLine("t,offset,w,h,near,far,vpx,vpy,vpz,vqx,vqy,vqz,vqw,aL,aR,aU,aD,rvpx,rvpy,rvpz,rvqx,rvqy,rvqz,rvqw,rhpx,rhpy,rhpz,rhqx,rhqy,rhqz,rhqw");
            _depthBin = new FileStream(Path.Combine(_recDir, "depth.bin"), FileMode.Create, FileAccess.Write);

            _roomSaved = false;
            try
            {
                var mruk = MRUK.Instance;
                if (mruk != null && mruk.Rooms.Count > 0)
                {
                    File.WriteAllText(Path.Combine(_recDir, "room.json"), mruk.SaveSceneToJsonString(true, null));
                    _roomSaved = true;
                }
            }
            catch (Exception e) { Debug.LogWarning("[LinkSession] room not saved: " + e.Message); }

            var rig = EnsureRig();
            var ts = rig != null ? rig.trackingSpace : null;
            File.WriteAllText(Path.Combine(_recDir, "meta.txt"),
                $"nome={name}\ndata={DateTime.Now:yyyy-MM-dd HH:mm:ss}\nunity={Application.unityVersion}\n" +
                $"cena={UnityEngine.SceneManagement.SceneManager.GetActiveScene().path}\n" +
                $"trackingSpace={(ts != null ? $"{ts.position.ToString("F4")} {ts.rotation.eulerAngles.ToString("F2")}" : "-")}\n" +
                $"sala={(_roomSaved ? "room.json" : "no")}\n");

            _jpgQueue = new BlockingCollection<(string, byte[], int, int)>(12);
            var queue = _jpgQueue;
            _jpgWritten = 0;
            _jpgThread = new Thread(() =>
            {
                foreach (var item in queue.GetConsumingEnumerable())
                {
                    try
                    {
                        // Row 0 at the bottom, like the Unity texture: LoadImage during replay gives back the same texture.
                        byte[] jpg = ImageConversion.EncodeArrayToJPG(item.rgba, GraphicsFormat.R8G8B8A8_UNorm, (uint)item.w, (uint)item.h, 0, 90);
                        File.WriteAllBytes(item.path, jpg);
                        _jpgWritten++;
                    }
                    catch (Exception e) { Debug.LogWarning("[LinkSession] JPEG: " + e.Message); }
                    _pool.Add(item.rgba);
                }
            }) { IsBackground = true, Name = "LinkSession JPEG" };
            _jpgThread.Start();

            _camFrames = _camDropped = _depthFrames = _poseCount = 0;
            _lastCamT = -1;
            _tapeOk = LinkNative.TapeRecordStart(Path.Combine(_recDir, "tracking.bin")); // same t = 0 as the recording
            _recStart = Now;
            LinkCameraSource.FrameLoaded += OnCameraFrame;
            LinkDepth.FrameLoaded += OnDepthFrame;
            LinkSessionRig.Late = OnLate;
            Recording = true;
            Debug.Log("[LinkSession] recording → " + _recDir);
            return $"recording the session (camera{(_minInterval > 0 ? $" at {0.95f / _minInterval:F0} fps" : "")}, depth, {(_tapeOk ? "tracking (head, hands, controllers)" : "head")}" +
                   $"{(_roomSaved ? ", room" : ", NO room (run room-load first if you want one)")}) → {_recDir}";
        }

        static void OnCameraFrame(byte[] rgbaBottomFirst, LinkCameraSource.FrameInfo info)
        {
            if (!Recording) return;
            double t = Now - _recStart;
            if (_minInterval > 0 && _lastCamT >= 0 && t - _lastCamT < _minInterval) return;
            int need = (int)(info.width * info.height * 4);
            if (!_pool.TryTake(out var buf) || buf.Length != need) buf = new byte[need];
            Buffer.BlockCopy(rgbaBottomFirst, 0, buf, 0, need);
            string file = $"cam/{_camFrames + 1:D6}.jpg";
            if (!_jpgQueue.TryAdd((Path.Combine(_recDir, file), buf, (int)info.width, (int)info.height))) { _camDropped++; _pool.Add(buf); return; }
            _camFrames++;
            _lastCamT = t;
            float fy = info.fy < info.fx * 0.9f ? info.fx : info.fy; // same correction as the feed
            _camCsv.WriteLine(string.Format(Inv, "{0:F4},{1},{2},{3},{4:F3},{5:F3},{6:F3},{7:F3}", t, file, info.width, info.height, info.fx, fy, info.cx, info.cy));
        }

        static void OnDepthFrame(LinkDepth.DepthInfo info, ushort[] data, Pose view)
        {
            if (!Recording) return;
            double t = Now - _recStart;
            int bytes = (int)(info.width * info.height * 2);
            if (_depthBytes == null || _depthBytes.Length != bytes) _depthBytes = new byte[bytes];
            Buffer.BlockCopy(data, 0, _depthBytes, 0, bytes);
            long offset = _depthBin.Position;
            _depthBin.Write(_depthBytes, 0, bytes);
            _depthFrames++;
            _depthCsv.WriteLine(string.Format(Inv, "{0:F4},{1},{2},{3},{4:R},{5:R},{6:F5},{7:F5},{8:F5},{9:F6},{10:F6},{11:F6},{12:F6},{13:R},{14:R},{15:R},{16:R}",
                t, offset, info.width, info.height, info.nearZ, info.farZ,
                view.position.x, view.position.y, view.position.z, view.rotation.x, view.rotation.y, view.rotation.z, view.rotation.w,
                info.angleLeft, info.angleRight, info.angleUp, info.angleDown) +
                string.Format(Inv, ",{0:F5},{1:F5},{2:F5},{3:F6},{4:F6},{5:F6},{6:F6},{7:F5},{8:F5},{9:F5},{10:F6},{11:F6},{12:F6},{13:F6}",
                    info.vpx, info.vpy, info.vpz, info.vqx, info.vqy, info.vqz, info.vqw,
                    info.hpx, info.hpy, info.hpz, info.hqx, info.hqy, info.hqz, info.hqw));
        }

        static void OnLate()
        {
            if (!Recording) return;
            var head = Head();
            if (head == null) return;
            Vector3 p = head.position; Quaternion q = head.rotation;
            _poses.WriteLine(string.Format(Inv, "{0:F4},{1:F5},{2:F5},{3:F5},{4:F6},{5:F6},{6:F6},{7:F6}", Now - _recStart, p.x, p.y, p.z, q.x, q.y, q.z, q.w));
            _poseCount++;
        }

        static string StopRecording()
        {
            Recording = false;
            LinkCameraSource.FrameLoaded -= OnCameraFrame;
            LinkDepth.FrameLoaded -= OnDepthFrame;
            if (LinkSessionRig.Late == (Action)OnLate) LinkSessionRig.Late = null;
            double seconds = Now - _recStart;
            int tracking = _tapeOk ? LinkNative.TapeRecordStop() : 0;
            _jpgQueue.CompleteAdding();
            _jpgThread.Join(15000);
            _poses.Dispose(); _camCsv.Dispose(); _depthCsv.Dispose(); _depthBin.Dispose();
            long size = new DirectoryInfo(_recDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            _last = $"{Path.GetFileName(_recDir)}: {seconds:F0}s, {_jpgWritten} frames ({_camDropped} dropped), " +
                    $"{_depthFrames} depth frames, {_poseCount} poses, {tracking} tracking packets, room {(_roomSaved ? "yes" : "no")}, {size / 1048576.0:F0} MB";
            Debug.Log("[LinkSession] recorded " + _last);
            return "session recorded: " + _last + "\n→ " + _recDir;
        }

        // =============================== replay ===============================
        struct PoseRec { public double t; public Vector3 p; public Quaternion q; }
        struct CamRec { public double t; public string file; public uint w, h; public float fx, fy, cx, cy; }
        struct DepthRec { public double t; public long offset; public uint w, h; public float near, far, aL, aR, aU, aD; public Pose view; public float[] raw; }

        public static bool Replaying { get; private set; }
        static string _rDir;
        static List<PoseRec> _rPoses = new List<PoseRec>();
        static List<CamRec> _rCams = new List<CamRec>();
        static List<DepthRec> _rDepth = new List<DepthRec>();
        static double _rDuration, _rBaseT, _rBaseWall, _rSpeed = 1;
        static bool _rLoop, _rRig, _rRunning, _rWorldLock, _rTape, _rPaused;
        static int _rCamIdx, _rDepthIdx, _rLoops;
        static uint _rFrameCounter;
        static Texture2D _rTex;
        static LinkCameraSource.FrameInfo _rInfo;
        static FileStream _rDepthBin;
        static byte[] _rDepthBytes;
        static ushort[] _rDepthData;
        static Pose? _rHead;

        public static Texture2D ReplayTexture => _rTex;
        public static string ReplayName => Replaying ? Path.GetFileName(_rDir) : null;
        public static double Duration => _rDuration;
        public static double CurrentTime => ReplayTime();
        public static bool Paused => _rPaused;
        public static double Speed => _rSpeed;
        public static bool TapeActive => _rTape;
        public static int CameraFrameIndex => _rCamIdx;
        public static int CameraFrameCount => _rCams.Count;
        /// <summary>Why the last replay stopped ("end", "stopped by hand", "left Play Mode"…).</summary>
        public static string LastStopReason { get; private set; } = "";
        public static LinkCameraSource.FrameInfo ReplayInfo => _rInfo;

        /// <summary>Recorded head pose at the current replay time.</summary>
        public static bool TryGetReplayHead(out Pose pose)
        {
            pose = _rHead ?? default;
            return Replaying && _rHead.HasValue;
        }

        static double ReplayTime() => _rRunning ? _rBaseT + (_rPaused ? 0 : (Now - _rBaseWall) * _rSpeed) : 0;

        static string FindSession(string name)
        {
            if (!Directory.Exists(Root)) return null;
            var dirs = new DirectoryInfo(Root).GetDirectories().Where(d => File.Exists(Path.Combine(d.FullName, "camera.csv")))
                .OrderByDescending(d => d.Name.Length > 15 ? d.Name.Substring(d.Name.Length - 15) : d.Name).ToList();
            if (string.IsNullOrEmpty(name) || name == "last" || name == "ultima" || name == "última") return dirs.FirstOrDefault()?.FullName;
            var exact = dirs.FirstOrDefault(d => d.Name == name);
            if (exact != null) return exact.FullName;
            return dirs.FirstOrDefault(d => d.Name.StartsWith(name + "-") || d.Name.StartsWith(name))?.FullName;
        }

        static IEnumerable<string[]> Csv(string path)
        {
            if (!File.Exists(path)) yield break;
            foreach (var line in File.ReadLines(path).Skip(1))
                if (!string.IsNullOrWhiteSpace(line)) yield return line.Split(',');
        }

        static float F(string s) => float.Parse(s, NumberStyles.Float, Inv);
        static double D(string s) => double.Parse(s, NumberStyles.Float, Inv);

        static string StartReplay(string args)
        {
            if (Recording) return "stop the recording first (session stop)";
            if (!EditorApplication.isPlaying) return "Play Mode only";
            if (Replaying) StopReplay("replaced");
            string name = "";
            _rLoop = false; _rSpeed = 1; _rPaused = false;
            bool forceRig = false, noRig = false;
            foreach (var part in args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == "loop") _rLoop = true;
                else if (part == "norig") noRig = true;
                else if (part == "rig") forceRig = true;
                else if (part == "paused") _rPaused = true;
                else if (part.StartsWith("speed=") && double.TryParse(part.Substring(6), NumberStyles.Float, Inv, out var sp) && sp > 0.05) _rSpeed = sp;
                else name = part;
            }
            _rDir = FindSession(name);
            if (_rDir == null) return "session not found: " + (name == "" ? "(none recorded)" : name) + "\n" + List();

            _rPoses = Csv(Path.Combine(_rDir, "poses.csv")).Select(c => new PoseRec
            { t = D(c[0]), p = new Vector3(F(c[1]), F(c[2]), F(c[3])), q = new Quaternion(F(c[4]), F(c[5]), F(c[6]), F(c[7])) }).ToList();
            _rCams = Csv(Path.Combine(_rDir, "camera.csv")).Select(c => new CamRec
            { t = D(c[0]), file = c[1], w = uint.Parse(c[2]), h = uint.Parse(c[3]), fx = F(c[4]), fy = F(c[5]), cx = F(c[6]), cy = F(c[7]) })
                .Where(c => File.Exists(Path.Combine(_rDir, c.file))).ToList(); // the recording may have stopped with JPEGs still queued
            _rDepth = Csv(Path.Combine(_rDir, "depth.csv")).Select(c => new DepthRec
            {
                t = D(c[0]), offset = long.Parse(c[1]), w = uint.Parse(c[2]), h = uint.Parse(c[3]), near = F(c[4]), far = F(c[5]),
                view = new Pose(new Vector3(F(c[6]), F(c[7]), F(c[8])), new Quaternion(F(c[9]), F(c[10]), F(c[11]), F(c[12]))),
                aL = F(c[13]), aR = F(c[14]), aU = F(c[15]), aD = F(c[16]),
                raw = c.Length >= 31 ? c.Skip(17).Take(14).Select(F).ToArray() : null,
            }).ToList();
            if (_rCams.Count == 0) return "session has no camera frames: " + _rDir;
            _rDuration = Math.Max(_rCams[_rCams.Count - 1].t, Math.Max(_rPoses.Count > 0 ? _rPoses[_rPoses.Count - 1].t : 0, _rDepth.Count > 0 ? _rDepth[_rDepth.Count - 1].t : 0));
            string bin = Path.Combine(_rDir, "depth.bin");
            _rDepthBin = _rDepth.Count > 0 && File.Exists(bin) ? new FileStream(bin, FileMode.Open, FileAccess.Read) : null;
            _rCamIdx = _rDepthIdx = -1; _rLoops = 0; _rHead = null; _rRunning = false;
            // Tracking recorded by the runtime (head, hands, controllers, microgestures): the head comes through it, without touching the rig.
            _rTape = File.Exists(Path.Combine(_rDir, "tracking.bin")) && LinkNative.TapeSupported;
            _rRig = forceRig || (!_rTape && !noRig);
            Replaying = true;
            LinkDepth.ReplayActive = true;
            LinkNative.DepthReplayMode(true); // the official (runtime) raycast also answers from the recorded depth
            if (_rRig)
            {
                EnsureRig();
                LinkSessionRig.TargetHead = () => Replaying && _rRunning ? _rHead : null;
            }

            // Room first (with the TrackingSpace still in place), then the clock starts.
            string room = Path.Combine(_rDir, "room.json");
            string roomMsg = "no recorded room";
            if (File.Exists(room))
            {
                var mruk = RoomTestTools.Ensure();
                _rWorldLock = mruk.EnableWorldLock;
                mruk.EnableWorldLock = false; // otherwise MRUK fights the recorded head over the TrackingSpace
                roomMsg = "loading the recorded room";
                mruk.LoadSceneFromJsonString(File.ReadAllText(room)).ContinueWith(t =>
                {
                    Debug.Log("[LinkSession] session room: " + (t.IsFaulted ? "failed " + t.Exception?.GetBaseException().Message : t.Result.ToString()));
                    BeginClock();
                }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
            }
            else BeginClock();

            Debug.Log("[LinkSession] replaying " + _rDir);
            return $"replaying {Path.GetFileName(_rDir)}: {_rDuration:F1}s, {_rCams.Count} frames, {_rDepth.Count} depth frames, " +
                   $"{_rPoses.Count} poses; {roomMsg}{(_rLoop ? "; looping" : "")}" +
                   $"{(_rTape ? "; recorded tracking (head, hands, controllers) comes in through the runtime" : _rRig ? "; the app's head follows the recorded one" : "")}";
        }

        static void BeginClock()
        {
            if (!Replaying || _rRunning) return;
            if (_rTape && LinkNative.TapeReplayStart(Path.Combine(_rDir, "tracking.bin")) <= 0) { _rTape = false; _rRig = true; }
            _rBaseT = 0;
            _rBaseWall = Now;
            _rRunning = true;
            PushTapeClock();
        }

        static void PushTapeClock()
        {
            if (_rTape) LinkNative.TapeReplayClock(ReplayTime(), _rPaused ? 0 : _rSpeed);
        }

        // ---------- timeline ----------
        public static string Pause(bool pause)
        {
            if (!Replaying || !_rRunning) return "no replay running";
            if (_rPaused == pause) return Status();
            _rBaseT = ReplayTime();
            _rBaseWall = Now;
            _rPaused = pause;
            PushTapeClock();
            return (pause ? "paused at " : "resuming from ") + _rBaseT.ToString("F2", Inv) + " s";
        }

        public static void Seek(double t)
        {
            if (!Replaying || !_rRunning) return;
            _rBaseT = Math.Max(0, Math.Min(_rDuration, t));
            _rBaseWall = Now;
            _rCamIdx = _rDepthIdx = -1; // reloads the frame and depth for the new time
            PushTapeClock();
        }

        static string SeekCommand(string arg)
        {
            if (!Replaying || !_rRunning) return "no replay running";
            arg = arg.Trim();
            if (!double.TryParse(arg.TrimStart('+'), NumberStyles.Float, Inv, out var v)) return "usage: session seek 12.5 | +2 | -2";
            Seek(arg.StartsWith("+") || arg.StartsWith("-") ? ReplayTime() + v : v);
            return $"at {ReplayTime():F2}/{_rDuration:F1} s";
        }

        public static string Step(int frames)
        {
            if (!Replaying || !_rRunning) return "no replay running";
            if (!_rPaused) Pause(true);
            int current = Math.Max(0, LastAtOrBefore(_rCams.Count, i => _rCams[i].t, ReplayTime()));
            int target = Math.Max(0, Math.Min(_rCams.Count - 1, current + frames));
            Seek(_rCams[target].t + 1e-4);
            return $"frame {target + 1}/{_rCams.Count} ({_rCams[target].t:F2} s, paused)";
        }

        public static string SetSpeed(double speed)
        {
            if (!Replaying || !_rRunning) return "no replay running";
            _rBaseT = ReplayTime();
            _rBaseWall = Now;
            _rSpeed = Math.Max(0.05, Math.Min(4.0, speed));
            PushTapeClock();
            return $"speed {_rSpeed:F2}";
        }

        static void ReplayTick()
        {
            if (!EditorApplication.isPlaying) { StopReplay("left Play Mode"); return; }
            if (!_rRunning) return;
            double t = ReplayTime();
            if (t > _rDuration)
            {
                if (!_rLoop) { StopReplay("end"); return; }
                _rLoops++;
                _rBaseT = 0;
                _rBaseWall = Now;
                _rCamIdx = _rDepthIdx = -1;
                t = 0;
            }
            _rHead = HeadAt(t);
            PushTapeClock();

            int ci = LastAtOrBefore(_rCams.Count, i => _rCams[i].t, t);
            if (ci >= 0 && ci != _rCamIdx) { _rCamIdx = ci; LoadCamera(_rCams[ci], t); }
            int di = LastAtOrBefore(_rDepth.Count, i => _rDepth[i].t, t);
            if (di >= 0 && di != _rDepthIdx) { _rDepthIdx = di; LoadDepth(_rDepth[di]); }
        }

        static int LastAtOrBefore(int count, Func<int, double> time, double t)
        {
            int lo = 0, hi = count - 1, best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (time(mid) <= t) { best = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return best;
        }

        static Pose? HeadAt(double t)
        {
            if (_rPoses.Count == 0) return null;
            int i = LastAtOrBefore(_rPoses.Count, k => _rPoses[k].t, t);
            if (i < 0) return new Pose(_rPoses[0].p, _rPoses[0].q);
            if (i >= _rPoses.Count - 1) return new Pose(_rPoses[i].p, _rPoses[i].q);
            var a = _rPoses[i]; var b = _rPoses[i + 1];
            float u = (float)((t - a.t) / Math.Max(1e-6, b.t - a.t));
            return new Pose(Vector3.Lerp(a.p, b.p, u), Quaternion.Slerp(a.q, b.q, u));
        }

        static void LoadCamera(CamRec c, double t)
        {
            if (_rTex == null) _rTex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "Link Session Frame" };
            _rTex.LoadImage(File.ReadAllBytes(Path.Combine(_rDir, c.file)), false);
            _rFrameCounter++;
            _rInfo = new LinkCameraSource.FrameInfo
            {
                width = c.w, height = c.h, valid = 1, frameId = _rFrameCounter,
                fx = c.fx, fy = c.fy, cx = c.cx, cy = c.cy,
                captureTimeNs = (long)(t * 1e9),
            };
        }

        static void LoadDepth(DepthRec d)
        {
            if (_rDepthBin == null) return;
            int bytes = (int)(d.w * d.h * 2);
            if (_rDepthBytes == null || _rDepthBytes.Length != bytes) _rDepthBytes = new byte[bytes];
            _rDepthBin.Seek(d.offset, SeekOrigin.Begin);
            int read = 0;
            while (read < bytes) { int n = _rDepthBin.Read(_rDepthBytes, read, bytes - read); if (n <= 0) return; read += n; }
            if (_rDepthData == null || _rDepthData.Length != d.w * d.h) _rDepthData = new ushort[d.w * d.h];
            Buffer.BlockCopy(_rDepthBytes, 0, _rDepthData, 0, bytes);
            var info = new LinkDepth.DepthInfo
            {
                width = d.w, height = d.h, valid = 1, frameId = (uint)(_rDepthIdx + 1),
                nearZ = d.near, farZ = d.far, angleLeft = d.aL, angleRight = d.aR, angleUp = d.aU, angleDown = d.aD,
            };
            LinkDepth.InjectReplay(info, _rDepthData, d.view);

            // For the runtime: poses in the client's STAGE space. Old sessions: undo Unity's conversion (FlipZ).
            var r = d.raw ?? new[] { d.view.position.x, d.view.position.y, -d.view.position.z, -d.view.rotation.x, -d.view.rotation.y, d.view.rotation.z, d.view.rotation.w,
                                      d.view.position.x, d.view.position.y, -d.view.position.z, -d.view.rotation.x, -d.view.rotation.y, d.view.rotation.z, d.view.rotation.w };
            info.vpx = r[0]; info.vpy = r[1]; info.vpz = r[2]; info.vqx = r[3]; info.vqy = r[4]; info.vqz = r[5]; info.vqw = r[6];
            info.hpx = r[7]; info.hpy = r[8]; info.hpz = r[9]; info.hqx = r[10]; info.hqy = r[11]; info.hqz = r[12]; info.hqw = r[13];
            LinkNative.DepthInject(_rDepthData, info);
        }

        static string StopReplay(string why)
        {
            if (!Replaying) return Status();
            string summary = $"{Path.GetFileName(_rDir)} ({why}; {ReplayTime():F1}s, {_rCamIdx + 1} frames, loops {_rLoops})";
            LastStopReason = why;
            Replaying = false;
            _rRunning = false;
            _rHead = null;
            LinkSessionRig.TargetHead = null;
            if (_rTape) LinkNative.TapeReplayStop();
            LinkNative.DepthReplayMode(false);
            _rTape = false;
            _rPaused = false;
            LinkDepth.EndReplay();
            LinkCameraSource.ResetAfterReplay();
            _rDepthBin?.Dispose(); _rDepthBin = null;
            if (MRUK.Instance != null && File.Exists(Path.Combine(_rDir, "room.json"))) MRUK.Instance.EnableWorldLock = _rWorldLock;
            if (_rTex != null) { UnityEngine.Object.Destroy(_rTex); _rTex = null; }
            _last = "replay of " + summary;
            Debug.Log("[LinkSession] " + _last);
            return "replay stopped: " + summary;
        }

        static string List()
        {
            if (!Directory.Exists(Root)) return "no sessions recorded (the Sessions/ folder does not exist yet)";
            var lines = new DirectoryInfo(Root).GetDirectories().OrderBy(d => d.Name).Select(d =>
            {
                int cams = Math.Max(0, File.Exists(Path.Combine(d.FullName, "camera.csv")) ? File.ReadLines(Path.Combine(d.FullName, "camera.csv")).Count() - 1 : 0);
                string last = File.Exists(Path.Combine(d.FullName, "camera.csv")) ? File.ReadLines(Path.Combine(d.FullName, "camera.csv")).LastOrDefault() : null;
                string dur = last != null && cams > 0 ? last.Split(',')[0] : "0";
                bool room = File.Exists(Path.Combine(d.FullName, "room.json"));
                return $"  {d.Name}: {dur}s, {cams} frames{(room ? ", with room" : "")}";
            });
            return "Sessions in " + Root + ":\n" + string.Join("\n", lines);
        }
    }
}
