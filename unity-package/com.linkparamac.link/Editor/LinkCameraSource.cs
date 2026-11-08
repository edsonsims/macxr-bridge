// SPDX-License-Identifier: MIT
// Link para Mac: lê os quadros da câmera do Quest que o runtime do Mac relaya.
// O runtime (liboxrsys-runtime.dylib) já está carregado no processo do Unity como runtime OpenXR
// ativo; pegamos os símbolos C (oxrsys_camera_*) por dlsym e copiamos o RGBA pra uma Texture2D.

using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace LinkParaMac.EditorTools
{
    public static class LinkCameraSource
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

        /// <summary>Cada quadro novo: RGBA com a linha 0 embaixo (como a textura). O buffer é reaproveitado: copie.</summary>
        public static event Action<byte[], FrameInfo> FrameLoaded;

        /// <summary>Depois de uma reprodução (LinkSession): volta a puxar do runtime do zero.</summary>
        public static void ResetAfterReplay()
        {
            _texture = null;
            _lastFrameId = 0;
            _lastInfo = default;
        }

        /// <summary>Último quadro já carregado (puxa um mais novo se houver), sem disputar com o feed.</summary>
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
            if (_resolved) return true; // só guarda o sucesso: fora do Play o runtime não está carregado
            // O runtime já está carregado no processo (runtime OpenXR ativo) e exporta os símbolos
            // com visibilidade default. RTLD_DEFAULT acha a instância VIVA, sem carregar uma 2ª cópia
            // (que teria um CameraRelay diferente, sem os quadros).
            IntPtr pc = dlsym(RTLD_DEFAULT, "oxrsys_camera_connected");
            IntPtr pg = dlsym(RTLD_DEFAULT, "oxrsys_camera_get_latest");
            if (pc == IntPtr.Zero || pg == IntPtr.Zero)
            {
                // Fallback: pega o handle da imagem JÁ carregada pelo caminho (NOLOAD nunca carrega cópia).
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
            if (pc == IntPtr.Zero || pg == IntPtr.Zero) return false; // runtime ainda não carregado (fora do Play)
            _connected = Marshal.GetDelegateForFunctionPointer<ConnectedDelegate>(pc);
            _getLatest = Marshal.GetDelegateForFunctionPointer<GetLatestDelegate>(pg);
            _resolved = true;
            return true;
        }

        public static bool IsConnected() => LinkSession.Replaying || (Resolve() && _connected() != 0);

        /// <summary>Puxa o quadro mais recente (se mais novo). Atualiza a Texture2D e devolve o FrameInfo.</summary>
        public static bool TryGetFrame(out Texture2D texture, out FrameInfo info)
        {
            texture = _texture;
            info = default;
            if (LinkSession.Replaying)
            {
                // Sessão gravada no lugar da câmera real (o headset pode nem estar ligado).
                info = LinkSession.ReplayInfo;
                var replay = LinkSession.ReplayTexture;
                if (replay == null || info.frameId == 0 || info.frameId == _lastFrameId) return false;
                _lastFrameId = info.frameId;
                _lastInfo = info;
                _texture = replay;
                texture = replay;
                return true;
            }
            if (!Resolve()) return false;

            // Primeira passada: descobrir dimensões sem copiar (capacidade 0).
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
            // O runtime entrega o RGBA de cima pra baixo (CoreGraphics); a Texture2D do Unity tem a linha 0 EMBAIXO.
            // Sem inverter as linhas, a imagem ficava de cabeça pra baixo no quad, no YOLO e na projeção dos raios
            // (o Edson viu "de cabeça pra baixo e espelhada"; o alinhamento com a profundidade confirmou: inversão vertical).
            int row = (int)info.width * 4, rows = (int)info.height;
            if (_flipped == null || _flipped.Length < need) _flipped = new byte[need];
            for (int y = 0; y < rows; y++) Buffer.BlockCopy(_buffer, y * row, _flipped, (rows - 1 - y) * row, row);
            _texture.LoadRawTextureData(_flipped);
            _texture.Apply(false);
            texture = _texture;
            FrameLoaded?.Invoke(_flipped, info);
            return true;
        }

        /// <summary>Salva o quadro atual como PNG (pra inspeção) e devolve um resumo.</summary>
        public static string Capture(string path)
        {
            if (!IsConnected()) return "câmera não conectada (canal camera 9949)";
            if (!TryGetFrame(out var tex, out var info))
                return $"sem quadro novo (valid={info.valid} {info.width}x{info.height} frameId={info.frameId})";
            if (string.IsNullOrEmpty(path)) path = Path.Combine(Directory.GetCurrentDirectory(), ".link", "camera.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // EncodeToPNG grava a linha 0 da textura no topo do PNG: usamos o buffer original (de cima pra baixo).
            var top = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
            top.LoadRawTextureData(_buffer);
            top.Apply(false);
            File.WriteAllBytes(path, top.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(top);
            return $"quadro {info.width}x{info.height} frameId={info.frameId} fx={info.fx:F1} fy={info.fy:F1} " +
                   $"camPos=({info.px:F2},{info.py:F2},{info.pz:F2}) → {path}";
        }
    }
}
