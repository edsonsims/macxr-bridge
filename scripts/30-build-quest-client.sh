#!/bin/zsh
# Builds the Quest client APK. Default: assembleRelease (debug-signed, stable for sideloading).
# Use --debug for assembleDebug.
set -euo pipefail
source "$(dirname "${(%):-%x}")/common.sh"

step "Quest client (Android)"
sdk="$(android_sdk_dir)" || { fail "Android SDK not found (ANDROID_HOME or ~/Library/Android/sdk)"; exit 1; }
cd "${OXR_ANDROID_DIR}"
if [[ ! -f local.properties ]]; then
  print -r -- "sdk.dir=${sdk}" > local.properties
  ok "local.properties created with sdk.dir=${sdk}"
fi
if [[ -z "${JAVA_HOME:-}" ]] && /usr/libexec/java_home -v 17 >/dev/null 2>&1; then
  export JAVA_HOME="$(/usr/libexec/java_home -v 17)"
fi
if [[ "${1:-}" == "--debug" ]]; then
  ./gradlew assembleDebug
  apk="${OXR_APK_DEBUG}"
else
  ./gradlew assembleRelease
  apk="${OXR_APK_RELEASE}"
fi
[[ -f "$apk" ]] || { fail "APK not generated: $apk"; exit 1; }
ok "APK: $apk"
