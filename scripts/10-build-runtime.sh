#!/bin/zsh
# Builds the OpenXR runtime (dylib + encoder helper + manifest). Use --test to run ctest.
set -euo pipefail
source "$(dirname "${(%):-%x}")/common.sh"

step "Runtime (preset ${OXR_PRESET})"
cd "${OXR_DIR}"
cmake --preset "${OXR_PRESET}"
cmake --build "${OXR_BUILD_DIR}"
if [[ "${1:-}" == "--test" ]]; then ctest --test-dir "${OXR_BUILD_DIR}" --output-on-failure; fi

[[ -f "${OXR_MANIFEST}" ]] || { fail "manifest not generated: ${OXR_MANIFEST}"; exit 1; }
ok "Runtime: ${OXR_BUILD_DIR}/runtime/liboxrsys-runtime.dylib"
ok "Manifest: ${OXR_MANIFEST}"
lipo -archs "${OXR_BUILD_DIR}/runtime/liboxrsys-runtime.dylib" | sed 's/^/   architectures: /'
