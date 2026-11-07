---
category: fixed
audience: users
area: books
action: none
breaking: false
---
Paged Books keeps mass-search confirmation open until matching book IDs load and the search is queued. Failures in ID lookup, search, Select All, bulk Update, and RSS Sync include retry actions; retries reuse the same IDs, records, and format. Changing the filter or format cancels an unstarted search. Individual author and book refresh/search failures now show an error notification.
