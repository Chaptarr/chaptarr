# Security scanning

The Unraid companion repository contains the package template, GitHub Actions
workflows, and shell tooling. Its Semgrep Community Edition workflow scans
workflow and shell files with the free `p/default` rules and metrics disabled.
The Trivy workflow scans applicable workflow configuration and source files.
The checkout has no application dependency manifests, so the Trivy dependency
vulnerability scanner is skipped.

Both workflows run for pushes, pull requests, scheduled scans, and manual
dispatches. Semgrep findings are uploaded to GitHub code scanning when the
workflow has permission; SARIF upload is skipped for pull requests from
external forks. Trivy scans the checked-out repository in its hosted job and
does not publish application artifacts.

Run the applicable local checks with:

```sh
./scripts/security-scan-local.sh
```

The wrapper reports findings without failing by default. Set
`SEMGREP_FAIL_ON_FINDINGS=1` or `TRIVY_EXIT_CODE=1` to make local findings fail
the command. `--staged` scans staged Semgrep inputs only; Trivy scans the whole
checkout. `--ci` disables repository-controlled Trivy configuration and ignore
files, as the hosted workflow does.

Semgrep uses the local Community Edition engine with `p/default` and metrics
off. The rule pack is fetched from the public Semgrep Registry when it is
missing or refreshed, so the first scan needs network access. For disconnected
runs, set `SEMGREP_CONFIG` to a cached local rules directory and
`SEMGREP_OFFLINE=1` after caching the Semgrep package. Semgrep uses the POSIX
I/O backend by default for compatibility with restricted Linux runners; set
`SEMGREP_EIO_BACKEND` only when a runner needs a different backend.

Trivy downloads its vulnerability and check databases automatically. Use
`TRIVY_OFFLINE=1` only after those databases are cached. The default timeout is
20 minutes for first downloads; set `TRIVY_TIMEOUT` to change it.

Run `./scripts/update-security-tools.sh` to refresh the local Semgrep package
cache, pull the current Semgrep fallback image, and refresh Trivy's database.
Install the optional pre-commit hook with
`./scripts/install-security-hooks.sh` if you want staged-file Semgrep scans
before committing.
