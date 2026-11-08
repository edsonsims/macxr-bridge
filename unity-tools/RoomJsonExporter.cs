// Link para Mac — exports the real room (MRUK) to JSON on the headset.
// Put this component on any GameObject in the scene that has MRUK, build for the Quest,
// open the app once and then run scripts/70-pull-room-json.sh <package name>.
// On the Mac, MRUK loads that JSON in the Editor (Data Source = Device With Json Fallback).
using System.IO;
using UnityEngine;
using Meta.XR.MRUtilityKit;

namespace LinkParaMac
{
    public class RoomJsonExporter : MonoBehaviour
    {
        [Tooltip("File name in Application.persistentDataPath")]
        public string fileName = "room.json";

        [Tooltip("Exports automatically when MRUK finishes loading the room")]
        public bool exportOnSceneLoaded = true;

        [Tooltip("Includes the room's global mesh (much larger file; useful for occlusion)")]
        public bool includeGlobalMesh = false;

        void Start()
        {
            if (!exportOnSceneLoaded) return;
            if (MRUK.Instance == null)
            {
                Debug.LogWarning("[LinkParaMac] MRUK not found in the scene.");
                return;
            }
            MRUK.Instance.RegisterSceneLoadedCallback(Export);
        }

        [ContextMenu("Export room now")]
        public void Export()
        {
            if (MRUK.Instance == null)
            {
                Debug.LogWarning("[LinkParaMac] MRUK not found in the scene.");
                return;
            }

            string json = MRUK.Instance.SaveSceneToJsonString(
                SerializationHelpers.CoordinateSystem.Unity, includeGlobalMesh);
            string path = Path.Combine(Application.persistentDataPath, fileName);
            File.WriteAllText(path, json);
            Debug.Log($"[LinkParaMac] Room exported to {path} ({json.Length} characters). " +
                      "On the Mac: scripts/70-pull-room-json.sh <package name>");
        }
    }
}
