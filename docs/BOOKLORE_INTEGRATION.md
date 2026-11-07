# BookLore integration

ChaptarrNG supports BookLore and BookOrbit as optional notification
destinations. They are external apps; ChaptarrNG does not require, install, or
bundle either one.

## Configure BookDrop uploads

In **Settings > Connect**, add **BookLore** and set its base URL, username, and
password. The default URL is `http://localhost:6060`. Use a BookLore account
with administrator or upload permission. The connection test checks that
BookLore accepts the credentials.

After an import, ChaptarrNG sends each imported file to BookLore's BookDrop
intake folder. Files are streamed from disk, and ChaptarrNG leaves the source
files in place. Uploads are sequential. If BookLore rejects a file, that
notification fails; files already sent remain in BookDrop and later files in
the same event are not sent.

BookLore applies its own supported-format and maximum-upload-size rules. The
BookLore v2.4.0 API source accepts `.pdf`, `.epub`, `.cbz`, `.cbr`, `.cb7`,
`.mobi`, `.azw3`, `.azw`, `.fb2`, `.m4b`, `.m4a`, `.mp3`, and `.opus`. Check the
rules for the BookLore version you run and its configured upload limit.

## API contract and URL handling

The client logs in with `POST /api/v1/auth/login` and the configured username
and password. It uses the returned `accessToken` as a bearer token for
`POST /api/v1/files/upload/bookdrop`, sending the file as multipart form field
`file`. A successful upload returns `200 OK` with an empty response body.

ChaptarrNG does not follow redirects for the login or upload requests. Set the
base URL to BookLore's final canonical address, including any reverse-proxy
path, so both API requests respond directly.

The request and upload behavior were verified against the official BookLore
v2.4.0 source at commit
[`f397e98c5ab021b02e8930ce278583af11637238`](https://github.com/booklore-app/booklore/tree/f397e98c5ab021b02e8930ce278583af11637238).
The live contract check authenticated, uploaded a file, received `200 OK`, and
verified the exact bytes in BookLore's BookDrop folder. BookLore v2.4.0 can
return an HTTP 400 data-conflict response when the same account logs in twice
within one second because its generated refresh tokens collide; ChaptarrNG
retries that conflict once after a short delay. It also retries server errors
once. BookLore reports invalid credentials as HTTP 400, which ChaptarrNG maps
to a credential-validation error without retrying.

## BookOrbit status

BookLore's repository identifies BookOrbit as its official successor and says
BookLore is entering maintenance mode. ChaptarrNG has a separate **BookOrbit**
destination for Book Dock uploads; it does not change the existing BookLore
provider or rewrite imported BookLore settings.

### Configure Book Dock uploads

In **Settings > Connect**, add **BookOrbit** and enter its base URL, username,
and password. The default URL is `http://localhost:3000`. Use a BookOrbit
account with the **Book Dock Access** permission and password login enabled.
The connection test verifies login and read access to Book Dock; BookOrbit checks the upload permission,
accepted format, and maximum file size when each file is sent.

After an import, ChaptarrNG streams each imported file to BookOrbit's Book
Dock as a multipart `file` upload. ChaptarrNG leaves the source files in place
and uploads files sequentially. If BookOrbit rejects a file, the notification
fails and stops at that file. Earlier uploads remain in Book Dock and later
files in that event are not sent. The upload is not atomic: if you retry a
partial batch, check Book Dock first so accepted files are not added again.

BookOrbit's current API uses `POST /api/v1/auth/login` with the configured
username and password, then sends the returned bearer token to
`POST /api/v1/book-dock/upload` with multipart field `file`. A successful
upload returns `201 Created`. ChaptarrNG does not follow redirects for either
request. Set the base URL to BookOrbit's final canonical address, including
any reverse-proxy path.

BookshelfNG notification rows configured for BookLore remain configured for
BookLore after migration. Review the migrated BookLore settings, install or
upgrade BookOrbit separately, grant Book Dock Access to an upload account,
then add and test a BookOrbit connection in ChaptarrNG. Keep or disable the
legacy BookLore connection after verifying the new destination. ChaptarrNG
does not infer that a saved BookLore URL or password belongs to a different
BookOrbit server.

The unpublished retirement candidate's proxy tests cover streamed uploads,
redirects, credential rejection, size rejection, stopping a partial batch,
and a later upload attempt. An isolated self-hosted BookOrbit v3.2.0 instance
also passed a live API contract check: initial setup returned 201, login and
Book Dock summary returned 200, a generated EPUB upload returned 201 and
appeared in the Book Dock listing, and an unsupported `.txt` upload was
rejected with HTTP 400. This verifies the API and file-admission path; it does
not verify BookOrbit's later processing of a production book or a transition
using a real BookLore user's settings. The Bookshelf conversion fixture checks
that a BookLore row's provider identity, settings, credentials, release-import
trigger, and retained backup survive conversion. BookOrbit support remains
candidate source work and is not in published `v0.9.977`; see the
[retirement matrix](FORK_FEATURES.md#bookshelfng-parity-matrix).

## Upstream references

- [BookLore v2.4.0 upload controller](https://github.com/booklore-app/booklore/blob/f397e98c5ab021b02e8930ce278583af11637238/booklore-api/src/main/java/org/booklore/controller/FileUploadController.java)
- [BookLore v2.4.0 upload service](https://github.com/booklore-app/booklore/blob/f397e98c5ab021b02e8930ce278583af11637238/booklore-api/src/main/java/org/booklore/service/upload/FileUploadService.java)
- [BookLore v2.4.0 authentication controller](https://github.com/booklore-app/booklore/blob/f397e98c5ab021b02e8930ce278583af11637238/booklore-api/src/main/java/org/booklore/controller/AuthenticationController.java)
- [BookLore v2.4.0 API error mapping](https://github.com/booklore-app/booklore/blob/f397e98c5ab021b02e8930ce278583af11637238/booklore-api/src/main/java/org/booklore/exception/ApiError.java)
- [BookLore v2.4.0 data-conflict response](https://github.com/booklore-app/booklore/blob/f397e98c5ab021b02e8930ce278583af11637238/booklore-api/src/main/java/org/booklore/exception/GlobalExceptionHandler.java)
- [BookLore v2.4.0 accepted file extensions](https://github.com/booklore-app/booklore/blob/f397e98c5ab021b02e8930ce278583af11637238/booklore-api/src/main/java/org/booklore/model/enums/BookFileExtension.java)
- [BookLore repository and BookOrbit successor notice](https://github.com/booklore-app/booklore)
- [BookOrbit v3.2.0 login request](https://github.com/bookorbit/bookorbit/blob/v3.2.0/server/src/modules/auth/dto/login.dto.ts)
- [BookOrbit v3.2.0 Book Dock upload endpoint and permission](https://github.com/bookorbit/bookorbit/blob/v3.2.0/server/src/modules/book-dock/book-dock.controller.ts)
- [BookOrbit v3.2.0 upload format validation](https://github.com/bookorbit/bookorbit/blob/v3.2.0/server/src/modules/upload/upload-validator.service.ts)
- [BookOrbit v3.2.0 release](https://github.com/bookorbit/bookorbit/releases/tag/v3.2.0)
