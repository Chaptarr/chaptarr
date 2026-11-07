# Portable encrypted backup compatibility

The retirement candidate adds the same passphrase-encrypted `.enc` backup
format used by BookshelfNG. It supports exporting from ChaptarrNG and restoring
that file through **System → Backup → Restore Backup**. This code is not in the
published ChaptarrNG `v0.9.977` release; BookshelfNG remains in service while
the retirement freeze is active.

The portable backup format is included in PR #131 application-code commit
`b7e900d49648269d5118bbfba44289c0200b095b`, introduced and cross-checked
against BookshelfNG source earlier in the branch. Exact-head Build
[39292223717](https://github.com/snapetech-org/chaptarrng/actions/runs/39292223717)
passed Linux, macOS, Windows, PostgreSQL migration, release-note, and receipt
checks on that code commit. Historical Build
[39267876585](https://github.com/snapetech-org/chaptarrng/actions/runs/39267876585)
passed on candidate `38be9422`. PR #131 merged to `main` on 2026-10-20.
The format remains unreleased. Keep using BookshelfNG or the published release
until the migration and retirement gates close.

## Export and restore

In **System → Backup**, choose **Download Encrypted Backup**, enter a
passphrase of at least 16 characters, confirm it, and store it separately from
the downloaded file. ChaptarrNG cannot recover a lost passphrase. Select the
`.enc` file in **Restore Backup** and enter the same passphrase. A successful
restore stages the database and requires a ChaptarrNG restart.

The file uses the BookshelfNG `BKSENC01` envelope: AES-256-GCM in authenticated
1 MiB chunks, PBKDF2-HMAC-SHA256 with 600,000 iterations, a random salt and
nonce prefix, and an authenticated final record. The format rejects altered,
truncated, trailing, and oversized data. Plaintext archives are limited to
900,000,000 bytes; restore uploads retain a 1 GiB request limit.

Encrypted export includes `Config.xml`, `INFO`, and the SQLite database when
SQLite is in use. PostgreSQL database backup remains an external operation;
use `pg_dump` and follow the separate
[PostgreSQL migration guidance](BOOKSHELFNG_POSTGRESQL_MIGRATION.md).

## API

The authenticated API exposes:

- `POST /api/v1/system/backup/encrypted`, with
  `X-Bookshelf-Backup-Passphrase`, to return a `.enc` download.
- `POST /api/v1/system/backup/restore/upload`, with multipart `restore` and
  `X-Bookshelf-Backup-Passphrase` for a BookshelfNG-compatible `.enc` file.

Send the passphrase only over HTTPS for remote requests. The ChaptarrNG host
must receive it to encrypt or restore a file, so that host and its
administrators remain inside the trust boundary. Temporary plaintext archives
and upload/decryption files are stored in owner-only temporary directories on
Unix systems and removed after the operation.

## ChaptarrNG scheduled backup encryption

This portable passphrase format is separate from ChaptarrNG's existing
scheduled full-backup encryption. Scheduled `.zip.enc` files use the
`CHAPTBK1` format and the mounted secret configured by
`CHAPTARR_BACKUP_ENCRYPTION_KEY_FILE`. Keep using that secret file when
restoring those `.zip.enc` files; use the passphrase field only for portable
BookshelfNG-format `.enc` files.
