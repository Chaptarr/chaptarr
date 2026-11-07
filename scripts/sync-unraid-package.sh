#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(git -C "$script_dir/.." rev-parse --show-toplevel)"
source_branch="$(git -C "$repo_root" branch --show-current)"
if [[ "$source_branch" != "main" ]]; then
	echo "Skipping Unraid package sync from '$source_branch'; only main may update the companion repository."
	exit 0
fi

origin_url="$(git -C "$repo_root" remote get-url origin 2>/dev/null || true)"
source_repo="${origin_url%/}"
source_repo="${source_repo##*/}"
source_repo="${source_repo%.git}"

case "$source_repo" in
	bookshelfng|chaptarrng) ;;
	*)
		echo "Unraid package sync is not configured for $source_repo." >&2
		exit 1
		;;
esac

target_repo="${source_repo}-unraid"
source_repo_upper="$(printf '%s' "$source_repo" | tr '[:lower:]' '[:upper:]')"
override_name="${source_repo_upper}_UNRAID_REPO"
target_dir="${!override_name:-$(dirname "$repo_root")/$target_repo}"
remote=origin
branch=main
source_path=packaging/unraid

if ! git -C "$repo_root" cat-file -e "HEAD:$source_path" >/dev/null 2>&1; then
	echo "No committed Unraid package source exists at $source_path yet; skipping sync."
	exit 0
fi

template_path="templates/$source_repo.xml"

if ! git -C "$repo_root" cat-file -e "HEAD:$template_path" >/dev/null 2>&1; then
	template_path="$source_path/templates/$source_repo.xml"
fi

if ! git -C "$repo_root" cat-file -e "HEAD:$template_path" >/dev/null 2>&1; then
	echo "No committed Unraid template exists at templates/$source_repo.xml or $source_path/templates/$source_repo.xml." >&2
	exit 1
fi

if ! command -v rsync >/dev/null 2>&1; then
	echo "rsync is required to sync the Unraid package." >&2
	exit 1
fi

if [[ ! -d "$target_dir" ]] || ! git -C "$target_dir" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
	echo "Unraid companion checkout not found at $target_dir; clone it or set $override_name." >&2
	exit 1
fi

remote_url="$(git -C "$target_dir" config --get "remote.$remote.url" 2>/dev/null || true)"
case "$remote_url" in
	"https://github.com/snapetech-org/$target_repo.git"|"https://github.com/snapetech-org/$target_repo"|"git@github.com:snapetech-org/$target_repo.git"|"git@github.com:snapetech-org/$target_repo"|"ssh://git@github.com/snapetech-org/$target_repo.git") ;;
	*)
		echo "Remote '$remote' must point to snapetech-org/$target_repo (found: ${remote_url:-missing})." >&2
		exit 1
		;;
esac

staging_dir="$(mktemp -d)"
trap 'rm -rf "$staging_dir"' EXIT
archive_paths=("$source_path")
if [[ "$template_path" != "$source_path/"* ]]; then
	archive_paths+=("$template_path")
fi
git -C "$repo_root" archive --format=tar HEAD "${archive_paths[@]}" | tar -xf - -C "$staging_dir"
source_dir="$staging_dir/$source_path"
template_file="$staging_dir/$template_path"

for required_file in README.md ca_profile.xml; do
	if [[ ! -f "$source_dir/$required_file" ]]; then
		echo "The committed Unraid package source is missing $source_path/$required_file." >&2
		exit 1
	fi
done

if [[ ! -f "$template_file" ]]; then
	echo "The committed Unraid template is missing $template_path." >&2
	exit 1
fi

if ! git -C "$target_dir" show-ref --verify --quiet "refs/heads/$branch"; then
	echo "The Unraid companion checkout must have a local '$branch' branch." >&2
	exit 1
fi

git -C "$target_dir" fetch --prune "$remote"
if ! git -C "$target_dir" show-ref --verify --quiet "refs/remotes/$remote/$branch"; then
	echo "Remote '$remote' has no '$branch' branch in $target_dir." >&2
	exit 1
fi

current_branch="$(git -C "$target_dir" branch --show-current)"
status_output="$(git -C "$target_dir" status --porcelain --untracked-files=all)"
if [[ -n "$status_output" ]]; then
	if [[ "$current_branch" != "$branch" ]]; then
		echo "Unraid companion checkout has local changes on '$current_branch'; switch to '$branch' after cleaning it." >&2
		exit 1
	fi

	local_head="$(git -C "$target_dir" rev-parse HEAD)"
	remote_head="$(git -C "$target_dir" rev-parse "refs/remotes/$remote/$branch")"
	if [[ "$local_head" != "$remote_head" ]]; then
		echo "Unraid companion checkout has local changes and is not at $remote/$branch; update it before syncing." >&2
		exit 1
	fi

	while IFS= read -r status_line; do
		path="${status_line:3}"
		case "$path" in
			README.md|ca_profile.xml|docs/*|templates/*) ;;
			*)
				echo "Unraid companion checkout has unrelated local changes: $path" >&2
				exit 1
				;;
		esac

		if [[ ! -f "$source_dir/$path" || ! -f "$target_dir/$path" ]] || ! cmp -s "$source_dir/$path" "$target_dir/$path"; then
			echo "Unraid companion checkout has a local change that differs from committed package source: $path" >&2
			exit 1
		fi
	done <<< "$status_output"
else
	if [[ "$current_branch" != "$branch" ]]; then
		git -C "$target_dir" switch "$branch"
	fi
	local_head="$(git -C "$target_dir" rev-parse HEAD)"
	remote_head="$(git -C "$target_dir" rev-parse "refs/remotes/$remote/$branch")"
	if ! git -C "$target_dir" merge-base --is-ancestor "$local_head" "$remote_head"; then
		echo "Unraid companion checkout has local commits that are not on $remote/$branch; reconcile them before syncing." >&2
		exit 1
	fi
	git -C "$target_dir" merge --ff-only "$remote/$branch"
fi

rsync --archive "$source_dir/README.md" "$target_dir/README.md"
rsync --archive "$source_dir/ca_profile.xml" "$target_dir/ca_profile.xml"
template_staging_dir="$staging_dir/companion-templates"
mkdir -p "$template_staging_dir"
rsync --archive "$template_file" "$template_staging_dir/$source_repo.xml"
rsync --archive --delete "$template_staging_dir/" "$target_dir/templates/"
if [[ -d "$source_dir/docs" ]]; then
	rsync --archive --delete "$source_dir/docs/" "$target_dir/docs/"
elif [[ -d "$target_dir/docs" ]]; then
	rm -rf "$target_dir/docs"
fi

managed_paths=(README.md ca_profile.xml templates)
if [[ -d "$source_dir/docs" || -e "$target_dir/docs" ]] || [[ -n "$(git -C "$target_dir" ls-files -- docs)" ]]; then
	managed_paths+=(docs)
fi
git -C "$target_dir" add --all -- "${managed_paths[@]}"

source_commit="$(git -C "$repo_root" rev-parse --short HEAD)"
if ! git -C "$target_dir" diff --cached --quiet; then
	git -C "$target_dir" commit -m "chore: sync Unraid package from $source_commit"
fi

GIT_TERMINAL_PROMPT=0 git -C "$target_dir" push "$remote" "HEAD:refs/heads/$branch"
echo "Synced $source_path to $target_dir ($branch)."
