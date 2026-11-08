#!/bin/zsh
# Link para Mac — instalação em um passo: runtime OpenXR do Mac, app do Quest, microfone do Quest, ffmpeg.
# Precisa: Xcode (ferramentas de linha de comando), Android SDK/NDK, Homebrew, o Quest no cabo com a depuração USB
# autorizada ("Sempre permitir"). O macOS pede a senha do Mac uma vez (driver do microfone).
set -euo pipefail
here="$(dirname "${(%):-%x}")"
source "$here/common.sh"
zsh "$here/00-check-prereqs.sh"
zsh "$here/10-build-runtime.sh"
zsh "$here/30-build-quest-client.sh"
zsh "$here/40-install-quest.sh"
adb="$(adb_bin)" && "$adb" shell pm grant net.demonixis.oxrsys.android android.permission.RECORD_AUDIO 2>/dev/null && ok "microfone liberado no app do Quest" || warn "libere o microfone no Quest quando o app pedir"
zsh "$here/50-configure-runtime.sh"
zsh "$here/65-install-mic-driver.sh"
step "ffmpeg (gravações e espectador)"
if [[ -x /opt/homebrew/bin/ffmpeg ]]; then ok "ffmpeg presente"; else brew install ffmpeg && ok "ffmpeg instalado"; fi
print
ok "Pronto. No Unity: Window > Link para Mac > Painel (e Instalação e diagnóstico pra conferir), Play com o Quest no cabo."
