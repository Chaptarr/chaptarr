---
category: security
audience: operators
area: validation
action: run local tests through trusted launcher
breaking: false
---

Local test runs now require a trusted Linux sandbox that hides operator files, ambient secrets, service sockets, and network access from candidate test code while preserving an explicitly selected read-only Playwright cache. Dependency roots that resolve outside the candidate tree are rejected before they can be mounted into the sandbox.
