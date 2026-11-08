#!/bin/zsh
# Reconecta o app OXRSys no Quest por USB com o Unity em Play:
# acorda a tela do Mac, traz o Unity pra frente (o Editor não renderiza em segundo plano/tela apagada),
# refaz o adb reverse e reinicia o app. Espera 5 s entre parar e abrir: o servidor só larga um cliente
# antigo (às vezes reconectado por Wi-Fi) depois de 3 s sem tracking, e recusa o novo enquanto isso.
set -uo pipefail
source "$(dirname "${(%):-%x}")/common.sh"
adb="$(adb_bin)" || { fail "adb não encontrado"; exit 1; }
caffeinate -u -t 2
osascript -e 'tell application id "com.unity3d.UnityEditor5.x" to activate' >/dev/null 2>&1
for p in "${LPM_PORTS[@]}"; do "$adb" reverse "tcp:${p}" "tcp:${p}" >/dev/null 2>&1; done
"$adb" shell am force-stop net.demonixis.oxrsys.android
sleep 5
"$adb" shell am start -n net.demonixis.oxrsys.android/com.oculus.NativeActivity >/dev/null
sleep 12
"$adb" logcat -d -t 800 2>&1 | grep -E "OXRSys-Android: (USB ADB transport selected|Connection lost|Decoded frame #)" | tail -2 | cut -c1-110
