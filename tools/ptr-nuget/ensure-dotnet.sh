#!/usr/bin/env bash
# Ensures a .NET runtime of the required major version is available for PtrPublisher.
# 1. Uses an existing runtime if one is found (GitHub-hosted runners).
# 2. Otherwise installs the runtime only (no SDK) into a user-writable directory with the
#    official dotnet-install.sh; no root/admin rights are needed. Later runs reuse it.
# 3. Sets DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 when libicu is missing.
# Exports DOTNET_ROOT/PATH for later steps through GITHUB_ENV/GITHUB_PATH when present.
set -euo pipefail

major="${PTR_DOTNET_MAJOR:-10}"
install_dir="${PTR_DOTNET_DIR:-${RUNNER_TOOL_CACHE:-$HOME/.ptr}/ptr-dotnet}"
install_script_url="https://dot.net/v1/dotnet-install.sh"

log() { echo "[ensure-dotnet] $*"; }

has_runtime() {
  local dotnet="$1"
  [ -x "$dotnet" ] || command -v "$dotnet" >/dev/null 2>&1 || return 1
  "$dotnet" --list-runtimes 2>/dev/null | grep -q "^Microsoft.NETCore.App ${major}\."
}

export_env() {
  local root="$1" source="$2"
  if [ -n "${GITHUB_ENV:-}" ]; then
    echo "DOTNET_ROOT=$root" >> "$GITHUB_ENV"
    echo "PTR_DOTNET=$root/dotnet" >> "$GITHUB_ENV"
    echo "PTR_DOTNET_SOURCE=$source" >> "$GITHUB_ENV"
  fi
  if [ -n "${GITHUB_PATH:-}" ]; then echo "$root" >> "$GITHUB_PATH"; fi
  echo "PTR_DOTNET=$root/dotnet"
}

set_invariant_if_no_icu() {
  if ! (ldconfig -p 2>/dev/null | grep -q libicu) && [ "$(uname -s)" = "Linux" ]; then
    log "libicu not found; using DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1."
    export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
    if [ -n "${GITHUB_ENV:-}" ]; then echo "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1" >> "$GITHUB_ENV"; fi
  fi
}

set_invariant_if_no_icu

if has_runtime dotnet; then
  root="$(dirname "$(readlink -f "$(command -v dotnet)")")"
  log "Found preinstalled .NET ${major} runtime in $root."
  export_env "$root" "preinstalled"
  exit 0
fi

if has_runtime "$install_dir/dotnet"; then
  log "Found cached .NET ${major} runtime in $install_dir."
  export_env "$install_dir" "cached"
  exit 0
fi

command -v curl >/dev/null 2>&1 || { log "ERROR: curl is required to install .NET."; exit 1; }
log "No .NET ${major} runtime found; installing the runtime into $install_dir."
mkdir -p "$install_dir"
script="$(mktemp)"
start=$(date +%s)
if ! curl -fsSL --retry 3 --connect-timeout 15 "$install_script_url" -o "$script" ||
   ! bash "$script" --channel "${major}.0" --runtime dotnet --install-dir "$install_dir" --no-path; then
  rm -f "$script"
  log "ERROR: Could not download .NET ${major}. The runner cannot reach the .NET download servers."
  log "Fix: install the .NET ${major} runtime on the runner once, or use the self-contained (bundled .NET) package."
  exit 1
fi
rm -f "$script"
log "Installed in $(( $(date +%s) - start ))s ($(du -sh "$install_dir" | cut -f1))."
has_runtime "$install_dir/dotnet" || { log "ERROR: .NET ${major} runtime not usable after install."; exit 1; }
export_env "$install_dir" "installed"
