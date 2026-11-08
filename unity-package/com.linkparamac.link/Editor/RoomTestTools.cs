// SPDX-License-Identifier: MIT
// Link para Mac: teste da cena (MRUK) carregada do headset pelo proxy spatial, em Play Mode.
// Comandos: room-load | room | room-mesh | room-capture

using System;
using System.Linq;
using System.Text;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace LinkParaMac.EditorTools
{
    public static class RoomTestTools
    {
        static readonly StringBuilder Events = new StringBuilder();

        static void Log(string text)
        {
            Events.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {text}");
            Debug.Log("[LinkRoom] " + text);
        }

        internal static MRUK Ensure()
        {
            var mruk = MRUK.Instance;
            if (mruk == null)
            {
                var go = new GameObject("MRUK (Link para Mac)");
                mruk = go.AddComponent<MRUK>();
                mruk.SceneLoadedEvent.AddListener(() => Log($"SceneLoadedEvent: {mruk.Rooms.Count} sala(s)"));
                Log("MRUK criado na cena");
            }
            // Added at runtime the serialized settings object does not exist yet.
            if (mruk.SceneSettings == null) mruk.SceneSettings = new MRUK.MRUKSettings();
            mruk.SceneSettings.LoadSceneOnStartup = false;
            mruk.SceneSettings.DataSource = MRUK.SceneDataSource.Device;
            return mruk;
        }

        public static string Load(string arg)
        {
            if (!Application.isPlaying) return "só em Play Mode";
            var mruk = Ensure();
            bool capture = arg != null && arg.Trim() == "capture";
            Log($"LoadSceneFromDevice(requestSceneCaptureIfNoDataFound={capture})…");
            var task = mruk.LoadSceneFromDevice(capture);
            task.ContinueWith(t =>
            {
                if (t.IsFaulted) Log("LoadSceneFromDevice falhou: " + t.Exception?.GetBaseException().Message);
                else Log($"LoadSceneFromDevice -> {t.Result}");
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
            return "carregando a sala do headset… (veja 'room')";
        }

        /// <summary>Low-level: fetch anchors by component (default ROOM_LAYOUT) with OVRAnchor, bypassing MRUK.</summary>
        public static string Fetch(string arg)
        {
            if (!Application.isPlaying) return "só em Play Mode";
            var list = new System.Collections.Generic.List<OVRAnchor>();
            var options = new OVRAnchor.FetchOptions();
            string which = (arg ?? "").Trim().ToLowerInvariant();
            if (which == "all") { /* sem filtro */ }
            else if (which == "semantic") options.SingleComponentType = typeof(OVRSemanticLabels);
            else if (which == "anchors") options.SingleComponentType = typeof(OVRStorable);
            else options.SingleComponentType = typeof(OVRRoomLayout);
            Log($"FetchAnchorsAsync({(options.SingleComponentType != null ? options.SingleComponentType.Name : "sem filtro")})…");
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
            return "buscando âncoras no headset… (veja 'room')";
        }

        public static string Capture()
        {
            if (!Application.isPlaying) return "só em Play Mode";
            var task = OVRScene.RequestSpaceSetup();
            task.ContinueWith(ok => Log($"RequestSpaceSetup -> {ok}"));
            return "Space Setup pedido no headset…";
        }

        public static string Describe()
        {
            var sb = new StringBuilder();
            var mruk = MRUK.Instance;
            if (mruk == null) sb.AppendLine("MRUK ausente (use room-load)");
            else
            {
                sb.AppendLine($"MRUK: {mruk.Rooms.Count} sala(s), IsWorldLockActive={mruk.IsWorldLockActive}");
                foreach (var room in mruk.Rooms)
                {
                    sb.AppendLine($"  Sala {room.name}: {room.Anchors.Count} âncoras, paredes={room.WallAnchors.Count}, chão={(room.FloorAnchor != null ? "sim" : "não")}, globalMesh={(room.GlobalMeshAnchor != null ? "sim" : "não")}");
                    foreach (var a in room.Anchors.Take(40))
                    {
                        string shape = a.VolumeBounds.HasValue ? $"vol={a.VolumeBounds.Value.size}" : a.PlaneRect.HasValue ? $"plane={a.PlaneRect.Value.size} boundary={a.PlaneBoundary2D.Count}" : "sem geometria";
                        sb.AppendLine($"    {string.Join("|", a.AnchorLabels)} pos={a.transform.position} rot={a.transform.rotation.eulerAngles} {shape}");
                    }
                }
            }
            sb.Append("Eventos:\n").Append(Events);
            return sb.ToString();
        }

        /// <summary>Desenha caixas/quads nas âncoras da sala pra ver o cômodo real no passthrough.</summary>
        // Mantém o material PADRÃO do primitivo (sempre compatível com o render pipeline ativo)
        // e só troca a cor — criar um Material novo com Shader.Find dava magenta (shader errado).
        static void Tint(GameObject go, Color color)
        {
            var r = go.GetComponent<Renderer>();
            var mat = r.material; // instância do material padrão do primitivo
            mat.color = color;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        }

        public static string Draw()
        {
            if (!Application.isPlaying) return "só em Play Mode";
            var mruk = MRUK.Instance;
            if (mruk == null || mruk.Rooms.Count == 0) return "sem sala (room-load primeiro)";
            var parent = new GameObject("Room draw (Link para Mac)");
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
                        // VolumeBounds é local à âncora; o centro e o tamanho vêm do bounds.
                        go.transform.SetPositionAndRotation(a.transform.TransformPoint(b.center), a.transform.rotation);
                        go.transform.localScale = b.size;
                        Tint(go, new Color(1f, 0.5f, 0f, 1f));
                        volumes++;
                    }
                    else if (a.PlaneRect.HasValue)
                    {
                        var rect = a.PlaneRect.Value;
                        // Caixa fina (3 cm) em vez de quad de uma face: visível pelos dois lados.
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
            return $"desenhei {planes} planos (azul) e {volumes} volumes (laranja) da sua sala";
        }

        public static string Mesh()
        {
            if (!Application.isPlaying) return "só em Play Mode";
            var mruk = MRUK.Instance;
            if (mruk == null || mruk.Rooms.Count == 0) return "sem sala (room-load primeiro)";
            var anchor = mruk.Rooms[0].GlobalMeshAnchor;
            if (anchor == null) return "a sala não tem GLOBAL_MESH";
            var mesh = anchor.LoadGlobalMeshTriangles();
            if (mesh == null) return "LoadGlobalMeshTriangles devolveu null";
            var go = new GameObject("GlobalMesh (Link para Mac)");
            go.transform.SetPositionAndRotation(anchor.transform.position, anchor.transform.rotation);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            // Pega o material padrão de um primitivo pra ter um shader válido do pipeline.
            var probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var defaultMat = probe.GetComponent<Renderer>().sharedMaterial;
            UnityEngine.Object.Destroy(probe);
            go.AddComponent<MeshRenderer>().material = new Material(defaultMat);
            Tint(go, new Color(0f, 1f, 0.6f, 1f));
            return $"malha global: {mesh.vertexCount} vértices, {mesh.triangles.Length / 3} triângulos (renderizada em verde)";
        }
    }
}
