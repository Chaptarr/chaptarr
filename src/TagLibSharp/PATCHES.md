# TagLibSharp fork

This directory contains the TagLibSharp source used by BookshelfNG, based on
[`Lidarr/taglib-sharp`](https://github.com/Lidarr/taglib-sharp) commit
`84f0a1860d2e059a74b91a4d5e1f2b4326e79d40` and licensed under LGPL-2.1.

BookshelfNG adds the opaque MPEG-4 `chpl` box implementation from
[`mono/taglib-sharp#377`](https://github.com/mono/taglib-sharp/pull/377) so tag
writes preserve Nero chapter data byte-for-byte. See `COPYING` for the library
license.

Line endings and trailing whitespace are normalized in the vendored text files;
this formatting-only cleanup does not change the imported code behavior.
