---
category: fixed
audience: users, operators
area: import
action: none
breaking: false
---
Unmapped files in an author folder are no longer rescanned after every author import when nothing has changed, which stops sustained high CPU use. Repeat rescans back off from one hour up to a day and resume at once when the files or author catalog change.
