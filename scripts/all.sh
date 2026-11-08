#!/bin/zsh
# Faz tudo em ordem: runtime → Home → cliente Quest → instalar no Quest → registrar runtime.
set -euo pipefail
here="$(dirname "${(%):-%x}")"
zsh "$here/00-check-prereqs.sh"
zsh "$here/10-build-runtime.sh"
zsh "$here/20-build-home.sh"
zsh "$here/30-build-quest-client.sh"
zsh "$here/40-install-quest.sh"
zsh "$here/50-configure-runtime.sh"
print
print "Pronto. Abra o OXRSys Home (20-build-home.sh mostra o caminho), ligue o cliente no Quest e dê Play no Unity."
