---
category: fixed
audience: users, operators
area: packaging
action: none
breaking: false
---
The standard ChaptarrNG image advertises its fresh-install port `8789`. Legacy BookshelfNG package adapters use that same image digest, preserve their service port, and detect legacy SQLite or PostgreSQL sources for supervised migration; no second compatibility image is built.
