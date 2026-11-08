#!/bin/zsh
# Link para Mac: mantém o sensor de presença do Quest "fechado" enquanto o headset está fora da cabeça
# (testes sem ninguém usando). Reenvia prox_close a cada 5 s. Parar: kill $(cat .runner/prox-keepalive.pid)
source "$(dirname "${(%):-%x}")/common.sh"
adb="$(adb_bin)"
while true; do
  "$adb" shell am broadcast -a com.oculus.vrpowermanager.prox_close >/dev/null 2>&1
  sleep 5
done
