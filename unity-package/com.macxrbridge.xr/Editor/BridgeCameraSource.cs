// SPDX-License-Identifier: MIT
// MacXR Bridge: reads the Quest camera frames relayed by the Mac runtime.
// The runtime (liboxrsys-runtime.dylib) is already loaded in the Unity process as the active OpenXR
// runtime; we grab the C symbols (oxrsys_camera_*) via dlsym and copy the RGBA into a Texture2D.

using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MacXRBridge.EditorTools
{
    public static class BridgeCameraSource
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct FrameInfo
        {
            public uint width, height, valid, frameId;
            public float fx, fy, cx, cy;
            public float qx, qy, qz, qw;   // cameraPose.orientation
            public float px, py, pz;       // cameraPose.position
            public long captureTimeNs;
        }

        [DllImport("/usr/lib/libSystem.B.dylib")] static extern IntPtr dlopen(string path, int mode);
        [DllImport("/usr/lib/libSystem.B.dylib")] static extern IntPtr dlsym(IntPtr handle, string symbol);
        const int RTLD_NOLOAD = 0x10;
        const int RTLD_NOW = 0x2;

        delegate int ConnectedDelegate();
        delegate int GetLatestDelegate(IntPtr dst, uint dstCapacity, uint lastFrameId, out FrameInfo info);

        static ConnectedDelegate _connected;
        static GetLatestDelegate _getLatest;
        static bool _resolved;

        static Texture2D _texture;
        static FrameInfo _lastInfo;

        /// <summary>Each new frame: RGBA with row 0 at the bottom (like the texture). The buffer is reused: copy it.</summary>
        public static event Action<byte[], FrameInfo> FrameLoaded;

        /// <summary>After a replay (BridgeSession): starts pulling from the runtime again from scratch.</summary>
        public static void ResetAfterReplay()
        {
            _texture = null;
            _lastFrameId = 0;
            _lastInfo = default;
        }

        /// <summary>Latest frame already loaded (pulls a newer one if available), without competing with the feed.</summary>
        public static bool TryGetLatestTexture(out Texture2D texture, out FrameInfo info)
        {
            TryGetFrame(out _, out _);
            texture = _texture;
            info = _lastInfo;
            return texture != null && info.frameId != 0;
        }
        static uint _lastFrameId;
        static byte[] _buffer;
        static byte[] _flipped;

        static readonly IntPtr RTLD_DEFAULT = new IntPtr(-2);

        static bool Resolve()
        {
            if (_resolved) return true; // only cache success: outside Play the runtime isn't loaded
            // The runtime is already loaded in the process (active OpenXR runtime) and exports the symbols
            // with default visibility. RTLD_DEFAULT finds the LIVE instance without loading a 2nd copy
            // (which would have a different CameraRelay, without the frames).
            IntPtr pc = dlsym(RTLD_DEFAULT, "oxrsys_camera_connected");
            IntPtr pg = dlsym(RTLD_DEFAULT, "oxrsys_camera_get_latest");
            if (pc == IntPtr.Zero || pg == IntPtr.Zero)
            {
                // Fallback: get the handle of the image ALREADY loaded, by path (NOLOAD never loads a copy).
                string json = Environment.GetEnvironmentVariable("XR_RUNTIME_JSON");
                if (!string.IsNullOrEmpty(json))
                {
                    string dylib = Path.Combine(Path.GetDirectoryName(json), "liboxrsys-runtime.dylib");
                    IntPtr handle = dlopen(dylib, RTLD_NOLOAD | RTLD_NOW);
                    if (handle != IntPtr.Zero)
                    {
                        if (pc == IntPtr.Zero) pc = dlsym(handle, "oxrsys_camera_connected");
                        if (pg == IntPtr.Zero) pg = dlsym(handle, "oxrsys_camera_get_latest");
                    }
                }
            }
            if (pc == IntPtr.Zero || pg == IntPtr.Zero) return false; // runtime not loaded yet (outside Play)
            _connected = Marshal.GetDelegateForFunctionPointer<ConnectedDelegate>(pc);
            _getLatest = Marshal.GetDelegateForFunctionPointer<GetLatestDelegate>(pg);
            _resolved = true;
            return true;
        }

        public static bool IsConnected() => BridgeSession.Replaying || (Resolve() && _connected() != 0);

        /// <summary>Pulls the most recent frame (if newer). Updates the Texture2D and returns the FrameInfo.</summary>
        public static bool TryGetFrame(out Texture2D texture, out FrameInfo info)
        {
            texture = _texture;
            info = default;
            if (BridgeSession.Replaying)
            {
                // Recorded session in place of the real camera (the headset may not even be on).
                info = BridgeSession.ReplayInfo;
                var replay = BridgeSession.ReplayTexture;
                if (replay == null || info.frameId == 0 || info.frameId == _lastFrameId) return false;
                _lastFrameId = info.frameId;
                _lastInfo = info;
                _texture = replay;
                texture = replay;
                return true;
            }
            if (!Resolve()) return false;

            // First pass: get the dimensions without copying (capacity 0).
            FrameInfo probe;
            _getLatest(IntPtr.Zero, 0, _lastFrameId, out probe);
            if (probe.valid == 0 || probe.width == 0 || probe.height == 0) { info = probe; return false; }

            int need = (int)(probe.width * probe.height * 4);
            if (_buffer == null || _buffer.Length < need) _buffer = new byte[need];
            if (_texture == null || _texture.width != probe.width || _texture.height != probe.height)
            {
                _texture = new Texture2D((int)probe.width, (int)probe.height, TextureFormat.RGBA32, false);
            }

            GCHandle h = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
            try
            {
                int ok = _getLatest(h.AddrOfPinnedObject(), (uint)need, _lastFrameId, out info);
                if (ok == 0 || info.valid == 0) return false;
            }
            finally { h.Free(); }

            _lastFrameId = info.frameId;
            _lastInfo = info;
            // The runtime delivers RGBA top to bottom (CoreGraphics); Unity's Texture2D has row 0 at the BOTTOM.
            // Without flipping the rows, the image was upside down on the quad, in YOLO and in the ray projection
            // (Edson saw it "upside down and mirrored"; alignment with depth confirmed it: a vertical flip).
            int row = (int)info.width * 4, rows = (int)info.height;
            if (_flipped == null || _flipped.Length < need) _flipped = new byte[need];
            for (int y = 0; y < rows; y++) Buffer.BlockCopy(_buffer, y * row, _flipped, (rows - 1 - y) * row, row);
            _texture.LoadRawTextureData(_flipped);
            _texture.Apply(false);
            texture = _texture;
            FrameLoaded?.Invoke(_flipped, info);
            return true;
        }

        /// <summary>Saves the current frame as a PNG (for inspection) and returns a summary.</summary>
        public static string Capture(string path)
        {
            if (!IsConnected()) return "camera not connected (camera channel 9949)";
            if (!TryGetFrame(out var tex, out var info))
                return $"no new frame (valid={info.valid} {info.width}x{info.height} frameId={info.frameId})";
            if (string.IsNullOrEmpty(path)) path = Path.Combine(Directory.GetCurrentDirectory(), ".macxr", "camera.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // EncodeToPNG writes texture row 0 at the top of the PNG: we use the original buffer (top to bottom).
            var top = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
            top.LoadRawTextureData(_buffer);
            top.Apply(false);
            File.WriteAllBytes(path, top.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(top);
            return $"frame {info.width}x{info.height} frameId={info.frameId} fx={info.fx:F1} fy={info.fy:F1} " +
                   $"camPos=({info.px:F2},{info.py:F2},{info.pz:F2}) → {path}";
        }
    }
}
