#!/bin/zsh
# Registers the runtime as the user's default (active_runtime.json + XR_RUNTIME_JSON for GUI apps,
# which includes Unity launched from the Hub) and writes the streaming config for mixed reality.
set -euo pipefail
source "$(dirname "${(%):-%x}")/common.sh"

step "Register the runtime"
[[ -f "${OXR_MANIFEST}" ]] || { fail "manifest not found: ${OXR_MANIFEST} (run 10-build-runtime.sh)"; exit 1; }
zsh "${OXR_DIR}/scripts/oxrsys_runtime_default.sh" set "${OXR_MANIFEST}"

step "Streaming config (${OXR_TOML})"
mkdir -p "${OXR_CONFIG_DIR}"
template="${OXR_BUILD_DIR}/runtime/oxrsys-runtime.toml"
[[ -f "$template" ]] || template="${OXR_DIR}/runtime/oxrsys-runtime.toml"
if [[ -f "${OXR_TOML}" ]]; then
  cp "${OXR_TOML}" "${OXR_TOML}.bak.$(date +%Y%m%d-%H%M%S)"
  info "backed up the existing toml"
else
  cp "$template" "${OXR_TOML}"
  info "toml created from the template"
fi

# Replaces the value of a `key = value` line (only lines that start with the key).
set_key() {
  local key="$1" value="$2"
  if grep -qE "^${key} = " "${OXR_TOML}"; then
    sed -i '' -E "s|^${key} = .*|${key} = ${value}|" "${OXR_TOML}"
  else
    warn "key ${key} not in the toml; skipping"
  fi
}
# Values tuned for MR development on Quest 3 over USB.
set_key transport '"auto"'                       # USB (adb reverse) first, Wi-Fi as fallback
set_key video_codec '"h265"'
set_key bitrate_mbps "${MXB_BITRATE_MBPS:-80}"   # over USB the client doesn't cap it; 80 Mbps is comfortable
set_key refresh_rate_hz "${MXB_REFRESH_HZ:-72}"
set_key passthrough_enabled true                 # the client keeps the Quest passthrough on
set_key app_alpha_blend_passthrough true         # the app can use a transparent background (real alpha below the eyes)
set_key client_reprojection '"pose"'
ok "toml updated:"
grep -nE "^(transport|video_codec|bitrate_mbps|refresh_rate_hz|resolution_scale|passthrough_enabled|app_alpha_blend_passthrough|client_reprojection) = " "${OXR_TOML}" | sed 's/^/   /'

print
zsh "${OXR_DIR}/scripts/oxrsys_runtime_default.sh" status "${OXR_MANIFEST}"
print
warn "GUI apps that are already open (Unity Hub, Unity) must be closed and reopened to inherit XR_RUNTIME_JSON."
