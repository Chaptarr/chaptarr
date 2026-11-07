---
category: fixed
audience: operators
area: bookshelf
action: Set HARDCOVER=true or HARDCOVER=false when migrating bare numeric provider IDs.
breaking: false
---
BookshelfNG migration now requires explicit Hardcover or Goodreads provider mode for bare numeric IDs. Legacy tags resolve to the same standard ChaptarrNG image; migration receives mode from preserved channel configuration or an explicit operator choice and fails closed when it is unavailable.
