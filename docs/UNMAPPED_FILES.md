# Retry unmapped files

ChaptarrNG can retry matching files that remain in Unmapped Files after catalog
or metadata changes, or after an operator fixes their metadata. Routine scans
also restage unchanged unmapped files, so a file is not permanently skipped
just because its contents did not change.

## Command-line helper

The helper requires Bash and curl. jq is also required when using --wait. Set
CHAPTARR_URL and CHAPTARR_API_KEY or pass --url and --api-key. Migrated scripts
can keep using BOOKSHELF_URL and BOOKSHELF_API_KEY; the ChaptarrNG variables
take precedence when both pairs are set. The API key must be able to queue
commands.

Queue a retry and return immediately:

    scripts/chaptarr-retry-unmapped.sh \
      --url http://localhost:8787 --api-key "$CHAPTARR_API_KEY"

Wait for completion and print the final command response:

    scripts/chaptarr-retry-unmapped.sh \
      --url http://localhost:8787 --api-key "$CHAPTARR_API_KEY" \
      --wait --json

## API request

The helper posts to /api/v1/command with the current command name and explicit
all-format selection:

    {"name":"RetryUnmappedMatch","mediaType":"all","unmappedFiles":{"scope":"all"}}

The response contains a command ID. Poll GET /api/v1/command/{id} to check its
status. The --wait option does this every two seconds until the command reaches
a terminal status.
