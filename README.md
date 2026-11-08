# Link para Mac

A "Meta Quest Link" for macOS: press Play in Unity on the Mac and the app shows up on the Quest 3 with
real head tracking, hands and passthrough. A fork of [OXRSys](https://github.com/demonixis/oxrsys)
(a native OpenXR runtime for macOS, MPL-2.0) with what's missing for mixed reality development
with the Meta XR SDK. Working name: "Link" is a Meta trademark; pick a neutral name before publishing.

Research, decisions and backlog: [`../docs/link-para-mac.md`](../docs/link-para-mac.md).

## How it works

```
Unity Editor (Mac) ──OpenXR──▶ OXRSys runtime ──VideoToolbox HEVC──▶ USB (adb reverse) or Wi-Fi ──▶ Quest client
                                      ◀────────── head, hands (XR_EXT_hand_tracking), controllers ◀──────────
```

- `oxrsys/` — the fork (branch `link-para-mac`; remote `upstream` = original project).
- `scripts/` — automated Phase 1 walkthrough (below).
- `unity-tools/` — Unity scripts: export the real room from the headset to use in the Editor.

## Phase 1 — make it work (before Nov 18)

Prerequisites on the Mac: macOS 14+, Xcode (+ Metal Toolchain), `brew install cmake ninja vulkan-headers`,
Java 17, Android SDK (platform 35, NDK 28.2.13676358, cmake 3.22.1, platform-tools), Unity 6000.0.66f2+.
Quest 3 in developer mode, with hand tracking turned on.

```bash
cd link-para-mac/scripts
./00-check-prereqs.sh       # checks everything and tells you what's missing
./all.sh                    # or one at a time:
./10-build-runtime.sh       #   runtime (build/macos-arm64/runtime/oxrsys-runtime.json)
./20-build-home.sh          #   OXRSys Home app (status, ADB, settings)
./30-build-quest-client.sh  #   client APK (assembleRelease)
./40-install-quest.sh       #   installs on the Quest + adb reverse 9944/9945/9946/9948
./50-configure-runtime.sh   #   user default runtime + toml with passthrough and alpha turned on
```

Then, in the Unity project (Unity 6 + OpenXR Plugin + Meta XR All-in-One 207):

```bash
./60-unity-add-package.sh /path/to/UnityProject   # adds the net.demonixis.oxrsys-unity package
```

In Unity: `Tools → OpenXR → Use OXRSys Runtime` → open the client on the Quest → **Play**.
Note: Unity must have been opened *after* `50-configure-runtime.sh` so it inherits `XR_RUNTIME_JSON`.

### Validation checklist (day 1)
1. Empty scene with `OVRCameraRig`: Play shows the scene on the Quest and the head moves the camera.
2. Hands: `OVRHand` / ISDK receive joints; pinch works.
3. Passthrough: `OVRPassthroughLayer` or a camera with a transparent background shows up as passthrough on the Quest.
   If OVRPlugin complains that `XR_FB_passthrough` is missing, that's the first fork item (see backlog).
4. Latency good enough to iterate (move your hand and see the response without motion sickness).
5. Real room in the Editor: `unity-tools/RoomJsonExporter.cs` in the build → `./70-pull-room-json.sh <package>` → MRUK with Data Source = Device With Json Fallback.

### What does NOT come through the Link yet (use a headset build)
Quest camera (PCA), spatial anchors, MRUK scene/anchors from the headset (use the JSON), microgestures, headset audio.

## Fork backlog (Phase 2, after Nov 18)
1. `XR_FB_passthrough` (and `XR_FB_composition_layer_alpha_blend`) exposed to the app, mapped to the alpha the runtime already streams — so `OVRPassthroughLayer` works with no app changes.
2. Quest camera relay: Camera2 on the client → H.264 → Mac → virtual camera (CoreMediaIO Camera Extension) with camera intrinsics and pose, so `PassthroughCameraAccess` works in the Editor.
3. Scene and anchor relay (`XR_FB_scene`, `XR_FB_spatial_entity`) over the "spatial" channel 9948, already reserved in the protocol.
4. Microgestures (`XR_META_hand_tracking_microgestures`), headset audio (microphone and speaker), one-click installer, documentation.

## License
Code inherited from OXRSys stays MPL-2.0 (modified files stay MPL-2.0). New code outside `oxrsys/`: to be decided.
