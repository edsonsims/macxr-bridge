#!/bin/zsh
# End-to-end check of MacXR Bridge with Unity in Play mode and the Quest on USB.
# Reconnects the Quest app, loads the room, checks depth, environment raycast and detection,
# and prints a summary. Usage (Unity open and in Play mode): zsh macxr-bridge/scripts/90-verify-bridge.sh
set -uo pipefail
source "$(dirname "${(%):-%x}")/common.sh"
BRIDGE_DIR="${MXB_ROOT}/../hello-world/unity/HelloWorld/.macxr"
[[ -d "$BRIDGE_DIR/cmd" ]] || { fail "Unity bridge not found at $BRIDGE_DIR (is the project open?)"; exit 1; }

# Sends a command to the Editor's CommandBridge and waits for the reply (up to $2 seconds).
bridge() {
  local n; n=$(( $(ls "$BRIDGE_DIR/out" | grep -E '^[0-9]+\.out$' | sed 's/\.out//' | sort -n | tail -1) + 1 ))
  print -r -- "$1" > "$BRIDGE_DIR/cmd/$n.cmd"
  for i in {1..${2:-30}}; do [[ -f "$BRIDGE_DIR/out/$n.out" ]] && { cat "$BRIDGE_DIR/out/$n.out"; print; return 0; }; sleep 1; done
  print "(no reply from Unity in ${2:-30}s — is the Editor compiling or out of focus?)"; return 1
}

grep -q '"isPlaying": true' "$BRIDGE_DIR/status.json" 2>/dev/null || { warn "Unity is not in Play mode; entering…"; bridge play 10; sleep 20; }

step "Quest"
zsh "${MXB_SCRIPTS_DIR}/80-reconnect-quest.sh"
sleep 5

step "Runtime"
L="${OXR_CONFIG_DIR}/oxrsys-runtime.log"
grep -E "Client connected via|camera channel connected|CameraRelay: depth #|EnvironmentRaycast:" "$L" | tail -6 | cut -c1-160

step "Room (MRUK)"
bridge room-load 10 >/dev/null; sleep 8
bridge room 10 | head -4

step "Depth"
sleep 3
bridge depth 15

step "Detection (live camera, threshold 0.3)"
bridge "camera-show detect thr=0.3" 15
sleep 15
bridge detect-status 15

step "Summary"
print "Images: $BRIDGE_DIR/depth.png (depth), $BRIDGE_DIR/camera.png (camera: 'camera' command)."
print "Look at the objects through the headset: the labels should land on the real surfaces."
