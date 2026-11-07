---
category: changed
audience: operators
area: build
action: Configure SIXLABORS_LICENSE_KEY when building from source; official images need no build key at runtime.
breaking: true
---
ImageSharp 4.1.3 requires a valid Six Labors license when ChaptarrNG is built from source. Official images already include a licensed build, so no build key must be configured at runtime. All use remains subject to Six Labors' license terms.
