#!/bin/zsh
# Installs the APK on the Quest and sets up adb reverse for the USB streaming ports.
# Use --debug to install the debug APK. Set QUEST_SERIAL=<serial> if more than one device is connected.
set -euo pipefail
source "$(dirname "${(%):-%x}")/common.sh"

step "Install on the Quest"
adb="$(adb_bin)" || { fail "adb not found"; exit 1; }
serial="$(quest_serial)" || { fail "No authorized headset on adb. Plug in the cable and accept 'Allow USB debugging' on the headset."; exit 1; }
apk="${OXR_APK_RELEASE}"; [[ "${1:-}" == "--debug" ]] && apk="${OXR_APK_DEBUG}"
[[ -f "$apk" ]] || { fail "APK not found: $apk (run 30-build-quest-client.sh)"; exit 1; }

"$adb" -s "$serial" install -r "$apk"
ok "APK installed on ${serial}"

for p in "${LPM_PORTS[@]}"; do "$adb" -s "$serial" reverse "tcp:${p}" "tcp:${p}"; done
ok "adb reverse set up on ports ${LPM_PORTS[*]}"
"$adb" -s "$serial" reverse --list | sed 's/^/   /'

pkg="$("$adb" -s "$serial" shell pm list packages 2>/dev/null | grep -F "net.demonixis.oxrsys.android" | head -1 | sed 's/package://')"
[[ -n "$pkg" ]] && ok "Package: ${pkg}" || warn "Couldn't find the package by the name 'oxrsys'; look for the app under Library → Unknown Sources."
print
info "On the headset: Library → Unknown Sources → open the \"OXRSys\" app. It waits for the Mac."
info "Hand tracking must be turned on in Settings → Movement tracking → Hands and controllers."
