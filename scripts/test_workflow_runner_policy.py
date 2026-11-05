import re
import unittest
from pathlib import Path


WORKFLOW = Path(__file__).resolve().parents[1] / ".github" / "workflows" / "build.yml"


def event_block(source: str, event: str) -> str:
    lines = source.splitlines()
    start = lines.index(f"  {event}:")
    body = []
    for line in lines[start + 1 :]:
        if line.strip() and len(line) - len(line.lstrip()) <= 2:
            break
        body.append(line)
    return "\n".join(body)


def job_block(source: str, job: str) -> str:
    lines = source.splitlines()
    start = lines.index(f"  {job}:")
    body = []
    for line in lines[start + 1 :]:
        if line.strip() and len(line) - len(line.lstrip()) <= 2:
            break
        body.append(line)
    return "\n".join(body)


def step_block(source: str, name: str) -> str:
    lines = source.splitlines()
    marker = f"    - name: {name}"
    start = lines.index(marker)
    body = []
    for line in lines[start + 1 :]:
        if line.startswith("    - name:"):
            break
        body.append(line)
    return "\n".join(body)


class WorkflowRunnerPolicyTests(unittest.TestCase):
    def test_build_uses_only_dispatch_and_owner_pr_routes(self) -> None:
        source = WORKFLOW.read_text(encoding="utf-8")
        self.assertNotRegex(source, r"(?m)^  push:")
        release_call = event_block(source, "workflow_call")
        for field in ("action:", "runner_pool:", "release_tag:", "release_sha:"):
            self.assertIn(field, release_call)
        target = event_block(source, "pull_request_target")
        self.assertIn("branches: [main]", target)
        self.assertIn(
            "types: [opened, reopened, synchronize, ready_for_review, labeled]",
            target,
        )
        self.assertNotRegex(source, r"(?m)^  pull_request:")

        release_notes = job_block(source, "release-notes")
        self.assertIn("github.event_name == 'pull_request_target'", release_notes)
        self.assertIn("github.actor == 'snapetech'", release_notes)
        self.assertIn("github.event.pull_request.head.repo.id == github.repository_id", release_notes)
        self.assertIn("github.event.pull_request.base.ref == 'main'", release_notes)
        self.assertIn("contains(github.event.pull_request.labels.*.name, 'action:ci')", release_notes)
        self.assertIn("contains(github.event.pull_request.labels.*.name, 'pool:private')", release_notes)

        build = job_block(source, "build")
        for token in (
            "github.ref == 'refs/heads/main'",
            "inputs.action == 'ci'",
            "inputs.action == 'publish'",
            "inputs.runner_pool == 'private'",
            "inputs.release_tag != ''",
            "inputs.release_sha != ''",
            "inputs.release_sha || github.sha",
            "Verify exact release tag source",
            'test "$(git rev-parse "${RELEASE_TAG}^{commit}")" = "$RELEASE_SHA"',
        ):
            self.assertIn(token, build)

    def test_build_matrix_supports_hosted_and_private_capacity_for_each_platform(self) -> None:
        source = WORKFLOW.read_text(encoding="utf-8")
        build = job_block(source, "build")
        self.assertIn("inputs.runner_pool == 'public'", build)
        self.assertIn("'windows-2025'", build)
        self.assertIn("'macos-15-intel'", build)
        self.assertIn("'ubuntu-24.04'", build)
        self.assertIn('"group":"ng-shared-main-jit"', build)
        self.assertIn('"ng-shared-main-chaptarrng"', build)
        self.assertIn('"ng-main-run-{0}"', build)
        self.assertIn("github.run_id", build)
        self.assertIn('"group":"packer-windows"', build)
        self.assertIn('"on-demand"', build)
        self.assertIn('"ChaptarrNG"', build)
        self.assertIn('"gha-{0}-{1}-build"', build)
        self.assertNotIn("|| matrix.os", build)
        self.assertNotIn("TRUSTED_MAIN_RUNNERS_ENABLED", source)

    def test_workflow_jobs_use_explicit_runner_routes(self) -> None:
        workflow_dir = WORKFLOW.parent
        hosted_routes = {
            "build.yml",
            "helm-handoff.yml",
            "bookshelfng-standalone-macos-acceptance.yml",
            "standalone-asset-validation.yml",
            "bookshelfng-api-differential.yml",
        }
        for workflow_path in sorted(workflow_dir.glob("*.yml")):
            source = workflow_path.read_text(encoding="utf-8")
            lines = source.splitlines()
            for index, line in enumerate(lines):
                if not re.match(r"^\s*runs-on:", line):
                    continue
                if workflow_path.name in hosted_routes and (
                    "inputs.runner_pool == 'public'" in line
                    or line.rstrip().endswith("runs-on: >-")
                ):
                    self.assertIn("fromJSON", source, str(workflow_path))
                    continue
                self.assertNotRegex(
                    line,
                    r"runs-on:.*\b(?:ubuntu-latest|ubuntu-[0-9]|windows-latest|macos-[0-9])",
                    str(workflow_path),
                )
                self.assertNotRegex(
                    line,
                    r"\|\|\s*['\"]?(?:ubuntu-|windows-latest|macos-[0-9])|\|\|\s*matrix\.os",
                    str(workflow_path),
                )
                if line.rstrip().endswith("runs-on:"):
                    block = "\n".join(lines[index : index + 3])
                    self.assertIn("group:", block, str(workflow_path))
                else:
                    self.assertRegex(
                        line,
                        r"self-hosted|group|fromJSON",
                        f"{workflow_path}: {line}",
                    )

    def test_bookshelfng_differential_uses_runner_temp_in_step_context(self) -> None:
        path = WORKFLOW.parent / "bookshelfng-api-differential.yml"
        source = path.read_text(encoding="utf-8")
        differential = job_block(source, "differential-smoke")
        job_environment = differential.split("    steps:", 1)[0]
        self.assertNotIn("runner.temp", job_environment)
        run_start = source.index(
            "      - name: Run read/write API differential"
        )
        upload_start = source.index(
            "      - name: Upload sanitized differential results and startup logs",
            run_start,
        )
        run_step = source[run_start:upload_start]
        self.assertIn("BOOKSHELFNG_DIFFERENTIAL_WORK_DIR: ${{ runner.temp }}", run_step)
        self.assertIn(
            "path: ${{ runner.temp }}/bookshelfng-api-differential-${{ github.run_id }}/evidence/",
            source,
        )

    def test_bookshelfng_differential_mounts_contract_comparator_in_isolated_runner(self) -> None:
        integration = WORKFLOW.parents[2] / "integration"
        runner = (integration / "run-bookshelfng-api-differential.sh").read_text(encoding="utf-8")
        compose = (integration / "bookshelfng-api-differential.compose.yml").read_text(encoding="utf-8")
        self.assertIn("export BOOKSHELFNG_API_CONTRACT_SCRIPT=", runner)
        self.assertIn("${BOOKSHELFNG_API_CONTRACT_SCRIPT:?", compose)
        self.assertIn("target: /harness/bookshelfng-api-contract.mjs", compose)

    def test_standalone_asset_validation_allows_trusted_public_runners(self) -> None:
        path = WORKFLOW.parent / "standalone-asset-validation.yml"
        source = path.read_text(encoding="utf-8")
        self.assertTrue(
            source.startswith("# ng-runner-policy: trusted-hosted-or-private-jit-v1"),
            path.name,
        )
        self.assertIn("default: public", event_block(source, "workflow_dispatch"))

        for job_name in (
            "verify-runtime-pack-install",
            "frontend",
            "extra-platform-runtimes",
            "extra-standalone",
            "standalone",
        ):
            job = job_block(source, job_name)
            self.assertIn("inputs.runner_pool == 'public'", job, job_name)
            self.assertIn("inputs.runner_pool == 'private'", job, job_name)
            self.assertIn("'ubuntu-24.04'", job, job_name)
            self.assertIn('"group":"ng-shared-main-jit"', job, job_name)

    def test_macos_handoff_runs_do_not_serialize_public_and_private_pools(self) -> None:
        path = WORKFLOW.parent / "bookshelfng-standalone-macos-acceptance.yml"
        source = path.read_text(encoding="utf-8")
        self.assertIn(
            "group: bookshelfng-standalone-macos-acceptance-${{ github.ref }}-${{ inputs.runner_pool }}-${{ inputs.architecture }}",
            source,
        )

    def test_windows_release_jobs_use_the_private_packer_route(self) -> None:
        windows_release_jobs = (
            ("release.yml", "windows-migration-installer", "windows-migration-installer"),
            ("release-distribution.yml", "build-chocolatey", "build-chocolatey"),
            ("release-distribution.yml", "publish-chocolatey", "publish-chocolatey"),
        )
        for workflow_name, job_name, label_suffix in windows_release_jobs:
            source = (WORKFLOW.parent / workflow_name).read_text(encoding="utf-8")
            job = job_block(source, job_name)
            runner_match = re.search(r"(?m)^    runs-on: (?P<runner>[^\n]+)$", job)
            self.assertIsNotNone(runner_match, f"{workflow_name}:{job_name}")
            runner = runner_match.group("runner").replace("\\", "")
            self.assertIn('"group":"packer-windows"', runner)
            self.assertIn('"on-demand"', runner)
            self.assertIn('"ChaptarrNG"', runner)
            self.assertIn(f'"gha-{{0}}-{label_suffix}"', runner)
            self.assertNotRegex(runner, r"windows-latest|ubuntu-latest|macos-[0-9]")

        for workflow_name in ("build.yml", "release.yml", "release-distribution.yml"):
            source = (WORKFLOW.parent / workflow_name).read_text(encoding="utf-8")
            self.assertNotRegex(source, r"(?m)^\s+runs-on: windows-latest\s*$")

    def test_release_workflows_keep_explicit_runner_policy(self) -> None:
        build = (WORKFLOW.parent / "build.yml").read_text(encoding="utf-8")
        self.assertTrue(
            build.startswith(
                "# ng-runner-policy: trusted-hosted-or-private-jit-v1\n# packer-windows-jit-v1"
            )
        )
        self.assertIn("default: public", event_block(build, "workflow_dispatch"))
        self.assertIn("inputs.runner_pool == 'public'", job_block(build, "build"))
        for workflow_name in ("release.yml", "release-distribution.yml"):
            source = (WORKFLOW.parent / workflow_name).read_text(encoding="utf-8")
            self.assertTrue(
                source.startswith(
                    "# ng-runner-policy: private-jit-only-v1\n# packer-windows-jit-v1"
                ),
                workflow_name,
            )
            self.assertNotRegex(
                source,
                r"(?m)^\s*runs-on:\s*(?:ubuntu-latest|ubuntu-[0-9]+|windows-latest|macos-[0-9]+)",
                workflow_name,
            )

    def test_release_dispatch_builds_the_exact_main_tag(self) -> None:
        source = (WORKFLOW.parent / "release.yml").read_text(encoding="utf-8")
        self.assertNotRegex(source, r"(?m)^  push:")
        dispatch = event_block(source, "workflow_dispatch")
        for field, value in (("action", "publish"), ("runner_pool", "private")):
            input_match = re.search(
                rf"(?ms)^      {field}:\n(?P<body>.*?)(?=^      [A-Za-z0-9_-]+:|\Z)",
                dispatch,
            )
            self.assertIsNotNone(input_match, field)
            self.assertIn("required: true", input_match.group("body"))
            self.assertIn(f"          - {value}", input_match.group("body"))
        self.assertIn("release_tag:", dispatch)
        self.assertIn("required: true", dispatch)
        run_name = source.split("run-name:", 1)[1].split("\non:", 1)[0]
        self.assertIn("format('action:{0}:pool:{1}:tag:{2}'", run_name)
        self.assertIn("inputs.release_tag", run_name)

        preflight = job_block(source, "preflight")
        for token in (
            "github.ref == 'refs/heads/main'",
            "inputs.action == 'publish'",
            "inputs.runner_pool == 'private'",
            "RELEASE_TAG: ${{ inputs.release_tag }}",
            'TAG_VERSION="$(git show "$TAG:package.json"',
            'git merge-base --is-ancestor "$RELEASE_SHA" refs/remotes/origin/main',
            "release_sha=$RELEASE_SHA",
            "packaging/scripts/check_stable_release_version.py",
        ):
            self.assertIn(token, preflight)

        validate = job_block(source, "validate")
        for token in (
            "action: publish",
            "runner_pool: private",
            "release_tag: ${{ needs.preflight.outputs.release_tag }}",
            "release_sha: ${{ needs.preflight.outputs.release_sha }}",
        ):
            self.assertIn(token, validate)

        release = job_block(source, "release")
        self.assertIn("ref: ${{ needs.preflight.outputs.release_sha }}", release)
        self.assertIn("EXPECTED_RELEASE_SHA: ${{ needs.preflight.outputs.release_sha }}", release)
        self.assertIn("RELEASE_TAG: ${{ needs.preflight.outputs.release_tag }}", release)

        windows = job_block(source, "windows-migration-installer")
        for token in (
            "github.ref == 'refs/heads/main'",
            "inputs.action == 'publish'",
            "inputs.runner_pool == 'private'",
            "needs: [preflight, release, standalone-assets]",
        ):
            self.assertIn(token, windows)

        publisher = job_block(source, "publish-release")
        self.assertIn("EXPECTED_RELEASE_TAG: ${{ needs.preflight.outputs.release_tag }}", publisher)
        self.assertIn('"$RELEASE_TAG" != "$EXPECTED_RELEASE_TAG"', publisher)

        packages = job_block(source, "publish-packages")
        self.assertIn("action: publish", packages)
        self.assertIn("runner_pool: private", packages)

    def test_distribution_dispatch_requires_main_private_publish_route(self) -> None:
        source = (WORKFLOW.parent / "release-distribution.yml").read_text(encoding="utf-8")
        self.assertNotRegex(source, r"(?m)^  push:")
        dispatch = event_block(source, "workflow_dispatch")
        for field, value in (("action", "publish"), ("runner_pool", "private")):
            input_match = re.search(
                rf"(?ms)^      {field}:\n(?P<body>.*?)(?=^      [A-Za-z0-9_-]+:|\Z)",
                dispatch,
            )
            self.assertIsNotNone(input_match, field)
            self.assertIn("required: true", input_match.group("body"))
            self.assertIn(f"          - {value}", input_match.group("body"))
        call = event_block(source, "workflow_call")
        self.assertIn("action:", call)
        self.assertIn("runner_pool:", call)
        run_name = source.split("run-name:", 1)[1].split("\non:", 1)[0]
        self.assertIn("inputs.action", run_name)
        self.assertIn("inputs.runner_pool", run_name)
        self.assertNotIn("inputs.tag", run_name)

        target = job_block(source, "release-target")
        for token in (
            "github.ref == 'refs/heads/main'",
            "inputs.action == 'publish'",
            "inputs.runner_pool == 'private'",
            "EVENT_NAME: ${{ github.event_name }}",
            "ACTION: ${{ inputs.action }}",
            "RUNNER_POOL: ${{ inputs.runner_pool }}",
            'git merge-base --is-ancestor "$release_sha" refs/remotes/origin/main',
        ):
            self.assertIn(token, target)

        self.assertIn("needs: [release-target]", job_block(source, "credentials"))
        for name in ("build-chocolatey", "publish-chocolatey"):
            job = job_block(source, name)
            for token in (
                "github.ref == 'refs/heads/main'",
                "inputs.action == 'publish'",
                "inputs.runner_pool == 'private'",
                '"group":"packer-windows"',
            ):
                self.assertIn(token, job)

    def test_buildkit_proxy_is_limited_to_private_trusted_main_and_release_jobs(self) -> None:
        source = WORKFLOW.read_text(encoding="utf-8")
        docker_job = job_block(source, "docker-image-build")
        expected_proxy_gate = "BUILDKIT_EGRESS_PROXY: ${{ inputs.runner_pool == 'private' && (github.ref == 'refs/heads/main' || startsWith(github.ref, 'refs/tags/v')) && 'http://10.0.2.100:3128' || '' }}"
        self.assertIn(expected_proxy_gate, docker_job)

        buildx = step_block(source, "Set up Docker Buildx")
        build = step_block(
            source, "Build image for all published architectures without pushing"
        )
        for proxy_name in ("HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy"):
            self.assertIn(
                f"env.{proxy_name}=${{{{ env.BUILDKIT_EGRESS_PROXY }}}}", buildx
            )
            self.assertIn(
                f"{proxy_name}=${{{{ env.BUILDKIT_EGRESS_PROXY }}}}", build
            )
        buildx_driver_options = [
            line.strip()
            for line in buildx.splitlines()
            if line.strip().startswith("env.")
        ]
        self.assertTrue(buildx_driver_options)
        self.assertTrue(all("," not in option for option in buildx_driver_options))
        self.assertIn("NO_PROXY=localhost,127.0.0.1", build)

    def test_installed_helm_handoff_uses_the_private_proxy_only_on_private_runners(self) -> None:
        helm_workflow = (WORKFLOW.parent / "helm-handoff.yml").read_text(encoding="utf-8")
        installed_handoff = WORKFLOW.parents[2] / "scripts" / "test_bookshelfng_helm_install_handoff.sh"
        script = installed_handoff.read_text(encoding="utf-8")
        expected_proxy_gate = "BUILDKIT_EGRESS_PROXY: ${{ inputs.runner_pool == 'private' && github.ref == 'refs/heads/main' && 'http://10.0.2.100:3128' || '' }}"

        self.assertIn(expected_proxy_gate, helm_workflow)
        for name in ("HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy"):
            self.assertIn(
                f'--build-arg "{name}=${{BUILDKIT_EGRESS_PROXY}}"',
                script,
            )
        self.assertIn("--build-arg NO_PROXY=localhost,127.0.0.1", script)
        self.assertIn("--build-arg no_proxy=localhost,127.0.0.1", script)

    def test_owner_pr_trigger_dispatches_only_metadata_to_private_validation(self) -> None:
        source = WORKFLOW.read_text(encoding="utf-8")

        self.assertIn("workflow_dispatch:", source)
        self.assertNotRegex(source, r"(?m)^  push:")
        self.assertNotRegex(source, r"(?m)^  pull_request:")

        dispatcher = job_block(source, "dispatch-owner-pr")
        for token in (
            "github.event_name == 'pull_request_target'",
            "github.actor == 'snapetech'",
            "github.event.pull_request.user.login == 'snapetech'",
            "github.event.pull_request.head.repo.full_name == github.repository",
            "github.event.pull_request.head.repo.id == github.repository_id",
            "github.event.pull_request.base.ref == 'main'",
            "contains(github.event.pull_request.labels.*.name, 'action:ci')",
            "contains(github.event.pull_request.labels.*.name, 'pool:private')",
            "actions: write",
            "contents: none",
            '"ref": "main"',
        ):
            self.assertIn(token, dispatcher)
        self.assertIn('"action": "ci"', dispatcher)
        self.assertIn('"runner_pool": "private"', dispatcher)
        self.assertIn("owner-pr:{0}:{1}:{2}:action:{3}:pool:{4}", source)
        self.assertNotIn("actions/checkout", dispatcher)
        self.assertNotIn("secrets.", dispatcher)

        policy = job_block(source, "private-runner-policy")
        self.assertIn("github.ref == 'refs/heads/main'", policy)
        self.assertIn("gha-{0}-private-runner-policy", policy)

        validation = job_block(source, "owner-pr-validation")
        self.assertIn("github.actor == 'github-actions[bot]'", validation)
        self.assertIn("inputs.action == 'ci'", validation)
        self.assertIn("inputs.runner_pool == 'private'", validation)
        self.assertIn("ref: ${{ inputs.pr_sha }}", validation)
        self.assertIn("persist-credentials: false", validation)
        self.assertIn("test \"$(git rev-parse HEAD)\" = \"$EXPECTED_PR_SHA\"", validation)
        self.assertIn("node scripts/check-release-notes.mjs", validation)
        self.assertIn("python3 scripts/test_workflow_runner_policy.py", validation)

        reporter = job_block(source, "report-owner-pr-check")
        self.assertIn("checks: write", reporter)
        self.assertIn("name: Report trusted owner PR validation", reporter)
        self.assertIn('"name": "ChaptarrNG owner PR validation"', reporter)

        release = (WORKFLOW.parent / "release.yml").read_text(encoding="utf-8")
        release_validation = job_block(release, "validate")
        for permission in (
            "actions: write",
            "checks: write",
            "contents: read",
            "pull-requests: read",
        ):
            self.assertIn(permission, release_validation)

    def test_dispatches_expose_their_authorized_runner_pools(self) -> None:
        workflows = (
            (WORKFLOW, "ci", WORKFLOW.read_text(encoding="utf-8")),
            (
                WORKFLOW.parent / "helm-handoff.yml",
                "ci",
                (WORKFLOW.parent / "helm-handoff.yml").read_text(encoding="utf-8"),
            ),
            (
                WORKFLOW.parent / "security.yml",
                "scan",
                (WORKFLOW.parent / "security.yml").read_text(encoding="utf-8"),
            ),
            (
                WORKFLOW.parent / "semgrep-community.yml",
                "scan",
                (WORKFLOW.parent / "semgrep-community.yml").read_text(encoding="utf-8"),
            ),
            (
                WORKFLOW.parent / "standalone-asset-validation.yml",
                "ci",
                (WORKFLOW.parent / "standalone-asset-validation.yml").read_text(encoding="utf-8"),
            ),
            (
                WORKFLOW.parent / "bookshelfng-api-differential.yml",
                "ci",
                (WORKFLOW.parent / "bookshelfng-api-differential.yml").read_text(encoding="utf-8"),
            ),
            (
                WORKFLOW.parent / "trivy-filesystem.yml",
                "scan",
                (WORKFLOW.parent / "trivy-filesystem.yml").read_text(encoding="utf-8"),
            ),
        )
        for path, action, source in workflows:
            dispatch = event_block(source, "workflow_dispatch")
            if path.name == "helm-handoff.yml":
                self.assertNotRegex(source, r"(?m)^  push:")
            public_routes = {
                "build.yml",
                "helm-handoff.yml",
                "standalone-asset-validation.yml",
                "bookshelfng-api-differential.yml",
            }
            pool_options = ("public", "private") if path.name in public_routes else ("private",)
            for field, options in (("action", (action,)), ("runner_pool", pool_options)):
                input_match = re.search(
                    rf"(?ms)^      {field}:\n(?P<body>.*?)(?=^      [A-Za-z0-9_-]+:|\Z)",
                    dispatch,
                )
                self.assertIsNotNone(input_match, f"{path.name}: missing {field}")
                input_body = input_match.group("body")
                self.assertIn("required: true", input_body, f"{path.name}: {field}")
                self.assertIn("type: choice", input_body, f"{path.name}: {field}")
                for option in options:
                    self.assertRegex(
                        input_body,
                        rf"(?m)^          - {re.escape(option)}$",
                        f"{path.name}: {field}",
                    )
                if path.name in public_routes and field == "runner_pool":
                    self.assertIn("default: public", input_body, f"{path.name}: {field}")

            run_name = source.split("run-name:", 1)[1].split("\non:", 1)[0]
            self.assertIn("inputs.action", run_name, path.name)
            self.assertIn("inputs.runner_pool", run_name, path.name)
            self.assertIn("action:", run_name, path.name)
            self.assertIn("pool:", run_name, path.name)

    def test_fedora_rpm_acceptance_is_an_exact_head_hosted_or_private_dispatch(self) -> None:
        source = WORKFLOW.read_text(encoding="utf-8")
        self.assertIn("rpm_bridge_only:", source)
        self.assertIn("inputs.rpm_bridge_only != true", job_block(source, "build"))

        rpm_bridge_path = (
            WORKFLOW.parents[2]
            / "packaging"
            / "scripts"
            / "test-build-bookshelfng-rpm-bridge.sh"
        )
        rpm_bridge = rpm_bridge_path.read_text(encoding="utf-8")
        self.assertIn("src/Directory.Build.props", rpm_bridge)
        self.assertIn("candidate-version.txt", rpm_bridge)
        self.assertIn("CHAPTARRNG_CANDIDATE_VERSION=$candidate_version", rpm_bridge)
        self.assertIn("ChaptarrNG-$candidate_version-linux-x64.tar.gz", rpm_bridge)
        self.assertIn('"$candidate_version"', rpm_bridge)
        self.assertNotIn("0.9.977", rpm_bridge)

        rpm_job = job_block(source, "rpm-bridge-acceptance")
        for token in (
            "github.event_name == 'workflow_dispatch'",
            "inputs.rpm_bridge_only == true",
            "github.ref == 'refs/heads/main'",
            "inputs.runner_pool == 'public'",
            "'ubuntu-24.04'",
            '"group":"ng-shared-main-jit"',
            '"gha-{0}-rpm-bridge-acceptance"',
            "ref: ${{ github.sha }}",
            'test "$(git rev-parse HEAD)" = "$GITHUB_SHA"',
            "TASK_SCRATCH_DIR",
            "packaging/scripts/test-build-bookshelfng-rpm-bridge.sh",
        ):
            self.assertIn(token, rpm_job)

        self.assertIn("inputs.rpm_bridge_only != true", job_block(source, "postgres-migrations"))
        self.assertIn("inputs.rpm_bridge_only != true", job_block(source, "docker-image-build"))

    def test_legacy_channel_contracts_are_an_exact_head_hosted_or_private_dispatch(self) -> None:
        source = WORKFLOW.read_text(encoding="utf-8")
        self.assertIn("channel_contracts_only:", source)
        self.assertIn("inputs.channel_contracts_only != true", job_block(source, "build"))
        job = job_block(source, "channel-contracts")
        for token in (
            "github.event_name == 'workflow_dispatch'",
            "inputs.channel_contracts_only == true",
            "github.ref == 'refs/heads/main'",
            "inputs.runner_pool == 'public'",
            "'ubuntu-24.04'",
            '"group":"ng-shared-main-jit"',
            '"gha-{0}-channel-contracts"',
            "ref: ${{ github.sha }}",
            'test "$(git rev-parse HEAD)" = "$GITHUB_SHA"',
            "node --test packaging/scripts/test-bookshelf-legacy-channels.mjs",
            "python3 scripts/test_workflow_runner_policy.py",
        ):
            self.assertIn(token, job)
        self.assertIn("inputs.channel_contracts_only != true", job_block(source, "postgres-migrations"))
        self.assertIn("inputs.channel_contracts_only != true", job_block(source, "docker-image-build"))

    def test_security_scans_use_private_jit_without_hosted_fallback(self) -> None:
        workflow_dir = WORKFLOW.parent
        self.assertFalse((workflow_dir / "codeql.yml").exists())
        self.assertFalse((workflow_dir / "codeql.yml.disabled").exists())
        self.assertFalse((workflow_dir.parent / "codeql").exists())
        for path in workflow_dir.glob("*.yml"):
            self.assertNotIn("github/codeql-action/", path.read_text(encoding="utf-8"), path.name)
        workflow_name = "semgrep-community.yml"
        source = (workflow_dir / workflow_name).read_text(encoding="utf-8")
        job = job_block(source, "semgrep")
        runner_match = re.search(r"(?m)^    runs-on: (?P<runner>[^\n]+)$", job)
        self.assertIsNotNone(runner_match, workflow_name)
        runner = runner_match.group("runner").replace("\\", "")
        self.assertIn('"group":"ng-shared-main-jit"', runner)
        self.assertIn('"self-hosted"', runner)
        self.assertIn('"ng-main-run-{0}"', runner)
        self.assertNotIn("ubuntu-latest", runner)
        semgrep = (workflow_dir / "semgrep-community.yml").read_text(encoding="utf-8")
        self.assertIn("docker info", semgrep)
        self.assertIn("docker run", semgrep)

    def test_playwright_install_uses_linux_dependencies(self) -> None:
        source = WORKFLOW.read_text(encoding="utf-8")
        self.assertIn("Install Chromium and system dependencies on Linux", source)
        self.assertIn("install --with-deps chromium", source)
        self.assertNotIn("runner.environment", source)
        linux_install = step_block(source, "Install Chromium and system dependencies on Linux")
        self.assertNotIn("pwsh", linux_install)
        self.assertIn('"$driver/node/linux-x64/node"', linux_install)
        self.assertIn('"$driver/package/cli.js"', linux_install)

    def test_postgres_restore_uses_client_matching_server_major(self) -> None:
        source = WORKFLOW.read_text(encoding="utf-8")
        migrations = job_block(source, "postgres-migrations")
        self.assertIn("image: postgres:16", migrations)
        self.assertIn("sudo apt-get install -y postgresql-client-16", migrations)
        self.assertIn("if [[ \"$RUNNER_POOL\" == \"public\" ]]", migrations)
        self.assertIn("PostgreSQL 16 client binaries are required for the migration smoke.", migrations)
        self.assertIn('postgres:16 "$tool" "$@"', migrations)
        self.assertIn("/usr/lib/postgresql/16/bin", migrations)
        self.assertIn("GITHUB_PATH", migrations)

    def test_frontend_build_runs_after_tests_and_before_publish(self) -> None:
        build = job_block(WORKFLOW.read_text(encoding="utf-8"), "build")

        tests = build.index("- name: Test\n")
        frontend_build = build.index("- name: Build frontend")
        publish = build.index("- name: Publish")

        self.assertIn(
            "dotnet test src/Chaptarr.Core.Test/Chaptarr.Core.Test.csproj",
            build[tests:frontend_build],
        )
        self.assertLess(frontend_build, publish)


if __name__ == "__main__":
    unittest.main()

