#!/bin/zsh
# Puxa do headset o room.json gerado pelo RoomJsonExporter (unity-tools) para usar a sala real no Editor.
# Uso: 70-pull-room-json.sh com.suaempresa.helloworld [destino.json]
set -euo pipefail
source "$(dirname "${(%):-%x}")/common.sh"

pkg="${1:-}"; [[ -n "$pkg" ]] || { fail "Uso: $0 <package name do app Unity> [destino.json]"; exit 1; }
dest="${2:-${LPM_ROOT}/unity-tools/room.json}"
adb="$(adb_bin)" || { fail "adb não encontrado"; exit 1; }
serial="$(quest_serial)" || { fail "nenhum headset autorizado no adb"; exit 1; }
src="/sdcard/Android/data/${pkg}/files/room.json"
"$adb" -s "$serial" pull "$src" "$dest"
ok "Sala salva em $dest ($(wc -c < "$dest" | tr -d ' ') bytes)"
info "No Unity: arraste o JSON para Assets/, e no componente MRUK → Scene Settings → Data Source = Device With Json Fallback → Scene Jsons = room.json"
