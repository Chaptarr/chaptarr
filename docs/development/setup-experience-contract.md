# Setup experience contract

Apply this contract through ChaptarrNG's existing **System → Quickstart** and
provider settings. Quickstart already contains sections for optional
connections, indexers, download clients, custom formats, quality profiles,
metadata profiles, matching, root folders, and settings backups. Its guided
mode advances through these sections and supports returning from linked
configuration pages. Keep that existing route and state model.

## Product-specific flow

Start by asking what the operator wants ChaptarrNG to manage: ebooks,
audiobooks, or both. Use explicit existing configuration to preselect known
format roots or services; never scan a library or change existing author paths
to guess. Show only the relevant format-specific folder, profile, and matching
choices. Preserve the current behavior that fills missing new-author paths
from unambiguous defaults while keeping explicit and existing paths intact.

For acquisition, ask whether the operator already uses an indexer or download
client. If yes, open the matching existing provider form and its schema-driven
test. If no, show supported choices and explain which actions stay unavailable
until one is configured. Keep indexer, client, and completed-download path
choices together in the current Quickstart sequence so path mismatches are
caught before a grab is attempted.

Show Hardcover, metadata providers, Audiobookshelf, Calibre, Grimmory, BookLore,
or other supported optional connections when relevant to the selected library
workflow, and keep any existing configured connection visible. Do not ask about
e-reader device formats unless ChaptarrNG can apply that answer to an existing
format/profile setting. ChaptarrNG
organizes and acquires ebooks and audiobooks; device syncing belongs to a
configured reader/library integration. Keep unpublished integrations out of
the public setup path.

## Contract

- Read existing settings and connection status through current Quickstart
  selectors and provider APIs.
- Ask one decision at a time, with inline explanations for format, root folder,
  profile, matching, and acquisition choices.
- Keep optional sections skippable and retain the current full settings pages.
- Reuse provider schemas, save actions, and connection tests; do not create a
  second configuration store.
- Preserve explicit operator settings and existing data. Preview impact before
  any action that changes library roots, format routing, or matching behavior.
- On failure, show the provider's actionable error in Quickstart and keep the
  operator at that section; preserve state when visiting a linked settings page.
- End with a review of active formats, roots, providers, and optional
  integrations, including Calibre when enabled for ebook roots. Backups remain
  available through the existing backup section.

The design contract is specific to ChaptarrNG's book acquisition and library
workflow. It is not a template for the other NG projects' setup questions.

## Implementation tasks

These tasks extend the current Quickstart sections and keep their settings
models intact.

- [x] **C1 — P1: Start guided mode for any missing core setup item.**
  In `frontend/src/System/Quickstart/Quickstart.js`, replace the current
  root-folder-only `showGuidance` condition with readiness derived from the
  already loaded root-folder, enabled-indexer, and enabled-download-client
  state. Keep the existing completion tracking in
  `frontend/src/System/Quickstart/QuickstartSection.js`.
  **Done when:** an installation with a root folder but no enabled indexer or
  download client opens on that missing section; already configured sections
  are skipped; optional connections and tuning sections can be skipped without
  blocking root-folder setup.

- [x] **C2 — P1: Make ebook and audiobook root setup explicit in the existing
  root-folder section.** In
  `frontend/src/System/Quickstart/QuickstartRootFoldersSection.js` and the
  existing root-folder modal, expose the current ebook, audiobook, or both
  scope at the point where a new root is added. Use existing root-folder scope
  fields as the saved answer; do not add a second “interest” preference.
  **Done when:** a new operator can tell which format a root serves before
  saving it, both formats can use separate roots, and existing root paths and
  scopes are not changed by Quickstart.

- [x] **C3 — P2: Branch optional connections by their supported formats and
  show a useful end state.** In
  `frontend/src/System/Quickstart/Quickstart.js`,
  `frontend/src/System/Quickstart/QuickstartSection.js`, and the existing
  connection sections, show AudioBookShelf for audiobook workflows already
  selected or present in saved roots. Show Grimmory and Hardcover for any
  selected or saved library format, and retain configured connections when
  this session's scope is narrower. Keep the proxy form available for either
  format.
  Include configured, skipped, and failed states in the existing guided review.
  **Done when:** no optional connection is required to acquire or organize
  either format, a failed connection leaves the operator at its current
  section, and the review lists formats, roots, indexers, clients, and relevant
  optional connections.
