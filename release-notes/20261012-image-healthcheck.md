---
category: fixed
audience: users, operators
area: containers
action: none
breaking: false
---
Container images now use the bundled wget client to check the anonymous /ping endpoint, so Docker health status reflects application availability without relying on curl or other tools that are absent from the runtime image.
