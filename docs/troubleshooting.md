# Troubleshooting

First stops: **Window ▸ MacXR Bridge ▸ Setup & Diagnostics** (every piece, with fixes), the Panel's Status section,
and the logs listed at the end of this page.

## Connection

**Play runs, but nothing appears on the Quest.**
Unity probably isn't using the MacXR Bridge runtime. Setup & Diagnostics ▸ *This Unity uses the runtime* fixes it in
the running Editor. Unity opened before `50-configure-runtime.sh` doesn't inherit `XR_RUNTIME_JSON`; quitting and
reopening Unity Hub and Unity fixes it for good. The `status` command shows which runtime the Editor sees.

**The Quest app connects, then gives up after about 5 seconds ("no initial video frames").**
Unity on macOS stops rendering when its window isn't in front, so no video is encoded. Bring Unity to the front with
the Game view visible, then restart the Quest app. `scripts/80-reconnect-quest.sh` does both.

**It's on Wi-Fi even though the cable is plugged in.**
The Quest app only picks USB if Unity is already in Play when it connects. Press Play first, then open the app, or run
`scripts/80-reconnect-quest.sh` while in Play. Over Wi-Fi the bitrate drops to about 10 Mbps, with more stutter.

**The headset asks to "Allow USB debugging" every time.**
Tick **Always allow from this computer** in the prompt. Unity stops the adb server when it quits, and an authorization
that wasn't remembered is asked for again.

**After reinstalling the Quest app, the headset only shows passthrough or the Quest home.**
Installing an APK closes the app, and re-authorizing USB drops `adb reverse`. Reopen the app (Library ▸ Unknown
Sources ▸ OXRSys) or run `scripts/80-reconnect-quest.sh`.

**The connection drops when I take the headset off.**
The proximity sensor pauses the Quest app, like any app. Put it back on and it reconnects. For tests with nobody
wearing it, run `scripts/prox-keepalive.sh` (it keeps the sensor "covered" and resends every 5 s).

## Camera, depth and room

**No camera, depth or headset stats after the headset slept.**
Put the headset on: the app reopens the camera when it resumes. Otherwise run `scripts/80-reconnect-quest.sh`.

**MRUK says `NoRoomsFound`.**
Run Space Setup on the headset first, or launch it from the Editor with `room-capture`.

**`EnvironmentRaycastManager.IsSupported` is false in the Editor.**
Unity is running an older runtime build. Quit Unity, run `scripts/10-build-runtime.sh`, reopen Unity and check with
`detect-status`. Unity loads the runtime when XR starts and keeps it until it quits.

**Labels land in the wrong place.**
Turn on `debug on` to see each detection's ray in the headset: a red ray means the ray hit nothing (a depth or room
problem); a green ray ending in the wrong place means a calibration or detection problem. `camera-show lens=camera2`
compares the calibrated camera pose with Camera2's. Check `detect-status` for how many raycasts fell back to the room
or to physics.

**Anchors stop updating after the headset was off.**
When the Quest app pauses, it releases its spaces, and anchors already in Unity are orphaned. Load them again by UUID
(`anchor-load`), as you would after a Quest Link drop.

## Audio

**No sound in the headset.**
`audio beep` plays a test tone. `audio` shows whether the tap is running. It's added to the scene's `AudioListener` in
Play, so the scene needs one.

**The headset microphone isn't listed on the Mac.**
Install the driver: `zsh scripts/65-install-mic-driver.sh` (asks for your password). On the Quest, allow the
microphone for the OXRSys app. `mic` shows the device and level; `mic test` records 3 seconds.

**Unity freezes the first time the app uses the microphone.**
macOS is waiting for you to allow Unity to use the microphone. Accept the prompt once.

## Editor

**Unity crashed after I edited a script during Play.**
Recompiling in the middle of Play reloads scripts while OpenXR is still running. The command bridge sets *Script Changes
While Playing* to *Recompile After Finished Playing*; leave it that way and edit scripts with Play stopped.

**A command never gets a reply.**
Commands wait while Unity compiles, and the Editor barely updates in the background. Bring Unity to the front. Compile
errors are not in `.macxr/unity.log`; look in `~/Library/Logs/Unity/Editor.log` for `error CS`.

**`10-build-runtime.sh --test` fails with port errors.**
Stop Play first: Unity's streaming server holds the ports the loopback tests use.

**Recording or the spectator stream does nothing.**
They need ffmpeg (`brew install ffmpeg`). Setup & Diagnostics checks for it.

## Logs

| What | Where |
|---|---|
| Runtime | `~/Library/Application Support/OXRSys/oxrsys-runtime.log` |
| Unity Console (mirror) | `<project>/.macxr/unity.log` |
| Unity Editor (including compile errors) | `~/Library/Logs/Unity/Editor.log` |
| Quest app | `adb logcat -s OXRSys-Android` |

When scripting `adb logcat -d`, check `adb get-state` first: with no device, it waits forever.
