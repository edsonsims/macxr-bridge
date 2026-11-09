#!/bin/zsh
# Builds and installs the "Headset Microphone (MacXR Bridge)" audio device (a Core Audio driver).
# macOS asks for the Mac password once (installing into /Library/Audio/Plug-Ins/HAL requires admin rights).
set -euo pipefail
source "$(dirname "${(%):-%x}")/common.sh"
step "Headset microphone on the Mac"
# (also removes QuestMic.driver, the driver's pre-rename name)
zsh "${MXB_ROOT}/mic-driver/build.sh"
# root can't read ~/Documents (macOS privacy): go through /tmp.
rm -rf /tmp/MacXRBridgeMic.driver && cp -R "${MXB_ROOT}/mic-driver/build/MacXRBridgeMic.driver" /tmp/
osascript -e "do shell script \"rm -rf /Library/Audio/Plug-Ins/HAL/MacXRBridgeMic.driver /Library/Audio/Plug-Ins/HAL/QuestMic.driver && cp -R /tmp/MacXRBridgeMic.driver /Library/Audio/Plug-Ins/HAL/ && chown -R root:wheel /Library/Audio/Plug-Ins/HAL/MacXRBridgeMic.driver && killall coreaudiod\" with administrator privileges with prompt \"MacXR Bridge wants to install the Headset Microphone audio device.\""
rm -rf /tmp/MacXRBridgeMic.driver
sleep 3
if system_profiler SPAudioDataType 2>/dev/null | grep -q "Headset Microphone"; then ok "Headset Microphone installed"; else warn "the device hasn't shown up yet (wait a few seconds)"; fi
