#!/bin/zsh
# Does everything in order: runtime → Home → Quest client → install on the Quest → register the runtime.
set -euo pipefail
here="$(dirname "${(%):-%x}")"
zsh "$here/00-check-prereqs.sh"
zsh "$here/10-build-runtime.sh"
zsh "$here/20-build-home.sh"
zsh "$here/30-build-quest-client.sh"
zsh "$here/40-install-quest.sh"
zsh "$here/50-configure-runtime.sh"
print
print "Done. Open OXRSys Home (20-build-home.sh shows the path), start the client on the Quest and press Play in Unity."
