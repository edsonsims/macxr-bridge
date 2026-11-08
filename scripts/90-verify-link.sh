#!/bin/zsh
# Verificação de ponta a ponta do Link para Mac com o Unity em Play e o Quest no USB.
# Reconecta o app do Quest, carrega a sala, confere profundidade, raycast de ambiente e detecção,
# e imprime um resumo. Uso (Unity aberto e em Play): zsh link-para-mac/scripts/90-verify-link.sh
set -uo pipefail
source "$(dirname "${(%):-%x}")/common.sh"
LINK="${LPM_ROOT}/../hello-world/unity/HelloWorld/.link"
[[ -d "$LINK/cmd" ]] || { fail "bridge do Unity não encontrado em $LINK (o projeto está aberto?)"; exit 1; }

# Manda um comando ao LinkBridge do Editor e espera a resposta (até $2 segundos).
bridge() {
  local n; n=$(( $(ls "$LINK/out" | grep -E '^[0-9]+\.out$' | sed 's/\.out//' | sort -n | tail -1) + 1 ))
  print -r -- "$1" > "$LINK/cmd/$n.cmd"
  for i in {1..${2:-30}}; do [[ -f "$LINK/out/$n.out" ]] && { cat "$LINK/out/$n.out"; print; return 0; }; sleep 1; done
  print "(sem resposta do Unity em ${2:-30}s — o Editor está compilando ou sem foco?)"; return 1
}

grep -q '"isPlaying": true' "$LINK/status.json" 2>/dev/null || { warn "o Unity não está em Play; entrando…"; bridge play 10; sleep 20; }

step "Quest"
zsh "${LPM_SCRIPTS_DIR}/80-reconnect-quest.sh"
sleep 5

step "Runtime"
L="${OXR_CONFIG_DIR}/oxrsys-runtime.log"
grep -E "Client connected via|camera channel connected|CameraRelay: depth #|EnvironmentRaycast:" "$L" | tail -6 | cut -c1-160

step "Sala (MRUK)"
bridge room-load 10 >/dev/null; sleep 8
bridge room 10 | head -4

step "Profundidade"
sleep 3
bridge depth 15

step "Detecção (câmera ao vivo, limiar 0.3)"
bridge "camera-show detect thr=0.3" 15
sleep 15
bridge detect-status 15

step "Resumo"
print "Imagens: $LINK/depth.png (profundidade), $LINK/camera.png (câmera: comando 'camera')."
print "Olhe pros objetos com o headset: as etiquetas devem pousar nas superfícies reais."
