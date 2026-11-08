#!/bin/zsh
# Adiciona o pacote Unity do OXRSys (menu Tools/OpenXR → Use OXRSys Runtime) a um projeto Unity,
# apontando para a cópia local do fork. Uso: 60-unity-add-package.sh /caminho/do/ProjetoUnity
set -euo pipefail
source "$(dirname "${(%):-%x}")/common.sh"

proj="${1:-}"
[[ -n "$proj" && -f "$proj/Packages/manifest.json" ]] || { fail "Uso: $0 /caminho/do/ProjetoUnity (precisa ter Packages/manifest.json)"; exit 1; }
pkg_dir="${OXR_DIR}/scripts/unity"
[[ -f "$pkg_dir/package.json" ]] || { fail "pacote não encontrado em $pkg_dir"; exit 1; }

# Caminho relativo a partir de Packages/ (o Unity resolve "file:" relativo à pasta Packages).
rel="$(python3 -c 'import os,sys; print(os.path.relpath(sys.argv[1], sys.argv[2]))' "$pkg_dir" "$proj/Packages")"
python3 - "$proj/Packages/manifest.json" "file:${rel}" <<'PY'
import json, sys
path, dep = sys.argv[1], sys.argv[2]
with open(path) as f:
    m = json.load(f)
m.setdefault("dependencies", {})["net.demonixis.oxrsys-unity"] = dep
with open(path, "w") as f:
    json.dump(m, f, indent=2)
    f.write("\n")
print(f"net.demonixis.oxrsys-unity -> {dep}")
PY
ok "manifest.json atualizado. No Unity: Tools → OpenXR → Use OXRSys Runtime, depois Play."
