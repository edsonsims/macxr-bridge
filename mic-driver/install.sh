#!/bin/zsh
# Installs the "Headset Microphone" into Core Audio (needs admin rights: macOS asks for the password once).
# To uninstall: sudo rm -rf /Library/Audio/Plug-Ins/HAL/MacXRBridgeMic.driver && sudo killall coreaudiod
set -euo pipefail
cd "$(dirname "$0")"
[[ -d build/MacXRBridgeMic.driver ]] || zsh build.sh
echo "Installing into /Library/Audio/Plug-Ins/HAL (macOS will ask for the Mac password)…"
sudo rm -rf /Library/Audio/Plug-Ins/HAL/MacXRBridgeMic.driver
sudo cp -R build/MacXRBridgeMic.driver /Library/Audio/Plug-Ins/HAL/
sudo chown -R root:wheel /Library/Audio/Plug-Ins/HAL/MacXRBridgeMic.driver
sudo killall coreaudiod || true
sleep 2
system_profiler SPAudioDataType 2>/dev/null | grep -A3 "Headset Microphone" || echo "(the device shows up in a few seconds)"
echo "ok: 'Headset Microphone (MacXR Bridge)' installed"
