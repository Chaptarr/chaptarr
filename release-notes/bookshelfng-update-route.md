category: changed
audience: users, operators
area: migration, updates
action: none
breaking: false
---

Fresh `main` installs use checksum-verified ChaptarrNG GitHub releases unless
an operator has explicitly selected another update source. The BookshelfNG
database importer also selects this route during migration. Package-managed
installs keep their package-manager update route.

The old Readarr update service cannot deliver the first ChaptarrNG release to
old BookshelfNG binaries. Standalone users therefore need one supported
package, installer, or archive handoff before later in-app updates work.
