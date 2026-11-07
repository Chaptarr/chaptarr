# Supplemental metadata catalogs

The current source checkout can search public catalogs alongside ChaptarrNG's
configured primary metadata provider. These adapters and field preferences are
not included in the published `v0.9.977` release. Check the current
release notes before relying on these adapters in a deployed instance.

## Available catalogs

| Catalog | Source key | Credentials |
| --- | --- | --- |
| Open Library | `openlibrary` | Optional contact email |
| Google Books | `googlebooks` | `GOOGLE_BOOKS_API_KEY` or a saved API key |
| Library of Congress | `loc` | None |
| Gutendex / Project Gutenberg | `gutendex` | None |
| Internet Archive | `internetarchive` | None |
| National Diet Library Search | `ndl` | None |
| Europeana | `europeana` | `EUROPEANA_API_KEY` or a saved API key |
| Apify Goodreads-compatible actor | `apify-goodreads` | Actor ID and `HARDCOVER_APIFY_TOKEN` |

Catalogs are selected under **Settings > Metadata > Catalog sources**. When
enabled, their results are added to the normal search. They can also be chosen
directly from the **Add New** provider menu. Keyed catalogs appear there only
when their required credentials are configured.

If no source selection has been saved, ChaptarrNG enables Library of Congress
and Gutendex. Google Books and Europeana are also included when their keys are
configured. Set `CHAPTARR_METADATA_SOURCES` to a comma- or semicolon-separated
list to control the sources from the deployment environment. The legacy
`BOOKSHELF_METADATA_SOURCES` variable is read as a migration fallback. An
environment selection takes precedence over saved settings.

## Credentials and environment settings

The settings page accepts saved credentials, or they can be provided through
the environment:

| Environment variable | Purpose |
| --- | --- |
| `GOOGLE_BOOKS_API_KEY` | Google Books API key |
| `EUROPEANA_API_KEY` | Europeana API key |
| `OPEN_LIBRARY_CONTACT_EMAIL` | Contact address included in Open Library requests |
| `HARDCOVER_APIFY_GOODREADS_ACTOR` | Apify actor ID, such as `publisher~actor-name` |
| `HARDCOVER_APIFY_TOKEN` | Apify API token |
| `HARDCOVER_APIFY_GOODREADS_INPUT_TEMPLATE` | Optional JSON template; use `{{query}}` for the escaped search term |

Saved API keys are write-only through `GET /api/v1/config/metadataprovider`:
the response returns an empty credential field and a `has...` flag. A saved
value can be replaced or cleared in settings. Environment values take
precedence and are not changed by the settings page. Requests requiring Google
Books or Europeana keys use memory-only response caching so a URL containing a
key is not written into the persistent HTTP cache. Request logs redact the
credential query parameters.

## Per-field preferences

Optional preferences can select a catalog for title, description, publisher,
language, release date, page count, cover, author image, and genres. ChaptarrNG
checks work identity and ISBN evidence before applying a field from another
catalog. A selected catalog remains the source of the book's durable provider
identity; a preference does not replace that identity with a similarly named
record.

## Provider IDs and migration

Supplemental records use opaque `meta:` provider IDs. The encoded value keeps
the catalog name and its original provider ID together, including IDs that
contain punctuation or colons. ChaptarrNG preserves these IDs for book and
author refresh, edition lookup, add deduplication, and provider-alias matching.
Do not strip the prefix or edit the encoded value.

The BookshelfNG importer converts recognized supplemental provider IDs to
this form and retains the original values in its generated backup. That
conversion does not establish parity for every provider setting or credential;
review catalog selection and credentials after migration. See the
[BookshelfNG migration guide](MIGRATE_FROM_BOOKSHELFNG.md) and the
[retirement status](BOOKSHELFNG_RETIREMENT.md) for the current support limits.
