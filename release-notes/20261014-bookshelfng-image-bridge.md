---
category: changed
audience: operators
area: distribution
action: Pull and recreate containers that use a moving BookshelfNG image tag; pinned tags and digests still need a manual update.
breaking: false
---
ChaptarrNG releases map the mutable BookshelfNG latest, hardcover, and softcover GHCR tags to the standard multi-architecture image; Docker Hub mirrors them when credentials are configured. The workflow checks each published alias digest.
