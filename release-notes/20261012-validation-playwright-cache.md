---
category: security
audience: operators
area: validation
action: none
breaking: false
---

The isolated NUnit task now receives the run-specific Playwright browser cache it needs for Linux direct-download tests, without restoring access to the operator's home or unrelated environment variables.
