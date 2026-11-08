#!/bin/zsh
# Compila o app OXRSys Home (seleção de runtime, ADB, status e configurações) sem assinatura.
set -euo pipefail
source "$(dirname "${(%):-%x}")/common.sh"

step "OXRSys Home (Debug, sem assinatura)"
cd "${OXR_DIR}"
xcodebuild -project "clients/home/OXRSys Home.xcodeproj" \
  -scheme "OXRSys Home" \
  -configuration Debug \
  -destination 'platform=macOS' \
  -derivedDataPath "${OXR_HOME_DERIVED}" \
  CODE_SIGNING_ALLOWED=NO \
  build | tail -5

[[ -d "${OXR_HOME_APP}" ]] || { fail "app não encontrado em ${OXR_HOME_APP}"; exit 1; }
ok "Home: ${OXR_HOME_APP}"
info "Para abrir: open \"${OXR_HOME_APP}\""
