#!/bin/zsh
# Reconnects the OXRSys app on the Quest over USB while Unity is in Play mode:
# wakes the Mac's display, brings Unity to the front (the Editor doesn't render in the background/with the display off),
# redoes adb reverse and restarts the app. Waits 5 s between stopping and starting: the server only drops an old
# client (sometimes reconnected over Wi-Fi) after 3 s without tracking, and rejects the new one until then.
set -uo pipefail
source "$(dirname "${(%):-%x}")/common.sh"
adb="$(adb_bin)" || { fail "adb not found"; exit 1; }
caffeinate -u -t 2
osascript -e 'tell application id "com.unity3d.UnityEditor5.x" to activate' >/dev/null 2>&1
for p in "${LPM_PORTS[@]}"; do "$adb" reverse "tcp:${p}" "tcp:${p}" >/dev/null 2>&1; done
"$adb" shell am force-stop net.demonixis.oxrsys.android
sleep 5
"$adb" shell am start -n net.demonixis.oxrsys.android/com.oculus.NativeActivity >/dev/null
sleep 12
"$adb" logcat -d -t 800 2>&1 | grep -E "OXRSys-Android: (USB ADB transport selected|Connection lost|Decoded frame #)" | tail -2 | cut -c1-110
