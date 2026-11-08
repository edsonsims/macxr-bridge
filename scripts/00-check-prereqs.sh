#!/bin/zsh
# Confere o que o Mac precisa para compilar o Link para Mac (runtime, Home e cliente Quest)
# e para rodar o Unity com o Meta XR SDK. Só lê; não instala nada.
set -uo pipefail
source "$(dirname "${(%):-%x}")/common.sh"

missing=0
need() { fail "$1"; [[ -n "${2:-}" ]] && info "  como resolver: $2"; missing=$((missing+1)); }

step "Sistema"
os_ver="$(sw_vers -productVersion)"
os_major="${os_ver%%.*}"
if (( os_major >= 14 )); then ok "macOS ${os_ver}"; else need "macOS ${os_ver} (o runtime pede macOS 14 ou mais novo)"; fi
arch_name="$(uname -m)"
if [[ "$arch_name" == "arm64" ]]; then ok "Apple Silicon (arm64)"; else warn "Arquitetura ${arch_name}: use OXR_PRESET=macos-x64"; fi

step "Xcode e ferramentas C++"
if xcodebuild -version >/dev/null 2>&1; then
  ok "$(xcodebuild -version | head -1) em $(xcode-select -p)"
else
  need "Xcode não encontrado" "instale o Xcode na App Store e rode: sudo xcode-select -s /Applications/Xcode.app"
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
if command -v swift >/dev/null 2>&1; then ok "$(swift --version 2>&1 | head -1)"; else need "Swift (vem com o Xcode)"; fi

step "Android (cliente do Quest)"
if /usr/libexec/java_home -v 17 >/dev/null 2>&1; then
  ok "Java 17 em $(/usr/libexec/java_home -v 17)"
else
  need "Java 17" "brew install --cask temurin@17   (ou use o JDK do Android Studio: export JAVA_HOME=\"/Applications/Android Studio.app/Contents/jbr/Contents/Home\")"
fi
if sdk="$(android_sdk_dir)"; then
  ok "Android SDK em ${sdk}"
  [[ -d "${sdk}/platforms/android-35" ]] && ok "  platform android-35" || need "  platform android-35" "sdkmanager \"platforms;android-35\""
  [[ -d "${sdk}/ndk/28.2.13676358" ]] && ok "  NDK 28.2.13676358" || need "  NDK 28.2.13676358" "sdkmanager \"ndk;28.2.13676358\""
  if ls -d "${sdk}"/cmake/3.22.* >/dev/null 2>&1; then ok "  CMake do SDK 3.22.x"; else need "  CMake do SDK 3.22.1" "sdkmanager \"cmake;3.22.1\""; fi
  [[ -d "${sdk}/build-tools" ]] && ok "  build-tools" || need "  build-tools" "sdkmanager \"build-tools;35.0.0\""
else
  need "Android SDK" "instale o Android Studio ou defina ANDROID_HOME"
fi
if adb="$(adb_bin)"; then
  ok "adb em ${adb}"
  devs="$("$adb" devices | awk 'NR>1 && NF==2 {print $1" ("$2")"}')"
  if [[ -n "$devs" ]]; then ok "Dispositivos adb: $(print -r -- "$devs" | tr '\n' ' ')"; else warn "Nenhum headset no adb. Ligue o Quest 3 por USB e aceite a depuração USB no headset."; fi
else
  need "adb" "sdkmanager \"platform-tools\""
fi

step "Unity"
if [[ -n "$(print -rl -- /Applications/Unity/Hub/Editor/*(N/))" ]]; then
  for d in /Applications/Unity/Hub/Editor/*(N/); do
    v="$(basename "$d")"
    if [[ "$v" == 6000.* ]]; then ok "Unity ${v}"; else warn "Unity ${v} (o SDK Meta 207 pede 6000.0.66f2 ou mais novo)"; fi
  done
else
  need "Unity Hub/Editor" "instale o Unity Hub e o Unity 6000.0.66f2 ou mais novo com o módulo Android Build Support"
fi

step "Fork"
[[ -d "${OXR_DIR}/.git" ]] && ok "oxrsys clonado em ${OXR_DIR} ($(git -C "${OXR_DIR}" rev-parse --abbrev-ref HEAD))" || need "clone do oxrsys em ${OXR_DIR}"
[[ -f "${OXR_MANIFEST}" ]] && ok "runtime já compilado: ${OXR_MANIFEST}" || info "runtime ainda não compilado (rode 10-build-runtime.sh)"

print
if (( missing == 0 )); then ok "Tudo pronto. Próximo passo: ./all.sh"; else warn "${missing} item(ns) faltando. Resolva e rode de novo."; exit 1; fi
