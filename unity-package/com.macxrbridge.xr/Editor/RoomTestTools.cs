// SPDX-License-Identifier: MIT
// MacXR Bridge: tests for the scene (MRUK) loaded from the headset through the spatial proxy, in Play Mode.
// Commands: room-load | room | room-mesh | room-capture

using System;
using System.Linq;
using System.Text;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace MacXRBridge.EditorTools
{
    public static class RoomTestTools
    {
        static readonly StringBuilder Events = new StringBuilder();

        static void Log(string text)
        {
            Events.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {text}");
            Debug.Log("[BridgeRoom] " + text);
        }

        internal static MRUK Ensure()
        {
            var mruk = MRUK.Instance;
            if (mruk == null)
            {
                var go = new GameObject("MRUK (MacXR Bridge)");
                mruk = go.AddComponent<MRUK>();
                mruk.SceneLoadedEvent.AddListener(() => Log($"SceneLoadedEvent: {mruk.Rooms.Count} room(s)"));
                Log("MRUK created in the scene");
            }
            // Added at runtime the serialized settings object does not exist yet.
            if (mruk.SceneSettings == null) mruk.SceneSettings = new MRUK.MRUKSettings();
            mruk.SceneSettings.LoadSceneOnStartup = false;
            mruk.SceneSettings.DataSource = MRUK.SceneDataSource.Device;
            return mruk;
        }

        public static string Load(string arg)
        {
            if (!Application.isPlaying) return "Play Mode only";
            var mruk = Ensure();
            bool capture = arg != null && arg.Trim() == "capture";
            Log($"LoadSceneFromDevice(requestSceneCaptureIfNoDataFound={capture})…");
            var task = mruk.LoadSceneFromDevice(capture);
            task.ContinueWith(t =>
            {
                if (t.IsFaulted) Log("LoadSceneFromDevice failed: " + t.Exception?.GetBaseException().Message);
                else Log($"LoadSceneFromDevice -> {t.Result}");
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
            return "loading the room from the headset… (see 'room')";
        }

        /// <summary>Low-level: fetch anchors by component (default ROOM_LAYOUT) with OVRAnchor, bypassing MRUK.</summary>
        public static string Fetch(string arg)
        {
            if (!Application.isPlaying) return "Play Mode only";
            var list = new System.Collections.Generic.List<OVRAnchor>();
            var options = new OVRAnchor.FetchOptions();
            string which = (arg ?? "").Trim().ToLowerInvariant();
            if (which == "all") { /* no filter */ }
            else if (which == "semantic") options.SingleComponentType = typeof(OVRSemanticLabels);
            else if (which == "anchors") options.SingleComponentType = typeof(OVRStorable);
            else options.SingleComponentType = typeof(OVRRoomLayout);
            Log($"FetchAnchorsAsync({(options.SingleComponentType != null ? options.SingleComponentType.Name : "no filter")})…");
            OVRAnchor.FetchAnchorsAsync(list, options).ContinueWith(result =>
            {
                Log($"FetchAnchorsAsync -> Success={result.Success} Status={result.Status} count={list.Count}");
                foreach (var a in list.Take(30))
                {
                    string extra = "";
                    if (a.TryGetComponent<OVRRoomLayout>(out var layout) && layout.TryGetRoomLayout(out var ceiling, out var floor, out var walls))
                        extra = $" room: floor={floor} ceiling={ceiling} walls={walls.Length}";
                    if (a.TryGetComponent<OVRAnchorContainer>(out var container))
                        extra += $" container={container.Uuids.Length}";
                    if (a.TryGetComponent<OVRSemanticLabels>(out var labels))
                        extra += $" labels={labels.Labels}";
                    Log($"  {a.Uuid}{extra}");
                }
            });
            return "fetching anchors from the headset… (see 'room')";
        }

        public static string Capture()
        {
            if (!Application.isPlaying) return "Play Mode only";
            var task = OVRScene.RequestSpaceSetup();
            task.ContinueWith(ok => Log($"RequestSpaceSetup -> {ok}"));
            return "Space Setup requested on the headset…";
        }

        public static string Describe()
        {
            var sb = new StringBuilder();
            var mruk = MRUK.Instance;
            if (mruk == null) sb.AppendLine("MRUK missing (use room-load)");
            else
            {
                sb.AppendLine($"MRUK: {mruk.Rooms.Count} room(s), IsWorldLockActive={mruk.IsWorldLockActive}");
                foreach (var room in mruk.Rooms)
                {
                    sb.AppendLine($"  Room {room.name}: {room.Anchors.Count} anchors, walls={room.WallAnchors.Count}, floor={(room.FloorAnchor != null ? "yes" : "no")}, globalMesh={(room.GlobalMeshAnchor != null ? "yes" : "no")}");
                    foreach (var a in room.Anchors.Take(40))
                    {
                        string shape = a.VolumeBounds.HasValue ? $"vol={a.VolumeBounds.Value.size}" : a.PlaneRect.HasValue ? $"plane={a.PlaneRect.Value.size} boundary={a.PlaneBoundary2D.Count}" : "no geometry";
                        sb.AppendLine($"    {string.Join("|", a.AnchorLabels)} pos={a.transform.position} rot={a.transform.rotation.eulerAngles} {shape}");
                    }
                }
            }
            sb.Append("Events:\n").Append(Events);
            return sb.ToString();
        }

        /// <summary>Draws boxes/quads on the room anchors to see the real room in passthrough.</summary>
        // Keeps the primitive's DEFAULT material (always compatible with the active render pipeline)
        // and only changes the color — creating a new Material with Shader.Find came out magenta (wrong shader).
        static void Tint(GameObject go, Color color)
        {
            var r = go.GetComponent<Renderer>();
            var mat = r.material; // instance of the primitive's default material
            mat.color = color;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        }

        public static string Draw()
        {
            if (!Application.isPlaying) return "Play Mode only";
            var mruk = MRUK.Instance;
            if (mruk == null || mruk.Rooms.Count == 0) return "no room (run room-load first)";
            var parent = new GameObject("Room draw (MacXR Bridge)");
            int planes = 0, volumes = 0;
            foreach (var room in mruk.Rooms)
            {
                foreach (var a in room.Anchors)
                {
                    if (a.VolumeBounds.HasValue)
                    {
                        var b = a.VolumeBounds.Value;
                        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        go.name = $"Vol {string.Join("|", a.AnchorLabels)}";
                        go.transform.SetParent(parent.transform, false);
                        // VolumeBounds is local to the anchor; the center and size come from the bounds.
                        go.transform.SetPositionAndRotation(a.transform.TransformPoint(b.center), a.transform.rotation);
                        go.transform.localScale = b.size;
                        Tint(go, new Color(1f, 0.5f, 0f, 1f));
                        volumes++;
                    }
                    else if (a.PlaneRect.HasValue)
                    {
                        var rect = a.PlaneRect.Value;
                        // Thin box (3 cm) instead of a single-sided quad: visible from both sides.
                        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        go.name = $"Plane {string.Join("|", a.AnchorLabels)}";
                        go.transform.SetParent(parent.transform, false);
                        go.transform.SetPositionAndRotation(a.transform.TransformPoint(new Vector3(rect.center.x, rect.center.y, 0f)), a.transform.rotation);
                        go.transform.localScale = new Vector3(rect.size.x, rect.size.y, 0.03f);
                        Tint(go, new Color(0f, 0.8f, 1f, 1f));
                        planes++;
                    }
                }
            }
            return $"drew {planes} planes (blue) and {volumes} volumes (orange) of your room";
        }

        public static string Mesh()
        {
            if (!Application.isPlaying) return "Play Mode only";
            var mruk = MRUK.Instance;
            if (mruk == null || mruk.Rooms.Count == 0) return "no room (run room-load first)";
            var anchor = mruk.Rooms[0].GlobalMeshAnchor;
            if (anchor == null) return "the room has no GLOBAL_MESH";
            var mesh = anchor.LoadGlobalMeshTriangles();
            if (mesh == null) return "LoadGlobalMeshTriangles returned null";
            var go = new GameObject("GlobalMesh (MacXR Bridge)");
            go.transform.SetPositionAndRotation(anchor.transform.position, anchor.transform.rotation);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            // Grab a primitive's default material to get a valid shader for the pipeline.
            var probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var defaultMat = probe.GetComponent<Renderer>().sharedMaterial;
            UnityEngine.Object.Destroy(probe);
            go.AddComponent<MeshRenderer>().material = new Material(defaultMat);
            Tint(go, new Color(0f, 1f, 0.6f, 1f));
            return $"global mesh: {mesh.vertexCount} vertices, {mesh.triangles.Length / 3} triangles (rendered in green)";
        }
    }
}
