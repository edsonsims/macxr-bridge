# Getting started

This guide takes you from a fresh clone to your Unity app running on a Quest 3 from Play Mode on your Mac.
It takes about 15 minutes, most of it compiling.

## 1. What you need

**Hardware**
- An Apple Silicon Mac (M1 or newer) with macOS 14 or newer.
- A Meta Quest 3 in [developer mode](https://developers.meta.com/horizon/documentation/native/android/mobile-device-setup/),
  with hand tracking on (Settings ▸ Movement tracking ▸ Hands and controllers).
- A USB-C data cable. Wi-Fi works too, but you need the cable for the install and it gives the best quality.

**Mac software** (the check script in step 3 tells you how to install anything that's missing)
- Xcode with the command line tools and the Metal Toolchain (`xcodebuild -downloadComponent MetalToolchain`).
- Homebrew with `cmake`, `ninja` and `vulkan-headers`.
- Java 17 (`brew install --cask temurin@17`, or Android Studio's bundled JDK).
- Android SDK with platform 35, NDK 28.2.13676358, CMake 3.22.1, build-tools and platform-tools (`adb`).
- ffmpeg, for recordings and the spectator stream (`install.sh` installs it with Homebrew).

**Unity project**
- Unity 6000.0 (6000.0.66f2 or newer, as Meta XR SDK 207 requires; tested with 6000.0.85f2).
- OpenXR plug-in (tested with 1.15.1) and XR Plug-in Management.
- Meta XR Core SDK 207 and Meta MR Utility Kit (MRUK) 207. The package's Editor code references both, so they must be
  in the project. The Interaction SDK is optional but recommended for hands.

## 2. Get the code

The runtime and the Quest app live in a submodule (`oxrsys/`), so clone recursively:

```bash
git clone --recursive https://github.com/edsonsims/macxr-bridge.git
cd macxr-bridge
```

Already cloned without `--recursive`? Run `git submodule update --init`.

## 3. Check the prerequisites

```bash
zsh scripts/00-check-prereqs.sh
```

It only reads: it lists every tool with ✔ or ✖ and the exact command to install what's missing. Run it again until
everything you need is green. Plug in the Quest and accept **Allow USB debugging** on the headset with
**Always allow from this computer** ticked, so the check can see it.

## 4. Install

```bash
zsh scripts/install.sh
```

What it does, in order (each step is also its own script, if you'd rather run them one at a time):

| Step | Script | What it does |
|---|---|---|
| 1 | `00-check-prereqs.sh` | Checks the tools again |
| 2 | `10-build-runtime.sh` | Builds the macOS OpenXR runtime (`liboxrsys-runtime.dylib`, the video encoder helper and the runtime manifest) |
| 3 | `30-build-quest-client.sh` | Builds the Quest app (`app-release.apk`, debug-signed for sideloading) |
| 4 | `40-install-quest.sh` | Installs it on the Quest and sets up `adb reverse` for the USB channels |
| 5 | (inline) | Lets the Quest app use the headset microphone |
| 6 | `50-configure-runtime.sh` | Makes it your Mac's active OpenXR runtime (`~/.config/openxr/1/active_runtime.json` plus a LaunchAgent that sets `XR_RUNTIME_JSON` for apps opened from the Dock or Unity Hub) and writes streaming settings tuned for mixed reality |
| 7 | `65-install-mic-driver.sh` | Builds and installs the "Headset Microphone (MacXR Bridge)" Core Audio driver. **Asks for your Mac password** |
| 8 | (inline) | Installs ffmpeg with Homebrew if it's missing |

Optional: `zsh scripts/20-build-home.sh` builds **OXRSys Home**, the upstream macOS app for choosing the runtime,
checking ADB and viewing the stream's status.

## 5. Add the package to your Unity project

```bash
zsh scripts/60-unity-add-package.sh /path/to/YourUnityProject
```

This adds one line to `Packages/manifest.json`, as a local `file:` package:

```json
"com.macxrbridge.xr": "file:<relative path>/macxr-bridge/unity-package/com.macxrbridge.xr"
```

You can also add it by hand (Package Manager ▸ + ▸ Add package from disk ▸ `unity-package/com.macxrbridge.xr/package.json`).
Add `.macxr/`, `Recordings/`, `Sessions/` and `TestResults/` to your project's `.gitignore` (or keep `Sessions/` if you
want to version your test sessions).

## 6. Check everything in Unity

1. **Quit and reopen Unity Hub and Unity**, so they inherit the new OpenXR runtime.
2. Open **Window ▸ MacXR Bridge ▸ Setup & Diagnostics**. It checks:
   - **Mac OpenXR runtime**: built and registered.
   - **This Unity uses the runtime**: if Unity was opened before the install, the window points this Editor at
     the runtime (no restart needed).
   - **Project**: XR Plug-in Management for macOS (Standalone) uses OpenXR with the Meta features and the hand
     interaction profiles, and Run In Background is on. One button configures it.
   - **Quest app**: installed on the headset (checked with `adb` in the background).
   - **Headset microphone**: the audio driver is installed.
   - **ffmpeg**: present, for recordings and the stream.

   Rows that pass hide their buttons; the others offer Install, Rebuild or Fix.

## 7. First Play

1. Put on the Quest and open **Library ▸ Unknown Sources ▸ OXRSys**. It shows a waiting screen.
   The first time, allow the permissions it asks for (camera, microphone, spatial data).
2. In Unity, press **Play**. Within a few seconds your scene appears in the headset, over passthrough if your app
   uses it, with your hands tracked.
3. Open **Window ▸ MacXR Bridge ▸ Panel**: the Status section shows the transport (USB or Wi-Fi), the stream and
   the headset's battery; Real world shows the camera, depth and room.

Keep Unity in front with the Game view visible: on macOS the Editor barely renders when it's in the background.

If the Quest app was opened before Play, it may connect over Wi-Fi. To force USB, restart it while Play is running:

```bash
zsh scripts/80-reconnect-quest.sh
```

## 8. End-to-end check (optional)

With Unity in Play and the Quest on the cable:

```bash
zsh scripts/90-verify-bridge.sh /path/to/YourUnityProject
```

It reconnects the Quest, loads the room, checks depth, the environment raycast and object detection, and prints a
summary. It uses the Editor command bridge (see [commands.md](commands.md)).

## Configuration

**Streaming settings**: `~/Library/Application Support/OXRSys/oxrsys-runtime.toml`. `50-configure-runtime.sh` backs up
the existing file and sets:

| Key | Value | Why |
|---|---|---|
| `transport` | `"auto"` | USB (adb reverse) first, Wi-Fi as fallback |
| `video_codec` | `"h265"` | HEVC with VideoToolbox |
| `bitrate_mbps` | `80` (or `$MXB_BITRATE_MBPS`) | Comfortable over USB; Wi-Fi adapts down |
| `refresh_rate_hz` | `72` (or `$MXB_REFRESH_HZ`) | |
| `passthrough_enabled` | `true` | The Quest keeps passthrough available |
| `app_alpha_blend_passthrough` | `true` | Transparent pixels in your app show the real world |
| `client_reprojection` | `"pose"` | The Quest reprojects late frames with the latest head pose |

**Environment variables**

| Variable | Used by | Effect |
|---|---|---|
| `QUEST_SERIAL` | scripts | Which headset to use when more than one is on adb |
| `MXB_BITRATE_MBPS`, `MXB_REFRESH_HZ` | `50-configure-runtime.sh` | Override the bitrate and refresh rate |
| `MXB_UNITY_PROJECT` | `90-verify-bridge.sh` | Default Unity project for the check |
| `OXRSYS_MIC_DEFAULT=0` | runtime | Don't make the headset microphone the default Mac input while the app runs |

**Editor preferences** (EditorPrefs)

| Key | Default | Effect |
|---|---|---|
| `MacXRBridge.AutoCamera` | `true` | When the Quest camera connects in Play, feed it to `PassthroughCameraAccess` automatically |
| `MacXRBridge.AudioToMac` | `false` | Also play the app's sound on the Mac (`audio mac on` / `audio mac off`) |

## Updating

```bash
git pull
git submodule update --init
```

Then quit Unity, rebuild the runtime (`zsh scripts/10-build-runtime.sh`) and, if the Quest app changed, rebuild and
reinstall it (`zsh scripts/30-build-quest-client.sh && zsh scripts/40-install-quest.sh`). Unity loads the runtime when
it starts XR, so a runtime rebuilt while Unity is open only takes effect after a restart.

## Uninstalling

```bash
zsh oxrsys/scripts/oxrsys_runtime_default.sh unset         # stop using it as the OpenXR runtime (removes the LaunchAgent)
sudo rm -rf /Library/Audio/Plug-Ins/HAL/MacXRBridgeMic.driver && sudo killall coreaudiod   # microphone driver
adb uninstall net.demonixis.oxrsys.android                 # the Quest app
```

Then remove the `com.macxrbridge.xr` line from your project's `Packages/manifest.json`.
