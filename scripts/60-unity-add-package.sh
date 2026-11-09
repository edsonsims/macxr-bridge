#!/bin/zsh
# Adds the OXRSys Unity package (menu Tools/OpenXR → Use OXRSys Runtime) to a Unity project,
# pointing at the local copy of the fork. Usage: 60-unity-add-package.sh /path/to/UnityProject
set -euo pipefail
source "$(dirname "${(%):-%x}")/common.sh"

proj="${1:-}"
[[ -n "$proj" && -f "$proj/Packages/manifest.json" ]] || { fail "Usage: $0 /path/to/UnityProject (must contain Packages/manifest.json)"; exit 1; }
pkg_dir="${OXR_DIR}/scripts/unity"
[[ -f "$pkg_dir/package.json" ]] || { fail "package not found at $pkg_dir"; exit 1; }

# Path relative to Packages/ (Unity resolves "file:" relative to the Packages folder).
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
ok "manifest.json updated. In Unity: Tools → OpenXR → Use OXRSys Runtime, then Play."
