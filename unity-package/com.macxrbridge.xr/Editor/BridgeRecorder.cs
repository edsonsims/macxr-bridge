// SPDX-License-Identifier: MIT
// MacXR Bridge: mixed reality recording on the Mac ("record start [name] [nomic]" / "record stop" commands).
// Video: the BridgeComposite image (real Quest camera 1280x960 + app at the camera's pose/intrinsics) piped
// to ffmpeg (hardware h264). Sound: the app's (BridgeAudioTap) + your voice from the headset microphone (runtime),
// mixed at the end. File in <project>/Recordings/. Quest Link on Windows can't access the headset camera.
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace MacXRBridge.EditorTools
{
    [InitializeOnLoad]
    public static class BridgeRecorder
    {
        public const string Ffmpeg = "/opt/homebrew/bin/ffmpeg";
        public static bool Recording { get; private set; }
        public static string CurrentPath => _finalPath;
        public static double Seconds => Recording ? EditorApplication.timeSinceStartup - _startTime : 0;

        static int _w, _h;
        static Process _ffmpeg;
        static Thread _writer;
        static BlockingCollection<byte[]> _frames;
        static string _videoPath, _audioPath, _micPath, _finalPath, _ffmpegLog;
        static int _dropped, _written;
        static double _startTime;
        static Wav _app, _mic;
        static bool _withMic;
        static float[] _micBuffer = new float[9600];
        static string _lastResult = "-";

        sealed class Wav
        {
            readonly FileStream _file;
            public long Samples;
            public int Channels, Rate;
            public Wav(string path, int rate) { _file = new FileStream(path, FileMode.Create, FileAccess.Write); _file.Write(new byte[44], 0, 44); Rate = rate; }
            public void Write(float[] block, int count)
            {
                var bytes = new byte[count * 2];
                for (int i = 0; i < count; i++)
                {
                    short s = (short)(Mathf.Clamp(block[i], -1f, 1f) * 32767f);
                    bytes[2 * i] = (byte)(s & 0xff);
                    bytes[2 * i + 1] = (byte)((s >> 8) & 0xff);
                }
                _file.Write(bytes, 0, bytes.Length);
                Samples += count;
            }
            public void Finish()
            {
                int channels = Math.Max(1, Channels);
                long dataBytes = Samples * 2;
                _file.Seek(0, SeekOrigin.Begin);
                using (var bw = new BinaryWriter(_file, System.Text.Encoding.ASCII, true))
                {
                    bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); bw.Write((int)(36 + dataBytes));
                    bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE")); bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
                    bw.Write(16); bw.Write((short)1); bw.Write((short)channels); bw.Write(Rate);
                    bw.Write(Rate * channels * 2); bw.Write((short)(channels * 2)); bw.Write((short)16);
                    bw.Write(System.Text.Encoding.ASCII.GetBytes("data")); bw.Write((int)dataBytes);
                }
                _file.Dispose();
            }
        }

        static BridgeRecorder()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += s => { if (s == PlayModeStateChange.ExitingPlayMode && Recording) Stop(); };
        }

        public static string Command(string arg)
        {
            string a = (arg ?? "").Trim();
            if (a.StartsWith("start"))
            {
                string rest = a.Length > 5 ? a.Substring(5).Trim() : "";
                bool mic = !rest.Contains("nomic");
                rest = rest.Replace("nomic", "").Trim();
                return Start(rest, mic);
            }
            if (a == "stop") return Stop();
            return Status();
        }

        public static string Status() => Recording
            ? $"Recording for {Seconds:F0}s: {_written} frames ({_dropped} dropped){(_withMic ? " + your voice" : "")} → {_finalPath}"
            : "Recording: stopped. Last: " + _lastResult;

        public static string Start(string name, bool withMic = true)
        {
            if (Recording) return Status();
            if (!EditorApplication.isPlaying) return "Play Mode only";
            if (!File.Exists(Ffmpeg)) return "ffmpeg not found at " + Ffmpeg + " (brew install ffmpeg)";
            if (!BridgeCameraSource.TryGetLatestTexture(out var tex, out _)) return "the Quest camera hasn't arrived yet (wait for real detection to turn on)";
            _w = tex.width; _h = tex.height;

            string dir = Path.Combine(Directory.GetCurrentDirectory(), "Recordings");
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string baseName = string.IsNullOrEmpty(name) ? "macxr-" + stamp : name + "-" + stamp;
            _videoPath = Path.Combine(dir, baseName + ".video.mp4");
            _audioPath = Path.Combine(dir, baseName + ".app.wav");
            _micPath = Path.Combine(dir, baseName + ".mic.wav");
            _finalPath = Path.Combine(dir, baseName + ".mp4");
            _ffmpegLog = Path.Combine(dir, baseName + ".ffmpeg.log");

            var psi = new ProcessStartInfo(Ffmpeg,
                // Timestamp = arrival at ffmpeg: the video follows the wall clock and lines up with the recorded audio.
                $"-y -loglevel error -use_wallclock_as_timestamps 1 -f rawvideo -pix_fmt rgba -s {_w}x{_h} -i - " +
                $"-vf vflip -fps_mode cfr -r 30 -c:v h264_videotoolbox -b:v 16M -pix_fmt yuv420p \"{_videoPath}\"")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, CreateNoWindow = true,
            };
            _ffmpeg = Process.Start(psi);
            var errLog = _ffmpegLog;
            _ffmpeg.ErrorDataReceived += (_, e) => { if (e.Data != null) File.AppendAllText(errLog, e.Data + "\n"); };
            _ffmpeg.BeginErrorReadLine();
            _frames = new BlockingCollection<byte[]>(8);
            var stdin = _ffmpeg.StandardInput.BaseStream;
            var frames = _frames;
            _writer = new Thread(() =>
            {
                try { foreach (var f in frames.GetConsumingEnumerable()) { stdin.Write(f, 0, f.Length); Interlocked.Increment(ref _written); } }
                catch (Exception e) { Debug.LogWarning("[BridgeRecorder] pipe: " + e.Message); }
                try { stdin.Close(); } catch { }
            }) { IsBackground = true, Name = "BridgeRecorder writer" };
            _writer.Start();

            _app = new Wav(_audioPath, AudioSettings.outputSampleRate);
            MacXRBridge.BridgeAudioTap.RecordQueue = new ConcurrentQueue<float[]>();
            _withMic = withMic;
            _mic = null;
            if (withMic)
            {
                while (BridgeNative.MicPull(_micBuffer, out _) > 0) { } // drop what was in the ring buffer: start from now
                _mic = new Wav(_micPath, 48000) { Channels = 1 };
            }

            _dropped = _written = 0;
            _startTime = EditorApplication.timeSinceStartup;
            Recording = true;
            BridgeComposite.Composited += OnComposited;
            BridgeComposite.AddUser();
            Debug.Log($"[BridgeRecorder] recording {_w}x{_h} → {_finalPath}");
            return $"recording {_w}x{_h} at 30 fps (real camera + app + app audio{(withMic ? " + your voice" : "")}) → {_finalPath}";
        }

        static void OnComposited(RenderTexture rt, BridgeCameraSource.FrameInfo info)
        {
            if (!Recording || rt.width != _w || rt.height != _h) return;
            AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, req =>
            {
                if (!Recording || req.hasError) return;
                if (!_frames.TryAdd(req.GetData<byte>().ToArray())) _dropped++;
            });
        }

        static void Tick()
        {
            if (!Recording) return;
            DrainAudio();
        }

        static void DrainAudio()
        {
            var q = MacXRBridge.BridgeAudioTap.RecordQueue;
            if (q != null && _app != null)
            {
                if (_app.Channels == 0) _app.Channels = Math.Max(1, MacXRBridge.BridgeAudioTap.LastChannels);
                while (q.TryDequeue(out var block)) _app.Write(block, block.Length);
            }
            if (_mic != null)
            {
                int n;
                while ((n = BridgeNative.MicPull(_micBuffer, out int rate)) > 0)
                {
                    if (rate > 0) _mic.Rate = rate;
                    _mic.Write(_micBuffer, n);
                }
            }
        }

        public static string Stop()
        {
            if (!Recording) return Status();
            DrainAudio();
            Recording = false;
            BridgeComposite.Composited -= OnComposited;
            BridgeComposite.RemoveUser();
            MacXRBridge.BridgeAudioTap.RecordQueue = null;
            _app.Finish();
            _mic?.Finish();
            _frames.CompleteAdding();
            _writer.Join(5000);
            _ffmpeg.WaitForExit(15000);
            double seconds = EditorApplication.timeSinceStartup - _startTime;

            // Mux video + app audio + voice (the audio starts together with the video; -shortest trims the excess).
            bool hasApp = _app.Samples > 0, hasMic = _mic != null && _mic.Samples > 0;
            string result;
            if (File.Exists(_videoPath) && (hasApp || hasMic))
            {
                string inputs = $"-i \"{_videoPath}\"" + (hasApp ? $" -i \"{_audioPath}\"" : "") + (hasMic ? $" -i \"{_micPath}\"" : "");
                string audio = hasApp && hasMic
                    ? "-filter_complex \"[2:a]volume=2.0,aformat=channel_layouts=stereo[m];[1:a]aformat=channel_layouts=stereo[a];[a][m]amix=inputs=2:duration=first:normalize=0[out]\" -map 0:v -map \"[out]\""
                    : hasMic ? "-filter_complex \"[1:a]volume=2.0[out]\" -map 0:v -map \"[out]\"" : "-map 0:v -map 1:a";
                var mux = Process.Start(new ProcessStartInfo(Ffmpeg,
                    $"-y -loglevel error {inputs} {audio} -c:v copy -c:a aac -b:a 192k -shortest \"{_finalPath}\"")
                    { UseShellExecute = false, CreateNoWindow = true });
                mux.WaitForExit(60000);
                if (File.Exists(_finalPath)) { File.Delete(_videoPath); File.Delete(_audioPath); if (File.Exists(_micPath)) File.Delete(_micPath); }
                result = _finalPath + (hasMic ? " (with your voice)" : "");
            }
            else
            {
                if (File.Exists(_videoPath)) File.Move(_videoPath, _finalPath);
                result = _finalPath + " (no audio)";
            }
            _lastResult = $"{result} — {seconds:F0}s, {_written} frames, {_dropped} dropped";
            Debug.Log("[BridgeRecorder] " + _lastResult);
            return "recording saved: " + _lastResult;
        }
    }
}
