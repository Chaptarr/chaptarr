---
category: fixed
audience: users, operators
area: migration
action: none
breaking: false
---
The Windows migration installer keeps the existing Readarr service available for rollback until ChaptarrNG passes its health check. It records and restores the prior service startup mode and running state, and removes the disabled Readarr registration after the installer closes. Restore can recreate that registration if deferred cleanup already removed it.
