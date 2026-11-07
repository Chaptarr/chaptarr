---
category: security
audience: operators
area: ci-security
action: none
breaking: false
---
Pull request validation now uses GitHub-hosted runners for every author, keeping persistent local runners on main and reducing exposure of local files. Editing a PR description reruns the release-note check without rebuilding.
