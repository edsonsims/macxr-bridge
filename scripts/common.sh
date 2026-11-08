#!/bin/zsh
# Link para Mac — variáveis e funções compartilhadas pelos scripts.
# Este arquivo é carregado com `source`; não execute diretamente.

LPM_SCRIPTS_DIR="$(cd "$(dirname "${(%):-%x}")" && pwd)"
LPM_ROOT="$(cd "${LPM_SCRIPTS_DIR}/.." && pwd)"
OXR_DIR="${LPM_ROOT}/oxrsys"
OXR_PRESET="${OXR_PRESET:-macos-arm64}"
OXR_BUILD_DIR="${OXR_DIR}/build/${OXR_PRESET}"
OXR_MANIFEST="${OXR_BUILD_DIR}/runtime/oxrsys-runtime.json"
OXR_HOME_DERIVED="${OXR_DIR}/build/home"
OXR_HOME_APP="${OXR_HOME_DERIVED}/Build/Products/Debug/OXRSys Home.app"
OXR_ANDROID_DIR="${OXR_DIR}/clients/android-vr"
OXR_APK_RELEASE="${OXR_ANDROID_DIR}/app/build/outputs/apk/release/app-release.apk"
OXR_APK_DEBUG="${OXR_ANDROID_DIR}/app/build/outputs/apk/debug/app-debug.apk"
OXR_CONFIG_DIR="${HOME}/Library/Application Support/OXRSys"
OXR_TOML="${OXR_CONFIG_DIR}/oxrsys-runtime.toml"

# Portas do transporte USB (adb reverse). 9948 é o canal "spatial", ainda reservado.
LPM_PORTS=(9944 9945 9946 9948 9949)

autoload -U colors && colors
ok()   { print -P "%F{green}✔%f $*"; }
fail() { print -P "%F{red}✖%f $*"; }
warn() { print -P "%F{yellow}!%f $*"; }
info() { print -P "%F{cyan}→%f $*"; }
step() { print; print -P "%B== $* ==%b"; }

# Descobre o Android SDK pelas variáveis de ambiente ou pelo caminho padrão do Android Studio.
android_sdk_dir() {
  for d in "${ANDROID_HOME:-}" "${ANDROID_SDK_ROOT:-}" "${HOME}/Library/Android/sdk"; do
    [[ -n "$d" && -d "$d" ]] && { print -r -- "$d"; return 0; }
  done
  return 1
}

# Caminho do adb: PATH primeiro, depois platform-tools do SDK.
adb_bin() {
  if command -v adb >/dev/null 2>&1; then command -v adb; return 0; fi
  local sdk; sdk="$(android_sdk_dir)" || return 1
  [[ -x "${sdk}/platform-tools/adb" ]] && { print -r -- "${sdk}/platform-tools/adb"; return 0; }
  return 1
}

# Serial do headset: QUEST_SERIAL, ou o único dispositivo autorizado no adb.
quest_serial() {
  if [[ -n "${QUEST_SERIAL:-}" ]]; then print -r -- "${QUEST_SERIAL}"; return 0; fi
  local adb; adb="$(adb_bin)" || return 1
  local -a serials
  serials=("${(@f)$("$adb" devices | awk 'NR>1 && $2=="device" {print $1}')}")
  serials=("${(@)serials:#}")
  if (( ${#serials} == 1 )); then print -r -- "${serials[1]}"; return 0; fi
  if (( ${#serials} == 0 )); then return 1; fi
  warn "Mais de um dispositivo adb. Defina QUEST_SERIAL=<serial>. Encontrados: ${serials[*]}" >&2
  return 1
}
