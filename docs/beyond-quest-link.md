# Beyond Quest Link

MacXR Bridge started as "Quest Link for the Mac". Once the Meta XR SDK features worked, the Mac side turned out to
be a good place for tools that make mixed reality development faster. This page compares it with Meta's two official
ways to iterate in the Editor: **Quest Link** (Windows, a real headset) and the **Meta XR Simulator** (Windows and
macOS, a synthetic environment). Information about Meta's tools is from their documentation as of November 2026 (sources
at the end).

## At a glance

| | Quest Link | Meta XR Simulator | MacXR Bridge |
|---|---|---|---|
| Runs on | Windows only | Windows, macOS | macOS (Apple Silicon) |
| Your real headset, hands and room in Play Mode | Yes | No, synthetic rooms | Yes |
| Passthrough Camera API in Play Mode | Yes (Link v2.1+, developer toggle) | No | Yes, 1280×960 at 30 fps |
| Record a session and replay it later | No | Records your simulator inputs (keyboard, mouse, controller) to a VRS file and replays them | Records the **real** camera, depth, room and full tracking (head, hands, controllers, microgestures) and replays them in Play Mode, with a timeline |
| Automated verdict on a replay | No | The replay doesn't judge the outcome; your CI harness checks exit status and logs | Assertions on what your detector saw and where labels landed; JUnit report; exit code from the command line |
| Mixed reality video from the Editor | No (passthrough doesn't render in the Game view) | No real camera | Yes: real camera + app rendered from the camera's pose + app audio + your voice |
| Spectator view for other people | Compositor mirror on the PC monitor | Simulator window | Mixed reality window and an MJPEG stream on your network (browser, phone, TV, OBS) |
| Perception debug view in the headset | No | No | Yes: a ray per detection, depth points |
| Performance HUD | Yes (Oculus Debug Tool) | — | Yes, plus camera, depth, microphone, headset battery and temperature |
| Editor automation for scripts and AI coding agents | No | Test automation | Yes, a file-based command bridge for every tool |
| Source code | Closed | Closed | Open: runtime MPL-2.0, tools MIT |
| Eye tracking and face tracking | Yes, on headsets that have them | — | No |
| Play PC VR games | Yes | No | No, a development tool |

## What the extra tools give you

### Test without wearing the headset

Mixed reality apps depend on the real world: what the camera sees, where surfaces are, how your hands move. That makes
them slow to test: every change means putting the headset on and walking to the same spot. With MacXR Bridge you
record that moment once (`session rec`), then replay it as often as you like in Play Mode with the headset on your
desk. The runtime feeds the recorded tracking through OpenXR, so the Interaction SDK, MRUK and your own code react
exactly as if you were wearing it. Replays are hermetic: the recorded reference spaces, tracking space and depth come
back, whatever the live headset is doing.

The Meta XR Simulator also records and replays, but what it records is the input you give the simulator in a
synthetic room. There's no real camera image or depth to replay, so it can't test perception.

### Turn a session into a regression test

`session baseline` saves what your app did during a replay (which classes the detector saw and where each label landed)
as `expect.json`. `session test` replays it later and fails if a class disappears, a label moves more than its
tolerance (15 cm by default) or a forbidden class shows up. The replay runs in lockstep with the detector, so results
don't depend on how fast the Mac is: repeated runs give the same detection counts and label positions. Results go to
`TestResults/macxr-sessions.xml` (JUnit), and `-executeMethod MacXRBridge.EditorTools.BridgeSessionTests.RunAllFromCommandLine`
runs every test from the command line for CI. Details: [sessions-and-tests.md](sessions-and-tests.md).

### Mixed reality video straight from the Editor

`record start` composes each real camera frame with your app rendered by a virtual camera placed at the real camera's
pose, with its intrinsics. Virtual objects line up with the real world as they do in the headset. Your app's sound and
your voice from the headset microphone go into the MP4. That's a demo video or bug report without a capture rig.

### Show it to the room

The spectator window shows the same composite live in the Editor, and `stream on` serves it on your network as MJPEG:
open `http://<mac-ip>:8765/` on a phone, a TV browser or OBS. Useful for demos, pair programming and recording the
person wearing the headset.

### See what your perception code sees

`debug on` draws, in the headset, a ray for each detection box: green to where the label landed, red when the ray hit
nothing, with class, score and distance. `debug depth` adds the points where the depth sensor sees surfaces. You
see at a glance whether a wrong label is a detection problem, a raycast problem or a calibration problem.

### A better camera pose

The camera's pose relative to the head, as computed from Camera2's `LENS_POSE_ROTATION`, was about 11° off on our
Quest 3, which put labels about 20 cm too low at 1.5 m. MacXR Bridge calibrates the camera against the depth map (about
2° down, 3° right, 2° roll, ±1°) and uses that. See [features.md](features.md#passthrough-camera-api).

### Let scripts and agents drive the Editor

Every tool is a text command: drop `room-load` in `<project>/.macxr/cmd/1.cmd` and the answer appears in
`.macxr/out/1.out`. Scripts, CI and AI coding agents can enter Play, load the room, check depth, record, replay and
test without touching the screen; the Console is mirrored to `.macxr/unity.log` and the Editor state to
`.macxr/status.json`. Details: [commands.md](commands.md).

### Change it

The runtime, the Quest app and the wire protocol are open source. Missing an extension? The spatial proxy shows how to
forward any headset-side OpenXR call. See [architecture.md](architecture.md).

## Where Quest Link is still ahead

- Eye tracking and face tracking (on headsets that have the sensors).
- Passthrough geometry (projected surfaces), and refresh-rate changes forwarded to the headset.
- Depth textures for occlusion shaders in the Editor (MacXR Bridge provides depth raycasts only).
- It's Meta's own runtime: new Meta XR SDK features usually work over Link first. With MacXR Bridge, a new extension
  has to be added to the runtime.
- Windows PC VR in general: games, SteamVR, and so on.

## Sources

- Meta, [Use Link for app development (Unity)](https://developers.meta.com/horizon/documentation/unity/unity-link/):
  Link is Windows-only; developer runtime features include passthrough, Passthrough Camera API permissions (v2.1+),
  eye tracking and natural facial expressions.
- Meta, [Use passthrough over Link](https://developers.meta.com/horizon/documentation/unity/unity-passthrough-use-over-link/).
- Meta, [Migrate from WebCamTexture to PassthroughCameraAccess](https://developers.meta.com/horizon/documentation/unity/unity-pca-migration-from-webcamtexture/):
  the XR Simulator doesn't support the Passthrough Camera API.
- Meta, [Capture a Meta XR Simulator session](https://developers.meta.com/vr/documentation/unity/xrsim-session-capture/)
  and [No-code automation testing](https://developers.meta.com/vr/documentation/native/xrsim-native-automated-testing/).
- Meta, [Oculus Debug Tool](https://developers.meta.com/horizon/documentation/native/pc/dg-debug-tool/): Performance HUD
  and compositor mirror.
- Unity, [Meta OpenXR: Link](https://docs.unity3d.com/Packages/com.unity.xr.meta-openxr@1.0/manual/link.html):
  passthrough video doesn't render in the Game view.
