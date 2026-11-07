# Moving media between author folders

ChaptarrNG can move registered ebook or audiobook files for one author or for
up to 1,000 selected authors. A move operates on files recorded in the
ChaptarrNG instance that starts it; it does not merge another instance's
database, settings, history, or catalog. For a BookshelfNG application and data
migration, use the [migration guide](MIGRATE_FROM_BOOKSHELFNG.md).

Moving media requires an administrator API key, including when the move is
started from the web UI.

## Initialize paths for new authors

For separate ebook and audiobook libraries, configure the matching default
root folders under **Settings → Media Management**. The server-wide
**Initialize ebook and audiobook paths for new authors** setting is off by
default. When enabled, ChaptarrNG fills missing format-specific root folders
from their matching effective defaults when a new author is added, including
through an import. If a matching default is missing or ambiguous, that path
stays unset. Explicit roots and existing authors are left alone; this setting
does not move files. Use the move workflow below to relocate existing media.

The setting is available on current `main` and has not shipped in public
`v0.9.977`. During a BookshelfNG migration, its saved value is preserved; it
still applies only to new authors and does not backfill existing paths.
See the [migration guide](MIGRATE_FROM_BOOKSHELFNG.md) for the full settings
mapping.

## Move one author's format

1. Open **Authors**, edit the author, and set **Ebook Path** or **Audiobook
   Path** to a destination inside a configured root folder.
2. Choose **Move existing ebooks** or **Move existing audiobooks** under that
   path.
3. Review the source and destination, file and sidecar lists, missing files,
   conflicts, and copy-space estimate.
4. Start the move only when the preview is safe. Follow its command in
   **Activity → Queue**.

Changing a format path alone sets the destination for future imports, upgrades,
and renames; it does not move existing files. Starting a move also saves the
format path. If that path is the author's default `Path`, ChaptarrNG clears the
format-specific override and uses the default.

## Move one format for selected authors

1. In the author library, select authors and open **Author Editor**.
2. Choose **Move selected media**, select **Ebooks** or **Audiobooks**, and
   choose a configured destination root.
3. Review the destination for each author, the file and space totals, warnings,
   and conflicts.
4. Start the batch and follow the queued command in **Activity → Queue**.

The existing author folder name is appended to the destination root. A batch
accepts up to 1,000 authors; move larger libraries in separate batches.
Queueing saves the chosen format path for each author in that batch.

## What moves

The worker moves registered media files for the selected format and
book-level metadata or extra sidecars registered to those files. Author-wide
metadata and extras stay in the author's default `Path`. Untracked files are
left where they are. Missing registered files are shown in the preview and
skipped.

## Preview and failure behavior

The preview lists source and destination paths, media and sidecar counts,
missing files, conflicts, and required copy space. ChaptarrNG does not
overwrite destination files. If a batch has a conflict, the whole batch is
blocked until the conflict is resolved and a new preview is made.

Starting a move requires the preview token. The server recalculates the
preview and rejects the request if relevant library or file state changed;
make a fresh preview before trying again. The move runs as a background
command, one file at a time. This is not a transactional operation: after a
storage or permission failure, files already moved and their database updates
remain in place. Check **Activity → Queue**, fix the cause, and preview again
before retrying. Keep the source files and the ChaptarrNG database backed up
until the move is verified.

## Command-line scripts

The scripts require Bash, curl, and jq. Use the ChaptarrNG URL and an
administrator API key. They print the server preview and ask before queueing;
`--yes` skips only the prompt, not the server preview.

Move one author's files:

~~~sh
scripts/chaptarr-move-media.sh --url http://localhost:8787 \
  --api-key "$CHAPTARR_API_KEY" --author-id 42 \
  --format audiobook --destination '/media/audiobooks/Author'
~~~

Move a selected set of authors:

~~~sh
scripts/chaptarr-bulk-move-media.sh --url http://localhost:8787 \
  --api-key "$CHAPTARR_API_KEY" --author-ids 42,57 \
  --format ebook --destination-root /media/ebooks
~~~

Replace `--author-ids` with `--all` to include every author in the current
instance. This does not reach other ChaptarrNG or BookshelfNG instances.

## API flow

The API requires an administrator key in the `X-Api-Key` header. For one
author, preview with `id`, `format` (`ebook` or `audiobook`), and
`destinationPath` at `POST /api/v1/author/media-move/preview`. For a batch,
send `authorIds`, `format`, and `destinationRootPath` to
`POST /api/v1/author/media-move/bulk/preview`.

Inspect the response and keep its `previewToken`. Send the same body with that
token to the matching `/start` endpoint. A successful request returns a
command resource; poll `GET /api/v1/command/{id}` for status and progress.
The server rejects stale tokens, unsafe previews, and batches with conflicts.
