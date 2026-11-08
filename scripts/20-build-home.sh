#!/bin/zsh
# Builds the OXRSys Home app (runtime selection, ADB, status and settings) without code signing.
set -euo pipefail
source "$(dirname "${(%):-%x}")/common.sh"

step "OXRSys Home (Debug, unsigned)"
cd "${OXR_DIR}"
xcodebuild -project "clients/home/OXRSys Home.xcodeproj" \
  -scheme "OXRSys Home" \
  -configuration Debug \
  -destination 'platform=macOS' \
  -derivedDataPath "${OXR_HOME_DERIVED}" \
  CODE_SIGNING_ALLOWED=NO \
  build | tail -5

[[ -d "${OXR_HOME_APP}" ]] || { fail "app not found at ${OXR_HOME_APP}"; exit 1; }
ok "Home: ${OXR_HOME_APP}"
info "To open it: open \"${OXR_HOME_APP}\""
