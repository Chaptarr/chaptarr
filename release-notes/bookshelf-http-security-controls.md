---
category: security
audience: operators
area: http
action: Configure trusted proxy addresses and allowed origins when using forwarded headers or cross-origin browser clients.
breaking: true
---
ChaptarrNG now trusts forwarded headers only from configured proxies, disables cross-origin access until origins are allowed, and limits API traffic and request bodies. The backup restore upload keeps its separate 1 GiB limit.
