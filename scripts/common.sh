#!/bin/zsh
# MacXR Bridge — variables and functions shared by the scripts.
# This file is loaded with `source`; do not run it directly.

MXB_SCRIPTS_DIR="$(cd "$(dirname "${(%):-%x}")" && pwd)"
MXB_ROOT="$(cd "${MXB_SCRIPTS_DIR}/.." && pwd)"
OXR_DIR="${MXB_ROOT}/oxrsys"
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

# USB transport ports (adb reverse). 9948 is the "spatial" channel, still reserved.
MXB_PORTS=(9944 9945 9946 9948 9949)

autoload -U colors && colors
ok()   { print -P "%F{green}✔%f $*"; }
fail() { print -P "%F{red}✖%f $*"; }
warn() { print -P "%F{yellow}!%f $*"; }
info() { print -P "%F{cyan}→%f $*"; }
step() { print; print -P "%B== $* ==%b"; }

# Finds the Android SDK from the environment variables or Android Studio's default path.
android_sdk_dir() {
  for d in "${ANDROID_HOME:-}" "${ANDROID_SDK_ROOT:-}" "${HOME}/Library/Android/sdk"; do
    [[ -n "$d" && -d "$d" ]] && { print -r -- "$d"; return 0; }
  done
  return 1
}

# Path to adb: PATH first, then the SDK's platform-tools.
adb_bin() {
  if command -v adb >/dev/null 2>&1; then command -v adb; return 0; fi
  local sdk; sdk="$(android_sdk_dir)" || return 1
  [[ -x "${sdk}/platform-tools/adb" ]] && { print -r -- "${sdk}/platform-tools/adb"; return 0; }
  return 1
}

# Headset serial: QUEST_SERIAL, or the only authorized device on adb.
quest_serial() {
  if [[ -n "${QUEST_SERIAL:-}" ]]; then print -r -- "${QUEST_SERIAL}"; return 0; fi
  local adb; adb="$(adb_bin)" || return 1
  local -a serials
  serials=("${(@f)$("$adb" devices | awk 'NR>1 && $2=="device" {print $1}')}")
  serials=("${(@)serials:#}")
  if (( ${#serials} == 1 )); then print -r -- "${serials[1]}"; return 0; fi
  if (( ${#serials} == 0 )); then return 1; fi
  warn "More than one adb device. Set QUEST_SERIAL=<serial>. Found: ${serials[*]}" >&2
  return 1
}
