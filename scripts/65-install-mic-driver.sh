#!/bin/zsh
# Builds and installs the "Quest 3 Microphone (Link para Mac)" audio device (a Core Audio driver).
# macOS asks for the Mac password once (installing into /Library/Audio/Plug-Ins/HAL requires admin rights).
set -euo pipefail
source "$(dirname "${(%):-%x}")/common.sh"
step "Quest microphone on the Mac"
zsh "${LPM_ROOT}/mic-driver/build.sh"
# root can't read ~/Documents (macOS privacy): go through /tmp.
rm -rf /tmp/QuestMic.driver && cp -R "${LPM_ROOT}/mic-driver/build/QuestMic.driver" /tmp/
osascript -e "do shell script \"rm -rf /Library/Audio/Plug-Ins/HAL/QuestMic.driver && cp -R /tmp/QuestMic.driver /Library/Audio/Plug-Ins/HAL/ && chown -R root:wheel /Library/Audio/Plug-Ins/HAL/QuestMic.driver && killall coreaudiod\" with administrator privileges with prompt \"Link para Mac wants to install the Quest 3 Microphone audio device.\""
rm -rf /tmp/QuestMic.driver
sleep 3
if system_profiler SPAudioDataType 2>/dev/null | grep -q "Quest 3 Microphone"; then ok "Quest 3 Microphone installed"; else warn "the device hasn't shown up yet (wait a few seconds)"; fi
