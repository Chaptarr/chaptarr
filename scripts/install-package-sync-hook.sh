#!/usr/bin/env bash
set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
source_repo="$(basename "$repo_root")"
source_hook="$repo_root/.githooks/post-commit"
sync_script="$repo_root/scripts/sync-unraid-package.sh"
local_hooks_dir="$(git -C "$repo_root" config --local --path core.hooksPath || true)"
effective_hooks_dir="$(git -C "$repo_root" config --path core.hooksPath || true)"

if [[ ! -f "$source_hook" || ! -f "$sync_script" ]]; then
	echo "A tracked package sync hook or script is missing from $repo_root." >&2
	exit 1
fi

if [[ -n "$effective_hooks_dir" && -z "$local_hooks_dir" ]]; then
	echo "A shared core.hooksPath is configured. Set a repository-local core.hooksPath before installing this hook." >&2
	exit 1
fi

hooks_dir="$local_hooks_dir"
if [[ -z "$hooks_dir" ]]; then
	hooks_dir="$(git -C "$repo_root" rev-parse --git-path hooks)"
fi

if [[ "$hooks_dir" != /* ]]; then
	hooks_dir="$repo_root/$hooks_dir"
fi

mkdir -p "$hooks_dir"
hook_path="$hooks_dir/post-commit"
chmod +x "$source_hook" "$sync_script"

if [[ -e "$hook_path" || -L "$hook_path" ]]; then
	if [[ "$hook_path" -ef "$source_hook" ]]; then
		echo "$source_repo package sync hook is already installed."
		exit 0
	fi

	echo "Refusing to replace an existing post-commit hook: $hook_path" >&2
	exit 1
fi

ln -s "$source_hook" "$hook_path"
echo "Installed $source_repo package sync hook at $hook_path"
