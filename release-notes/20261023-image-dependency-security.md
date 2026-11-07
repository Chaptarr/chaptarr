---
category: security
audience: operators
area: dependencies
action: none
breaking: false
---
Cover resizing now uses SkiaSharp, and PDF metadata extraction uses the .NET 10-compatible, MIT-licensed PDFsharp 6.2.4 instead of PdfSharpCore. Neither path depends on ImageSharp, so source and package builds no longer require the Six Labors build license or include ImageSharp's transitive dependency. Resized JPEG, PNG, JFIF, and WebP covers retain their requested format.
