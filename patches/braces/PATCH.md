# Local security patch

This is a local copy of `braces` 3.0.3, licensed under MIT. The package is
resolved only in the frontend build and lint dependency tree.

The parser rejects brace or parenthesis nesting deeper than 256 before it can
build an AST that overflows the JavaScript call stack. This addresses the
recursive-walker risk tracked as CVE-2026-93687 / GHSA-vfj7-8cjw-p6xm. The
published upstream package has no fixed release, so this small local patch
keeps the toolchain protected until an upstream fix is available.

Remove this copy and the `braces` resolution from `package.json` when a
published upstream release includes an equivalent bound.
