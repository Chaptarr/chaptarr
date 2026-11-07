---
category: fixed
audience: users, operators
area: monitoring
action: Restore a pre-upgrade backup or re-unmonitor affected authors if migration 104 already ran.
breaking: false
---
The author-monitoring migration now preserves disabled authors from older releases. Libraries that already ran the faulty migration must restore a prior backup or re-unmonitor affected authors because the removed legacy flags cannot be recovered automatically.
