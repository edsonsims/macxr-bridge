// SPDX-License-Identifier: MIT
// Link para Mac: live mixed reality spectator (what the Quest camera sees + the app on top).
//  - "Window > Link para Mac > Spectator" window (can be captured in OBS as a window);
//  - "stream on [port]" serves an MJPEG stream on the local network: http://<mac-ip>:8765/ opens in a browser, on a
//    phone, on a TV or as a "Browser/Media Source" in OBS, to show the app live to people in the room.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace LinkParaMac.EditorTools
{
    public class LinkSpectatorWindow : EditorWindow
    {
        [MenuItem("Window/Link para Mac/Spectator (Mixed Reality)")]
        public static void Open() => GetWindow<LinkSpectatorWindow>("Link: Spectator");

        void OnEnable()
        {
            LinkComposite.AddUser();
            LinkComposite.Composited += OnFrame;
        }

        void OnDisable()
        {
            LinkComposite.Composited -= OnFrame;
            LinkComposite.RemoveUser();
        }

        void OnFrame(RenderTexture rt, LinkCameraSource.FrameInfo info) => Repaint();

        void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (LinkStream.Running)
                {
                    GUILayout.Label("On the network: " + LinkStream.Url + $"  ({LinkStream.Clients} watching)", EditorStyles.miniLabel);
                    if (GUILayout.Button("Stop stream", EditorStyles.toolbarButton, GUILayout.Width(90))) LinkStream.Stop();
                }
                else if (GUILayout.Button("Stream on local network", EditorStyles.toolbarButton, GUILayout.Width(150))) LinkStream.Start(LinkStream.DefaultPort);
                GUILayout.FlexibleSpace();
                if (!LinkRecorder.Recording && GUILayout.Button("Record", EditorStyles.toolbarButton, GUILayout.Width(60))) LinkRecorder.Start("");
                if (LinkRecorder.Recording && GUILayout.Button($"Stop ({LinkRecorder.Seconds:F0}s)", EditorStyles.toolbarButton, GUILayout.Width(90))) LinkRecorder.Stop();
            }
            var rt = LinkComposite.Output;
            Rect area = new Rect(0, EditorGUIUtility.singleLineHeight + 2, position.width, position.height - EditorGUIUtility.singleLineHeight - 2);
            if (!EditorApplication.isPlaying || rt == null)
            {
                EditorGUI.DrawRect(area, Color.black);
                GUI.Label(new Rect(area.x + 10, area.y + 10, area.width - 20, 40),
                    EditorApplication.isPlaying ? "Waiting for the Quest camera…" : "Enter Play Mode with the Quest connected.", EditorStyles.whiteLabel);
                return;
            }
            GUI.DrawTexture(area, rt, ScaleMode.ScaleToFit, false);
        }
    }

    [InitializeOnLoad]
    public static class LinkStream
    {
        public const int DefaultPort = 8765;
        public static bool Running => _listener != null;
        public static int Clients { get { lock (Lock) return _clients.Count; } }
        public static string Url { get; private set; } = "-";
        public static int MaxFps = 15;

        static readonly object Lock = new object();
        static HttpListener _listener;
        static readonly List<Stream> _clients = new List<Stream>();
        static BlockingCollection<byte[]> _encode;
        static Thread _encoder;
        static double _nextFrame;
        static int _w, _h;
        static byte[] _lastJpeg;

        static LinkStream()
        {
            EditorApplication.playModeStateChanged += s => { if (s == PlayModeStateChange.EnteredEditMode && Running) Stop(); };
            AssemblyReloadEvents.beforeAssemblyReload += () => { if (Running) Stop(); };
        }

        public static string Command(string arg)
        {
            string a = (arg ?? "").Trim();
            if (a.StartsWith("on")) return Start(int.TryParse(a.Substring(2).Trim(), out var p) ? p : DefaultPort);
            if (a == "off") { Stop(); return "stream stopped"; }
            return Running ? $"Stream: {Url} — {Clients} watching, up to {MaxFps} fps" : "Stream: stopped (stream on)";
        }

        static string LanAddress()
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] b = ua.Address.GetAddressBytes();
                    if (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168)) return ua.Address.ToString();
                }
            }
            return "localhost";
        }

        public static string Start(int port)
        {
            if (Running) return Command("");
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://*:{port}/");
                _listener.Start();
            }
            catch (Exception e) { _listener = null; return "couldn't open port " + port + ": " + e.Message; }
            Url = $"http://{LanAddress()}:{port}/";
            _encode = new BlockingCollection<byte[]>(2);
            var queue = _encode;
            _encoder = new Thread(() => EncodeLoop(queue)) { IsBackground = true, Name = "LinkStream JPEG" };
            _encoder.Start();
            var listener = _listener;
            new Thread(() => AcceptLoop(listener)) { IsBackground = true, Name = "LinkStream HTTP" }.Start();
            LinkComposite.AddUser();
            LinkComposite.Composited += OnFrame;
            Debug.Log("[LinkStream] spectator on the network: " + Url);
            return "live spectator at " + Url + " (open it in a browser on the same network, or in OBS as a Browser Source)";
        }

        public static void Stop()
        {
            if (!Running) return;
            LinkComposite.Composited -= OnFrame;
            LinkComposite.RemoveUser();
            try { _listener.Stop(); _listener.Close(); } catch { }
            _listener = null;
            _encode?.CompleteAdding();
            lock (Lock) { foreach (var c in _clients) { try { c.Close(); } catch { } } _clients.Clear(); }
            Url = "-";
        }

        static void OnFrame(RenderTexture rt, LinkCameraSource.FrameInfo info)
        {
            if (Clients == 0 || EditorApplication.timeSinceStartup < _nextFrame) return;
            _nextFrame = EditorApplication.timeSinceStartup + 1.0 / Math.Max(1, MaxFps);
            _w = rt.width; _h = rt.height;
            int w = _w, h = _h;
            AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, req =>
            {
                if (req.hasError || _encode == null || _encode.IsAddingCompleted) return;
                _encode.TryAdd(Pack(req.GetData<byte>().ToArray(), w, h));
            });
        }

        // RGBA with the size up front (the encoder runs off the main thread).
        static byte[] Pack(byte[] rgba, int w, int h)
        {
            var b = new byte[rgba.Length + 8];
            BitConverter.GetBytes(w).CopyTo(b, 0);
            BitConverter.GetBytes(h).CopyTo(b, 4);
            Buffer.BlockCopy(rgba, 0, b, 8, rgba.Length);
            return b;
        }

        static void EncodeLoop(BlockingCollection<byte[]> queue)
        {
            foreach (var packed in queue.GetConsumingEnumerable())
            {
                try
                {
                    int w = BitConverter.ToInt32(packed, 0), h = BitConverter.ToInt32(packed, 4);
                    var rgba = new byte[packed.Length - 8];
                    Buffer.BlockCopy(packed, 8, rgba, 0, rgba.Length);
                    // Row 0 at the bottom (GPU readback): the JPEG comes out upright.
                    byte[] jpeg = ImageConversion.EncodeArrayToJPG(rgba, GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h, 0, 80);
                    _lastJpeg = jpeg;
                    Broadcast(jpeg);
                }
                catch (Exception e) { Debug.LogWarning("[LinkStream] " + e.Message); }
            }
        }

        static void Broadcast(byte[] jpeg)
        {
            byte[] head = Encoding.ASCII.GetBytes($"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n");
            byte[] tail = Encoding.ASCII.GetBytes("\r\n");
            List<Stream> clients;
            lock (Lock) clients = _clients.ToList();
            foreach (var c in clients)
            {
                try { c.Write(head, 0, head.Length); c.Write(jpeg, 0, jpeg.Length); c.Write(tail, 0, tail.Length); c.Flush(); }
                catch { lock (Lock) _clients.Remove(c); try { c.Close(); } catch { } }
            }
        }

        static void AcceptLoop(HttpListener listener)
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); } catch { break; }
                try
                {
                    string path = ctx.Request.Url.AbsolutePath;
                    if (path == "/stream")
                    {
                        ctx.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
                        ctx.Response.SendChunked = true;
                        ctx.Response.Headers.Add("Cache-Control", "no-cache");
                        lock (Lock) _clients.Add(ctx.Response.OutputStream);
                    }
                    else if (path == "/snapshot.jpg" && _lastJpeg != null)
                    {
                        ctx.Response.ContentType = "image/jpeg";
                        ctx.Response.OutputStream.Write(_lastJpeg, 0, _lastJpeg.Length);
                        ctx.Response.Close();
                    }
                    else
                    {
                        byte[] html = Encoding.UTF8.GetBytes(
                            "<!doctype html><html><head><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'>" +
                            "<title>Link para Mac — live</title><style>html,body{margin:0;height:100%;background:#000}" +
                            "img{width:100%;height:100%;object-fit:contain}</style></head><body><img src='/stream' alt='live mixed reality'></body></html>");
                        ctx.Response.ContentType = "text/html; charset=utf-8";
                        ctx.Response.OutputStream.Write(html, 0, html.Length);
                        ctx.Response.Close();
                    }
                }
                catch { }
            }
        }
    }
}
