#!/bin/zsh
# Installs the "Quest 3 Microphone" into Core Audio (needs admin rights: macOS asks for the password once).
# To uninstall: sudo rm -rf /Library/Audio/Plug-Ins/HAL/QuestMic.driver && sudo killall coreaudiod
set -euo pipefail
cd "$(dirname "$0")"
[[ -d build/QuestMic.driver ]] || zsh build.sh
echo "Installing into /Library/Audio/Plug-Ins/HAL (macOS will ask for the Mac password)…"
sudo rm -rf /Library/Audio/Plug-Ins/HAL/QuestMic.driver
sudo cp -R build/QuestMic.driver /Library/Audio/Plug-Ins/HAL/
sudo chown -R root:wheel /Library/Audio/Plug-Ins/HAL/QuestMic.driver
sudo killall coreaudiod || true
sleep 2
system_profiler SPAudioDataType 2>/dev/null | grep -A3 "Quest 3 Microphone" || echo "(the device shows up in a few seconds)"
echo "ok: 'Quest 3 Microphone (Link para Mac)' installed"
