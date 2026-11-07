# Fixed server and notification copy review

The source catalog and caller reviews are recorded below. The current source snapshot contains 4,101 English IDs, with 1,146 missing direct values per target locale (13,752 cells across twelve locales). Translation generation and final headed Agentic VM acceptance remain queued. Agent review does not claim qualified native-speaker approval.

## Source coverage

- Server/API, validation, operational progress, and persisted failure messages: 554 distinct messages and 736 unique source callers (746 template/branch bindings). One wholly dynamic Telegram exception/status template is excluded because it contains no application wording.
- Provider field labels/help/warnings: 409 mapped sources and 643 callers, with all 811 explicit schema attribute values resolving to catalog IDs.
- Static provider dropdown labels and explanatory hints: 136 sources, 174 callers, 27 enum types, and 165 enum members. Numeric values, member identifiers, protocol `EnumMember` values, dynamic provider option names, and example hints stay unchanged.
- Outbound human-readable headings, bodies, fields, tests and sensor names: 57 sources and 108 callers, plus two explicitly fingerprinted exclusions for the Proper release marker and retag metadata field names. Transport keys, event names, topics, paths, book/author titles and provider responses stay data.

All seventeen authored loading messages also retain their original no-repeat variety behind stable IDs, with contributor/product names protected. The runtime fallback data is checked against its source inventory.

The expanded source set has 4,101 English IDs, including 1,151 IDs beyond the 2,950-ID baseline. The current target catalogs have 2,955 direct values each and are missing 1,146 values apiece. Registries carry stable IDs, source fingerprints, exact caller lines/hashes, and protected literal rules. `check-localized-source-coverage.cjs` and `check-server-message-catalogs.cjs` reject stale sources, missing target values, unsafe paths and placeholder/protected-value changes. `--allow-pending` is a preparation check, not final translation acceptance.

## Presentation callers

`server-message-callers.json` fingerprints every introduced presentation boundary. Reviewed paths include:

- Form result alerts, input help/errors/warnings, OAuth and enhanced-select validation. Multiple validation values are translated individually before joining.
- Provider notices for indexers, download clients, notifications and import lists; root-folder/list save failures; import-list synchronization preview/history; first-run authentication and Quickstart connection/Hardcover failures.
- Backup/restore/settings alerts, author media moves, queue status/detail/popovers, pending imports, import-progress drawers/sidebar messages, health status and persisted history details.
- Search-series fetch/save failure presentation and shared `getErrorMessage` consumers. Other search/action connectors retain raw server text until the sidebar or shared error renderer presents it.
- Proxy connection details are matched before insertion into a localized outer sentence. Unmapped-files log-preview failures and naming-validation fallback errors use the same bounded matcher.

The matcher handles only registered fixed wording, prefers exact matches, anchors templates, preserves captured strings byte-for-byte, and refuses ambiguous delimiters, conflicting matches, wholly dynamic patterns and oversized/non-string inputs. Literal examples such as `hc:{id}` remain literal. Known M4B singular/plural CPU wording maps to one count-preserving source template. Unknown messages and diagnostic exception/stack/provider payloads remain unchanged.

## Backend/schema and outbound callers

SchemaBuilder caches untranslated metadata and localizes cloned fields/options for each response, including tokens from the declaring settings type. This prevents labels from staying in a previous UI language. Sensitive values, saved configuration values and underlying enum values keep their existing paths. Regression tests switch languages after cache creation and confirm the original cached metadata and example hints stay intact.

NotificationFactory configures each provider instance with the current localization service. Shared headings and directly constructed notification bodies use stable keys; proxy-level test senders receive the service through dependency injection. One-pass substitution prevents a dynamic title/path containing braces from becoming another template. Home Assistant display names follow UI language changes through a locale-sensitive worker fingerprint; MQTT keys, IDs, enums, units and Jinja templates remain unchanged.

Download failures carry an explicit translation ID only when the app authored that failure. A provider error that happens to equal an app phrase has no ID and stays raw. History retains its original message while outgoing human copy uses the explicit ID/tokens. Health-check text already uses existing catalog-backed localization; forwarded provider status/exception fields remain data. Deletion bodies preserve author/book names and retain DTO English fallback compatibility for direct callers.

No notification is sent by localization tests. All notification transports are mocked. PR #181 and #182 discussion/review timelines were checked; both contain no review feedback to resolve. Final delivery requires strict coverage, all twelve translated catalogs, the full new-source semantic review, current-head tests/build, and headed provider/schema/RTL review in the Agentic VM.

## Independent review checkpoint

The coordinating agent independently reviewed matcher/presentation routing, raw schema metadata caching and per-response localization, one-pass notification substitution, provider setup, download-failure provenance/persistence and locale-sensitive MQTT fingerprints. No blocking issue was found in that pass; the outbound-provider and full caller review continues. The commit hook also flagged a dynamically constructed parameter-token regular expression. The parser now uses a static `valueN` token expression and a validated-name set, eliminating that construction while retaining bounded fixed-part matching.

## Headed mixed-direction review

A task-owned Chaptarr instance at `http://192.168.50.148:18879` was reviewed in an isolated headed Agentic VM Chrome session. Arabic root-folder validation reproduced reordered Latin path punctuation and mixed-script filenames. The presentation helper now inserts FSI/PDI boundaries around each captured value; a repeated real failing save and a URL/query/port/PUID-shaped capture confirmed the rendered correction. Raw HTTP400 messages remain exact and contain no display controls. Three screenshots and raw-response evidence are retained under `chaptarrng-artifacts/vm-review-capture-v1`; both the browser session and preview backend were closed. Four temporary Arabic values in copied build output enabled this implementation test; they are not repository translations or native approval. Final translated provider/schema/RTL/narrow-layout acceptance is still pending.

The same flow exposed two existing Arabic labels using broadcast/press-media wording for a book/audio library. `MediaManagement` and `MediaLibrary` now use the existing settings-header terminology for library media, with current-source draft provenance. No source IDs or frozen new-source inputs changed for those two contextual corrections.

## Source-only follow-up at 957f1a2e

The clean follow-up checkout fast-forwarded to current `main`, including its
pinned-narration and private-runner changes. This source-only delta adds 69
English IDs: 51 server/provenance/notification/grab-error templates, seven UI
sources and eleven task labels. Ten existing IDs gain missing presentation
callers. `source-followup-20261031.json` binds that bounded scope to the source
registries, English fingerprints and unchanged target-catalog hashes.

The server registry now has 537 messages, 711 unique source callers and 721
bindings. Its presentation ledger covers 74 calls across 34 files. The UI/task
follow-up registry has 28 IDs and 62 bindings, including the ten reused IDs.
Known task IDs map to display labels; unknown task names and command dispatch
retain their raw API data. Match-provenance observed/expected metadata remains
raw; only source-proven detail prose is matched. Interactive grab errors use the
bounded presenter for both visible errors and tooltips. Provider/OS/runtime
payloads remain opaque, including nested captured diagnostic values.

At the 957f1a2e follow-up head there were 4,084 English sources and 1,129
missing values per target locale. No target translations were added in that
source-only milestone.
The historical v4 bundle remains unmodified; it lacks these 69 sources and also
contains five IDs that already have direct targets. A future current-head
bundle must preserve those values and include this source delta. Remaining
exception-wrapper/log source classification and full translated/native/headed
acceptance are still open; this checkpoint does not claim whole-app closure.

## Diagnostic and exception wrapper audit at d94dd074

The audit added 17 fixed or bounded API error messages across 25 source
callsites. The existing `OAuthInput` and `Form` components pass these messages
through `translateServerMessage`, so the new keys cover current UI error
surfaces. Dynamic AudioBookShelf status text and Hardcover identifier type and
input values remain captured data. The startup-only remote-access failure,
raw provider responses, exception details, paths, and user/library values stay
outside the translation set. Exact callsites and source fingerprints are in
`diagnostic-exception-wrapper-audit-20261031.json`; no native-speaker review is
claimed.

The current input now has 4,101 English IDs and 1,146 pending values per locale,
or 13,752 target cells. All 17 new entries still need translation and review.
The Arabic whole-document RTL gate also remains in place until logical-layout
safety and headed visual review are complete.
