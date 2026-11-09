# unity-tools

MacXR Bridge Unity scripts. Copy them into the project's `Assets/MacXRBridge/`.

- `RoomJsonExporter.cs` — on the headset, saves the real MRUK room to `room.json` (persistentDataPath).
  Pull it with `scripts/70-pull-room-json.sh <package name>` and, in the Editor, set up the MRUK component:
  Scene Settings → Data Source = **Device With Json Fallback** → Scene Jsons = `room.json`.
  That way, on the Mac (where the runtime doesn't deliver the scene yet), the Editor uses your actual room.

Planned (Phase 1):
- `PassthroughCameraSource` — swappable frame source for the Editor (the Mac's webcam or frames recorded on the Quest),
  so the object detection pipeline can run on the Mac until the camera relay exists.
