---
category: fixed
audience: operators
area: validation
action: none
breaking: false
---
Queued pull request checks now fetch the pull request head before validating release notes, so merging or deleting its source branch no longer causes a missing-commit failure.
