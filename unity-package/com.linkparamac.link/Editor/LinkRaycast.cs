// SPDX-License-Identifier: MIT
// Link para Mac: raycast de reserva pro Editor (no headset o app usa o EnvironmentRaycastManager).
// Ordem: profundidade real relayada do Quest (LinkDepth) → sala real do MRUK (planos e volumes carregados
// do headset pelo proxy) → colisores da cena (menos os que o app manda ignorar: LinkHooks.IgnoreCollider) → opcional:
// distância fixa.

using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace LinkParaMac.EditorTools
{
    public static class LinkRaycast
    {
        /// <summary>Distância da etiqueta quando nada é atingido (0 = desligado). Só pra teste.</summary>
        public static float FixedDistance;
        public static int Hits, Misses;
        public static string LastSource = "-";

        public static bool Raycast(Ray ray, float maxDistance, out Vector3 point, out Vector3 normal)
        {
            point = default;
            normal = Vector3.up;
            if (LinkDepth.Raycast(ray, maxDistance, out point, out normal)) { LastSource = "profundidade"; Hits++; return true; }
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
                if (LinkHooks.IgnoreCollider != null && LinkHooks.IgnoreCollider(hit.collider)) continue; // etiquetas, sala mock (app)
                best = hit.distance; point = hit.point; normal = hit.normal; LastSource = "Physics:" + hit.collider.name;
            }

            if (best < float.MaxValue) { Hits++; return true; }
            if (FixedDistance > 0f)
            {
                point = ray.GetPoint(FixedDistance); normal = -ray.direction; LastSource = "fixo"; Hits++;
                return true;
            }
            Misses++;
            return false;
        }
    }
}
