// SPDX-License-Identifier: MIT
// Link para Mac: spatial anchor (OVRSpatialAnchor) tests via LinkBridge, in Play Mode.
// Commands: anchor <x> <y> <z> | anchors | anchor-save <name|all> | anchor-load <uuid|uuid,uuid> | anchor-erase <uuid>

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace LinkParaMac.EditorTools
{
    public static class AnchorTestTools
    {
        static readonly List<OVRSpatialAnchor> Created = new List<OVRSpatialAnchor>();
        static readonly StringBuilder Events = new StringBuilder();
        static int _counter;

        static void Log(string text)
        {
            Events.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {text}");
            Debug.Log("[LinkAnchors] " + text);
        }

        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        public static string Create(string arg)
        {
            if (!Application.isPlaying) return "Play Mode only";
            var parts = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            Vector3 position = parts.Length >= 3 ? new Vector3(F(parts[0]), F(parts[1]), F(parts[2])) : new Vector3(0f, 1.2f, 0.8f);
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = $"LinkAnchor {++_counter}";
            go.transform.localScale = Vector3.one * 0.08f;
            go.transform.SetPositionAndRotation(position, Quaternion.identity);
            var renderer = go.GetComponent<Renderer>();
            if (renderer != null) renderer.material.color = Color.cyan;
            var anchor = go.AddComponent<OVRSpatialAnchor>();
            Created.Add(anchor);
            Log($"{go.name}: OVRSpatialAnchor added at {position}");
            return $"{go.name} created at {position}; use 'anchors' to see Created/Localized/Uuid";
        }

        public static string List()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"OVRPlugin.initialized={OVRPlugin.initialized}");
            var all = UnityEngine.Object.FindObjectsByType<OVRSpatialAnchor>(FindObjectsSortMode.None);
            sb.AppendLine($"OVRSpatialAnchor in scene: {all.Length}");
            foreach (var a in all)
            {
                if (a == null) continue;
                sb.AppendLine($"  {a.gameObject.name}: Created={a.Created} Localized={a.Localized} Uuid={(a.Created ? a.Uuid.ToString() : "-")} pos={a.transform.position} PendingCreation={a.PendingCreation}");
            }
            sb.Append("Events:\n").Append(Events);
            return sb.ToString();
        }

        public static string Save(string arg)
        {
            if (!Application.isPlaying) return "Play Mode only";
            var targets = Targets(arg);
            if (targets.Count == 0) return "no created anchor with that name";
            var task = OVRSpatialAnchor.SaveAnchorsAsync(targets);
            task.ContinueWith(result =>
            {
                Log($"SaveAnchorsAsync({targets.Count}) -> Success={result.Success} Status={result.Status}");
            });
            return $"saving {targets.Count} anchor(s)… (see 'anchors')";
        }

        public static string Erase(string arg)
        {
            if (!Application.isPlaying) return "Play Mode only";
            var targets = Targets(arg);
            if (targets.Count == 0) return "no anchor with that name";
            foreach (var a in targets)
            {
                var name = a.gameObject.name;
                a.EraseAnchorAsync().ContinueWith(result => Log($"EraseAnchorAsync({name}) -> Success={result.Success} Status={result.Status}"));
            }
            return $"erasing {targets.Count} anchor(s)…";
        }

        public static string Load(string arg)
        {
            if (!Application.isPlaying) return "Play Mode only";
            var uuids = new List<Guid>();
            foreach (var s in arg.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Guid.TryParse(s, out var g)) uuids.Add(g);
            }
            if (uuids.Count == 0) return "usage: anchor-load <uuid>[,<uuid>]";
            var unbound = new List<OVRSpatialAnchor.UnboundAnchor>();
            var task = OVRSpatialAnchor.LoadUnboundAnchorsAsync(uuids, unbound);
            task.ContinueWith(result =>
            {
                Log($"LoadUnboundAnchorsAsync({uuids.Count}) -> Success={result.Success} Status={result.Status} unbound={unbound.Count}");
                foreach (var u in unbound)
                {
                    var captured = u;
                    captured.LocalizeAsync().ContinueWith(ok =>
                    {
                        Log($"LocalizeAsync({captured.Uuid}) -> {ok}");
                        if (!ok) return;
                        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                        go.name = $"LoadedAnchor {captured.Uuid.ToString().Substring(0, 8)}";
                        go.transform.localScale = Vector3.one * 0.08f;
                        var renderer = go.GetComponent<Renderer>();
                        if (renderer != null) renderer.material.color = Color.yellow;
                        var anchor = go.AddComponent<OVRSpatialAnchor>();
                        captured.BindTo(anchor);
                        Created.Add(anchor);
                        Log($"{go.name} bound (BindTo) at {go.transform.position}");
                    });
                }
            });
            return $"loading {uuids.Count} uuid(s)… (see 'anchors')";
        }

        static List<OVRSpatialAnchor> Targets(string arg)
        {
            Created.RemoveAll(a => a == null);
            if (string.IsNullOrWhiteSpace(arg) || arg.Trim() == "all") return Created.Where(a => a.Created).ToList();
            return Created.Where(a => a.Created && a.gameObject.name.IndexOf(arg.Trim(), StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }
    }
}
