#!/bin/zsh
# Instala o "Quest 3 Microphone" no Core Audio (precisa de administrador: o macOS pede a senha uma vez).
# Desinstalar: sudo rm -rf /Library/Audio/Plug-Ins/HAL/QuestMic.driver && sudo killall coreaudiod
set -euo pipefail
cd "$(dirname "$0")"
[[ -d build/QuestMic.driver ]] || zsh build.sh
echo "Instalando em /Library/Audio/Plug-Ins/HAL (o macOS vai pedir a senha do Mac)…"
sudo rm -rf /Library/Audio/Plug-Ins/HAL/QuestMic.driver
sudo cp -R build/QuestMic.driver /Library/Audio/Plug-Ins/HAL/
sudo chown -R root:wheel /Library/Audio/Plug-Ins/HAL/QuestMic.driver
sudo killall coreaudiod || true
sleep 2
system_profiler SPAudioDataType 2>/dev/null | grep -A3 "Quest 3 Microphone" || echo "(o dispositivo aparece em alguns segundos)"
echo "ok: 'Quest 3 Microphone (Link para Mac)' instalado"
