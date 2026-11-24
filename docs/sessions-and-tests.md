# Sessions and tests

Record a real mixed reality session once with the headset. Then replay it in Play Mode as often as you like without
wearing it, and turn it into an automated test of your app's perception.

Everything here is in the **Sessions** section of **Window ▸ MacXR Bridge ▸ Panel**, and available as `session …`
commands (see [commands.md](commands.md)).

## Record

1. Press Play with the Quest connected and wear the headset.
2. `session rec [name] [fps=N]` (or **Record session** in the Panel). Look around and do what you want to test.
3. `session stop`.

The session goes to `<project>/Sessions/<name>-<yyyyMMdd-HHmmss>/`:

| File | What |
|---|---|
| `cam/` and `camera.csv` | Every camera frame (JPEG, quality 90, stored upright) with its timestamp, size and intrinsics |
| `depth.bin` and `depth.csv` | Raw environment depth frames and each frame's view pose, already in Unity world space |
| `poses.csv` | The head pose (position and rotation) for every Unity frame |
| `tracking.bin` | The raw tracking stream from the headset: head, controllers, all hand joints, buttons, microgestures |
| `room.json` | The MRUK room at the time (`MRUK.SaveSceneToJsonString`) |
| `meta.txt` | Name, date, Unity version, scene, tracking space and the runtime's LOCAL reference |

A 17-second session is about 87 MB (498 camera frames, 80 depth frames, 1,504 poses). `fps=N` caps the camera frame
rate to save space.

## Replay

`session play [name|last] [loop] [speed=X] [paused] [norig]`. The Quest app can be closed and the headset off: Play
Mode runs on the recording.

During a replay:
- The recorded camera frames feed `PassthroughCameraAccess`, so your detection sees what the headset saw.
- The recorded depth is injected into the runtime, so `XR_META_environment_raycast` and `EnvironmentRaycastManager`
  answer from the session, not the live headset.
- The room loads from `room.json` (MRUK world locking is off during the replay).
- The tracking tape goes through the runtime's OpenXR input, so head, hands, pinches and microgestures arrive exactly
  as if the headset sent them: the Interaction SDK and your own code react to them.
- Replays are **hermetic**: the recorded LOCAL reference and tracking space are restored (and the live ones put back
  afterwards), recorded depth never expires and pose prediction is off. The replayed head matches `poses.csv` within
  1 cm and 0.1°, whatever the idle headset is doing.
- Older sessions without `tracking.bin` move the camera rig instead; `norig` leaves the head free.

**Timeline**: `session pause`, `session resume`, `session seek <s|+s|-s>`, `session step [n]` (camera frames, negative
to go back), `session speed <0.25–2>`. The Panel has a slider, frame-step buttons and speed presets. Camera, depth and
tracking share one clock.

Also: `session list`, `session stop`. A mixed reality video of a replay works too (`record start` during it).

## Perception tests

### Baseline

`session baseline [name]` replays the session and saves what your app did as `expect.json` in its folder:

```json
{
  "detections": [ { "label": "laptop", "minCount": 22 }, { "label": "tv monitor", "minCount": 3 } ],
  "placed": [
    { "label": "laptop", "position": { "x": -0.164, "y": 1.237, "z": 0.724 }, "tolerance": 0.15 }
  ],
  "forbidden": []
}
```

- `detections`: classes the detector must report at least `minCount` times (the baseline uses half of what it saw).
- `placed`: where each label must land: the median position of that label, within `tolerance` meters (15 cm by
  default).
- `forbidden`: classes that must never appear (add them by hand, for example false positives you fixed).

Edit the file as you like; it's the test's specification.

### Test

`session test [name|all] [realtime]` replays and checks every expectation. The report shows each label's median
position and how far it moved, and how many raycasts fell back to the Editor raycast. Results also go to
`TestResults/macxr-sessions.xml` (JUnit). `session report` shows the last report (or the progress of a running test).

### Deterministic by design

By default the replay runs in **lockstep** with your detector: every 6 camera frames (about 5 per second of session)
the test loads the frame, lets your app start exactly one detection, waits for the inference to finish and only then
moves on. The result doesn't depend on how fast the Mac is. Repeated runs give the same detection counts and the same
label positions. `realtime` turns lockstep off and replays at normal speed.

### What your app needs

- **Detections** are read from Meta's AI Building Blocks `ObjectDetectionAgent`
  (`OnDetectionResponseReceived`). If you detect objects another way, only the `placed` checks apply.
- **Placements** come from your app through `BridgeHooks.ReportPlacedBatch` (or `ReportPlaced`).
- **Lockstep** uses three hooks: `DetectNow`, `HoldDetection` and `DetectionStarted`. Without them, the test still
  steps through the session but gives each step a fixed 0.4 s, so results can vary with the Mac's speed.

See [connect-your-app.md](connect-your-app.md) for the hooks.

## Run tests from the command line (CI)

```bash
/Applications/Unity/Hub/Editor/<version>/Unity.app/Contents/MacOS/Unity \
  -projectPath /path/to/YourUnityProject \
  -executeMethod MacXRBridge.EditorTools.BridgeSessionTests.RunAllFromCommandLine
```

It opens the project, runs every session that has an `expect.json`, writes `TestResults/macxr-sessions.xml` and exits
with code 0 if everything passed. It needs the runtime installed (`scripts/10-build-runtime.sh` and
`scripts/50-configure-runtime.sh`) but no headset. A run took about 2 minutes (118 s), including opening the Editor.

Keep the sessions you test with in version control (or in shared storage) next to the project: they're your test
fixtures.
