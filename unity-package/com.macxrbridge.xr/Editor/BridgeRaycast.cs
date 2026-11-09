// SPDX-License-Identifier: MIT
// MacXR Bridge: fallback raycast for the Editor (on the headset the app uses EnvironmentRaycastManager).
// Order: real depth relayed from the Quest (BridgeDepth) → real MRUK room (planes and volumes loaded
// from the headset through the proxy) → scene colliders (except those the app says to ignore: BridgeHooks.IgnoreCollider) → optional:
// fixed distance.

using System.Collections.Generic;
using System.Linq;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace MacXRBridge.EditorTools
{
    public static class BridgeRaycast
    {
        /// <summary>Label distance when nothing is hit (0 = off). For testing only.</summary>
        public static float FixedDistance;
        public static int Hits, Misses;
        public static string LastSource = "-";
        /// <summary>How many fallback ray casts each source answered ("miss" = none). Session tests report it.</summary>
        public static readonly Dictionary<string, int> Sources = new Dictionary<string, int>();
        public static string SourcesSummary() =>
            Sources.Count == 0 ? "none" : string.Join(", ", Sources.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"));
        static void Count(string source) => Sources[source] = (Sources.TryGetValue(source, out int n) ? n : 0) + 1;

        public static bool Raycast(Ray ray, float maxDistance, out Vector3 point, out Vector3 normal)
        {
            point = default;
            normal = Vector3.up;
            if (BridgeDepth.Raycast(ray, maxDistance, out point, out normal)) { LastSource = "depth"; Hits++; Count("depth"); return true; }
            float best = float.MaxValue;

            var mruk = MRUK.Instance;
            if (mruk != null)
            {
                foreach (var room in mruk.Rooms)
                {
                    if (room == null) continue;
                    if (room.Raycast(ray, maxDistance, out RaycastHit hit, out MRUKAnchor _) && hit.distance < best)
                    {
                        best = hit.distance; point = hit.point; normal = hit.normal; LastSource = "MRUK";
                    }
                }
            }

            foreach (var hit in Physics.RaycastAll(ray, maxDistance))
            {
                if (hit.distance >= best || hit.collider == null) continue;
                if (BridgeHooks.IgnoreCollider != null && BridgeHooks.IgnoreCollider(hit.collider)) continue; // labels, mock room (app)
                best = hit.distance; point = hit.point; normal = hit.normal; LastSource = "Physics:" + hit.collider.name;
            }

            if (best < float.MaxValue) { Hits++; Count(LastSource.StartsWith("Physics") ? "physics" : LastSource); return true; }
            if (FixedDistance > 0f)
            {
                point = ray.GetPoint(FixedDistance); normal = -ray.direction; LastSource = "fixed"; Hits++; Count("fixed");
                return true;
            }
            Misses++;
            Count("miss");
            return false;
        }
    }
}
