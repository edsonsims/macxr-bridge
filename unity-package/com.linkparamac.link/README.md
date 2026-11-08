# Link para Mac

Unity Play Mode on a **Meta Quest 3 from a Mac** — what Quest Link does on Windows, plus developer tools Quest Link
does not have. Built on a fork of [OXRSys](https://github.com/demonixis/oxrsys) (macOS OpenXR runtime that streams to
the Quest), with the Meta XR SDK 207 running unchanged in the Editor.

## What works in Play Mode

| Quest Link parity | |
|---|---|
| Passthrough (`XR_FB_passthrough`) | official layer, styles |
| Spatial anchors | create, save, load, erase (proxied to the headset) |
| Scene / MRUK | real room with semantic labels (walls, floor, table, screen…) and global mesh |
| Passthrough Camera API (PCA) | the real Quest camera, 1280×960 @ 30 fps, calibrated pose and intrinsics |
| Environment depth + raycast | `EnvironmentRaycastManager` works in the Editor (`XR_META_environment_raycast` answered with the relayed depth) |
| Headset audio | the app's mix plays in the Quest |
| **Headset microphone** | "Quest 3 Microphone (Link para Mac)" audio device, default input while the app runs — Unity's `Microphone` records your voice from the headset |
| Microgestures | `XR_META_hand_tracking_microgestures` |
| Display refresh rate, color space, passthrough preferences | `XR_FB_display_refresh_rate`, `XR_FB_color_space`, `XR_META_passthrough_preferences` |
| USB **and Wi-Fi** | room, anchors, camera, depth, audio and mic also over Wi-Fi (Air Link style) |

| Beyond Quest Link | |
|---|---|
| **Mixed reality recording** | real camera + your app rendered at the camera's pose/intrinsics + app audio + your voice → MP4 (hardware H.264) |
| **Live spectator** | Editor window and an MJPEG stream on the local network (browser, phone, TV, OBS Browser Source) |
| **Session record & replay** | record camera, depth, room and the full tracking stream (head, hands, controllers, microgestures); replay **without the headset** — the runtime feeds the recorded tracking through OpenXR, so the whole app reacts as if you were wearing it |
| **Replay timeline** | pause, seek, frame step, 0.25×–2× |
| **Perception tests** | `baseline` a session, then `test` it: classes the detector must see, where labels must land (cm tolerance), forbidden classes. Deterministic lockstep with the detector; JUnit report |
| **In-headset debug overlay** | detector rays (green = landed, red = missed) with class/distance, environment depth points |
| **Performance HUD** | transport, bitrate, latency (Mac + Quest), decode/encode times, headset fps, battery, temperature, camera/depth/mic |

## Install

1. `link-para-mac/scripts/install.sh` — builds the runtime, the Quest client (installs it over USB), registers the
   OpenXR runtime, installs the microphone driver (asks for the Mac password once) and ffmpeg.
2. Add this package to your project (`Packages/manifest.json`):
   `"com.linkparamac.link": "file:<path>/link-para-mac/unity-package/com.linkparamac.link"`
3. **Window ▸ Link para Mac ▸ Instalação e diagnóstico** checks every piece and fixes what is missing (it also points
   this Editor at the runtime, no special launch needed). **Window ▸ Link para Mac ▸ Painel** has everything else.

Requires: Apple Silicon Mac, Unity 6000.0, Meta XR SDK (core, MRUK) 207, OpenXR plug-in, Quest 3 in developer mode.

## Connect your app

The package is generic. App-specific bits go through `LinkParaMac.EditorTools.LinkHooks` (Editor code in your project):
`WireDetection` (turn on your real detection when the Quest camera arrives), `ReportPlacedBatch` (where your app placed
things — used by tests and the debug overlay), `IgnoreCollider`, `ApplyAppThreshold`, and `DetectNow` / `HoldDetection`
/ `DetectionStarted` for deterministic tests. See `HelloWorldLinkHooks.cs` in the Hello World project for a complete
example (~100 lines).

## Commands

Everything in the panel is also a command for automation (file bridge `.link/cmd/N.cmd`):
`record start [name] [nomic]` · `record stop` · `session rec [name]` · `session play [name] [loop]` ·
`session pause|resume|seek <s>|step [n]|speed <x>` · `session baseline [name]` · `session test [name|all]` ·
`debug on|depth|off` · `hud on|off` · `stream on|off` · `mic [test]` · `audio beep` · `room-load` · `depth`.

Command line test run (opens the Editor, runs every session that has `expect.json`, exits with the result):

```
Unity -projectPath <project> -executeMethod LinkParaMac.EditorTools.LinkSessionTests.RunAllFromCommandLine
```

## License

Unity package: MIT. Runtime and Quest client (OXRSys fork): MPL-2.0. Microphone driver: MIT.
