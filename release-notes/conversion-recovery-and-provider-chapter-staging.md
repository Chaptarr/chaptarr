---
category: fixed
audience: users, operators
area: conversion
action: none
breaking: false
---
Detached audiobook conversions now retry after temporary scheduler or SQLite numeric-type errors instead of remaining queued until restart. Provider-supplied chapter data is preserved on filesystems that cannot create symbolic links by staging copies when space permits.
