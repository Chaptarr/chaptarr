---
category: fixed
audience: users, operators
area: authentication
action: none
breaking: true
---

ChaptarrNG now always requires the provider to affirm `email_verified=true` for OIDC sign-in. The legacy `OidcRequireEmailVerified=false` setting no longer disables this check. Providers must return a verified email claim.
