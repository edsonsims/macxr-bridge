#!/bin/zsh
# Link para Mac — one-step install: Mac OpenXR runtime, Quest app, Quest microphone, ffmpeg.
# Requires: Xcode (command line tools), Android SDK/NDK, Homebrew, the Quest on the cable with USB debugging
# authorized ("Always allow"). macOS asks for the Mac password once (microphone driver).
set -euo pipefail
here="$(dirname "${(%):-%x}")"
source "$here/common.sh"
zsh "$here/00-check-prereqs.sh"
zsh "$here/10-build-runtime.sh"
zsh "$here/30-build-quest-client.sh"
zsh "$here/40-install-quest.sh"
adb="$(adb_bin)" && "$adb" shell pm grant net.demonixis.oxrsys.android android.permission.RECORD_AUDIO 2>/dev/null && ok "microphone allowed in the Quest app" || warn "allow the microphone on the Quest when the app asks"
zsh "$here/50-configure-runtime.sh"
zsh "$here/65-install-mic-driver.sh"
step "ffmpeg (recordings and spectator)"
if [[ -x /opt/homebrew/bin/ffmpeg ]]; then ok "ffmpeg present"; else brew install ffmpeg && ok "ffmpeg installed"; fi
print
ok "Done. In Unity: Window ▸ Link para Mac ▸ Panel (and Setup & Diagnostics to check everything), then Play with the Quest on the cable."
