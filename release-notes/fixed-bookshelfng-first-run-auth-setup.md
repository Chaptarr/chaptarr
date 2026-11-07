---
category: fixed
audience: operators
area: migration
action: Review first-run authentication after migration.
breaking: false
---
When a migrated installation has authentication configured but no local user account, ChaptarrNG now opens its first-run account setup instead of leaving the UI behind unusable credentials. The existing authentication mode is preserved when a user already exists.
