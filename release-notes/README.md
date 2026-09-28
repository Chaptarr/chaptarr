# Curated release notes

Before running `.github/workflows/release.yml`, add a Markdown file named for
the exact tag, for example `release-notes/v0.9.937.md`. The workflow uses that
file as the release body and keeps the release in draft until the GHCR image
build succeeds.

Each file should describe changes users will notice in Chaptarr and any action
operators need to take. Do not use GitHub's generated commit list: this fork
shares upstream history, so the generated list would present upstream changes
as changes made by this fork.
