---
category: security
audience: users, operators
area: bookshelf
action: Set BOOKSHELF_OIDC_ALLOW_PRIVATE_ISSUER=true when using a private OIDC issuer.
breaking: false
---
The paged Bookshelf API now defaults to 50 records, sorts by stored title, and includes each book's author and links. OIDC requests reject private or reserved targets unless the existing private-issuer setting is enabled, while retaining configured proxy and certificate handling.
