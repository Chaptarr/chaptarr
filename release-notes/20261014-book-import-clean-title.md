---
category: fixed
audience: users, operators
area: api
action: none
breaking: false
---
Importing a book through the API no longer fails with a database constraint error when the title has no cleaned form, and unexpected failures now return a server error instead of a validation error.
