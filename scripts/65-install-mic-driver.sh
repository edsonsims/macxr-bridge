#!/bin/zsh
# Compila e instala o dispositivo de áudio "Quest 3 Microphone (Link para Mac)" (driver do Core Audio).
# O macOS pede a senha do Mac uma vez (instalar em /Library/Audio/Plug-Ins/HAL exige administrador).
set -euo pipefail
source "$(dirname "${(%):-%x}")/common.sh"
step "Microfone do Quest no Mac"
zsh "${LPM_ROOT}/mic-driver/build.sh"
# O root não lê ~/Documents (privacidade do macOS): passa por /tmp.
rm -rf /tmp/QuestMic.driver && cp -R "${LPM_ROOT}/mic-driver/build/QuestMic.driver" /tmp/
osascript -e "do shell script \"rm -rf /Library/Audio/Plug-Ins/HAL/QuestMic.driver && cp -R /tmp/QuestMic.driver /Library/Audio/Plug-Ins/HAL/ && chown -R root:wheel /Library/Audio/Plug-Ins/HAL/QuestMic.driver && killall coreaudiod\" with administrator privileges with prompt \"Link para Mac quer instalar o dispositivo de áudio Quest 3 Microphone.\""
rm -rf /tmp/QuestMic.driver
sleep 3
if system_profiler SPAudioDataType 2>/dev/null | grep -q "Quest 3 Microphone"; then ok "Quest 3 Microphone instalado"; else warn "o dispositivo ainda não apareceu (espere alguns segundos)"; fi
