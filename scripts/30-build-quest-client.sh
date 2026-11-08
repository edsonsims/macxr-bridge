#!/bin/zsh
# Compila o APK do cliente para o Quest. Padrão: assembleRelease (debug-signed, estável para sideload).
# Use --debug para assembleDebug.
set -euo pipefail
source "$(dirname "${(%):-%x}")/common.sh"

step "Cliente Quest (Android)"
sdk="$(android_sdk_dir)" || { fail "Android SDK não encontrado (ANDROID_HOME ou ~/Library/Android/sdk)"; exit 1; }
cd "${OXR_ANDROID_DIR}"
if [[ ! -f local.properties ]]; then
  print -r -- "sdk.dir=${sdk}" > local.properties
  ok "local.properties criado com sdk.dir=${sdk}"
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
[[ -f "$apk" ]] || { fail "APK não gerado: $apk"; exit 1; }
ok "APK: $apk"
