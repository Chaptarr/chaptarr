---
category: fixed
audience: users
area: import
action: none
breaking: false
---
Manual Import's author and book pickers now explain when the library has no candidates or a filter has no matches, and offer a Clear filter action. The book picker shows a retry action when its list fails to load and hides old results until the current request succeeds, so a delayed or failed load cannot be mistaken for the selected author's books. Edition and quality selectors keep their title and Cancel control visible on failures and offer Retry; an empty quality list cannot be submitted.
