#!/bin/zsh
# Instala o APK no Quest e configura o adb reverse das portas do streaming USB.
# Use --debug para instalar o APK de debug. QUEST_SERIAL=<serial> se houver mais de um dispositivo.
set -euo pipefail
source "$(dirname "${(%):-%x}")/common.sh"

step "Instalar no Quest"
adb="$(adb_bin)" || { fail "adb não encontrado"; exit 1; }
serial="$(quest_serial)" || { fail "Nenhum headset autorizado no adb. Ligue o cabo e aceite 'Permitir depuração USB' no headset."; exit 1; }
apk="${OXR_APK_RELEASE}"; [[ "${1:-}" == "--debug" ]] && apk="${OXR_APK_DEBUG}"
[[ -f "$apk" ]] || { fail "APK não encontrado: $apk (rode 30-build-quest-client.sh)"; exit 1; }

"$adb" -s "$serial" install -r "$apk"
ok "APK instalado em ${serial}"

for p in "${LPM_PORTS[@]}"; do "$adb" -s "$serial" reverse "tcp:${p}" "tcp:${p}"; done
ok "adb reverse configurado nas portas ${LPM_PORTS[*]}"
"$adb" -s "$serial" reverse --list | sed 's/^/   /'

pkg="$("$adb" -s "$serial" shell pm list packages 2>/dev/null | grep -F "net.demonixis.oxrsys.android" | head -1 | sed 's/package://')"
[[ -n "$pkg" ]] && ok "Pacote: ${pkg}" || warn "Não achei o pacote pelo nome 'oxrsys'; procure o app em Biblioteca → Fontes desconhecidas."
print
info "No headset: Biblioteca → Fontes desconhecidas → abra o app \"OXRSys\". Ele fica esperando o Mac."
info "Rastreamento de mãos precisa estar ligado em Configurações → Movimento → Mãos e controles."
