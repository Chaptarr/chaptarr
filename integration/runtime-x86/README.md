# Native x86 runtime qualification

The custom .NET 10.0.12 CoreCLR pack must execute this probe before its producer
job succeeds. The archive workflow compiles it with the pinned SDK, then runs it
in a native 32-bit Debian container with the candidate runtime, no network,
read-only inputs and no additional capabilities. Direct, compiled and interpreted
nullable-double accessors must each return the exact value one million times.
FluentValidation must accept valid values and reject negative/missing values.
Compacting collections both inside and after a catch must preserve retained
delegates, their closure and referenced objects. The after-catch case is a control;
it does not replace the inside-catch check.

The November 6 retained runtime pack passes the earlier nullable checks but fails
the new inside-catch check on its first collection. A small dependency-free
reproduction has the same result, while its after-catch control passes. Disabling
background GC or tiered compilation does not qualify that pack: expanded native
test runs still abort. This is an open runtime qualification failure, separate
from the return-buffer correction below. Keep this new check fail-closed when
reusing old runtime packs; do not infer installed startup safety from the earlier
probe's success. Private invocations, logs and core dumps are retained under
`/var/tmp/chaptarrng-native-standalone-20261105/x86-core-tests/`.

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
