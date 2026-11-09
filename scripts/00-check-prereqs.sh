#!/bin/zsh
# Checks what the Mac needs to build MacXR Bridge (runtime, Home and Quest client)
# and to run Unity with the Meta XR SDK. Read-only; installs nothing.
set -uo pipefail
source "$(dirname "${(%):-%x}")/common.sh"

missing=0
need() { fail "$1"; [[ -n "${2:-}" ]] && info "  how to fix: $2"; missing=$((missing+1)); }

step "System"
os_ver="$(sw_vers -productVersion)"
os_major="${os_ver%%.*}"
if (( os_major >= 14 )); then ok "macOS ${os_ver}"; else need "macOS ${os_ver} (the runtime requires macOS 14 or newer)"; fi
arch_name="$(uname -m)"
if [[ "$arch_name" == "arm64" ]]; then ok "Apple Silicon (arm64)"; else warn "Architecture ${arch_name}: use OXR_PRESET=macos-x64"; fi

step "Xcode and C++ tools"
if xcodebuild -version >/dev/null 2>&1; then
  ok "$(xcodebuild -version | head -1) at $(xcode-select -p)"
else
  need "Xcode not found" "install Xcode from the App Store and run: sudo xcode-select -s /Applications/Xcode.app"
fi
if xcrun -sdk macosx -f metal >/dev/null 2>&1; then ok "Metal Toolchain"; else need "Metal Toolchain" "xcodebuild -downloadComponent MetalToolchain"; fi
for tool in cmake ninja; do
  if command -v $tool >/dev/null 2>&1; then ok "$tool $($tool --version | head -1 | tr -d '\n' | sed 's/cmake version //')"; else need "$tool" "brew install $tool"; fi
done
if [[ -d /opt/homebrew/include/vulkan || -d /usr/local/include/vulkan ]] || brew list vulkan-headers >/dev/null 2>&1; then
  ok "Vulkan headers"
else
  need "Vulkan headers" "brew install vulkan-headers"
fi
if command -v swift >/dev/null 2>&1; then ok "$(swift --version 2>&1 | head -1)"; else need "Swift (comes with Xcode)"; fi

step "Android (Quest client)"
if /usr/libexec/java_home -v 17 >/dev/null 2>&1; then
  ok "Java 17 at $(/usr/libexec/java_home -v 17)"
else
  need "Java 17" "brew install --cask temurin@17   (or use Android Studio's JDK: export JAVA_HOME=\"/Applications/Android Studio.app/Contents/jbr/Contents/Home\")"
fi
if sdk="$(android_sdk_dir)"; then
  ok "Android SDK at ${sdk}"
  [[ -d "${sdk}/platforms/android-35" ]] && ok "  platform android-35" || need "  platform android-35" "sdkmanager \"platforms;android-35\""
  [[ -d "${sdk}/ndk/28.2.13676358" ]] && ok "  NDK 28.2.13676358" || need "  NDK 28.2.13676358" "sdkmanager \"ndk;28.2.13676358\""
  if ls -d "${sdk}"/cmake/3.22.* >/dev/null 2>&1; then ok "  SDK CMake 3.22.x"; else need "  SDK CMake 3.22.1" "sdkmanager \"cmake;3.22.1\""; fi
  [[ -d "${sdk}/build-tools" ]] && ok "  build-tools" || need "  build-tools" "sdkmanager \"build-tools;35.0.0\""
else
  need "Android SDK" "install Android Studio or set ANDROID_HOME"
fi
if adb="$(adb_bin)"; then
  ok "adb at ${adb}"
  devs="$("$adb" devices | awk 'NR>1 && NF==2 {print $1" ("$2")"}')"
  if [[ -n "$devs" ]]; then ok "adb devices: $(print -r -- "$devs" | tr '\n' ' ')"; else warn "No headset on adb. Connect the Quest 3 over USB and accept USB debugging on the headset."; fi
else
  need "adb" "sdkmanager \"platform-tools\""
fi

step "Unity"
if [[ -n "$(print -rl -- /Applications/Unity/Hub/Editor/*(N/))" ]]; then
  for d in /Applications/Unity/Hub/Editor/*(N/); do
    v="$(basename "$d")"
    if [[ "$v" == 6000.* ]]; then ok "Unity ${v}"; else warn "Unity ${v} (Meta SDK 207 requires 6000.0.66f2 or newer)"; fi
  done
else
  need "Unity Hub/Editor" "install Unity Hub and Unity 6000.0.66f2 or newer with the Android Build Support module"
fi

step "Fork"
[[ -d "${OXR_DIR}/.git" ]] && ok "oxrsys cloned at ${OXR_DIR} ($(git -C "${OXR_DIR}" rev-parse --abbrev-ref HEAD))" || need "oxrsys clone at ${OXR_DIR}"
[[ -f "${OXR_MANIFEST}" ]] && ok "runtime already built: ${OXR_MANIFEST}" || info "runtime not built yet (run 10-build-runtime.sh)"

print
if (( missing == 0 )); then ok "All set. Next step: ./all.sh"; else warn "${missing} item(s) missing. Fix them and run again."; exit 1; fi
