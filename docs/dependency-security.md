# Dependency security

ChaptarrNG tracks frontend and .NET dependencies in their lock and central
version files. Run the audits after dependency restore so results cover the
versions that CI and release builds will consume:

```sh
yarn audit
dotnet list src/Chaptarr.sln package --vulnerable --include-transitive
```

CI also runs Trivy dependency and secret scans and Semgrep Community Edition;
CodeQL analysis is disabled. `yarn audit` checks
the frontend build and lint dependency tree; the .NET command checks resolved
packages across the solution's target frameworks.

## Frontend parser depth limit

The `braces` package currently has no published release with a fix for
[GHSA-vfj7-8cjw-p6xm](https://github.com/advisories/GHSA-vfj7-8cjw-p6xm).
ChaptarrNG keeps a local MIT-licensed copy of `braces` 3.0.3 at
`patches/braces`, resolved through `package.json`. Its parser rejects nested
brace and parenthesis input beyond 256 levels before building a deeply nested
AST. This limits the recursive parser path that can exhaust the Node.js call
stack. The package is used by frontend build tooling; the change does not alter
the Chaptarr application runtime.

`patches/braces/PATCH.md` records the exact local modification. Remove the
copy and the `braces` resolution after an upstream release contains an
equivalent bound, then regenerate `yarn.lock` and rerun the full audit and
frontend build.

## .NET cryptography package

The solution centrally pins `System.Security.Cryptography.Pkcs` to `10.0.13`
in `src/Directory.Packages.props`. That is the patched .NET 10 version for
[GHSA-846f-f764-76mx](https://github.com/advisories/GHSA-846f-f764-76mx) and
[GHSA-2fmv-rv2c-59x7](https://github.com/advisories/GHSA-2fmv-rv2c-59x7).
Keep the central pin current when updating packages that bring Pkcs
transitively, and verify all target frameworks after restoring the solution.


## Frontend glob matching — November 6, 2026

The modern minimatch consumers resolve to 11.0.1 for
[GHSA-g3hv-75wg-m249](https://github.com/advisories/GHSA-g3hv-75wg-m249).
Legacy callable-API consumers retain explicit 3.1.4 or 5.x resolutions;
`babel-plugin-lodash` and `filelist` need these exceptions before the general
resolution. Verify the actual installed graph as well as the lockfile: a
successful install alone does not establish that every intended override applied.
The production frontend build, all 193 CJS tests, TypeScript checks and focused
application lint pass against the resolved graph. No minimatch 7–10 remains.

A fresh Yarn audit still reports build-tool findings for
[glob cancellation](https://github.com/advisories/GHSA-3g3m-2fjr-6pw4)
(four moderate dependency paths) and
[deepmerge object prototypes](https://github.com/advisories/GHSA-gcqx-w48m-w2h4)
(two low paths). These are not bundled application HTTP dependencies: glob is
used by local build tooling, and deepmerge is reached through the TypeScript
checker and Rollup resolver. Build configuration and patterns come from the
checked-out repository; untrusted code still requires isolated runners without
secrets. This bounds the currently observed exposure, rather than declaring the
advisories fixed. The audit has zero high/critical findings; deepmerge has no
published patched version in the advisory. Keep the findings visible and
reassess when updating those parent tools. No alert was dismissed.
