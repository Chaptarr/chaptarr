#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REFERENCE_CHART="${BOOKSHELFNG_REFERENCE_CHART:?pinned BookshelfNG chart checkout is required}"
LEGACY_IMAGE_REF="${BOOKSHELFNG_LEGACY_IMAGE_REF:-docker.io/snapetech/bookshelfng:hardcover-v0.4.21.76}"
LEGACY_IMAGE_REPOSITORY="${LEGACY_IMAGE_REF%%:*}"
LEGACY_IMAGE_DIGEST="${BOOKSHELFNG_LEGACY_IMAGE_DIGEST:?pinned BookshelfNG .76 image digest is required}"
EVIDENCE_DIR="${BOOKSHELFNG_HELM_EVIDENCE_DIR:-${RUNNER_TEMP:-/tmp}/bookshelfng-helm-install-evidence}"
CLUSTER_NAME=bookshelfng-helm-handoff
REGISTRY_NAME=kind-registry
REGISTRY_PORT=5001
REGISTRY_IMAGE=registry@sha256:1290056e2357eceb0c047cb60ae9abb370305a218e2df650eb8d3ea021b1c151
KIND_NODE_IMAGE=kindest/node:v1.37.0@sha256:a1ed56cfb0e7b93589bdf97c8cd566405a265939e3620fc4f5de89adff580ae5
RELEASE_NAME=bookshelfng
NAMESPACE=default

RUNNER_POOL="${RUNNER_POOL:-}"
BUILDKIT_EGRESS_PROXY="${BUILDKIT_EGRESS_PROXY:-}"
if [[ "$RUNNER_POOL" == "public" ]]; then
    [[ -z "$BUILDKIT_EGRESS_PROXY" ]] || {
        printf 'The public hosted route must use direct egress, without the private JIT proxy.\n' >&2
        exit 1
    }
    unset HTTP_PROXY HTTPS_PROXY http_proxy https_proxy
elif [[ "$RUNNER_POOL" == "private" && "$BUILDKIT_EGRESS_PROXY" == "http://10.0.2.100:3128" ]]; then
    export HTTP_PROXY="$BUILDKIT_EGRESS_PROXY" HTTPS_PROXY="$BUILDKIT_EGRESS_PROXY"
    export http_proxy="$BUILDKIT_EGRESS_PROXY" https_proxy="$BUILDKIT_EGRESS_PROXY"
else
    printf 'Select the explicit public hosted pool or the trusted-main private JIT proxy route.\n' >&2
    exit 1
fi
export NO_PROXY=localhost,127.0.0.1 no_proxy=localhost,127.0.0.1

mkdir -p "$EVIDENCE_DIR"
exec > >(tee -a "$EVIDENCE_DIR/acceptance.log") 2>&1

log() {
    printf '[helm-installed-handoff] %s\n' "$*"
}

cleanup() {
    local status=$?
    trap - EXIT
    set +e
    if command -v kubectl >/dev/null 2>&1 && kubectl cluster-info >/dev/null 2>&1; then
        kubectl get all,pvc -A -o wide > "$EVIDENCE_DIR/resources.txt" 2>&1
        kubectl get events -A --sort-by=.metadata.creationTimestamp > "$EVIDENCE_DIR/events.txt" 2>&1
        while IFS= read -r pod; do
            [ -n "$pod" ] || continue
            name="${pod#*/}"
            kubectl logs -n "$NAMESPACE" "$name" --all-containers=true --prefix=true \
                > "$EVIDENCE_DIR/${name}.log" 2>&1
            kubectl logs -n "$NAMESPACE" "$name" --all-containers=true --previous --prefix=true \
                > "$EVIDENCE_DIR/${name}-previous.log" 2>&1
        done < <(kubectl get pods -n "$NAMESPACE" -o name 2>/dev/null)
        helm history "$RELEASE_NAME" -n "$NAMESPACE" > "$EVIDENCE_DIR/helm-history.txt" 2>&1
        kind export logs "$EVIDENCE_DIR/kind-logs" --name "$CLUSTER_NAME" >/dev/null 2>&1
    fi
    kind delete cluster --name "$CLUSTER_NAME" >/dev/null 2>&1
    docker rm -f "$REGISTRY_NAME" >/dev/null 2>&1
    exit "$status"
}
trap cleanup EXIT

require() {
    if ! "$@"; then
        log "FAILED: $*"
        exit 1
    fi
}

wait_running() {
    local pod="$1" phase="" attempt
    for attempt in $(seq 1 180); do
        phase="$(kubectl get pod "$pod" -n "$NAMESPACE" -o go-template='{{.status.phase}}' 2>/dev/null || true)"
        [ "$phase" = Running ] && return 0
        sleep 2
    done
    kubectl describe pod "$pod" -n "$NAMESPACE" || true
    return 1
}

wait_stopped() {
    local pod="$1" attempt
    for attempt in $(seq 1 150); do
        if ! kubectl get pod "$pod" -n "$NAMESPACE" >/dev/null 2>&1; then
            return 0
        fi
        sleep 2
    done
    kubectl describe pod "$pod" -n "$NAMESPACE" || true
    return 1
}

pod_exec() {
    kubectl exec -n "$NAMESPACE" bookshelfng-0 -c bookshelfng -- "$@"
}

assert_pvc_identity() {
    local actual_uid
    actual_uid="$(kubectl get pvc bookshelfng-config -n "$NAMESPACE" -o jsonpath='{.metadata.uid}')"
    require test "$actual_uid" = "$SOURCE_PVC_UID"
}

require_sql_count() {
    local description="$1" expected="$2" sql="$3" actual
    actual="$(pod_exec sqlite3 -readonly /config/readarr.db "$sql")"
    if [[ "$actual" != "$expected" ]]; then
        log "FAILED populated fixture assertion: $description expected=$expected actual=$actual SQL=$sql"
        return 1
    fi
}

pod_logical_hash() {
    pod_exec /bin/sh -ec '
      sqlite3 -readonly /config/readarr.db ".dump" > /tmp/bookshelfng-source.sql
      sha256sum /tmp/bookshelfng-source.sql | cut -d" " -f1
    '
}

pod_file_hash() {
    local path="$1"
    pod_exec sha256sum "$path" | cut -d' ' -f1
}

assert_ping() {
    local label="$1" body attempt
    local port_log="$EVIDENCE_DIR/port-forward-${label}.log"
    kubectl port-forward -n "$NAMESPACE" service/bookshelfng 18787:8787 > "$port_log" 2>&1 &
    local forward_pid=$!
    for attempt in $(seq 1 60); do
        if body="$(curl -fsS --max-time 3 http://127.0.0.1:18787/ping 2>/dev/null)"; then
            kill "$forward_pid" >/dev/null 2>&1 || true
            wait "$forward_pid" >/dev/null 2>&1 || true
            printf '%s\n' "$body" | tee "$EVIDENCE_DIR/ping-${label}.json"
            grep -Eq '"status"[[:space:]]*:[[:space:]]*"OK"' <<< "$body"
            return
        fi
        sleep 2
    done
    kill "$forward_pid" >/dev/null 2>&1 || true
    wait "$forward_pid" >/dev/null 2>&1 || true
    cat "$port_log"
    return 1
}

assert_source_file_hashes() {
    local expected_db="$1" expected_config="$2" expected_canary="$3"
    local actual_db actual_config actual_canary
    actual_db="$(pod_logical_hash)"
    actual_config="$(pod_file_hash /config/config.xml)"
    actual_canary="$(pod_file_hash /config/channel-test-canary.txt)"
    require test "$actual_db" = "$expected_db"
    require test "$actual_config" = "$expected_config"
    require test "$actual_canary" = "$expected_canary"
}

install_legacy() {
    # The zero-replica bootstrap intentionally leaves a WaitForFirstConsumer
    # claim pending until the fixture seed pod mounts it below.
    helm upgrade --install "$RELEASE_NAME" "$REFERENCE_CHART" \
        --namespace "$NAMESPACE" --create-namespace \
        --set replicaCount=0 \
        --set-string "image.repository=$LEGACY_IMAGE_REPOSITORY" \
        --set-string image.tag=hardcover-v0.4.21.76 \
        --set-string image.digest="$LEGACY_IMAGE_DIGEST" \
        --set image.pullPolicy=IfNotPresent
}

upgrade_candidate() {
    local mode="$1" replicas="$2"
    shift 2
    helm upgrade "$RELEASE_NAME" "$ROOT/charts/bookshelfng" \
        --namespace "$NAMESPACE" --reuse-values \
        --set bookshelfMigration.confirmed=true \
        --set bookshelfMigration.providerMode=hardcover \
        --set "bookshelfMigration.mode=$mode" \
        --set image.allowCustomRepository=true \
        --set-string image.repository="localhost:${REGISTRY_PORT}/chaptarrng" \
        --set-string image.digest="$CANDIDATE_DIGEST" \
        --set image.pullPolicy=IfNotPresent \
        --set "replicaCount=$replicas" \
        "$@"
}

assert_migrated_fixture() {
    log "Checking converted edition provider identity and narrator values:"
    pod_exec sqlite3 -readonly -header -column /config/readarr.db \
        'SELECT Id, BookId, ForeignEditionId, HardcoverEditionId, OpenLibraryEditionId, Narrator, quote(NarratorNames) AS NarratorNames, json_array_length(NarratorNames) AS NarratorCount, json_extract(NarratorNames, "$[0]") AS FirstNarrator FROM Editions WHERE Id IN (40,41) ORDER BY Id;' \
        | tee "$EVIDENCE_DIR/migrated-editions.txt"
    require_sql_count 'audiobook book identity, monitoring, and selected edition' 1 \
        "SELECT COUNT(*) FROM Books WHERE Id=30 AND MediaType=0 AND AudiobookMonitored=1 AND ForeignEditionId='hc:audio-40';"
    require_sql_count 'ebook book identity and unselected edition state' 1 \
        'SELECT COUNT(*) FROM Books WHERE Id=31 AND MediaType=1 AND EbookMonitored=0 AND ForeignEditionId IS NULL;'
    require_sql_count 'audiobook edition Hardcover provider identity' 1 \
        "SELECT COUNT(*) FROM Editions WHERE Id=40 AND BookId=30 AND HardcoverEditionId='audio-40';"
    require_sql_count 'audiobook edition narrator value' 1 \
        "SELECT COUNT(*) FROM Editions WHERE Id=40 AND BookId=30 AND json_valid(NarratorNames) AND json_array_length(NarratorNames)=1 AND json_extract(NarratorNames, '\$[0]')='A Narrator';"
    require_sql_count 'ebook edition provider identity' 1 \
        "SELECT COUNT(*) FROM Editions WHERE Id=41 AND BookId=31 AND OpenLibraryEditionId='ol:OL123M';"
    require_sql_count 'audiobook file path and catalog-search timestamp' 1 \
        "SELECT COUNT(*) FROM BookFiles WHERE Id=50 AND Path='/library/Fixture Author/book.m4b' AND LastRemoteSearchTime='2026-01-04T05:06:07Z';"
    require_sql_count 'ebook file path and media type' 1 \
        "SELECT COUNT(*) FROM BookFiles WHERE Id=51 AND MediaType='ebook' AND Path='/library/Fixture Author/book.epub';"
    require_sql_count 'series membership split across both media rows' 2 \
        'SELECT COUNT(*) FROM SeriesBookLink WHERE SeriesId=70 AND BookId IN (30,31);'
    require_sql_count 'download-client media categories' 1 \
        "SELECT COUNT(*) FROM DownloadClients WHERE Id=109 AND Settings LIKE '%legacy-audio%' AND Settings LIKE '%legacy-ebooks%';"
    require_sql_count 'saved custom configuration value' 1 \
        "SELECT COUNT(*) FROM Config WHERE Key='FixtureSetting' AND Value='preserved';"
}

test -d "$REFERENCE_CHART"
test -f "$REFERENCE_CHART/Chart.yaml"
test -f "$ROOT/src/Chaptarr.Core.Test/Datastore/Fixtures/BookshelfNG.Schema.158.sql"
test -f "$ROOT/src/Chaptarr.Core.Test/Datastore/Fixtures/BookshelfNG.PopulatedMigration158.sql"

log "Pulling pinned BookshelfNG .76 candidate $LEGACY_IMAGE_REF"
[[ "$LEGACY_IMAGE_DIGEST" =~ ^sha256:[0-9a-f]{64}$ ]]
docker pull "$LEGACY_IMAGE_REPOSITORY@$LEGACY_IMAGE_DIGEST"
printf 'BookshelfNG image: %s@%s (%s)\n' "$LEGACY_IMAGE_REPOSITORY" "$LEGACY_IMAGE_DIGEST" "$LEGACY_IMAGE_REF" \
    | tee "$EVIDENCE_DIR/image-provenance.txt"
printf 'Kind node image: %s\nRegistry image: %s\n' "$KIND_NODE_IMAGE" "$REGISTRY_IMAGE" \
    | tee -a "$EVIDENCE_DIR/image-provenance.txt"

log "Creating isolated Kind cluster and local OCI registry"
docker run --detach --restart=always --publish "127.0.0.1:${REGISTRY_PORT}:5000" \
    --network bridge --name "$REGISTRY_NAME" "$REGISTRY_IMAGE"
kind create cluster --name "$CLUSTER_NAME" --image "$KIND_NODE_IMAGE" --config=- --wait 180s <<'YAML'
kind: Cluster
apiVersion: kind.x-k8s.io/v1alpha4
containerdConfigPatches:
  - |-
    [plugins."io.containerd.grpc.v1.cri".registry]
      config_path = "/etc/containerd/certs.d"
YAML
for node in $(kind get nodes --name "$CLUSTER_NAME"); do
    registry_dir="/etc/containerd/certs.d/localhost:${REGISTRY_PORT}"
    docker exec "$node" mkdir -p "$registry_dir"
    printf '[host."http://%s:5000"]\n' "$REGISTRY_NAME" \
        | docker exec -i "$node" sh -ec "cat > '$registry_dir/hosts.toml'"
done
if [ "$(docker inspect -f='{{json .NetworkSettings.Networks.kind}}' "$REGISTRY_NAME")" = null ]; then
    docker network connect kind "$REGISTRY_NAME"
fi
kubectl apply -f - <<YAML
apiVersion: v1
kind: ConfigMap
metadata:
  name: local-registry-hosting
  namespace: kube-public
data:
  localRegistryHosting.v1: |
    host: "localhost:${REGISTRY_PORT}"
    help: "https://kind.sigs.k8s.io/docs/user/local-registry/"
YAML

candidate_tag="candidate-${GITHUB_SHA:-local}"
candidate_tagged_image="localhost:${REGISTRY_PORT}/chaptarrng:${candidate_tag}"
log "Building ChaptarrNG at ${GITHUB_SHA:-local} as the standard image; the chart selects Hardcover mode"
docker build --platform linux/amd64 --file "$ROOT/Dockerfile.cross" \
    --build-arg "HTTP_PROXY=${BUILDKIT_EGRESS_PROXY}" \
    --build-arg "HTTPS_PROXY=${BUILDKIT_EGRESS_PROXY}" \
    --build-arg "http_proxy=${BUILDKIT_EGRESS_PROXY}" \
    --build-arg "https_proxy=${BUILDKIT_EGRESS_PROXY}" \
    --build-arg NO_PROXY=localhost,127.0.0.1 \
    --build-arg no_proxy=localhost,127.0.0.1 \
    --target chaptarrng-standard --tag "$candidate_tagged_image" "$ROOT"
docker push "$candidate_tagged_image" 2>&1 | tee "$EVIDENCE_DIR/docker-push.log"
CANDIDATE_DIGEST="$(awk '$1 == "digest:" { digest=$2 } $2 == "digest:" { digest=$3 } END { print digest }' \
    "$EVIDENCE_DIR/docker-push.log" | tail -n 1)"
[[ "$CANDIDATE_DIGEST" =~ ^sha256:[0-9a-f]{64}$ ]]
CANDIDATE_IMAGE="localhost:${REGISTRY_PORT}/chaptarrng@${CANDIDATE_DIGEST}"
printf 'ChaptarrNG image: %s\n' "$CANDIDATE_IMAGE" | tee -a "$EVIDENCE_DIR/image-provenance.txt"

log "Installing the pinned BookshelfNG chart with the public .76 image and an empty persistent claim"
install_legacy

kubectl create configmap bookshelfng-schema-fixture -n "$NAMESPACE" \
    --from-file=schema.sql="$ROOT/src/Chaptarr.Core.Test/Datastore/Fixtures/BookshelfNG.Schema.158.sql" \
    --from-file=populated.sql="$ROOT/src/Chaptarr.Core.Test/Datastore/Fixtures/BookshelfNG.PopulatedMigration158.sql" \
    --dry-run=client -o yaml | kubectl apply -f -
cat > "$EVIDENCE_DIR/seed-pod.yaml" <<YAML
apiVersion: v1
kind: Pod
metadata:
  name: bookshelfng-seed
  namespace: ${NAMESPACE}
spec:
  restartPolicy: Never
  securityContext:
    # The chart starts as root and normalizes PUID/PGID ownership. Match that
    # bootstrap here so the seed can initialize a fresh root-owned local-path PVC.
    runAsUser: 0
    runAsGroup: 0
  containers:
    - name: seed
      image: ${CANDIDATE_IMAGE}
      imagePullPolicy: IfNotPresent
      command: ["/bin/sh", "-ec"]
      args:
        - |
          printf 'Seed identity: '
          id
          stat -c 'config mount: mode=%a owner=%u:%g type=%F' /config
          grep ' /config ' /proc/mounts || true
          test -w /config
          : > /config/.bookshelfng-seed-write-check
          rm -f /config/.bookshelfng-seed-write-check
          sqlite3 /config/readarr.db < /fixture/schema.sql
          sqlite3 /config/readarr.db < /fixture/populated.sql
          printf '%s\\n' '<?xml version="1.0" encoding="utf-8"?><Config><BindAddress>0.0.0.0</BindAddress><Port>8787</Port><EnableSsl>False</EnableSsl><UrlBase></UrlBase><LaunchBrowser>False</LaunchBrowser></Config>' > /config/config.xml
          printf '%s\\n' 'helm-handoff-canary=preserve-me' > /config/channel-test-canary.txt
          test "\$(sqlite3 -readonly /config/readarr.db 'PRAGMA integrity_check;')" = ok
          test "\$(sqlite3 -readonly /config/readarr.db 'SELECT MAX(Version) FROM VersionInfo;')" = 158
          test "\$(sqlite3 -readonly /config/readarr.db 'SELECT COUNT(*) > 0 FROM Authors;')" = 1
          test "\$(sqlite3 -readonly /config/readarr.db 'SELECT COUNT(*) > 0 FROM Books;')" = 1
          test "\$(sqlite3 -readonly /config/readarr.db 'SELECT COUNT(*) > 0 FROM Editions;')" = 1
          echo 'Populated BookshelfNG schema 158 fixture seeded with config and canary.'
      volumeMounts:
        - name: config
          mountPath: /config
        - name: fixture
          mountPath: /fixture
          readOnly: true
  volumes:
    - name: config
      persistentVolumeClaim:
        claimName: bookshelfng-config
    - name: fixture
      configMap:
        name: bookshelfng-schema-fixture
YAML
kubectl apply -f "$EVIDENCE_DIR/seed-pod.yaml"
if ! kubectl wait --for=jsonpath='{.status.phase}'=Succeeded pod/bookshelfng-seed -n "$NAMESPACE" --timeout=5m; then
    kubectl describe pod bookshelfng-seed -n "$NAMESPACE" 2>&1 \
        | tee "$EVIDENCE_DIR/seed-pod-describe.txt" || true
    kubectl get pod bookshelfng-seed -n "$NAMESPACE" -o yaml \
        > "$EVIDENCE_DIR/seed-pod-status.yaml" 2>&1 || true
    kubectl logs pod/bookshelfng-seed -n "$NAMESPACE" --all-containers=true 2>&1 \
        | tee "$EVIDENCE_DIR/fixture-seed-failed.log" || true
    exit 1
fi
kubectl logs -n "$NAMESPACE" pod/bookshelfng-seed | tee "$EVIDENCE_DIR/fixture-seed.log"
kubectl delete pod bookshelfng-seed -n "$NAMESPACE" --wait=true
SOURCE_PVC_UID="$(kubectl get pvc bookshelfng-config -n "$NAMESPACE" -o jsonpath='{.metadata.uid}')"
[[ "$SOURCE_PVC_UID" =~ ^[0-9a-f-]{36}$ ]]
printf 'Source PVC UID: %s\n' "$SOURCE_PVC_UID" | tee -a "$EVIDENCE_DIR/image-provenance.txt"

log "Starting the actual BookshelfNG .76 service against the populated fixture"
helm upgrade "$RELEASE_NAME" "$REFERENCE_CHART" --namespace "$NAMESPACE" \
    --reuse-values --set replicaCount=1 --wait --timeout 15m
kubectl rollout status -n "$NAMESPACE" statefulset/bookshelfng --timeout=15m
assert_ping bookshelfng-076
legacy_revision="$(helm history "$RELEASE_NAME" -n "$NAMESPACE" -o json \
    | ruby -rjson -e 'puts JSON.parse(STDIN.read).select { |entry| entry["status"] == "deployed" }.last.fetch("revision")')"
printf 'BookshelfNG rollback revision: %s\n' "$legacy_revision" | tee -a "$EVIDENCE_DIR/image-provenance.txt"
helm upgrade "$RELEASE_NAME" "$REFERENCE_CHART" --namespace "$NAMESPACE" \
    --reuse-values --set replicaCount=0 --wait --timeout 10m
wait_stopped bookshelfng-0

log "Taking the exact pre-handoff database/config/canary fingerprints"
cat > "$EVIDENCE_DIR/snapshot-pod.yaml" <<YAML
apiVersion: v1
kind: Pod
metadata:
  name: bookshelfng-snapshot
  namespace: ${NAMESPACE}
spec:
  restartPolicy: Never
  securityContext:
    # BookshelfNG owns the installed claim using its configured Linux user;
    # inspect the stopped source as root without changing its ownership.
    runAsUser: 0
    runAsGroup: 0
  containers:
    - name: snapshot
      image: ${CANDIDATE_IMAGE}
      imagePullPolicy: IfNotPresent
      command: ["/bin/sh", "-ec"]
      args:
        - |
          sqlite3 -readonly /config/readarr.db 'PRAGMA integrity_check;' | grep -Fx ok
          test "\$(sqlite3 -readonly /config/readarr.db 'SELECT MAX(Version) FROM VersionInfo;')" = 158
          sqlite3 -readonly /config/readarr.db .dump > /tmp/readarr-source.sql
          printf 'SOURCE_SQL_DUMP_SHA256=%s\\n' "\$(sha256sum /tmp/readarr-source.sql | cut -d' ' -f1)"
          printf 'CONFIG_SHA256=%s\\n' "\$(sha256sum /config/config.xml | cut -d' ' -f1)"
          printf 'CANARY_SHA256=%s\\n' "\$(sha256sum /config/channel-test-canary.txt | cut -d' ' -f1)"
          grep -Fx 'helm-handoff-canary=preserve-me' /config/channel-test-canary.txt
      volumeMounts:
        - name: config
          mountPath: /config
  volumes:
    - name: config
      persistentVolumeClaim:
        claimName: bookshelfng-config
YAML
kubectl apply -f "$EVIDENCE_DIR/snapshot-pod.yaml"
if ! kubectl wait --for=jsonpath='{.status.phase}'=Succeeded pod/bookshelfng-snapshot -n "$NAMESPACE" --timeout=5m; then
    kubectl describe pod bookshelfng-snapshot -n "$NAMESPACE" 2>&1 \
        | tee "$EVIDENCE_DIR/snapshot-pod-describe.txt" || true
    kubectl get pod bookshelfng-snapshot -n "$NAMESPACE" -o yaml \
        > "$EVIDENCE_DIR/snapshot-pod-status.yaml" 2>&1 || true
    kubectl logs pod/bookshelfng-snapshot -n "$NAMESPACE" --all-containers=true 2>&1 \
        | tee "$EVIDENCE_DIR/snapshot-pod-failed.log" || true
    exit 1
fi
snapshot_output="$(kubectl logs -n "$NAMESPACE" pod/bookshelfng-snapshot)"
printf '%s\n' "$snapshot_output" | tee "$EVIDENCE_DIR/source-before-handoff.txt"
SOURCE_SQL_DUMP_SHA256="$(sed -n 's/^SOURCE_SQL_DUMP_SHA256=//p' <<< "$snapshot_output")"
CONFIG_SHA256="$(sed -n 's/^CONFIG_SHA256=//p' <<< "$snapshot_output")"
CANARY_SHA256="$(sed -n 's/^CANARY_SHA256=//p' <<< "$snapshot_output")"
[[ "$SOURCE_SQL_DUMP_SHA256" =~ ^[0-9a-f]{64}$ && "$CONFIG_SHA256" =~ ^[0-9a-f]{64}$ && "$CANARY_SHA256" =~ ^[0-9a-f]{64}$ ]]
kubectl delete pod bookshelfng-snapshot -n "$NAMESPACE" --wait=true

log "Upgrading the existing Helm release in manual mode and checking that the source remains byte-for-byte/logically unchanged"
upgrade_candidate manual 1
wait_running bookshelfng-0
kubectl logs -n "$NAMESPACE" pod/bookshelfng-0 -c bookshelfng | tee "$EVIDENCE_DIR/manual-mode.log"
grep -Fq 'deferred by BOOKSHELFNG_MIGRATION_MODE' "$EVIDENCE_DIR/manual-mode.log"
assert_pvc_identity
assert_source_file_hashes "$SOURCE_SQL_DUMP_SHA256" "$CONFIG_SHA256" "$CANARY_SHA256"
test "$(pod_exec sqlite3 -readonly /config/readarr.db 'SELECT MAX(Version) FROM VersionInfo;')" = 158
log "Manual mode preserved schema 158, config, and canary."

log "Stopping the deferred candidate while keeping the same Helm claim before switching to automatic mode"
upgrade_candidate manual 0 --wait --timeout 15m
wait_stopped bookshelfng-0
assert_pvc_identity

log "Choosing automatic migration against the same installed Helm release and persistent volume"
upgrade_candidate automatic 1 --wait --timeout 30m
kubectl rollout status -n "$NAMESPACE" statefulset/bookshelfng --timeout=30m
pod_exec /usr/local/bin/chaptarr-healthcheck
assert_ping chaptarrng-migrated
assert_pvc_identity
test "$(pod_exec sqlite3 -readonly /config/readarr.db 'PRAGMA integrity_check;')" = ok
test -z "$(pod_exec sqlite3 -readonly /config/readarr.db 'PRAGMA foreign_key_check;')"
test "$(pod_exec sqlite3 -readonly /config/readarr.db 'SELECT MAX(Version) FROM VersionInfo;')" = 125
assert_migrated_fixture
grep -Fxq 'helm-handoff-canary=preserve-me' < <(pod_exec cat /config/channel-test-canary.txt)
pod_exec /bin/sh -ec '
  expected=$1
  found=0
  for backup in /config/readarr.db.bookshelfng-backup-*.db; do
    [ -f "$backup" ] || continue
    version="$(sqlite3 -readonly "$backup" "SELECT MAX(Version) FROM VersionInfo;")"
    [ "$version" = 158 ] || continue
    sqlite3 -readonly "$backup" .dump > /tmp/bookshelfng-backup.sql
    digest="$(sha256sum /tmp/bookshelfng-backup.sql | cut -d" " -f1)"
    if [ "$digest" = "$expected" ]; then found=1; fi
  done
  test "$found" = 1
' _ "$SOURCE_SQL_DUMP_SHA256"
log "Automatic migration preserved populated rows, passed database checks, and retained the exact schema-158 source backup."

log "Running the chart's confirmed SQLite restore Job, then reopening the pinned BookshelfNG revision"
upgrade_candidate automatic 0 \
    --set bookshelfMigration.rollback.enabled=true \
    --set bookshelfMigration.rollback.confirmed=true \
    --set bookshelfMigration.rollback.databaseType=sqlite \
    --wait --wait-for-jobs --timeout=30m
kubectl get jobs -n "$NAMESPACE" -o wide | tee "$EVIDENCE_DIR/rollback-jobs.txt"
helm rollback "$RELEASE_NAME" "$legacy_revision" -n "$NAMESPACE" --wait --timeout 15m
kubectl rollout status -n "$NAMESPACE" statefulset/bookshelfng --timeout=15m
assert_ping bookshelfng-after-rollback
assert_pvc_identity
kubectl scale statefulset/bookshelfng -n "$NAMESPACE" --replicas=0 --timeout=5m
wait_stopped bookshelfng-0
kubectl apply -f "$EVIDENCE_DIR/snapshot-pod.yaml"
if ! kubectl wait --for=jsonpath='{.status.phase}'=Succeeded pod/bookshelfng-snapshot -n "$NAMESPACE" --timeout=5m; then
    kubectl describe pod bookshelfng-snapshot -n "$NAMESPACE" 2>&1 \
        | tee "$EVIDENCE_DIR/rollback-snapshot-pod-describe.txt" || true
    kubectl logs pod/bookshelfng-snapshot -n "$NAMESPACE" --all-containers=true 2>&1 \
        | tee "$EVIDENCE_DIR/rollback-snapshot-pod-failed.log" || true
    exit 1
fi
rollback_snapshot_output="$(kubectl logs -n "$NAMESPACE" pod/bookshelfng-snapshot)"
printf '%s\n' "$rollback_snapshot_output" | tee "$EVIDENCE_DIR/source-after-rollback.txt"
RESTORED_SQL_DUMP_SHA256="$(sed -n 's/^SOURCE_SQL_DUMP_SHA256=//p' <<< "$rollback_snapshot_output")"
RESTORED_CONFIG_SHA256="$(sed -n 's/^CONFIG_SHA256=//p' <<< "$rollback_snapshot_output")"
RESTORED_CANARY_SHA256="$(sed -n 's/^CANARY_SHA256=//p' <<< "$rollback_snapshot_output")"
test "$RESTORED_SQL_DUMP_SHA256" = "$SOURCE_SQL_DUMP_SHA256"
test "$RESTORED_CONFIG_SHA256" = "$CONFIG_SHA256"
test "$RESTORED_CANARY_SHA256" = "$CANARY_SHA256"
kubectl delete pod bookshelfng-snapshot -n "$NAMESPACE" --wait=true

printf 'RESULT=PASS\nBOOKSHELFNG_IMAGE=%s@%s\nCHAPTARRNG_IMAGE=%s\nSOURCE_PVC_UID=%s\nSOURCE_SQL_DUMP_SHA256=%s\nRESTORED_SQL_DUMP_SHA256=%s\nCONFIG_SHA256=%s\nRESTORED_CONFIG_SHA256=%s\nCANARY_SHA256=%s\nRESTORED_CANARY_SHA256=%s\nROLLBACK_REVISION=%s\n' \
    "docker.io/snapetech/bookshelfng" "$LEGACY_IMAGE_DIGEST" "$CANDIDATE_IMAGE" \
    "$SOURCE_PVC_UID" "$SOURCE_SQL_DUMP_SHA256" "$RESTORED_SQL_DUMP_SHA256" "$CONFIG_SHA256" "$RESTORED_CONFIG_SHA256" \
    "$CANARY_SHA256" "$RESTORED_CANARY_SHA256" "$legacy_revision" \
    | tee "$EVIDENCE_DIR/acceptance-result.txt"
log "Installed Helm No/Yes, populated-data, health, backup, chart-restore, and BookshelfNG recovery acceptance passed."
