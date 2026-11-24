# Commands and automation

Every MacXR Bridge tool is also a one-line text command. You can send commands from a shell script, a CI job or an AI
coding agent that can only read and write files, without touching the Unity window. The same tools are buttons in
**Window ▸ MacXR Bridge ▸ Panel**.

## The command bridge

The package watches `<project>/.macxr/cmd/` while the Editor is open:

1. Write one command per file: `<project>/.macxr/cmd/<n>.cmd` (for example `12.cmd` containing `room-load`).
2. About twice a second, the Editor takes the lowest-named file, deletes it, runs the command and writes the reply to
   `<project>/.macxr/out/<n>.out`. Commands wait while scripts are compiling.
3. Alongside:
   - `.macxr/unity.log` mirrors the Console (compile errors don't show up here; check
     `~/Library/Logs/Unity/Editor.log`).
   - `.macxr/status.json` holds the Editor state: Unity version, Play/pause/compiling, build target, open scene,
     `XR_RUNTIME_JSON` and the active XR loader.

A small zsh helper that sends a command and waits for the reply (this is what `scripts/90-verify-bridge.sh` uses):

```zsh
BRIDGE_DIR=/path/to/YourUnityProject/.macxr
bridge() {   # bridge "<command>" [timeout seconds]
  local n; n=$(( $(ls "$BRIDGE_DIR/out" | grep -E '^[0-9]+\.out$' | sed 's/\.out//' | sort -n | tail -1) + 1 ))
  print -r -- "$1" > "$BRIDGE_DIR/cmd/$n.cmd"
  for i in {1..${2:-30}}; do [[ -f "$BRIDGE_DIR/out/$n.out" ]] && { cat "$BRIDGE_DIR/out/$n.out"; return 0; }; sleep 1; done
  print "(no reply in ${2:-30}s)"; return 1
}
bridge play 10; sleep 20; bridge room-load; bridge depth
```

Keep the Editor in front: on macOS it barely updates while it's in the background, so replies can be slow.

The command bridge also sets **Script Changes While Playing** to *Recompile After Finished Playing*. Recompiling in the
middle of Play reloads scripts while OpenXR is still running and can crash Unity.

> **Security**: `sh` and `shbg` run shell commands as you. Anything that can write to your project's `.macxr/cmd/`
> folder can run them. Add `.macxr/` to your `.gitignore`.

## Command reference

Arguments in `[brackets]` are optional; `a|b` means one of them.

### Editor

| Command | What it does |
|---|---|
| `status` | Writes and returns the Editor state (same as `status.json`) |
| `play`, `stop` | Enter or exit Play Mode |
| `refresh` | `AssetDatabase.Refresh()` |
| `menu <Menu/Item/Path>` | Runs a menu item |
| `scene <Assets/…/Scene.unity>` | Opens a scene |
| `runtime <manifest.json>` | Sets `XR_RUNTIME_JSON` in this Editor process |
| `env <VAR>` | Reads an environment variable of the Editor process |
| `xr-standalone` | Configures the macOS XR settings (OpenXR, Meta feature, interaction profiles, Run In Background) |
| `hierarchy` | Lists the open scenes' hierarchy |
| `find <name>`, `pos <object>`, `dump <object>` | Finds objects, shows a transform, dumps components |
| `props <object> <Type> [p1,p2]` | Reads a component's fields (use `\|` as the separator when names contain spaces) |
| `enable <object> <Type\|self> <true\|false>` | Enables or disables a component or object |
| `screenshot <file.png>` | Game view screenshot (Play only) |
| `winshot <window title>` | Focuses an Editor window and captures it |
| `sh <zsh command>`, `shbg <zsh command>` | Runs a shell command (waits for it, or in the background) |
| `clearlog` | Empties `.macxr/unity.log` |

### Headset and input

| Command | What it does |
|---|---|
| `hands` | Hand tracking state |
| `micro` | Microgestures received |
| `hud on\|off` | Performance HUD in the headset |
| `debug on\|off\|depth\|nodepth` | Detection rays and depth points in the headset |

### Room and anchors

| Command | What it does |
|---|---|
| `room-load` | `MRUK.LoadSceneFromDevice()` |
| `room` | Describes the loaded room (walls, floor, labels, mesh) |
| `room-draw` | Draws the room's planes and volumes |
| `room-mesh` | Global mesh info |
| `room-fetch [all\|anchors\|semantic]` | Raw anchors from the headset |
| `room-capture` | Launches Space Setup on the headset |
| `anchor <x> <y> <z>` | Creates a test anchor (cyan cube) |
| `anchors` | Lists the test anchors |
| `anchor-save all\|<name>` | Saves anchors on the headset |
| `anchor-load <uuid>[,<uuid>]` | Loads saved anchors (yellow spheres) |
| `anchor-erase <name>` | Erases an anchor |

### Camera, detection and depth

| Command | What it does |
|---|---|
| `camera [file.png]` | Saves the latest Quest camera frame |
| `camera-show [detect] [noquad] [thr=<0–1>] [fixed] [flipv] [fliph] [lens=camera2] [test]` | Shows the camera on a quad in front of you (`noquad` hides it); `detect` drives your detection (`BridgeHooks.WireDetection`); `thr=` lowers the confidence threshold for this Play session; `fixed` places misses at 1.2 m; `lens=camera2` uses Camera2's lens rotation instead of the calibration; `test` uses `.macxr/test.png` instead of the camera |
| `camera-show off` | Stops the feed |
| `detect-status` | Camera, detector, `EnvironmentRaycastManager` and raycast statistics, plus your `AppStatus` |
| `yolo-test [thr=0.1] [cpu]` | Runs the detection model directly on `.macxr/test.png` |
| `agent-test` | Checks what the `ObjectDetectionAgent` receives (saves `.macxr/agent-tex.png`) |
| `depth` | Saves `.macxr/depth.png` and casts a test ray from the head |

### Audio and microphone

| Command | What it does |
|---|---|
| `audio` | App audio status |
| `audio beep` | Plays a test tone in the headset |
| `audio mac on\|off` | Also play the app's sound on the Mac |
| `mic` | Headset microphone status and level |
| `mic test` | Records 3 seconds from the headset microphone |
| `mic default on\|off` | Makes the headset microphone the Mac's default input, or restores the previous one |

### Video and spectator

| Command | What it does |
|---|---|
| `record start [name] [nomic]` | Starts a mixed reality recording (`nomic` leaves out your voice) |
| `record stop` | Finishes it: `<project>/Recordings/<name>-<date>.mp4` |
| `spectator` | Opens the Spectator window |
| `stream on [port]`, `stream off` | MJPEG stream of the composite on `http://<mac-ip>:8765/` |

### Sessions and tests

| Command | What it does |
|---|---|
| `session rec [name] [fps=N]` | Records a session |
| `session stop` | Stops recording or replaying |
| `session play [name\|last] [loop] [speed=X] [paused] [norig]` | Replays a session |
| `session pause`, `session resume` | Pauses or resumes the replay |
| `session seek <s\|+s\|-s>` | Jumps to a time |
| `session step [n]` | Steps n camera frames (negative to go back) |
| `session speed <x>` | Replay speed (0.25–2) |
| `session list` | Lists sessions |
| `session baseline [name]` | Writes `expect.json` from a replay |
| `session test [name\|all] [realtime]` | Replays and checks; JUnit in `TestResults/macxr-sessions.xml` |
| `session report` | Last test report, or the progress of a running test |

More in [sessions-and-tests.md](sessions-and-tests.md).

## Scripts

All in `scripts/`, written for zsh (the macOS default shell). Run them from anywhere.

| Script | What it does |
|---|---|
| `00-check-prereqs.sh` | Read-only check of everything the build and Unity need, with install commands |
| `10-build-runtime.sh [--test]` | Builds the macOS runtime; `--test` runs its unit tests (with Play stopped: Unity holds the ports) |
| `20-build-home.sh` | Builds the OXRSys Home app (optional) |
| `30-build-quest-client.sh [--debug]` | Builds the Quest app |
| `40-install-quest.sh [--debug]` | Installs it on the Quest and sets up `adb reverse` |
| `50-configure-runtime.sh` | Registers the runtime and writes the streaming settings |
| `60-unity-add-package.sh <project>` | Adds the Unity package to a project |
| `65-install-mic-driver.sh` | Installs the headset microphone driver (asks for your password) |
| `70-pull-room-json.sh <app package> [dest.json]` | Pulls a room JSON exported by `unity-tools/RoomJsonExporter.cs` from a headset build |
| `80-reconnect-quest.sh` | Brings Unity to the front, redoes `adb reverse` and restarts the Quest app over USB |
| `90-verify-bridge.sh [project]` | End-to-end check in Play: room, depth, raycast, detection |
| `prox-keepalive.sh` | Keeps the headset's proximity sensor "covered" for tests with nobody wearing it |
| `all.sh` | Runtime, Home app, Quest app, install, registration |
| `install.sh` | Everything a new Mac needs, in one go |
| `runner.command` | The task runner (below) |

Set `QUEST_SERIAL=<serial>` when more than one headset is connected.

## Task runner

`scripts/runner.command` is for tools that can write files but can't run commands on the Mac directly, such as an AI
agent working in a sandbox with your project folder mounted. Double-click it to open it in Terminal. It runs, in order,
every `.sh` file dropped into `.runner/queue/` and writes each one's output to `logs/<name>.log`. To stop it, close the
window, press Ctrl-C or create `.runner/STOP`.

Two rules that save time:
- Never run `adb` through the command bridge's `sh`: a blocked `adb` freezes the Editor. Use the runner.
- Overwriting `runner.command` while it runs kills it (zsh reads scripts as it goes). Restart it afterwards.

> **Security**: the runner executes whatever is put in its queue. Only run it while you're using it.
