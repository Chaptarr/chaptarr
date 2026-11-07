---
category: security
audience: operators
area: dependencies
action: none
breaking: false
---
The frontend build and lint dependency tree now rejects brace and parenthesis patterns nested deeper than 256 levels, preventing the recursive parser from exhausting the JavaScript call stack while the upstream package has no published fix.
