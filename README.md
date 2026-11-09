# MacXR Bridge

A Quest Link-style workflow for macOS: press Play in Unity on the Mac and the app shows up on the Quest 3 with
real head tracking, hands and passthrough. A fork of [OXRSys](https://github.com/demonixis/oxrsys)
(a native OpenXR runtime for macOS, MPL-2.0) with what's missing for mixed reality development
with the Meta XR SDK.

> MacXR Bridge is an independent project. It is not affiliated with, sponsored or endorsed by Meta Platforms, Inc.
> Meta, Meta Quest, Quest Link and Air Link are trademarks of Meta Platforms, Inc., used here only to describe compatibility.
> Built on [OXRSys](https://github.com/demonixis/oxrsys) by Yannick Comte and contributors (MPL-2.0).

Research, decisions and the full development log: [`../docs/macxr-bridge.md`](../docs/macxr-bridge.md).
What works and every tool, in detail: [`unity-package/com.macxrbridge.xr/README.md`](unity-package/com.macxrbridge.xr/README.md).

## What you get

- **Quest Link parity in Play Mode**: passthrough, spatial anchors (create, save, load, erase), MRUK room with semantic
  labels and global mesh, the Passthrough Camera API with the real camera, environment depth and
  `EnvironmentRaycastManager`, headset audio and microphone, microgestures, over USB or Wi-Fi.
- **Beyond Quest Link**: mixed reality video recording with your voice, a live spectator view (window and local-network
  stream), session record and replay without the headset (with a timeline), deterministic perception tests with a JUnit
  report and a command-line runner, an in-headset debug overlay and a performance HUD.

## Requirements

Apple Silicon Mac, macOS 14+, Xcode (+ Metal Toolchain), Homebrew (`cmake ninja vulkan-headers`), Java 17, Android SDK
(platform 35, NDK 28.2.13676358, cmake 3.22.1, platform-tools). Unity 6000.0 with the OpenXR plug-in and Meta XR SDK 207
(Core, MRUK). Quest 3 in developer mode, hand tracking on. Tested with Unity 6000.0.85f2 and a Quest 3.

## Install

```bash
zsh macxr-bridge/scripts/00-check-prereqs.sh             # tells you what is missing
zsh macxr-bridge/scripts/install.sh                      # runtime, Quest app, OpenXR registration, microphone, ffmpeg
zsh macxr-bridge/scripts/60-unity-add-package.sh /path/to/UnityProject
```

`install.sh` asks for the Mac password once (microphone driver). Keep the Quest on the cable and accept
"Allow USB debugging" with **Always allow from this computer** ticked.

In Unity: **Window ▸ MacXR Bridge ▸ Setup & Diagnostics** checks every piece (and points the Editor at the runtime),
then open the OXRSys app on the Quest and press **Play**. **Window ▸ MacXR Bridge ▸ Panel** has every tool.

## How it works

```
Unity Editor (Mac) ──OpenXR──▶ OXRSys runtime (fork) ════ USB (adb reverse) or Wi-Fi ════▶ Quest client app
                                video  HEVC (VideoToolbox) ─────────────────────────────▶ decode + reprojection
                                tracking  ◀── head, hands, controllers, microgestures
                                spatial   ◀─▶ anchors, scene (MRUK), proxied to the headset
                                camera    ◀── camera frames, depth, microphone, headset stats
                                          ──▶ app audio
```

The runtime is a native OpenXR runtime for macOS; the Meta XR SDK runs unchanged in the Editor. The microphone reaches
macOS apps through a small Core Audio driver ("Headset Microphone (MacXR Bridge)").

## Repository layout

- `oxrsys/` — the runtime and Quest client: a fork of OXRSys (branch `macxr-bridge`; remote `upstream` = the original).
- `unity-package/com.macxrbridge.xr/` — the Unity package (Editor windows, commands, recording, sessions, tests).
- `mic-driver/` — the Core Audio driver for the headset microphone.
- `scripts/` — `00` prerequisites · `10` runtime · `20` OXRSys Home app · `30` Quest client · `40` install on the Quest ·
  `50` register the runtime · `60` add the Unity package · `65` microphone driver · `70` pull a room JSON ·
  `80` reconnect the Quest · `90` end-to-end check · `install.sh` everything · `runner.command` task runner for automation.
- `tools/align-camera-depth.py` — checks camera/depth alignment from saved frames.
- `unity-tools/` — room JSON exporter for a headset build (from before the live room worked; kept as a fallback).

## Troubleshooting

- **The Quest asks for USB debugging again**: tick "Always allow from this computer". Unity stops the adb server when it
  quits, and an authorization that was not remembered is asked for again.
- **No camera or stats after the headset slept**: put it on (the proximity sensor pauses the app) or run
  `scripts/80-reconnect-quest.sh`.
- **Play shows nothing on the Quest**: Setup & Diagnostics ▸ "This Unity uses the runtime"; Unity opened before
  `50-configure-runtime.sh` does not inherit `XR_RUNTIME_JSON` (the Setup window fixes it in-process).
- **Wi-Fi**: Mac and Quest on the same local network; USB is used whenever the cable and authorization are there.
- Runtime tests (`10-build-runtime.sh --test`) only pass with Play stopped: Unity holds the streaming ports.

## Known limits

Apple Silicon only. Tested on Quest 3 with Meta XR SDK 207 and Unity 6000.0. Refresh-rate requests are accepted but not
forwarded to the headset. The spectator stream is MJPEG on the local network, without authentication. The microphone
driver is ad-hoc signed and built on your Mac.

## License

The runtime and Quest client stay MPL-2.0 (OXRSys license; modified files stay MPL-2.0 and their source must be available
when distributed). Unity package and microphone driver: MIT.
