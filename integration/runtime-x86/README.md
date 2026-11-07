# Native x86 runtime qualification

The custom .NET 10.0.12 CoreCLR pack must execute this probe before its producer
job succeeds. The archive workflow compiles it with the pinned SDK, then runs it
in a native 32-bit Debian container with the candidate runtime, no network,
read-only inputs and no additional capabilities. Direct, compiled and interpreted
nullable-double accessors must each return the exact value one million times.
FluentValidation, including a nested child validator, must accept valid values
and reject negative/missing values.
Compacting collections both inside and after a catch must preserve retained
delegates, their closure and referenced objects. The after-catch case is a control;
it does not replace the inside-catch check.

The retained November 6 runtime pack passes the nullable checks but fails the
inside-catch check on its first collection. The runtime builder therefore applies
an exact Unix x86 correction that tracks movable root slots during both promotion
and relocation and ignores a repeated relocation of the same slot. The corrected
runtime passes repeated compacting collections both inside and after catch
handlers. Pinned roots and every non-Unix-x86 target retain upstream behavior.
Keep this check fail-closed when reusing old runtime packs; do not infer installed
startup safety from the earlier probe's success.

The corrected runtime also accounts for the complete current-head Core test
inventory under native x86. SQLite-heavy groups run in bounded isolated test
processes because one long-lived 32-bit VSTest host can abort during cleanup after
thousands of cases even when the container reports no memory-limit event. Every
runnable discovered case has a passing result; the
remaining entries are intentional skips, the explicit live BookLore contract,
or result-name aliases from Unicode and custom scorecard cases. This sharding is
test-host containment, not a waiver of installed startup, migration or rollback
acceptance. Structured private results are retained for the handoff; raw crash
and root-trace diagnostics are not retained.

The original runtime reproduces incorrect nullable returns and access violations
outside ChaptarrNG. The pinned upstream Unix x86
[`ThisPtrRetBufPrecodeWorker`](https://github.com/dotnet/runtime/blob/v10.0.12/src/coreclr/vm/i386/asmhelpers.S)
swaps EDI/ESI, while the managed delegate arguments use ECX/EDX, as implemented in
the [Windows x86 worker](https://github.com/dotnet/runtime/blob/v10.0.12/src/coreclr/vm/i386/asmhelpers.asm).
The runtime builder applies an exact, guarded source correction to those three
instructions and their comments. A changed upstream worker requires review.

An isolated diagnostic copy with that instruction correction passes the original
nullable/validation cases; the untouched runtime fails those cases. That result
predates the exception/collection regression above. Diagnostic copies are not release artifacts.
Acceptance still requires the source-built runtime pack and the complete native
x86 installed migration, target health and rollback workflow. Keep the public
predecessor's separate fresh-log failure and explicit existing auxiliary-database
fixture visible in the handoff evidence.
