#!/usr/bin/env bash
set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

printf 'Refreshing Semgrep Community Edition...\n'
if command -v uvx >/dev/null 2>&1; then
  uvx --refresh --from semgrep semgrep --version
elif command -v pipx >/dev/null 2>&1; then
  if pipx list --short 2>/dev/null | grep -Eq '^semgrep( |$)'; then
    pipx upgrade semgrep
  else
    pipx install semgrep
  fi
elif command -v semgrep >/dev/null 2>&1; then
  echo "Semgrep is installed outside uv/pipx. Update it with its package manager, then rerun this script." >&2
else
  echo "Install uv or pipx to update Semgrep (for example: uv tool install semgrep)." >&2
fi

if command -v trivy >/dev/null 2>&1; then
  cache_dir="${TRIVY_CACHE_DIR:-${XDG_CACHE_HOME:-$HOME/.cache}/trivy}"
  mkdir -p "$cache_dir"
  TRIVY_CACHE_DIR="$cache_dir" trivy image --download-db-only --timeout "${TRIVY_TIMEOUT:-30m}"
elif command -v docker >/dev/null 2>&1; then
  cache_dir="${TRIVY_CACHE_DIR:-${XDG_CACHE_HOME:-$HOME/.cache}/trivy}"
  mkdir -p "$cache_dir"
  docker pull aquasec/trivy:latest
  docker run --rm --user "$(id -u):$(id -g)" -e HOME=/tmp -e TRIVY_CACHE_DIR=/cache \
    -v "$cache_dir:/cache" aquasec/trivy:latest image --download-db-only
else
  echo "Trivy is not installed; install it with your platform package manager to refresh its database." >&2
fi

if command -v docker >/dev/null 2>&1; then
  docker pull semgrep/semgrep:latest
fi
