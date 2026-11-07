#!/usr/bin/env python3
"""Decide whether package.json is newer than the latest stable release."""

from __future__ import annotations

import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any


_VERSION = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)")
_TAG = re.compile(r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)")
_REPOSITORY = re.compile(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+")


def parse_version(value: Any) -> tuple[int, int, int]:
    if not isinstance(value, str):
        raise ValueError("package.json must contain a stable three-part version.")

    match = _VERSION.fullmatch(value)
    if match is None:
        raise ValueError(f"Expected a stable SemVer version, found {value}.")

    return tuple(int(part) for part in match.groups())


def latest_stable_release(releases: Any) -> dict[str, Any] | None:
    if not isinstance(releases, list):
        raise ValueError("GitHub Releases API returned an unexpected response.")

    candidates = []
    for release in releases:
        if not isinstance(release, dict):
            continue

        tag = release.get("tag_name")
        published_at = release.get("published_at")
        if (
            release.get("draft") is False
            and release.get("prerelease") is False
            and isinstance(tag, str)
            and _TAG.fullmatch(tag)
            and isinstance(published_at, str)
        ):
            candidates.append(release)

    return max(candidates, key=lambda release: release["published_at"], default=None)


def release_is_newer(version: str, latest_tag: str | None) -> bool:
    current = parse_version(version)
    if latest_tag is None:
        return True

    match = _TAG.fullmatch(latest_tag)
    if match is None:
        raise ValueError(f"Expected a stable SemVer release tag, found {latest_tag}.")

    latest = tuple(int(part) for part in match.groups())
    return current > latest


def fetch_published_releases(repository: str, token: str) -> list[dict[str, Any]]:
    if _REPOSITORY.fullmatch(repository) is None:
        raise ValueError("GITHUB_REPOSITORY must be in owner/name form.")
    if not token:
        raise ValueError("GH_TOKEN is required to inspect published releases.")

    api_base = os.environ.get("GITHUB_API_URL", "https://api.github.com").rstrip("/")
    parsed_base = urllib.parse.urlsplit(api_base)
    if parsed_base.scheme != "https" or not parsed_base.netloc or parsed_base.username or parsed_base.password:
        raise ValueError("GITHUB_API_URL must be an HTTPS URL without embedded credentials.")

    escaped_repository = "/".join(urllib.parse.quote(part, safe="") for part in repository.split("/"))
    opener = urllib.request.build_opener()
    releases: list[dict[str, Any]] = []

    # Enumerate pages so a repository with many prereleases cannot hide its
    # latest stable release beyond the first 100 entries.
    for page in range(1, 101):
        query = urllib.parse.urlencode({"per_page": 100, "page": page})
        request = urllib.request.Request(
            f"{api_base}/repos/{escaped_repository}/releases?{query}",
            headers={
                "Accept": "application/vnd.github+json",
                "Authorization": f"Bearer {token}",
                "User-Agent": "chaptarrng-release-preflight",
                "X-GitHub-Api-Version": "2022-11-28",
            },
        )

        try:
            with opener.open(request, timeout=30) as response:
                page_releases = json.load(response)
        except urllib.error.HTTPError as error:
            raise RuntimeError(f"GitHub Releases API returned HTTP {error.code}.") from None
        except (urllib.error.URLError, TimeoutError):
            raise RuntimeError("GitHub Releases API request failed.") from None
        except json.JSONDecodeError:
            raise RuntimeError("GitHub Releases API returned invalid JSON.") from None

        if not isinstance(page_releases, list):
            raise RuntimeError("GitHub Releases API returned an unexpected response.")
        releases.extend(page_releases)
        if len(page_releases) < 100:
            return releases

    raise RuntimeError("GitHub Releases API exceeded the 10,000-release pagination limit.")


def main() -> int:
    try:
        package = json.loads(Path("package.json").read_text(encoding="utf-8"))
        version = package.get("version") if isinstance(package, dict) else None
        parse_version(version)

        repository = os.environ.get("GITHUB_REPOSITORY", "")
        token = os.environ.get("GH_TOKEN", "")
        output_path = os.environ.get("GITHUB_OUTPUT", "")
        if not output_path:
            raise RuntimeError("GITHUB_OUTPUT is required by the release workflow.")

        latest = latest_stable_release(fetch_published_releases(repository, token))
        latest_tag = latest["tag_name"] if latest is not None else None
        release_requested = release_is_newer(version, latest_tag)

        if release_requested:
            print(f"Version {version} is newer than {latest_tag or '<none>'}; starting the stable release pipeline.")
        else:
            print(f"Version {version} is not newer than the latest published stable release {latest_tag}; skipping release publication.")

        with Path(output_path).open("a", encoding="utf-8") as output:
            output.write(f"release_requested={str(release_requested).lower()}\n")
        return 0
    except (OSError, json.JSONDecodeError, ValueError, RuntimeError) as error:
        print(str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
