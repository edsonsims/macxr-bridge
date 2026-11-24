# unity-tools

A fallback from before the live room worked. With the current runtime, MRUK loads your real room in Play Mode
directly (`room-load`), so you normally don't need this.

- `RoomJsonExporter.cs`: in a build on the headset, saves the real MRUK room to `room.json` (in `persistentDataPath`).
  Copy it into your project's `Assets/`, build, run it once on the headset, then pull the file with
  `scripts/70-pull-room-json.sh <your app's package name>`. In the Editor, set the MRUK component's
  Scene Settings ▸ Data Source to **Device With Json Fallback** and Scene Jsons to `room.json`.

Useful when you want the same room without a headset connected. Recorded sessions (`session rec`) also save the room
and bring it back on replay.
