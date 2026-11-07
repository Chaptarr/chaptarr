---
category: fixed
audience: users, operators
area: monitoring
action: none
breaking: false
---

An explicit `monitored` change on an author API update now reaches its format-specific monitoring gates. Setting an author to unmonitored no longer gets undone by unchanged audiobook or ebook fields echoed in the request.
