## Runner policy — owner directive, 2026-11-03

Public GitHub-hosted runner minutes are available again. Compatible
GitHub-hosted runners are authorized for trusted build, test, scan, packaging,
and release jobs; private ephemeral JIT remains available but is no longer
mandatory. Choose the runner OS and architecture to match each job. Use an
explicit route when a workflow offers both pools; do not add a silent fallback.

Keep untrusted pull-request code on an isolated disposable VM-backed runner
with no secrets, minimal read access, and restricted egress. Do not widen
trusted runner-group access to admit untrusted code. Preserve active builds and
other agents' work. Do not restart the broker while it owns running guests.

Runner choice does not satisfy release readiness. Keep publication, migration,
and cutover behind their existing fail-closed acceptance gates. Treat exact-head
validation, installed acceptance, operator data, and production readiness as
separate evidence.

# ChaptarrNG build notes

## Six Labors license — check these paths first

Before diagnosing, retrying, or asking Keith about a ChaptarrNG build that needs the Six Labors license, check these confirmed local paths first:

- The license key is recorded in the gitignored repository-root file `.env.sixlabors`. Check that the file exists without printing or copying its contents.
- The license file exists at `/home/keith/Documents/code/chaptarrng/sixlabors.lic` and `/home/keith/Downloads/sixlabors.lic`.
- For local builds, pass `-p:SixLaborsLicenseFile=/home/keith/Documents/code/chaptarrng/sixlabors.lic`; use the Downloads copy if the repository copy is unavailable.

Do not print or expose the key, add `.env.sixlabors` or either `.lic` file to Git, or ask Keith to locate these files unless both license-file paths have been checked and are actually unavailable.
