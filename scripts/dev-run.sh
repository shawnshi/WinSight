#!/bin/bash
# Uses repo-local index/models/settings so development does not overwrite the installed app's data.
# Build the helper first. Extra arguments are passed to the shell, e.g. --roots 'C:\Docs;D:\Notes'.
set -euo pipefail
case "$(uname -s)-$(uname -m)" in
  MINGW*-x86_64|MSYS*-x86_64) ;;
  *) echo "Windows x64 only: run in Git for Windows' bash with MSVC" >&2; exit 1 ;;
esac
cd "$(dirname "$0")/.."
helper="$PWD/.build/release/winsight.exe"
[[ -f "$helper" ]] || { echo "Build the helper first: swift build -c release --product winsight" >&2; exit 1; }
export PATH="$PWD/Vendor/pdfium/bin:$PWD/Vendor/ffmpeg/bin:$PATH"
if [[ -z "${WINSIGHT_INFERENCE_HOST:-}" ]]; then
  host_stage="$PWD/.dev/winml-dev-$(date +%s)-$$"
  pwsh.exe -NoProfile -File scripts/build-inference-host.ps1 -OutputDirectory "$(cygpath -w "$host_stage")"
  export WINSIGHT_INFERENCE_HOST="$(cygpath -w "$host_stage/WinSight.InferenceHost.exe")"
fi
root="$PWD"
mkdir -p "$root/.dev"
cd shell/src-tauri
exec cargo run -- --helper "$(cygpath -w "$helper")" \
  --index "$(cygpath -w "$root/.dev/index")" --models "$(cygpath -w "$root/.dev/models")" \
  --settings "$(cygpath -w "$root/.dev/settings.json")" --offer "" --offer-code "" "$@"
