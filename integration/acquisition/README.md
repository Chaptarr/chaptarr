# Migrated acquisition rehearsal

Build/extract a Linux musl x64 standalone candidate, then build the local client
image and run the rehearsal with a new task-owned output directory:

```sh
docker build -t chaptarrng-acquisition-qbittorrent:20261105 \
  -f integration/acquisition/Dockerfile.qbittorrent integration/acquisition
python3 integration/run-acquisition-smoke.py \
  --candidate /absolute/path/ChaptarrNG \
  --output /var/tmp/chaptarrng-acquisition-UNIQUE
python3 integration/run-acquisition-smoke.py --media-type audiobook \
  --candidate /absolute/path/ChaptarrNG \
  --output /var/tmp/chaptarrng-acquisition-audio-UNIQUE
```

The fixtures contain original synthetic EPUB content or an ffmpeg-generated
20-second MP3 tone, and a schema-158 source
database. Startup converts the database. The digest-pinned deployed SeerrNG
adapter monitors the existing migrated ebook and requests a search. A local
Torznab server supplies its real torrent. Two real qBittorrent processes transfer
the payload over loopback. Normal completed-download handling imports it.

Checks require scoped format-only availability, grab/import history, unchanged downloaded
bytes and reading content, configured series tags, and availability after restart.
All containers share an isolated network namespace with no external network or
published ports. The fixture uses disposable API keys and no production data.
Only this run's containers are stopped/removed; synthetic state, logs and
provenance remain in the output directory. The runner refuses to reuse it.
The MP3 path requires ffmpeg on the creating host and verifies exact imported
audio bytes with the retained default of audio tagging disabled.

The qBittorrent package comes from the pinned base image's Debian repositories;
the evidence records the resulting image ID. Build/package provenance remains
separate from behavior. The harness records its Git revision, dirty status and
candidate executable hash. Keep the standalone archive's manifest alongside the
results to identify the complete application payload.

The default mode tests the actual SeerrNG adapter. Full-server mode additionally
exercises its request approval/fulfillment database as described below. It does not establish live third-party tracker, provider, Usenet,
other audio formats, production migration or publication acceptance.

`integration/run-cover-proxy-smoke.py` is a separate synthetic case. It uses
Forms authentication and the deployed SeerrNG client to fetch the original PNG
in this directory through a registered book-cover proxy and the author-cover
route. Pass `--candidate`, a new `--output`, and the full public `--source-commit`
containing this fixture. It verifies original book bytes, author original/cached
rendition bytes, and denied unauthenticated, wrong-key, UI and nonimage requests.
Unlike the torrent cases, this case permits external networking to fetch the
digest-selected fixture from GitHub HTTPS. It creates only disposable synthetic
configuration, removes provider/client connections, and publishes no host ports.
Never substitute operator data or configuration into this networked case.

## Full Seerr server request and fulfillment

Use `--request-mode seerr-web` with a tested Seerr image and repeat for each
format. The harness initializes only synthetic accounts/configuration, signs in
as a non-admin, submits a pending catalog request, verifies self-approval is
denied, and approves as the owner. It then transfers/imports the media, invokes
the real library scanner and restarts both servers. Persisted checks require a
completed request, the original media/request/requester IDs, exactly one media
record for the imported book, zero orphaned identifiers and format-only links.
All service traffic stays within the network-none namespace.

The deployed pinned Seerr image can show availability while creating a duplicate
media record. [SeerrNG PR #527](https://github.com/snapetech-org/seerrng/pull/527)
fixes the identifier relation and catalog scan matching. Do not treat a passing
adapter/availability check as evidence that this older client preserves database
identity. Testing the repair requires a local image built from that source:

```sh
python3 integration/run-acquisition-smoke.py --request-mode seerr-web \
  --seerr-image LOCAL_TEST_IMAGE \
  --seerr-source-revision FULL_SEERR_COMMIT \
  --media-type ebook --candidate /absolute/path/ChaptarrNG \
  --output /var/tmp/chaptarrng-seerr-web-UNIQUE
```

The declared client revision, actual image IDs, candidate assembly hashes and
all harness source hashes are recorded. Keep the image build's source/module
provenance with the evidence. A derived test image establishes candidate
behavior; it does not establish deployment or publication of the Seerr repair.
