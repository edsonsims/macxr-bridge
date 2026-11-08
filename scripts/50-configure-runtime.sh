#!/bin/zsh
# Registra o runtime como padrão do usuário (active_runtime.json + XR_RUNTIME_JSON para apps GUI,
# o que inclui o Unity aberto pelo Hub) e grava a configuração de streaming para realidade mista.
set -euo pipefail
source "$(dirname "${(%):-%x}")/common.sh"

step "Registrar o runtime"
[[ -f "${OXR_MANIFEST}" ]] || { fail "manifest não encontrado: ${OXR_MANIFEST} (rode 10-build-runtime.sh)"; exit 1; }
zsh "${OXR_DIR}/scripts/oxrsys_runtime_default.sh" set "${OXR_MANIFEST}"

step "Configuração de streaming (${OXR_TOML})"
mkdir -p "${OXR_CONFIG_DIR}"
template="${OXR_BUILD_DIR}/runtime/oxrsys-runtime.toml"
[[ -f "$template" ]] || template="${OXR_DIR}/runtime/oxrsys-runtime.toml"
if [[ -f "${OXR_TOML}" ]]; then
  cp "${OXR_TOML}" "${OXR_TOML}.bak.$(date +%Y%m%d-%H%M%S)"
  info "backup do toml existente criado"
else
  cp "$template" "${OXR_TOML}"
  info "toml criado a partir do template"
fi

# Troca o valor de uma chave `chave = valor` (só linhas que começam com a chave).
set_key() {
  local key="$1" value="$2"
  if grep -qE "^${key} = " "${OXR_TOML}"; then
    sed -i '' -E "s|^${key} = .*|${key} = ${value}|" "${OXR_TOML}"
  else
    warn "chave ${key} não existe no toml; pulando"
  fi
}
# Valores pensados para desenvolvimento MR no Quest 3 por USB.
set_key transport '"auto"'                       # USB (adb reverse) primeiro, Wi-Fi como reserva
set_key video_codec '"h265"'
set_key bitrate_mbps "${LPM_BITRATE_MBPS:-80}"   # por USB o cliente não limita; 80 Mbps é confortável
set_key refresh_rate_hz "${LPM_REFRESH_HZ:-72}"
set_key passthrough_enabled true                 # o cliente mantém o passthrough do Quest ativo
set_key app_alpha_blend_passthrough true         # o app pode usar fundo transparente (alfa real abaixo dos olhos)
set_key client_reprojection '"pose"'
ok "toml ajustado:"
grep -nE "^(transport|video_codec|bitrate_mbps|refresh_rate_hz|resolution_scale|passthrough_enabled|app_alpha_blend_passthrough|client_reprojection) = " "${OXR_TOML}" | sed 's/^/   /'

print
zsh "${OXR_DIR}/scripts/oxrsys_runtime_default.sh" status "${OXR_MANIFEST}"
print
warn "Apps GUI já abertos (Unity Hub, Unity) precisam ser fechados e reabertos para herdar o XR_RUNTIME_JSON."
