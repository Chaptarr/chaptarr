# Native x86 runtime qualification

The custom .NET 10.0.12 CoreCLR pack must execute this probe before its producer
job succeeds. The archive workflow compiles it with the pinned SDK, then runs it
in a native 32-bit Debian container with the candidate runtime, no network,
read-only inputs and no additional capabilities. Direct, compiled and interpreted
nullable-double accessors must each return the exact value one million times.
FluentValidation must accept valid values and reject negative/missing values.

The original runtime reproduces incorrect nullable returns and access violations
outside ChaptarrNG. The pinned upstream Unix x86
[`ThisPtrRetBufPrecodeWorker`](https://github.com/dotnet/runtime/blob/v10.0.12/src/coreclr/vm/i386/asmhelpers.S)
swaps EDI/ESI, while the managed delegate arguments use ECX/EDX, as implemented in
the [Windows x86 worker](https://github.com/dotnet/runtime/blob/v10.0.12/src/coreclr/vm/i386/asmhelpers.asm).
The runtime builder applies an exact, guarded source correction to those three
instructions and their comments. A changed upstream worker requires review.

An isolated diagnostic copy with that instruction correction passes all probe
cases; the untouched runtime fails. Diagnostic copies are not release artifacts.
Acceptance still requires the source-built runtime pack and the complete native
x86 installed migration, target health and rollback workflow. Keep the public
predecessor's separate fresh-log failure and explicit existing auxiliary-database
fixture visible in the handoff evidence.
