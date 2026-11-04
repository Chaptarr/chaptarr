import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const preservedBookshelfEnvironment = [
  'HARDCOVER',
  'HARDCOVER_NATIVE',
  'HARDCOVER_AUTH',
  'HARDCOVER_API_KEY',
  'HARDCOVER_API_URL',
  'HARDCOVER_APIFY_TOKEN',
  'HARDCOVER_APIFY_GOODREADS_ACTOR',
  'HARDCOVER_APIFY_GOODREADS_INPUT_TEMPLATE',
  'BOOKSHELF_ALLOW_API_KEY_QUERY',
  'BOOKSHELF_DIAGNOSTICS_ENABLED',
  'BOOKSHELF_DIAGNOSTICS_AUTH_TOKEN',
  'BOOKSHELF_DIAGNOSTICS_OTLP_ENDPOINT',
  'BOOKSHELF_METADATA_SOURCES',
  'GOOGLE_BOOKS_API_KEY',
  'EUROPEANA_API_KEY',
  'OPEN_LIBRARY_CONTACT_EMAIL',
  'BOOKSHELF_OIDC_ISSUER_URL',
  'BOOKSHELF_OIDC_CLIENT_ID',
  'BOOKSHELF_OIDC_CLIENT_SECRET',
  'BOOKSHELF_OIDC_REDIRECT_URI',
  'BOOKSHELF_OIDC_SCOPES',
  'BOOKSHELF_OIDC_ALLOWED_EMAILS',
  'BOOKSHELF_OIDC_ALLOWED_EMAIL_DOMAINS',
  'BOOKSHELF_OIDC_ALLOW_ANY_VERIFIED_USER',
  'BOOKSHELF_OIDC_ALLOW_PRIVATE_ISSUER',
  'BOOKSHELF_OIDC_PROVIDER',
  'BOOKSHELF_SEERRNG_API_KEY',
  'BOOKSHELF_M4B_MERGE',
  'BOOKSHELF_M4B_AAC_BITRATE_KBPS',
  'BOOKSHELF_FFMPEG_PATH',
  'METADATA_URL',
  'PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH',
  'PUID',
  'PGID',
  'TZ',
  'MAX_PATH',
  'MAX_NAME',
];
const legacyApplicationPrefixes = ['BOOKSHELF', 'AUDIOARR', 'READARR'];
const legacyAppSettings = ['INSTANCENAME', 'THEME', 'LAUNCHBROWSER'];
const legacyAuthSettings = ['APIKEY', 'ENABLED', 'METHOD', 'REQUIRED', 'TRUSTCGNATIPADDRESSES'];
const legacyServerSettings = [
  'PORT', 'URLBASE', 'BINDADDRESS', 'ENABLESSL', 'SSLPORT', 'SSLCERTPATH', 'SSLCERTPASSWORD',
  'REQUESTSPERMINUTE', 'TRUSTEDPROXIES', 'ALLOWEDORIGINS', 'MAXREQUESTBODYSIZE',
];
const legacyLogSettings = ['LEVEL', 'ROTATE', 'SQL', 'CONSOLELEVEL', 'SYSLOGSERVER', 'SYSLOGPORT', 'SYSLOGLEVEL'];
const legacyUpdateSettings = ['MECHANISM', 'AUTOMATICALLY', 'SCRIPTPATH', 'BRANCH'];
const legacyPostgresSettings = [
  'HOST', 'PORT', 'USER', 'PASSWORD', 'MAINDB', 'LOGDB', 'CACHEDB', 'SSLMODE', 'ROOTCERT', 'NORESETONCLOSE',
];
const legacySectionEnvironment = legacyApplicationPrefixes.flatMap((prefix) => [
  ...legacyAppSettings.map((setting) => `${prefix}__APP__${setting}`),
  ...legacyAuthSettings.map((setting) => `${prefix}__AUTH__${setting}`),
  ...legacyServerSettings.map((setting) => `${prefix}__SERVER__${setting}`),
  ...legacyLogSettings.map((setting) => `${prefix}__LOG__${setting}`),
  ...legacyUpdateSettings.map((setting) => `${prefix}__UPDATE__${setting}`),
  ...legacyPostgresSettings.map((setting) => `${prefix}__POSTGRES__${setting}`),
]);

function read(relativePath) {
  return normalizeLineEndings(fs.readFileSync(path.join(repoRoot, relativePath), 'utf8'));
}

function normalizeLineEndings(contents) {
  return contents.replace(/\r\n/g, '\n');
}

function readWorkflowJob(workflow, name) {
  workflow = normalizeLineEndings(workflow);
  const marker = `\n  ${name}:\n`;
  const markerIndex = workflow.indexOf(marker);
  assert.notEqual(markerIndex, -1, `workflow job ${name} exists`);
  const start = markerIndex + 1;
  const nextJob = /\n  [a-z0-9-]+:\n/g;
  nextJob.lastIndex = start + marker.length - 1;
  const match = nextJob.exec(workflow);
  return workflow.slice(start, match ? match.index : workflow.length);
}

test('release workflow job extraction accepts Windows CRLF checkouts', () => {
  const workflow = read('.github/workflows/release.yml').replace(/\n/g, '\r\n');
  assert.match(readWorkflowJob(workflow, 'legacy-desktop-candidates'), /needs: \[release, standalone-assets\]/);
});

test('Windows Chocolatey release downloads private assets through the authenticated GitHub API', () => {
  const workflow = read('.github/workflows/release-distribution.yml');
  const job = readWorkflowJob(workflow, 'build-chocolatey');

  assert.match(job, /api\.github\.com\/repos\/\$env:GITHUB_REPOSITORY\/releases\/tags/);
  assert.match(job, /Authorization = \"Bearer \$env:GH_TOKEN\"/);
  assert.match(job, /\$apiUri = \[System\.Uri\]\$assets\[0\]\.url/);
  assert.match(job, /Accept = 'application\/octet-stream'/);
  assert.match(job, /Invoke-WebRequest -Uri \$apiUri\.AbsoluteUri -Headers \$assetHeaders -OutFile \$destination/);
  assert.match(job, /\$release\.draft -or \$release\.prerelease/);
  assert.match(job, /Get-FileHash .* -Algorithm SHA256/);
  assert.match(job, /Copy-Item packaging\/legacy\/chocolatey\/tools\/bookshelfng-startup-recovery\.ps1/,
    'the release package stages the recovery script required for stopped-service migrations');
  assert.doesNotMatch(job, /gh release download/);
  assert.doesNotMatch(job, /Invoke-WebRequest -Uri \$downloadUri\.AbsoluteUri -OutFile/);
});

test('AppImage keeps its desktop identity, BookshelfNG data directory, and Chaptarr launcher', () => {
  const appRun = read('packaging/legacy/appimage/AppRun');
  const desktop = read('packaging/legacy/appimage/com.snapetech.bookshelfng.desktop');

  assert.match(desktop, /^Name=ChaptarrNG$/m);
  assert.match(desktop, /^Exec=AppRun$/m);
  assert.match(appRun, /XDG_DATA_HOME:-\$HOME\/\.local\/share\}\/bookshelfng/);
  assert.match(appRun, /exec "\$HERE\/usr\/libexec\/bookshelfng-supervisor\.sh"/);
  assert.match(appRun, /bookshelfng-supervisor\.sh.*Chaptarr.*DATA_DIR/s);
  const supervisor = read('packaging/legacy/desktop/bookshelfng-supervisor.sh');
  assert.match(supervisor, /-preview-bookshelf-migration/);
  assert.match(supervisor, /-restore-bookshelf/);
  assert.match(supervisor, /-bookshelf-health-url=/);
  assert.match(supervisor, /-bookshelf-health-timeout-seconds=/);
  assert.match(supervisor, /custom_data_argument/);
  assert.match(supervisor, /expect_data_value/);
  assert.match(supervisor, /health_authority/);
  assert.match(supervisor, /setsid/);
  assert.match(supervisor, /--wait/);
  assert.match(supervisor, /app_pid_file/);
  assert.match(supervisor, /terminate_app_tree/);
  assert.match(supervisor, /BOOKSHELFNG_RETRY_MIGRATION/);
  assert.match(supervisor, /lock_owner_is_running/);
  assert.match(supervisor, /Z\*/);
  assert.match(supervisor, /bookshelfng-journal/);
  assert.match(supervisor, /channel-state/);
  assert.match(appRun, /HARDCOVER="\$\{HARDCOVER:-false\}"/);
  assert.match(appRun, /CHAPTARR_PACKAGE_UPDATE_MECHANISM:-External/);
  assert.match(appRun, /Replace this AppImage with a newer compatible build/);
});

test('macOS and FreeBSD standalone archives include the optional, recoverable BookshelfNG handoff', () => {
  const builder = read('packaging/scripts/build-standalone.sh');
  const launcher = read('scripts/bookshelfng-standalone-launch.sh');
  const supervisor = read('packaging/legacy/desktop/bookshelfng-supervisor.sh');
  const migrationGuide = read('docs/MIGRATE_FROM_BOOKSHELFNG.md');
  const handoffTest = read('packaging/scripts/test-bookshelf-standalone-handoff.py');

  assert.match(builder, /publish_standalone_project\(\)[\s\S]*?if \[\[ "\$rid" == linux-x86 \|\| "\$rid" == freebsd-x64 \]\][\s\S]*?dotnet publish "\$@" "\$\{restore_sources\[@\]\}"/,
    'macOS Bash can publish standalone RIDs without expanding an empty array under nounset');
  assert.match(builder, /if \[\[ "\$\(uname -s\)" == Darwin \]\][\s\S]*?tar -czf/,
    'macOS uses its supported BSD tar options');
  assert.match(builder, /elif command -v shasum[\s\S]*?shasum -a 256/,
    'macOS creates the candidate checksum with its system utility');
  assert.ok(builder.includes('if [[ "$rid" == osx-* || "$rid" == freebsd-x64 ]]'));
  assert.match(builder, /bookshelfng-standalone-launch\.sh/);
  assert.match(builder, /bookshelfng-supervisor\.sh/);
  assert.match(launcher, /has_controlling_tty\(\) \{\s*\[ -t 0 \] && \( : <\/dev\/tty >\/dev\/tty \)/);
  assert.match(launcher, /if has_controlling_tty; then[\s\S]*?BOOKSHELFNG_MIGRATION_MODE=ask[\s\S]*?BOOKSHELFNG_MIGRATION_MODE=manual/);
  assert.match(launcher, /uname -s[\s\S]*FreeBSD[\s\S]*DOTNET_OPENSSL_VERSION_OVERRIDE:-[\s\S]*libssl\.so\.17[\s\S]*DOTNET_OPENSSL_VERSION_OVERRIDE=17[\s\S]*libssl\.so\.35[\s\S]*DOTNET_OPENSSL_VERSION_OVERRIDE=35/,
    'FreeBSD selects an installed OpenSSL ABI when the operator did not set an override');
  assert.match(launcher, /libsqlite3\.so\.0[\s\S]*sqlite_library=\$candidate/);
  assert.match(launcher, /ln -s "\$sqlite_library" "\$bundle_dir\/libe_sqlite3\.so"/);
  assert.match(launcher, /Install the sqlite3 package/,
    'FreeBSD tells operators how to restore the SQLite runtime prerequisite');
  assert.match(launcher, /standalone-archive/);
  assert.match(migrationGuide, /bookshelfng-standalone-launch\.sh/);
  assert.match(migrationGuide, /running[\s\S]*Chaptarr[\s\S]*directly skips the optional handoff prompt/);
  assert.ok(supervisor.includes('if [ ! -x "$console" ]; then'));
  assert.match(supervisor, /console="\$app"/);
  assert.match(supervisor, /isolated_app_process=false/);
  assert.match(supervisor, /setsid --wait/);
  assert.ok(
    supervisor.indexOf('if has_controlling_tty; then') < supervisor.indexOf('elif command -v zenity'),
    'interactive terminal launches receive the y/N prompt even when a desktop session has zenity installed',
  );
  assert.match(supervisor, /has_controlling_tty\(\) \{\s*\[ -t 0 \] && \( : <\/dev\/tty >\/dev\/tty \)/,
    'a permission-only /dev/tty check does not classify a headless package process as interactive');
  assert.match(supervisor, /-restore-bookshelf/);
  assert.match(supervisor, /-bookshelf-health-url=/);
  assert.match(handoffTest, /def child_process_ids\([\s\S]*?def stop_candidate\([\s\S]*?os\.kill\(pid, signal\.SIGTERM\)/,
    'standalone acceptance cleanup stops app descendants directly on platforms without setsid --wait');
  assert.match(handoffTest, /def stop_candidate\([\s\S]*?os\.close\(master\)/,
    'standalone acceptance cleanup closes the controlling PTY so macOS can deliver hangup to its shell');
});

test('macOS installed handoff acceptance supports hosted and private runners without publication', () => {
  const workflow = read('.github/workflows/bookshelfng-standalone-macos-acceptance.yml');

  assert.match(workflow, /^  workflow_dispatch:$/m);
  assert.match(workflow, /default: public[\s\S]*?options: \[public, private\]/);
  assert.match(workflow, /architecture:[\s\S]*?default: all[\s\S]*?options: \[all, x64, arm64\]/);
  assert.match(workflow, /matrix:[\s\S]*?fromJSON\([\s\S]*?inputs\.architecture == 'all'[\s\S]*?inputs\.architecture == 'x64'/);
  assert.match(workflow, /"rid":"osx-x64","architecture":"X64","predecessor_version":"0\.4\.21\.76"/);
  assert.match(workflow, /"rid":"osx-arm64","architecture":"ARM64","predecessor_version":"0\.4\.21\.76"/);
  assert.match(workflow, /'macos-15-intel'/);
  assert.match(workflow, /'macos-15'/);
  assert.match(workflow, /"group":"private-apple-builds","labels":\["self-hosted","macOS","X64","private-apple"/);
  assert.match(workflow, /"group":"private-apple-builds","labels":\["self-hosted","macOS","ARM64","private-apple"/);
  assert.match(workflow, /build-standalone\.sh "\$version" "\$\{\{ matrix\.rid \}\}"/);
  assert.match(workflow, /BookshelfNG-v\$\{\{ matrix\.predecessor_version \}\}-\$\{\{ matrix\.rid \}\}\.tar\.gz/);
  assert.match(workflow, /predecessor_version":"0\.4\.21\.76"/);
  assert.match(workflow, /4f660a1367e28c01660c2e082c4f1778f652cca34affc0dba55d3f47ef67f747/);
  assert.match(workflow, /d964bb1146b6c9e313758a967815f53f8d9aa45314bc30cfa55e96bb299433ce/);
  assert.match(workflow, /test-bookshelf-standalone-handoff\.py/);
  assert.doesNotMatch(workflow, /upload-artifact|gh release (create|upload)|docker push/);
});

test('Helm handoff workflow supports hosted and private runners for its pinned Ruby contract', () => {
  const workflow = read('.github/workflows/helm-handoff.yml');
  const installedHandoff = read('scripts/test_bookshelfng_helm_install_handoff.sh');

  assert.match(workflow, /default: public[\s\S]*?options:[\s\S]*?- public[\s\S]*?- private/);
  assert.match(workflow, /'ubuntu-24\.04'/);
  assert.match(workflow, /"group":"ng-shared-main-jit"/);
  assert.match(workflow, /RUNNER_POOL: \$\{\{ inputs\.runner_pool \}\}/);
  assert.match(installedHandoff, /if \[\[ "\$RUNNER_POOL" == "public" \]\][\s\S]*?unset HTTP_PROXY HTTPS_PROXY http_proxy https_proxy/);
  assert.match(installedHandoff, /elif \[\[ "\$RUNNER_POOL" == "private" && "\$BUILDKIT_EGRESS_PROXY" == "http:\/\/10\.0\.2\.100:3128" \]\]/);
  assert.match(installedHandoff, /--target chaptarrng-standard/);
  assert.doesNotMatch(installedHandoff, /--target bookshelf-(?:softcover|hardcover)/);
  assert.match(workflow, /Prepare writable Ruby tool cache[\s\S]*?sudo install -d .*\/opt\/hostedtoolcache[\s\S]*?test -w \/opt\/hostedtoolcache[\s\S]*?ruby\/setup-ruby@824c0e29fdfc7d689c191c27acc969d2d04b7dd8/);
  assert.match(workflow, /ruby-version: "3\.4"/);
  assert.match(workflow, /BOOKSHELFNG_REFERENCE_CHART: \$\{\{ github\.workspace \}\}\/bookshelfng-reference\/charts\/bookshelfng/);
  assert.match(workflow, /run: ruby scripts\/test_bookshelfng_helm_handoff\.rb/);
  assert.doesNotMatch(workflow, /docker run|ruby:3\.4-bookworm/);
});

test('Helm installed handoff extracts Buildx digests from both Docker push formats', () => {
  const installedHandoff = read('scripts/test_bookshelfng_helm_install_handoff.sh');
  const awkProgram = installedHandoff.match(/awk '([^']+)' \\\n    "\$EVIDENCE_DIR\/docker-push\.log"/)?.[1];
  assert.ok(awkProgram, 'the installed handoff must retain its Docker push digest parser');

  const digest = `sha256:${'a'.repeat(64)}`;
  for (const pushOutput of [
    `digest: ${digest}`,
    `candidate-f46bd718: digest: ${digest} size: 6187`,
  ]) {
    const result = spawnSync('awk', [awkProgram], { input: `${pushOutput}\n`, encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stdout.trim(), digest);
  }
});

test('Helm .76 zero-replica bootstrap lets its delayed-binding claim reach the seed pod', () => {
  const installedHandoff = read('scripts/test_bookshelfng_helm_install_handoff.sh');
  const installLegacy = installedHandoff.match(/install_legacy\(\) \{([\s\S]*?)\n\}/)?.[1];
  assert.ok(installLegacy, 'the pinned BookshelfNG bootstrap install must remain explicit');
  assert.match(installLegacy, /--set replicaCount=0/);
  assert.doesNotMatch(installLegacy, /--wait/);
  assert.match(installedHandoff, /kubectl apply -f "\$EVIDENCE_DIR\/seed-pod\.yaml"[\s\S]*?kubectl wait --for=jsonpath='\{\.status\.phase\}'=Succeeded pod\/bookshelfng-seed/);
});

test('Helm candidate upgrade consumes helper arguments before forwarding Helm flags', () => {
  const installedHandoff = read('scripts/test_bookshelfng_helm_install_handoff.sh');
  const upgradeCandidate = installedHandoff.match(/upgrade_candidate\(\) \{([\s\S]*?)\n\}/)?.[1];
  assert.ok(upgradeCandidate, 'the candidate upgrade helper must remain explicit');
  assert.match(upgradeCandidate, /local mode="\$1" replicas="\$2"\s+shift 2[\s\S]*?helm upgrade/);
  assert.match(upgradeCandidate, /bookshelfMigration\.mode=\$mode[\s\S]*?"\$@"/);
});

test('Helm automatic migration restarts the deferred candidate without replacing its PVC', () => {
  const installedHandoff = read('scripts/test_bookshelfng_helm_install_handoff.sh');
  const manualMode = installedHandoff.indexOf('log "Manual mode preserved schema 158, config, and canary."');
  const scaleDown = installedHandoff.indexOf('upgrade_candidate manual 0 --wait --timeout 15m', manualMode);
  const automaticMode = installedHandoff.indexOf('upgrade_candidate automatic 1 --wait --timeout 30m', scaleDown);
  assert.ok(manualMode >= 0 && scaleDown > manualMode && automaticMode > scaleDown,
    'manual defer must be proven before stopping the unready pod and restarting in automatic mode');
  assert.match(installedHandoff.slice(scaleDown, automaticMode), /wait_stopped bookshelfng-0[\s\S]*?assert_pvc_identity/,
    'the deferred candidate must stop without replacing its persistent claim');
});

test('Helm fixture seeder can initialize the chart’s fresh root-owned PVC', () => {
  const installedHandoff = read('scripts/test_bookshelfng_helm_install_handoff.sh');
  const seedPod = installedHandoff.match(/cat > "\$EVIDENCE_DIR\/seed-pod\.yaml" <<YAML\n([\s\S]*?)\nYAML/)?.[1];
  assert.ok(seedPod, 'the installed handoff must define its populated BookshelfNG seed pod');
  assert.match(seedPod, /securityContext:[\s\S]*runAsUser: 0[\s\S]*runAsGroup: 0/);
  assert.doesNotMatch(seedPod, /fsGroup:/);
  assert.ok(
    seedPod.includes(String.raw`test "\$(sqlite3 -readonly /config/readarr.db 'PRAGMA integrity_check;')" = ok`),
    'seed database checks must expand inside the pod, after the unquoted YAML heredoc is evaluated'
  );
  assert.match(installedHandoff, /kubectl describe pod bookshelfng-seed[\s\S]*fixture-seed-failed\.log/);
});

test('Helm handoff fingerprints the restored legacy claim with root access and captures pod diagnostics', () => {
  const installedHandoff = read('scripts/test_bookshelfng_helm_install_handoff.sh');
  const snapshotPod = installedHandoff.match(/cat > "\$EVIDENCE_DIR\/snapshot-pod\.yaml" <<YAML\n([\s\S]*?)\nYAML/)?.[1];
  assert.ok(snapshotPod, 'the installed handoff must define its stopped-source snapshot pod');
  assert.match(snapshotPod, /securityContext:[\s\S]*runAsUser: 0[\s\S]*runAsGroup: 0/);
  assert.doesNotMatch(snapshotPod, /fsGroup:/);
  assert.match(installedHandoff, /kubectl describe pod bookshelfng-snapshot[\s\S]*snapshot-pod-failed\.log/);
  assert.equal(
    (snapshotPod.match(/\\\$\(/g) ?? []).length,
    4,
    'snapshot SQLite and fingerprint substitutions must expand in the pod, after the unquoted YAML heredoc is evaluated'
  );
  assert.match(
    installedHandoff,
    /kubectl apply -f "\$EVIDENCE_DIR\/snapshot-pod\.yaml"[\s\S]*?kubectl wait --for=jsonpath='\{\.status\.phase\}'=Succeeded pod\/bookshelfng-snapshot/,
    'the one-shot snapshot pod must be awaited by its Succeeded phase, not Ready condition'
  );
});

test('Docker headless Ask harness gives migration startup time and reports early exits', () => {
  const dockerCompatibility = read('packaging/scripts/test-docker-bookshelf-compatibility-defaults.sh');
  assert.match(dockerCompatibility, /timeout --signal=TERM --kill-after=2 5 sh/);
  assert.match(dockerCompatibility, /did not write its deferral marker within five seconds/);
});

test('Flatpak keeps the installed app ID and BookshelfNG sandbox data directory', () => {
  const manifest = read('packaging/legacy/flatpak/com.snapetech.BookshelfNG.yml');
  const desktop = read('packaging/legacy/flatpak/com.snapetech.BookshelfNG.desktop');
  const metainfo = read('packaging/legacy/flatpak/com.snapetech.BookshelfNG.metainfo.xml');

  assert.match(manifest, /^app-id: com\.snapetech\.BookshelfNG$/m);
  assert.match(manifest, /--filesystem=xdg-config\/bookshelfng:create/);
  assert.match(manifest, /install -D -m 0644 com\.snapetech\.BookshelfNG\.desktop \/app\/share\/applications/);
  assert.match(manifest, /install -D -m 0644 com\.snapetech\.BookshelfNG\.metainfo\.xml \/app\/share\/metainfo/);
  assert.match(manifest, /install -D -m 0644 com\.snapetech\.BookshelfNG\.png \/app\/share\/icons/);
  assert.match(manifest, /XDG_CONFIG_HOME:-\$HOME\/\.config\}\/bookshelfng/);
  assert.match(manifest, /\/app\/libexec\/bookshelfng-supervisor\.sh \/app\/lib\/bookshelfng\/Chaptarr/);
  assert.match(manifest, /install -D -m 0755 bookshelfng-supervisor\.sh \/app\/libexec\/bookshelfng-supervisor\.sh/);
  assert.match(manifest, /install -D -m 0755 bookshelfng-journal \/app\/libexec\/bookshelfng-journal/);
  assert.match(manifest, /HARDCOVER="\$\{HARDCOVER:-false\}"/);
  assert.match(manifest, /CHAPTARR_PACKAGE_UPDATE_MECHANISM:-External/);
  assert.match(desktop, /^Exec=bookshelfng-wrapper$/m);
  assert.match(desktop, /^Icon=com\.snapetech\.BookshelfNG$/m);
  assert.match(metainfo, /<launchable type="desktop-id">com\.snapetech\.BookshelfNG\.desktop<\/launchable>/);
});

test('Chocolatey deferred service upgrades retain both NSSM environments and recover after startup failure', () => {
  const installer = read('packaging/legacy/chocolatey/tools/chocolateyinstall.ps1.in');
  const recovery = read('packaging/legacy/chocolatey/tools/bookshelfng-startup-recovery.ps1');
  const choiceTest = read('packaging/scripts/test-chocolatey-migration-choice.ps1');
  const handoff = read('packaging/scripts/test-chocolatey-bookshelfng-handoff.ps1');
  const smoke = read('distribution/windows/installer-net-smoke/Program.cs');
  const sourceVerifierStart = smoke.indexOf('void VerifyBookshelfSource(');
  const migrationVerifierStart = smoke.indexOf('void VerifyBookshelfMigration(', sourceVerifierStart);
  assert.ok(sourceVerifierStart >= 0 && migrationVerifierStart > sourceVerifierStart,
    'BookshelfNG source verifier could not be isolated for contract checks');
  const sourceVerifier = smoke.slice(sourceVerifierStart, migrationVerifierStart);
  assert.match(smoke, /nativeDatabase is null[\s\S]*?GetBookshelfDataCanary\(databasePath\)[\s\S]*?GetNativeChaptarrDataCanary\(databasePath\)/,
    'the Windows canary command must read BookshelfNG and native ChaptarrNG databases using their respective schemas');
  assert.match(smoke, /void VerifyChaptarrDatabase\([\s\S]*?GetNativeChaptarrDataCanary\(databasePath\)/,
    'retained native migration targets must be checked with native-schema canaries');
  assert.match(smoke, /string GetNativeChaptarrDataCanary\([\s\S]*?SELECT Name FROM Authors[\s\S]*?SELECT Title FROM Editions WHERE Id = 40[\s\S]*?SELECT Path FROM BookFiles WHERE Id = 51/,
    'native migration canaries must cover author, edition, and file records without BookshelfNG-only tables');
  const migrationBlockStart = installer.indexOf('if ($hasMigrationData -and -not $alreadyMigrated) {');
  const deferredRecoveryStart = installer.indexOf('if ($hadService -and $hasMigrationData -and -not $wasRunning) {', migrationBlockStart);
  assert.ok(migrationBlockStart >= 0 && deferredRecoveryStart > migrationBlockStart,
    'Chocolatey migration preflight and application block could not be isolated');
  const migrationBlock = installer.slice(migrationBlockStart, deferredRecoveryStart);
  const previewIndex = migrationBlock.indexOf('$previewExit = Invoke-BookshelfUtility');
  const applyIndex = migrationBlock.indexOf('$migrationExit = Invoke-BookshelfUtility');
  assert.ok(previewIndex >= 0 && applyIndex > previewIndex,
    'Chocolatey must apply migration only after its read-only preview passes');
  assert.match(migrationBlock, /\$migrationArguments = @\([\s\S]*?'-migrate-bookshelf'[\s\S]*?'-confirm-bookshelf-service-stopped'[\s\S]*?"-data=\$dataDir"/,
    'Chocolatey must run the explicit conversion with the stopped-service guard against the selected data path');
  assert.match(migrationBlock, /migrationDatabaseMutationMayHaveStarted = \$true[\s\S]*?if \(\$mergeSqliteSources\) \{ \$migrationArguments \+= '-merge-bookshelf-databases' \}/,
    'Chocolatey rollback must cover conversion and preserve the opt-in dual-database path');
  const manualAcceptanceStart = handoff.indexOf('$manualResult = Invoke-Chocolatey');
  const manualAutomaticStart = handoff.indexOf('$env:CHAPTARRNG_TEST_ARCHIVE_PATH = $ArchivePath', manualAcceptanceStart);
  assert.ok(manualAcceptanceStart >= 0 && manualAutomaticStart > manualAcceptanceStart,
    'Chocolatey manual-defer acceptance block could not be isolated');
  const manualAcceptance = handoff.slice(manualAcceptanceStart, manualAutomaticStart);

  assert.match(installer, /Get-BookshelfServiceEnvironment 'AppEnvironment'/);
  assert.match(installer, /Get-BookshelfServiceEnvironment 'AppEnvironmentExtra'/);
  assert.match(installer, /function ConvertTo-WindowsNativeArgument[\s\S]*?function Invoke-BookshelfNssm[\s\S]*?Start-Process -FilePath \$nssm\.Source -ArgumentList \$argumentLine -Wait -PassThru -NoNewWindow/,
    'Chocolatey must preserve embedded quote characters when it calls NSSM from Windows PowerShell');
  assert.match(installer, /function Set-BookshelfServiceSetting[\s\S]*?Invoke-BookshelfNssm -Arguments @\('set', 'BookshelfNG', \$Name, \$Value\)/,
    'Chocolatey must use the quoted native-argument wrapper when restoring service settings');
  assert.match(installer, /function Set-BookshelfServiceEnvironment[\s\S]*?Invoke-BookshelfNssm -Arguments \$arguments/,
    'Chocolatey must use the same native-argument wrapper for service environment settings');
  assert.match(installer, /MigrationEnvironment = @\(\$effectiveEnvironment\)/);
  assert.match(installer, /schtasks\.exe \/Create/);
  assert.match(installer, /HealthUrl = \$healthUrl/);
  assert.match(installer, /OldAppEnvironmentExtra = @\(\$oldAppEnvironmentExtra\)/);
  assert.match(installer, /Get-EffectiveExtraEnvironment/);
  assert.match(installer, /targetDatabaseExit = Invoke-BookshelfUtility/);
  assert.match(installer, /BookshelfNG migration failed with exit code \$migrationExit/);
  assert.match(installer, /BookshelfSource = -not \$alreadyMigrated/);
  assert.match(installer, /\[string\[\]\] \$sqliteSourceNames = @\(\)\s*if \(-not \$postgresSourceForChoice\) \{\s*\$sqliteSourceNames = \[string\[\]\] @\(Get-BookshelfSqliteDatabaseNames \$dataDir\)\s*\}/,
    'Chocolatey must preserve a one-item SQLite source-name array before indexing its first element');
  const backupDiscoveryStart = installer.indexOf('function Get-BookshelfSqliteBackupFiles');
  const backupDiscoveryEnd = installer.indexOf('\nfunction Test-HasPopulatedSqliteDatabaseFamily', backupDiscoveryStart);
  assert.ok(backupDiscoveryStart >= 0 && backupDiscoveryEnd > backupDiscoveryStart,
    'Chocolatey SQLite rollback backup discovery helper could not be isolated');
  const backupDiscovery = installer.slice(backupDiscoveryStart, backupDiscoveryEnd);
  assert.match(backupDiscovery, /Get-ChildItem -LiteralPath \$DataDirectory -File -Force/,
    'Chocolatey must enumerate the chosen data directory directly so retained backups are visible');
  assert.doesNotMatch(backupDiscovery, /-Filter/,
    'Chocolatey rollback backup discovery must not rely on provider-specific filename filtering');
  for (const databaseName of ['readarr.db', 'audioarr.db', 'chaptarr.db']) {
    assert.ok(backupDiscovery.includes(`${databaseName}.bookshelfng-backup-`),
      `Chocolatey rollback backup discovery must recognize ${databaseName} snapshots`);
  }
  assert.match(installer, /Get-BookshelfSqliteBackupFiles -DataDirectory \$dataDir/,
    'Chocolatey must use the tested backup discovery helper before and after migration');
  assert.match(installer, /URLBASE/);
  assert.match(installer, /<EventTrigger>/);
  assert.match(installer, /CHAPTARRNG_TEST_FAIL_HANDOFF/);
  assert.match(handoff, /after-migration-health/);
  assert.match(handoff, /--verify-bookshelfng-source/);
  assert.match(manualAcceptance, /Invoke-MigrationFixture @\('--verify-bookshelfng-source', \$legacyDataDirectory\)/,
    'Chocolatey manual defer must verify the original BookshelfNG database, not only a canary that may prefer a native target');
  assert.match(manualAcceptance, /sourceBackupsAfterManual/,
    'Chocolatey manual defer must not create migration backups before acceptance');
  assert.match(sourceVerifier, /HasNativeChaptarrLineage/,
    'BookshelfNG source verification must reject an in-place native ChaptarrNG conversion');
  assert.match(sourceVerifier, /chaptarr\.db/);
  assert.match(sourceVerifier, /audioarr\.db/);
  assert.match(handoff, /readarr\.db\.chaptarr-failed-/);
  assert.match(handoff, /Where-Object \{ \$_\.Name -match/,
    'the failed migration database must be selected by its retained filename pattern');
  assert.match(handoff, /\\d\{8\}T\\d\{9\}Z/);
  assert.match(handoff, /--verify-chaptarr-database/);
  assert.match(smoke, /FindNativeChaptarrDatabase/);
  assert.match(smoke, /HasNativeChaptarrLineage/);
  assert.match(smoke, /VerifyBookshelfSource/);
  assert.doesNotMatch(installer, /<BootTrigger>/);
  assert.match(recovery, /'AppEnvironment' @\(\$Manifest\.OldAppEnvironment\)/);
  assert.match(recovery, /'AppEnvironmentExtra' @\(\$Manifest\.OldAppEnvironmentExtra\)/);
  assert.match(recovery, /-restore-bookshelf-postgres/);
  assert.match(recovery, /-restore-bookshelf/);
  assert.match(recovery, /\$manifest\.MigrationEnvironment/);
  assert.match(recovery, /Write-SharedMigrationJournal/);
  assert.match(recovery, /\[IO\.File\]::WriteAllText\(\$temporaryPath, "\$State`n", \$utf8\)/);
  assert.match(recovery, /intervention-required/);
  assert.match(recovery, /\$manifest\.State -eq 'healthy'/);
  assert.match(recovery, /\$manifest\.State -eq 'rolled-back'/);
  assert.match(recovery, /schtasks\.exe \/Delete/);
  assert.match(recovery, /PreviousApplicationDirectory/);
  assert.match(recovery, /if \(\$bookshelfSource\)/);
  assert.match(recovery, /ChaptarrNG database was preserved/);
  assert.match(choiceTest, /\$global:LASTEXITCODE = 0/,
    'the test resets its intentionally nonzero utility result before the PowerShell runner exits');
});

test('Chocolatey migration fingerprints do not depend on Get-FileHash module autoloading', () => {
  const install = read('packaging/legacy/chocolatey/tools/chocolateyinstall.ps1.in');
  const recovery = read('packaging/legacy/chocolatey/tools/bookshelfng-startup-recovery.ps1');

  for (const script of [install, recovery]) {
    assert.match(script, /function Get-BookshelfFileSha256[\s\S]*?\[Security\.Cryptography\.SHA256\]::Create\(\)/);
    assert.doesNotMatch(script, /Get-FileHash/);
  }
});

test('Chocolatey installed handoff passes local archive inputs to nested scripts and restores machine state', () => {
  const installedHandoff = read('packaging/scripts/test-chocolatey-bookshelfng-handoff.ps1');
  for (const name of [
    'CHAPTARRNG_TEST_ARCHIVE_PATH',
    'CHAPTARRNG_TEST_FAIL_HANDOFF',
    'CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS',
  ]) {
    assert.match(installedHandoff, new RegExp(`GetEnvironmentVariable\\('${name}', 'Machine'\\)`));
    assert.match(installedHandoff, new RegExp(`SetEnvironmentVariable\\('${name}',[\\s\\S]*?'Machine'\\)`));
  }

  const automaticRunStart = installedHandoff.indexOf('$automaticResult = Invoke-Chocolatey');
  const automaticSetupStart = installedHandoff.lastIndexOf('  $env:CHAPTARRNG_TEST_ARCHIVE_PATH = $ArchivePath', automaticRunStart);
  const automaticRunEnd = installedHandoff.indexOf('if ($automaticResult.ExitCode', automaticRunStart);
  assert.ok(automaticSetupStart >= 0 && automaticRunEnd > automaticRunStart,
    'Chocolatey automatic update fixture could not be isolated');
  const automaticRun = installedHandoff.slice(automaticSetupStart, automaticRunEnd);
  for (const name of [
    'CHAPTARRNG_TEST_ARCHIVE_PATH',
    'CHAPTARRNG_TEST_FAIL_HANDOFF',
    'CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS',
  ]) {
    assert.match(automaticRun, new RegExp(`GetEnvironmentVariable\\('${name}', 'Machine'\\)`));
    assert.match(automaticRun, new RegExp(`SetEnvironmentVariable\\('${name}',[\\s\\S]*?'Machine'\\)`));
  }
  assert.match(automaticRun, /SetEnvironmentVariable\('CHAPTARRNG_TEST_FAIL_HANDOFF', \$null, 'Machine'\)/,
    'automatic migration must expose the candidate archive while keeping failure injection disabled');
});

test('focused Windows Chocolatey failures retain installed-handoff diagnostics', () => {
  const workflow = read('.github/workflows/build.yml');
  const stage = workflow.match(/- name: Stage failed Windows Chocolatey handoff diagnostics([\s\S]*?)(?=\n    - name:)/)?.[1];
  const upload = workflow.match(/- name: Upload failed Windows Chocolatey handoff diagnostics([\s\S]*?)(?=\n    - name:)/)?.[1];
  assert.ok(stage, 'the focused Chocolatey workflow must stage failed installed-handoff state');
  assert.ok(upload, 'the focused Chocolatey workflow must upload failed installed-handoff state');
  assert.match(stage, /failure\(\)[\s\S]*?inputs\.windows_chocolatey_only == true/);
  assert.match(stage, /Copy-Item -LiteralPath \$handoffDirectory[\s\S]*?Copy-Item -LiteralPath \$programDataDirectory/);
  assert.match(upload, /failure\(\)[\s\S]*?inputs\.windows_chocolatey_only == true/);
  assert.match(upload, /\$\{\{ runner\.temp \}\}\/bookshelfng-windows-installed-handoff-/);
  assert.doesNotMatch(upload, /chocolatey-bookshelfng-handoff-|ProgramData/);
  assert.match(upload, /if-no-files-found: warn/);
});

test('Windows standalone migration installer is opt-in and Close defers handoff', () => {
  const xaml = read('distribution/windows/installer-net/MainWindow.xaml');
  const code = read('distribution/windows/installer-net/MainWindow.xaml.cs');
  const loadHandler = code.slice(code.indexOf('private async void OnLoaded'), code.indexOf('private async Task RunRestoreAsync'));
  const installHandler = code.slice(code.indexOf('private async void InstallButton_Click'), code.indexOf('private async void RestoreButton_Click'));

  assert.match(xaml, /x:Name="InstallButton"[\s\S]*Content="Migrate BookshelfNG"[\s\S]*Click="InstallButton_Click"/);
  assert.match(xaml, /x:Name="CloseButton"[\s\S]*Content="Close"[\s\S]*Click="CloseButton_Click"/);
  assert.doesNotMatch(loadHandler, /RunUpgradeAsync/, 'opening the installer does not start migration');
  assert.match(installHandler, /InstallPipeline\.RunUpgradeAsync/);
  assert.match(code, /private void CloseButton_Click\(object sender, RoutedEventArgs e\) => Close\(\);/);
});

test('Cloudron candidate preserves application ID, health endpoint, port, and mounts', () => {
  const manifest = JSON.parse(read('packaging/legacy/cloudron/CloudronManifest.json'));
  const dockerfile = read('packaging/legacy/cloudron/Dockerfile');
  const entrypoint = read('packaging/legacy/cloudron/cloudron-entrypoint.sh');

  assert.equal(manifest.id, 'tech.snapetech.bookshelfng');
  assert.equal(manifest.httpPort, 8787);
  assert.equal(manifest.healthCheckPath, '/ping');
  assert.deepEqual(manifest.persistentDirs, ['/config', '/books', '/downloads']);
  assert.match(dockerfile, /^FROM ghcr\.io\/snapetech-org\/chaptarrng:latest$/m);
  assert.match(dockerfile, /^CMD \["dotnet", "\/app\/Chaptarr\.dll", "-nobrowser", "-data=\/config"\]$/m,
    'the Cloudron wrapper restores the app command cleared by its replacement ENTRYPOINT');
  assert.match(dockerfile, /BOOKSHELFNG_MIGRATION_MODE=automatic/);
  assert.doesNotMatch(entrypoint, /BOOKSHELFNG_MIGRATION_MODE=/,
    'Cloudron app environment overrides can select manual mode before migration');
  assert.doesNotMatch(dockerfile, /docker\.io\/snapetech\/bookshelfng/);
});

test('catalog Compose handoff keeps the BookshelfNG container and named volumes', () => {
  const compose = read('packaging/legacy/catalogs/compose.yaml');
  const sample = read('packaging/legacy/catalogs/.env.sample');

  assert.match(compose, /container_name: bookshelfng/);
  assert.match(compose, /CHAPTARRNG_IMAGE:-ghcr\.io\/snapetech-org\/chaptarrng:latest/);
  assert.match(compose, /"127\.0\.0\.1:\$\{BOOKSHELFNG_HOST_PORT:-8787\}:\$\{BOOKSHELFNG_PORT:-8787\}\/tcp"/);
  assert.match(compose, /bookshelfng-config:\/config/);
  assert.match(compose, /bookshelfng-books:\/books/);
  assert.match(compose, /bookshelfng-downloads:\/downloads/);
  for (const key of preservedBookshelfEnvironment) {
    assert.match(compose, new RegExp(`^\\s+${key}: \\u0024\\{${key}:-`, 'm'),
      `catalog Compose preserves ${key}`);
    assert.match(sample, new RegExp(`^${key}=`, 'm'),
      `catalog .env.sample documents ${key}`);
  }
  for (const key of ['BOOKSHELF_PORT', 'BOOKSHELF_URLBASE', ...legacySectionEnvironment]) {
    assert.match(compose, new RegExp(`^\\s+${key}: \\u0024\\{${key}:-`, 'm'),
      `catalog Compose preserves legacy ${key}`);
    assert.match(sample, new RegExp(`^${key}=`, 'm'),
      `catalog .env.sample documents legacy ${key}`);
  }
});

test('SeerrNG migration smoke blocks external provider egress', () => {
  const compose = read('integration/seerrng-smoke.compose.yml');
  const readme = read('integration/README.md');

  assert.match(compose, /metadata-mock:[\s\S]*request\.url\.startsWith\('\/api\/v5\/author'\)/);
  assert.match(compose, /Author:\s*null/);
  assert.match(compose, /METADATA_URL: http:\/\/metadata-mock:8765/);
  assert.match(compose, /networks:\n\s+default:\n\s+internal: true/);
  assert.match(compose, /CHAPTARR__SERVER__PORT:\s+"8789"/);
  assert.match(readme, /network is internal, so the containers can reach each other but cannot contact\n+public providers or other external services/);
});

test('Co-op Cloud handoff keeps stack volumes and uses the standard ChaptarrNG image', () => {
  const compose = read('packaging/legacy/coop-cloud/bookshelfng/compose.yml');
  const sample = read('packaging/legacy/coop-cloud/bookshelfng/.env.sample');
  const readme = read('packaging/legacy/coop-cloud/bookshelfng/README.md');
  const release = read('packaging/legacy/coop-cloud/bookshelfng/release/next');

  assert.match(compose, /CHAPTARRNG_IMAGE:-ghcr\.io\/snapetech-org\/chaptarrng:latest/);
  assert.match(compose, /bookshelfng_config:\/config/);
  assert.match(compose, /bookshelfng_books:\/books/);
  assert.match(compose, /bookshelfng_downloads:\/downloads/);
  assert.match(compose, /loadbalancer\.server\.port=\$\{BOOKSHELFNG_PORT:-8787\}/);
  assert.match(compose, /backupbot\.backup=true/);
  assert.match(compose, /start_period: 245m/);
  assert.match(compose, /order: stop-first/);
  assert.match(compose, /failure_action: pause/);
  assert.match(compose, /version=\$\{COOP_CLOUD_RECIPE_VERSION:-0\.1\.0\+__CHAPTARRNG_VERSION__\}/);
  assert.match(sample, /^CHAPTARRNG_IMAGE=ghcr\.io\/snapetech-org\/chaptarrng:latest$/m);
  assert.doesNotMatch(sample, /BOOKSHELFNG_HOST_PORT/);
  for (const key of preservedBookshelfEnvironment) {
    assert.match(compose, new RegExp(`^\\s+${key}: \\u0024\\{${key}:-`, 'm'),
      `Co-op Cloud Compose preserves ${key}`);
    assert.match(sample, new RegExp(`^${key}=`, 'm'),
      `Co-op Cloud .env.sample documents ${key}`);
  }
  for (const key of ['BOOKSHELF_PORT', 'BOOKSHELF_URLBASE', ...legacySectionEnvironment]) {
    assert.match(compose, new RegExp(`^\\s+${key}: \\u0024\\{${key}:-`, 'm'),
      `Co-op Cloud Compose preserves legacy ${key}`);
    assert.match(sample, new RegExp(`^${key}=`, 'm'),
      `Co-op Cloud .env.sample documents legacy ${key}`);
  }
  assert.match(readme, / backs up all\nthree named volumes/);
  assert.match(readme, /operator-directed rollback after a successful migration must\nrestore the matching BookshelfNG data backup/);
  assert.match(release, /Create an independent Co-op Cloud backup before upgrading/);
});

test('Unraid BookshelfNG identity template preserves legacy configuration and masks credentials', () => {
  const template = read('packaging/legacy/unraid/bookshelfng.xml');

  assert.match(template, /<Name>BookshelfNG<\/Name>/);
  assert.match(template, /<Repository>ghcr\.io\/snapetech-org\/chaptarrng:latest<\/Repository>/);
  assert.match(template, /Target="CHAPTARR_BOOKSHELF_COMPATIBILITY"/);
  assert.match(template, /Target="BOOKSHELFNG_MIGRATION_MODE"/);
  for (const key of preservedBookshelfEnvironment) {
    assert.match(template, new RegExp(`Target="${key}"`),
      `Unraid template retains ${key}`);
  }
  for (const key of ['BOOKSHELF_PORT', 'BOOKSHELF_URLBASE', ...legacySectionEnvironment]) {
    assert.match(template, new RegExp(`Target="${key}"`),
      `Unraid template retains legacy ${key}`);
  }
  for (const key of [
    'HARDCOVER_AUTH', 'HARDCOVER_API_KEY', 'HARDCOVER_APIFY_TOKEN',
    'GOOGLE_BOOKS_API_KEY', 'EUROPEANA_API_KEY',
    'BOOKSHELF_DIAGNOSTICS_AUTH_TOKEN', 'BOOKSHELF_OIDC_CLIENT_SECRET',
    'BOOKSHELF_SEERRNG_API_KEY', 'BOOKSHELF__SERVER__SSLCERTPASSWORD',
    'BOOKSHELF__AUTH__APIKEY',
    'BOOKSHELF__POSTGRES__PASSWORD', 'AUDIOARR__POSTGRES__PASSWORD',
    'READARR__POSTGRES__PASSWORD',
  ]) {
    assert.match(template, new RegExp(`Target="${key}"[^>]*Mask="true"`),
      `Unraid template masks ${key}`);
  }
});

test('release builds and verifies desktop migration candidates only behind all readiness gates', () => {
  const workflow = read('.github/workflows/release.yml');
  const builder = read('packaging/scripts/build-bookshelf-desktop-candidates.sh');
  const desktopJob = readWorkflowJob(workflow, 'legacy-desktop-candidates');
  const publishJob = readWorkflowJob(workflow, 'publish-release');

  assert.match(desktopJob, /needs: \[release, standalone-assets\]/);
  assert.match(desktopJob, /EXPECTED_RELEASE_SHA: \$\{\{ needs\.release\.outputs\.sha \}\}/);
  assert.match(desktopJob, /name: chaptarrng-standalone-linux-x64/);
  assert.match(desktopJob, /sha256sum --check \"\$archive\.sha256\"/);
  assert.match(desktopJob, /build-bookshelf-desktop-candidates\.sh/);
  assert.match(desktopJob, /appimagetool\/releases\/download\/1\.9\.1\/appimagetool-x86_64\.AppImage/);
  assert.match(desktopJob, /APPIMAGETOOL_SHA256: ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0/);
  assert.match(desktopJob, /APPIMAGE_RUNTIME_SHA256: 2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d/);
  assert.match(desktopJob, /type2-runtime\/releases\/download\/20251108\/runtime-x86_64/);
  assert.match(desktopJob, /APPIMAGETOOL_RUNTIME_FILE=\$runtime/);
  assert.match(builder, /appimagetool_runtime_args\+=\(--runtime-file "\$APPIMAGETOOL_RUNTIME_FILE"\)/);
  const guardedSteps = desktopJob.match(/^      if: .*vars\.CHAPTARRNG_MIGRATION_BRIDGE_ENABLED == 'true'.*vars\.CHAPTARRNG_RELEASE_READY == 'true'.*vars\.BOOKSHELFNG_RELEASE_READY == 'true'.*$/gm) ?? [];
  assert.equal(guardedSteps.length, 5, 'download, build, and upload steps all require every release gate');
  assert.match(desktopJob, /name: chaptarrng-bookshelfng-desktop-candidates/);
  assert.match(publishJob, /needs: \[preflight, release, docker, standalone-assets, rpm-source, windows-migration-installer, legacy-desktop-candidates\]/);
  assert.match(publishJob, /name: chaptarrng-bookshelfng-desktop-candidates/);
  assert.match(publishJob, /ChaptarrNG-\$\{VERSION\}-bookshelfng-x86_64\.AppImage/);
  assert.match(publishJob, /ChaptarrNG-\$\{VERSION\}-bookshelfng\.flatpak/);
});

test('release pins Cloudron and Compose candidates to the built standard image digest', () => {
  const workflow = read('.github/workflows/release.yml');
  const builder = read('packaging/scripts/build-bookshelf-container-platform-candidates.sh');
  const publishJob = readWorkflowJob(workflow, 'publish-release');

  assert.ok(builder.includes('^sha256:[a-f0-9]{64}$'));
  assert.ok(builder.includes('tech.snapetech.bookshelfng'));
  assert.match(builder, /CHAPTARRNG_IMAGE=/);
  assert.ok(builder.includes('bookshelfng-container-platform-candidates.tar.gz'));
  assert.ok(publishJob.includes('CHAPTARRNG_IMAGE_DIGEST: ${{ needs.docker.outputs.image_digest }}'));
  assert.ok(publishJob.includes('build-bookshelf-container-platform-candidates.sh'));
  assert.ok(publishJob.includes('bookshelfng-container-platform-candidates.tar.gz'));
  assert.ok(publishJob.includes('tar -tzf "$RUNNER_TEMP/release-assets/$container_candidates"'));
});

test('Linux release jobs install the checksum-pinned ChaptarrNG GitHub CLI action before using it', () => {
  const action = 'uses: ./.github/actions/setup-github-cli';
  const release = read('.github/workflows/release.yml');
  const distribution = read('.github/workflows/release-distribution.yml');
  assert.doesNotMatch(`${release}\n${distribution}`, /snapetech-org\/bookshelfng\/\.github\/actions\/setup-github-cli/);

  const preflight = readWorkflowJob(release, 'preflight');
  assert.ok(preflight.includes('packaging/scripts/check_stable_release_version.py'));
  assert.ok(!preflight.includes('gh release'));

  for (const name of ['release', 'dispatch-legacy-ghcr', 'publish-release']) {
    const job = readWorkflowJob(release, name);
    assert.match(job, /ng-shared-main-jit/);
    assert.ok(job.includes(action), `${name} installs the pinned GitHub CLI on its private runner`);
    assert.ok(job.indexOf(action) < job.search(/gh release|dispatch-ghcr-release-bridge\.sh/),
      `${name} installs the CLI before the first use`);
    if (name === 'dispatch-legacy-ghcr') {
      assert.ok(job.includes('ref: ${{ needs.release.outputs.sha }}'));
      assert.ok(job.indexOf('actions/checkout@') < job.indexOf(action),
        'dispatch checks out the exact release source before using the local action and script');
    }
  }

  for (const name of ['release-target', 'build-legacy-ppa', 'build-aur']) {
    const job = readWorkflowJob(distribution, name);
    assert.match(job, /ng-shared-main-jit/);
    assert.ok(job.includes(action), `${name} installs the pinned GitHub CLI on its private runner`);
    assert.ok(job.indexOf(action) < job.search(/gh release/), `${name} installs the CLI before release operations`);
  }
});

test('Windows release uses private Packer and verifies the BookshelfNG handoff archive', () => {
  const workflow = read('.github/workflows/release-distribution.yml');
  const job = readWorkflowJob(workflow, 'build-chocolatey');
  const handoff = read('packaging/scripts/test-chocolatey-bookshelfng-handoff.ps1');
  const buildWorkflow = read('.github/workflows/build.yml');
  const build = readWorkflowJob(buildWorkflow, 'build');
  const legacyInstaller = read('packaging/legacy/chocolatey/tools/chocolateyinstall.ps1.in');

  assert.match(job, /runs-on: \$\{\{ fromJSON\(format\(/);
  assert.match(job, /"group":"packer-windows"/);
  assert.match(job, /"labels":\["self-hosted","Windows","X64","on-demand","packer-windows","ChaptarrNG","gha-\{0\}-build-chocolatey"\]/);
  assert.match(job, /github\.run_id/);
  const downloadStep = job.indexOf('name: Download and verify the release archive');
  const packageStep = job.indexOf('name: Render and pack Chocolatey package');
  assert.ok(downloadStep >= 0 && downloadStep < packageStep,
    'the release archive is downloaded and verified before the package is built');
  assert.doesNotMatch(job, /setup-github-cli-windows\.ps1|gh release download/,
    'the Windows job uses its authenticated REST download and does not install an unused GitHub CLI');
  assert.match(job, /api\.github\.com\/repos\/\$env:GITHUB_REPOSITORY\/releases\/tags/);
  assert.match(job, /Authorization = "Bearer \$env:GH_TOKEN"/);
  assert.match(job, /Invoke-WebRequest -Uri \$apiUri\.AbsoluteUri -Headers \$assetHeaders -OutFile \$destination/);
  assert.match(job, /Get-FileHash .* -Algorithm SHA256/);
  assert.match(job, /test-chocolatey-bookshelfng-handoff\.ps1/);
  assert.match(job, /setup-dotnet@/);
  assert.match(handoff, /BookshelfVersion = '0\.4\.21\.76'/);
  assert.match(handoff, /candidate Chocolatey package is missing the BookshelfNG startup recovery helper/);
  assert.match(handoff, /bookshelfng-startup-recovery\.ps1/);
  assert.ok(handoff.includes("Join-Path $repoRoot 'packaging\\legacy\\chocolatey\\tools\\bookshelfng-startup-recovery.ps1'"),
    'the installed-handoff fixture stages the recovery helper into the package');
  assert.match(handoff, /--bookshelf-data-canary/);
  assert.match(handoff, /--verify-bookshelfng-migration/);
  assert.match(handoff, /ChaptarrNG\.Installer\.Smoke\.exe/);
  assert.match(handoff, /ParentProcessId=\$serviceProcessId/);
  assert.match(handoff, /MigrationMode=manual/);
  const manualHealthRecheck = handoff.indexOf('$healthUrlAfterManual = Wait-BookshelfHealth');
  const automaticMigration = handoff.indexOf("$env:CHAPTARRNG_TEST_ARCHIVE_PATH", manualHealthRecheck);
  assert.ok(manualHealthRecheck >= 0 && automaticMigration > manualHealthRecheck,
    'the unchanged BookshelfNG service must pass /ping after manual defer and before automatic migration');
  assert.match(handoff, /if \(\$healthUrlAfterManual -ne \$healthUrl\)/,
    'manual defer must retain the original BookshelfNG health endpoint');
  assert.match(handoff, /CHAPTARRNG_TEST_ARCHIVE_PATH/);
  assert.match(legacyInstaller, /CHAPTARRNG_TEST_ARCHIVE_PATH/);
  assert.match(buildWorkflow, /windows_chocolatey_only:[\s\S]*?type: boolean[\s\S]*?default: false/);
  assert.match(build, /inputs\.windows_chocolatey_only[\s\S]*?fromJSON\('\["windows-x64"\]'\)/);
  assert.match(build, /name: Check translation calls and locale catalogs\n      if: inputs\.windows_chocolatey_only != true/);
  assert.match(build, /name: Test locale formatting and RTL CSS\n      if: inputs\.windows_chocolatey_only != true/);
  assert.match(buildWorkflow, /postgres-migrations:[\s\S]*?inputs\.windows_chocolatey_only/);
  assert.match(buildWorkflow, /docker-image-build:[\s\S]*?inputs\.windows_chocolatey_only/);
  assert.match(buildWorkflow, /rpm_bridge_only:[\s\S]*?type: boolean[\s\S]*?default: false/);
  const rpmAcceptance = readWorkflowJob(buildWorkflow, 'rpm-bridge-acceptance');
  assert.match(rpmAcceptance, /inputs\.rpm_bridge_only == true/);
  assert.match(rpmAcceptance, /ng-shared-main-jit/);
  assert.match(rpmAcceptance, /gha-\{0\}-rpm-bridge-acceptance/);
  assert.match(rpmAcceptance, /test-build-bookshelfng-rpm-bridge\.sh/);
  assert.match(rpmAcceptance, /name: Upload Fedora 44 RPM migration evidence[\s\S]*?if: always\(\)[\s\S]*?actions\/upload-artifact@/);
  const rpmHarness = read('packaging/scripts/test-build-bookshelfng-rpm-bridge.sh');
  assert.match(rpmHarness, /Docker CLI is missing from the runner/);
  assert.match(rpmHarness, /docker version --format/);
  assert.match(rpmHarness, /RPM bridge package transaction passed; evidence kept/);
  assert.match(buildWorkflow, /channel_contracts_only:[\s\S]*?type: boolean[\s\S]*?default: false/);
  const channelContracts = readWorkflowJob(buildWorkflow, 'channel-contracts');
  assert.match(channelContracts, /inputs\.channel_contracts_only == true/);
  assert.match(channelContracts, /github\.ref == 'refs\/heads\/main'/);
  assert.match(channelContracts, /ng-shared-main-jit/);
  assert.match(channelContracts, /gha-\{0\}-channel-contracts/);
  assert.match(channelContracts, /ref: \$\{\{ github\.sha \}\}/);
  assert.match(channelContracts, /node --test packaging\/scripts\/test-bookshelf-legacy-channels\.mjs/);
  assert.match(channelContracts, /Test Docker BookshelfNG migration supervisor and single image[\s\S]*?TASK_SCRATCH_DIR: \$\{\{ runner\.temp \}\}\/chaptarrng-docker-compatibility-[\s\S]*?test-docker-bookshelf-compatibility-defaults\.sh/);
  assert.match(channelContracts, /Upload Docker compatibility failure evidence[\s\S]*?if: failure\(\)[\s\S]*?actions\/upload-artifact@/);
  assert.match(build, /Test Chocolatey BookshelfNG \.76 to exact candidate handoff[\s\S]*?github\.event_name == 'workflow_dispatch'[\s\S]*?test-chocolatey-bookshelfng-handoff\.ps1/);
});

test('StartOS candidate documents installed test acceptance and remaining release gates', () => {
  const readme = read('packaging/legacy/startos/README.md');
  const manifest = read('packaging/legacy/startos/candidate/startos/manifest/index.ts');

  assert.match(readme, /`candidate\/` contains the StartOS wrapper source/);
  assert.match(readme, /package ID `bookshelfng`/);
  assert.match(readme, /volume IDs `main`,\s*`books`, and `downloads`/);
  assert.match(readme, /`\/config`, `\/books`, and `\/downloads`/);
  assert.match(readme, /exact ChaptarrNG\s+multi-architecture Hardcover digest/);
  assert.match(readme, /Synthetic x86_64 and aarch64 test-package updates\s+passed in local StartOS VMs/i);
  assert.match(readme, /official signed package, update from the public `\.71` StartOS package,\s+external\s+repository delivery, or package-manager rollback/i);
  assert.match(manifest, /packageRepo: 'https:\/\/github\.com\/snapetech(?:-org)?\/bookshelfng-startos'/);
  assert.match(manifest, /upstreamRepo: 'https:\/\/github\.com\/snapetech-org\/chaptarrng'/);
  assert.match(manifest, /source: \{ dockerTag: 'ghcr\.io\/snapetech-org\/chaptarrng@sha256:__CHAPTARRNG_IMAGE_DIGEST__' \}/);
});

test('interactive package migration choices default to No before conversion', () => {
  const debianPreinstall = read('packaging/legacy/debian/preinst');
  const rpmSpec = read('packaging/legacy/rpm/bookshelfng.spec.in');
  const aurInstall = read('packaging/legacy/aur/bookshelfng-bin/bookshelfng.install');
  const chocolatey = read('packaging/legacy/chocolatey/tools/chocolateyinstall.ps1.in');
  const desktopSupervisor = read('packaging/legacy/desktop/bookshelfng-supervisor.sh');

  assert.match(debianPreinstall, /has_controlling_tty\(\) \{\s*\[ -t 0 \] && \( : <\/dev\/tty >\/dev\/tty \)/,
    'Debian requires a TTY on stdin so dpkg pseudo-terminals cannot make headless apt prompt');
  assert.match(rpmSpec, /bookshelfng_has_controlling_tty\(\) \{\s*\( : <\/dev\/tty >\/dev\/tty \)/,
    'RPM uses its controlling terminal because Fedora redirects scriptlet stdin even for interactive installs');

  for (const [channel, hook] of [
    ['Debian', debianPreinstall],
    ['RPM', rpmSpec],
  ]) {
    assert.match(hook, /BOOKSHELFNG_MIGRATION_MODE:-automatic/,
      `${channel} preserves automatic mode as the unattended default`);
    assert.match(hook, /has_controlling_tty\(\)[\s\S]*?ask\)[\s\S]*?if ! [a-z_]*has_controlling_tty; then[\s\S]*?no terminal/,
      `${channel} fails closed when ask mode has no terminal`);
    assert.match(hook, /Replace BookshelfNG with ChaptarrNG now\?[\s\S]*?\[y\/N\]/,
      `${channel} presents the same default-No prompt`);
    assert.match(hook, /IFS= read -r migration_choice <\/dev\/tty[\s\S]*?y\|Y\|yes\|YES\|Yes[\s\S]*?exit 1/,
      `${channel} proceeds only for an explicit yes and cancels otherwise`);
    assert.match(hook, /case "\$migration_mode" in[\s\S]*?ask\)[\s\S]*?(?:snapshot_source_package|snapshot-ready|touch \/var\/lib\/bookshelfng\/\.chaptarrng-bookshelfng-migration-pending)/,
      `${channel} asks before creating the migration snapshot or pending marker`);
  }

  assert.match(aurInstall, /BOOKSHELFNG_MIGRATION_MODE:-automatic/);
  assert.match(aurInstall, /_bookshelfng_has_controlling_tty\(\) \{\s*\[ -t 0 \] && \( : <\/dev\/tty >\/dev\/tty \)[\s\S]*?ask\)[\s\S]*?if ! _bookshelfng_has_controlling_tty; then[\s\S]*?Replace BookshelfNG with ChaptarrNG now\?[\s\S]*?\[y\/N\]/,
    'AUR presents a default-No prompt and detects when pacman has no terminal');
  assert.match(aurInstall, /_deferred_marker=\/var\/lib\/bookshelfng\/\.chaptarrng-bookshelfng-migration-deferred[\s\S]*?install -Dm600 \/dev\/null "\$_deferred_marker"[\s\S]*?_restore_deferred_handoff[\s\S]*?cp -a "\$_handoff_dir\/source-app" "\$_source_dir"[\s\S]*?systemctl start "\$_service"/,
    'AUR records No and restores the saved BookshelfNG runtime after the pacman payload phase');
  assert.match(aurInstall, /if \[\[ -e "\$_deferred_marker" \]\]; then[\s\S]*?_restore_deferred_handoff/,
    'AUR retries the migration choice on a later package transaction');
  assert.doesNotMatch(aurInstall, /replacement cancelled[\s\S]*?exit 1/,
    'AUR does not rely on scriptlet failure to cancel pacman');

  assert.match(chocolatey, /function Confirm-BookshelfMigrationChoice[\s\S]*?\[Console\]::IsInputRedirected[\s\S]*?Read-Host[\s\S]*?\[y\/N\][\s\S]*?-notmatch '\^\(\?i:y\|yes\)\$'[\s\S]*?cancelled before staging files or stopping the service/);
  assert.match(chocolatey, /Confirm-BookshelfMigrationChoice -HasMigrationData \$hasMigrationDataForChoice -Mode \$requestedMigrationMode[\s\S]*?Complete-PriorBookshelfHandoff/,
    'Chocolatey asks before staging the new application or changing service state');

  assert.match(chocolatey, /& \$Executable @Arguments -exitimmediately 2>&1[\s\S]*?\$exitCode = \$LASTEXITCODE[\s\S]*?Write-Host \$line[\s\S]*?return \$exitCode/,
    'Chocolatey utility calls exit without prompting and keep stdout out of the returned exit code');
  assert.match(read('src/NzbDrone.Console/ConsoleApp.cs'), /if \(exitCode != ExitCodes\.Normal && startupArgs\?\.ExitImmediately != true\)/,
    'explicit exit-immediately mode suppresses all interactive failure waits');

  assert.match(desktopSupervisor, /zenity --question --default-cancel[\s\S]*?--cancel-label='Not now'/,
    'AppImage and Flatpak desktop dialogs default to No');
  assert.match(desktopSupervisor, /printf 'Replace BookshelfNG with ChaptarrNG now\?[\s\S]*?\[y\/N\][\s\S]*?IFS= read -r migration_choice <\/dev\/tty/);
  assert.match(desktopSupervisor, /case "\$migration_choice" in[\s\S]*?y\|Y\|yes\|YES\|Yes\)[\s\S]*?BookshelfNG conversion deferred[\s\S]*?exit 0/);
  assert.match(desktopSupervisor, /MigrationMode=ask needs zenity or an interactive terminal/);
  assert.match(desktopSupervisor, /run_utility\(\) \{\s*"\$console" -exitimmediately/,
    'standalone utility failures never wait for terminal input during rollback');
  assert.match(desktopSupervisor, /health_deadline=\$\(\([\s\S]*?timeout_seconds[\s\S]*?run_utility -check-bookshelf-migration[\s\S]*?-bookshelf-health-timeout-seconds=1[\s\S]*?health_deadline/,
    'standalone health checks retry while migration and service startup complete');
});

test('Linux and Windows VM jobs execute package-specific migration-choice checks', () => {
  const workflow = read('.github/workflows/build.yml');
  const buildJob = readWorkflowJob(workflow, 'build');

  assert.match(buildJob, /name: Test candidate BookshelfNG Debian bridge package[\s\S]*?if: matrix\.platform == 'linux-x64'/);
  assert.match(buildJob, /name: Test BookshelfNG AUR migration choice[\s\S]*?if: matrix\.platform == 'linux-x64'[\s\S]*?test-bookshelfng-aur-choice\.sh/);
  assert.match(buildJob, /name: Test candidate BookshelfNG RPM bridge package[\s\S]*?if: matrix\.platform == 'linux-x64'[\s\S]*?test-build-bookshelfng-rpm-bridge\.sh/);
  assert.match(buildJob, /name: Test Chocolatey BookshelfNG migration choice[\s\S]*?if: matrix\.platform == 'windows-x64'[\s\S]*?test-chocolatey-migration-choice\.ps1/);
  assert.match(buildJob, /name: Stage failed Windows Chocolatey handoff diagnostics[\s\S]*?Copy-Item -LiteralPath \$handoffDirectory[\s\S]*?Copy-Item -LiteralPath \$programDataDirectory/,
    'Windows handoff diagnostics must be staged under one artifact root');
  assert.match(buildJob, /name: Upload failed Windows Chocolatey handoff diagnostics[\s\S]*?path: \$\{\{ runner\.temp \}\}\/bookshelfng-windows-installed-handoff-\$\{\{ github\.run_id \}\}/,
    'Windows handoff diagnostics upload must use the staged single-root directory');
  assert.match(read('packaging/scripts/test-build-bookshelfng-deb-bridge.sh'), /bookshelfng-ask-headless\.log[\s\S]*?no terminal is available/);
  assert.match(read('packaging/scripts/test-build-bookshelfng-rpm-bridge.sh'), /ask-headless\.log[\s\S]*?no terminal is available/);
  assert.match(read('packaging/scripts/test-bookshelfng-aur-choice.sh'), /ask-headless\.log[\s\S]*?No terminal is available/);
});

test('AUR choice acceptance uses a pinned offline container and a real host PTY', () => {
  const aurTest = read('packaging/scripts/test-bookshelfng-aur-choice.sh');

  assert.match(aurTest, /test_image="fedora:44@sha256:[a-f0-9]{64}"/);
  assert.match(aurTest, /"--network", "none"/);
  assert.match(aurTest, /pty\.openpty\(\)/);
  assert.match(aurTest, /prompt_count != 2/);
  assert.doesNotMatch(aurTest, /dnf -q install -y expect/);
});

test('noninteractive image and appliance channels defer ask mode without starting ChaptarrNG', () => {
  const supervisor = read('docker-bookshelf-compat-supervisor.sh');
  const entrypoint = read('docker-entrypoint.sh');
  const chart = read('charts/bookshelfng/README.md');
  const startOsAction = read('packaging/legacy/startos/candidate/startos/actions/configureMigration.ts');
  const startOsMain = read('packaging/legacy/startos/candidate/startos/main.ts');

  assert.match(supervisor, /automatic\|ask\|manual\|retry/);
  assert.match(supervisor, /ask\)[\s\S]*?if \[ ! -t 0 \][\s\S]*?return 1/,
    'container ask mode defers safely when there is no interactive terminal');
  assert.match(supervisor, /if \[ -t 0 \]; then[\s\S]*?timeout --foreground --signal=TERM --kill-after=30/,
    'interactive TTY migration utilities remain in the foreground terminal process group');
  assert.match(supervisor, /dotnet \/app\/Chaptarr\.dll "\$command" "\$@" "-data=\$data_dir" -exitimmediately/,
    'container utilities never block on an interactive failure prompt before the migration choice');
  assert.match(supervisor, /wait_for_operator 'BookshelfNG migration is deferred/);
  assert.match(supervisor, /kill -TERM -"\$app_pid"/);
  assert.match(supervisor, /kill -KILL -"\$app_pid"/);
  assert.doesNotMatch(supervisor, /kill -TERM -- "-\$app_pid"/,
    'process-group signals stay compatible with the runtime /bin/sh builtins');
  assert.match(entrypoint, /find -P "\$path" -xdev ! -type l -exec chown "\$uid:\$gid"/);
  assert.match(entrypoint, /"\$DATA_DIR"\/\.chaptarrng-bookshelf-handoff/);
  assert.match(entrypoint, /"\$DATA_DIR"\/\.chaptarrng-bookshelfng-journal/);
  assert.match(entrypoint, /"\$DATA_DIR"\/asp/);
  assert.match(entrypoint, /"\$DATA_DIR"\/\*\.pid/);
  assert.match(chart, /Kubernetes has no\s+interactive terminal for `ask`, so that mode also defers migration/);
  assert.match(chart, /without Helm's `--wait` option[\s\S]*?kubectl delete pod/,
    'the unready deferred StatefulSet has a documented automatic restart sequence');
  assert.match(startOsAction, /Automatically migrate BookshelfNG data on startup/);
  assert.match(startOsAction, /default: true/);
  assert.match(startOsMain, /command: sdk\.useEntrypoint\(\)/,
    'StartOS preserves the pinned image entrypoint and its default application command');
  assert.match(startOsMain, /checkPortListening\(effects, uiPort/,
    'StartOS readiness uses the built-in check instead of a path that may be absent from the image rootfs');
  assert.doesNotMatch(startOsMain, /command: \['\/init'\]|runHealthScript/,
    'StartOS does not invoke the image init wrapper as a child or depend on a separately packaged health script');
  assert.match(startOsMain, /BOOKSHELFNG_MIGRATION_MODE: autoMigrate \? 'automatic' : 'manual'/,
    'StartOS exposes an explicit automatic/manual first-start choice instead of a terminal prompt');
});

test('StartOS candidate uses the patched SDK 3 dependency contract and documents its OS floor', () => {
  const packageJson = JSON.parse(read('packaging/legacy/startos/candidate/package.json'));
  const packageLock = JSON.parse(read('packaging/legacy/startos/candidate/package-lock.json'));
  const readme = read('packaging/legacy/startos/candidate/README.md');
  const dependencies = read('packaging/legacy/startos/candidate/startos/dependencies.ts');
  const index = read('packaging/legacy/startos/candidate/startos/index.ts');
  const init = read('packaging/legacy/startos/candidate/startos/init/index.ts');
  const manifest = read('packaging/legacy/startos/candidate/startos/manifest/index.ts');

  assert.equal(packageJson.dependencies['@start9labs/start-sdk'], '3.1.0');
  assert.equal(packageLock.packages[''].dependencies['@start9labs/start-sdk'], '3.1.0');
  assert.equal(packageLock.packages['node_modules/@start9labs/start-sdk'].version, '3.1.0');
  assert.match(dependencies, /sdk\.Dependencies\.of\(\)/);
  assert.match(index, /buildManifest\(versionGraph,\s*sdkManifest,\s*dependencies\)/);
  assert.match(init, /import \{ dependencies \} from '\.\.\/dependencies'/);
  assert.match(init, /versionGraph,\s*setInterfaces,\s*dependencies,\s*actions/);
  assert.doesNotMatch(manifest, /dependencies:\s*\{\s*\}/);
  assert.match(readme, /StartOS 0\.4\.0\.2 or newer/);
});
