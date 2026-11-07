# BookshelfNG Helm handoff chart

This chart keeps the existing BookshelfNG chart identity and Kubernetes
resource names while replacing its container with ChaptarrNG. It is a
**source-only retirement candidate**. There is no published ChaptarrNG Helm
chart, and a production cutover is not ready yet. ChaptarrNG releases and
compatibility bridges remain frozen until representative data, migration and
rollback, feature parity, and installed channel handoff pass technical checks.
Maintainer/operator sign-off is recorded separately and does not close these
readiness gates.

The chart name stays `bookshelfng`. With the same Helm release name and
namespace, it keeps the StatefulSet, Service, PVC, selector labels, service
account behavior, `/config` mount, and default port `8787`. The existing PVC remains
named `<release>-config` under the default naming rules. Keep the existing
release name, namespace, and values for an in-place cutover.

## Safety requirements

Rendering fails closed unless all of these are supplied:

- `bookshelfMigration.confirmed=true`, set after migration and channel checks
  pass for the target deployment;
- `bookshelfMigration.providerMode=hardcover` or `softcover` when the source
  mode cannot be read from `extraEnv` or the retained `image.tag`; an omitted
  mode follows the effective old runtime settings: a readable `HARDCOVER`
  override, `HARDCOVER_NATIVE=false` (which selects Goodreads), then the old
  image flavor (`hardcover` by default, or `softcover`); and
- `bookshelfMigration.mode=automatic`, `ask`, `manual`, or `retry`; and
- `image.digest=sha256:<64 lowercase hexadecimal characters>`, pointing to
  a validated ChaptarrNG multi-architecture image. A private image mirror
  also needs the explicit `image.allowCustomRepository=true` setting.

The retained `image.tag` value is used only to identify the predecessor's
provider flavor during the handoff. The ChaptarrNG image always comes from the
validated `image.digest`. The chart also rejects more than one replica while
the source SQLite database is migrated. Replica count zero is allowed for a
deliberate service stop.

The chart sets `CHAPTARR_BOOKSHELF_COMPATIBILITY=true`, the resolved
`HARDCOVER` mode, and migration timing in the pod environment. A readable
`HARDCOVER` value takes precedence over the old chart's retained `image.tag`;
`HARDCOVER_NATIVE=false` forces Goodreads mode. Otherwise, the chart infers
the predecessor flavor from the old image tag. The old chart defaults to
`hardcover`; an old `softcover` tag stays on the Goodreads dialect. When a
Secret makes the effective mode ambiguous (for example, `HARDCOVER=true` with
`HARDCOVER_NATIVE` from a Secret), set `providerMode` explicitly. Migration
timing defaults to `automatic`. The image entrypoint supervises first startup, verifies the local
health endpoint and database lineage, and restores source data/configuration
if the handoff fails. Its startup probe allows the migration handoff, and
liveness keeps a deliberately deferred or failed pod available for operator
recovery. Readiness requires a cleared handoff marker and the app's `/ping`
endpoint using the preserved port and URL base. It also verifies that the
saved app port matches the chart service port, so the pod does not receive
traffic when the old service mapping would miss it. The supervisor uses an
internal app-only health check while it confirms database migration. These
values override duplicates in `extraEnv`; keep other existing entries there
unchanged.

Set `bookshelfMigration.mode=manual` to update the chart/image while leaving
the BookshelfNG database untouched and the pod unready. Kubernetes has no
interactive terminal for `ask`, so that mode also defers migration. After
reviewing the source, run the Console from the held pod while BookshelfNG is
stopped, then switch the value to `automatic` and restart the pod:

```sh
kubectl exec bookshelfng-0 -- dotnet /app/Chaptarr.dll \
  -preview-bookshelf-migration -data=/config
kubectl exec bookshelfng-0 -- dotnet /app/Chaptarr.dll \
  -migrate-bookshelf -confirm-bookshelf-service-stopped -data=/config
```

If both `readarr.db` and `audioarr.db` contain data, add
`-merge-bookshelf-databases` to both commands. Automatic startup previews and
applies this merge itself; incompatible identities or settings keep both
source databases intact and the pod unready for operator review.

The second command changes the database. Keep an independent backup and retain
the source database backup created by ChaptarrNG.

To complete an `ask` or `manual` handoff through automatic startup, update the
candidate release without Helm's `--wait` option, then restart its held pod.
The pod is intentionally unready while the source database is deferred, so a
wait-enabled upgrade would block before the restart can happen:

```sh
helm upgrade "$RELEASE_NAME" "$CHAPTARRNG_CHART" \
  --version "$CHAPTARRNG_VERSION" \
  --namespace "$NAMESPACE" \
  --reuse-values \
  --set bookshelfMigration.mode=automatic
kubectl delete pod "${RELEASE_NAME}-0" --namespace "$NAMESPACE" --wait
kubectl rollout status "statefulset/${RELEASE_NAME}" \
  --namespace "$NAMESPACE" --timeout=5h
```

The compatibility supervisor handles the pod's termination signal and leaves
the source database unchanged until the restarted pod starts the accepted
automatic migration. Wait for the new pod to become Ready before sending it
traffic.

If a prior first-start attempt rolled back cleanly and left the pod waiting,
set `bookshelfMigration.mode=retry` only after correcting the cause. The
supervisor first verifies either a completed migration or a fresh successful
preview of the restored source; an `intervention-required` state still needs
manual recovery.

## Roll back after a successful migration

Do not run `helm rollback` directly against a converted database. Helm would
restore the old image while leaving ChaptarrNG's database in place. The chart
has an explicit restore Job for this case. For PostgreSQL it first validates
the retained log/cache dumps and database availability, then previews and
restores the newest retained BookshelfNG main database, keeps the current
ChaptarrNG database and log/cache dumps separately, and restores the source
log/cache dumps. The target PostgreSQL log/cache dumps remain under the
attempt directory with the Helm revision in their name. The Job is
fail-closed unless the app is scaled to zero, the operator confirms the action,
and the database type is explicit.

Before the original cutover, record the Helm revision that still ran
BookshelfNG. For a rollback, first scale the ChaptarrNG StatefulSet to zero and
wait for that pod to stop:

```sh
helm history "$RELEASE_NAME" --namespace "$NAMESPACE"
helm upgrade "$RELEASE_NAME" "$CHAPTARRNG_CHART" \
  --version "$CHAPTARRNG_VERSION" \
  --namespace "$NAMESPACE" \
  --reuse-values \
  --set replicaCount=0 \
  --wait --timeout 5h
```

Then run the restore Job. For a SQLite source:

```sh
helm upgrade "$RELEASE_NAME" "$CHAPTARRNG_CHART" \
  --version "$CHAPTARRNG_VERSION" \
  --namespace "$NAMESPACE" \
  --reuse-values \
  --set replicaCount=0 \
  --set bookshelfMigration.rollback.enabled=true \
  --set bookshelfMigration.rollback.confirmed=true \
  --set bookshelfMigration.rollback.databaseType=sqlite \
  --wait --wait-for-jobs --timeout 5h
```

For PostgreSQL, use `databaseType=postgresql` and set
`bookshelfMigration.rollback.postgresBackupPath` to the verified
`/config`-relative directory
`.chaptarrng-bookshelf-handoff/<attempt-id>/postgres-source` containing the
independent main/log/cache dumps. Keep the existing connection settings in
`extraEnv` or their referenced Kubernetes Secrets. The Job validates the
retained log/cache dumps and confirms the databases are idle before restoring
the main BookshelfNG schema.

Wait for the restore Job to complete successfully before rolling the release
back to the BookshelfNG revision recorded before cutover:

```sh
helm rollback "$RELEASE_NAME" "$BOOKSHELFNG_REVISION" \
  --namespace "$NAMESPACE" --wait --timeout 5h
```

If the Job fails, leave the StatefulSet stopped and inspect its logs and the
retained backups before retrying. A successful restore keeps post-migration
ChaptarrNG writes in a separate backup; the old BookshelfNG service will see
the pre-cutover source state.

Do not use this candidate against live BookshelfNG data. The published
ChaptarrNG `v0.9.977` image has known migration limits and is not ready for
this cutover. The startup supervisor is part of the image, but its behavior
still needs installed-channel acceptance against the target data before this
chart can be used for cutover.

## Future cutover shape

After the release and channel gates close, use the versioned chart published to
the existing `oci://ghcr.io/snapetech/bookshelfng/bookshelfng` identity. Keep
the existing release name and namespace.
First record the release and its values, including the old `image.tag`, stop
BookshelfNG, take an independent
backup or storage snapshot, and complete the migration preview and
rollback rehearsal on a copy. Preserve any old `HARDCOVER` or
`HARDCOVER_NATIVE` setting in `extraEnv`; if it was stored outside the chart,
set `bookshelfMigration.providerMode` to match the effective source mode. With
no saved mode setting, the old image tag supplies its default (`hardcover`
unless the installation used `softcover`). Obtain the matching
immutable image digest from release evidence.

The in-place upgrade will have this form; replace the placeholders with the
existing release details and the validated image digest:

```sh
helm upgrade bookshelfng oci://ghcr.io/snapetech/bookshelfng/bookshelfng \
  --version <chaptarrng-version> \
  --namespace <existing-namespace> \
  --reuse-values \
  --set-string image.repository=ghcr.io/snapetech-org/chaptarrng \
  --set-string image.digest=sha256:<validated-64-character-digest> \
  --set bookshelfMigration.confirmed=true \
  --set bookshelfMigration.mode=automatic
```

Set `bookshelfMigration.providerMode` when a Secret or unrecognized image tag
leaves the source mode ambiguous. The chart sets `HARDCOVER` from the resolved mode, so the
image digest is the same across both modes. With `--reuse-values`, explicitly override both
`image.repository` and `image.digest`; older chart values otherwise retain the
legacy image repository. Review the rendered manifests before applying.

This chart source lives in the ChaptarrNG repository. Its release workflow has
a publisher for the existing OCI identity, gated by both
`CHAPTARRNG_RELEASE_READY` and `BOOKSHELFNG_RELEASE_READY`, as well as
`CHAPTARRNG_MIGRATION_BRIDGE_ENABLED` and an explicit bridge request. No chart
artifact is published while the retirement freeze is active. Once the gates
close, existing installations can upgrade the same chart source and preserve
their release name, namespace, and PVC; pinned/offline chart archives still
need a manual chart and image update.

## Local validation

With Helm 3.20.0 and Ruby available, run:

```sh
mkdir -p /tmp/bookshelfng-reference
git -C /path/to/bookshelfng archive adea130aeea2b6ea29c28a6ea8bf4706169d15e4 charts/bookshelfng \
  | tar -x -C /tmp/bookshelfng-reference
BOOKSHELFNG_REFERENCE_CHART=/tmp/bookshelfng-reference/charts/bookshelfng \
  ruby scripts/test_bookshelfng_helm_handoff.rb
helm lint charts/bookshelfng \
  --set bookshelfMigration.confirmed=true \
  --set bookshelfMigration.providerMode=hardcover \
  --set-string image.digest=sha256:0000000000000000000000000000000000000000000000000000000000000000
```

The test requires the pinned BookshelfNG reference chart, renders both charts
with the same legacy release values, and compares every rendered resource
after normalizing only the app image and chart-version labels. It covers the
default release and retained ingress, service-account, persistence, resource,
node-selection, pod-label, and secret-backed environment settings. It also
checks that missing operator confirmation or a valid image digest fails, and
that an unreadable Secret-backed provider mode requires an explicit choice.
