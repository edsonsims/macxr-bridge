// SPDX-License-Identifier: MIT
// Link para Mac: fallback raycast for the Editor (on the headset the app uses EnvironmentRaycastManager).
// Order: real depth relayed from the Quest (LinkDepth) → real MRUK room (planes and volumes loaded
// from the headset through the proxy) → scene colliders (except those the app says to ignore: LinkHooks.IgnoreCollider) → optional:
// fixed distance.

using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace LinkParaMac.EditorTools
{
    public static class LinkRaycast
    {
        /// <summary>Label distance when nothing is hit (0 = off). For testing only.</summary>
        public static float FixedDistance;
        public static int Hits, Misses;
        public static string LastSource = "-";

        public static bool Raycast(Ray ray, float maxDistance, out Vector3 point, out Vector3 normal)
        {
            point = default;
            normal = Vector3.up;
            if (LinkDepth.Raycast(ray, maxDistance, out point, out normal)) { LastSource = "depth"; Hits++; return true; }
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
                if (LinkHooks.IgnoreCollider != null && LinkHooks.IgnoreCollider(hit.collider)) continue; // labels, mock room (app)
                best = hit.distance; point = hit.point; normal = hit.normal; LastSource = "Physics:" + hit.collider.name;
            }

            if (best < float.MaxValue) { Hits++; return true; }
            if (FixedDistance > 0f)
            {
                point = ray.GetPoint(FixedDistance); normal = -ray.direction; LastSource = "fixed"; Hits++;
                return true;
            }
            Misses++;
            return false;
        }
    }
}
