#!/bin/zsh
# MacXR Bridge: keeps the Quest proximity sensor "closed" while the headset is off the head
# (tests with nobody wearing it). Resends prox_close every 5 s. To stop: kill $(cat .runner/prox-keepalive.pid)
source "$(dirname "${(%):-%x}")/common.sh"
adb="$(adb_bin)"
while true; do
  "$adb" shell am broadcast -a com.oculus.vrpowermanager.prox_close >/dev/null 2>&1
  sleep 5
done
