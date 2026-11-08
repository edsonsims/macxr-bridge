// Link para Mac — exporta a sala real (MRUK) para JSON no headset.
// Coloque este componente em qualquer GameObject da cena que tem o MRUK, faça o build para o Quest,
// abra o app uma vez e depois rode scripts/70-pull-room-json.sh <package name>.
// No Mac, o MRUK carrega esse JSON no Editor (Data Source = Device With Json Fallback).
using System.IO;
using UnityEngine;
using Meta.XR.MRUtilityKit;

namespace LinkParaMac
{
    public class RoomJsonExporter : MonoBehaviour
    {
        [Tooltip("Nome do arquivo em Application.persistentDataPath")]
        public string fileName = "room.json";

        [Tooltip("Exporta automaticamente quando o MRUK termina de carregar a sala")]
        public bool exportOnSceneLoaded = true;

        [Tooltip("Inclui a malha global do cômodo (arquivo bem maior; útil para oclusão)")]
        public bool includeGlobalMesh = false;

        void Start()
        {
            if (!exportOnSceneLoaded) return;
            if (MRUK.Instance == null)
            {
                Debug.LogWarning("[LinkParaMac] MRUK não encontrado na cena.");
                return;
            }
            MRUK.Instance.RegisterSceneLoadedCallback(Export);
        }

        [ContextMenu("Exportar sala agora")]
        public void Export()
        {
            if (MRUK.Instance == null)
            {
                Debug.LogWarning("[LinkParaMac] MRUK não encontrado na cena.");
                return;
            }

            string json = MRUK.Instance.SaveSceneToJsonString(
                SerializationHelpers.CoordinateSystem.Unity, includeGlobalMesh);
            string path = Path.Combine(Application.persistentDataPath, fileName);
            File.WriteAllText(path, json);
            Debug.Log($"[LinkParaMac] Sala exportada para {path} ({json.Length} caracteres). " +
                      "No Mac: scripts/70-pull-room-json.sh <package name>");
        }
    }
}
