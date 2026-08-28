using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Books;
using Chaptarr.Http.Middleware;
using Chaptarr.Http.REST;
using CoreMediaCover = NzbDrone.Core.MediaCover.MediaCover;
using NLog;
using NLog.Config;
using NLog.Targets;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MetadataSource;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookResourceMapperFixture
    {
        [TestCase("work:12345")]
        [TestCase("WORK:12345")]
        public void should_not_map_unknown_foreign_book_prefixes(string foreignBookId)
        {
            var resource = new BookResource
            {
                ForeignBookId = foreignBookId,
                MediaType = "audiobook",
                Monitored = true
            };

            var model = resource.ToModel();

            Assert.That(model.HardcoverBookId, Is.Null);
            Assert.That(model.GoodreadsBookId, Is.Null);
            Assert.That(model.GoodreadsWorkId, Is.Null);
            Assert.That(model.OpenLibraryWorkId, Is.Null);
            Assert.That(model.GoogleBooksId, Is.Null);
        }

        [Test]
        public void should_not_map_numeric_foreign_book_id_without_facade()
        {
            var resource = new BookResource
            {
                ForeignBookId = "12345",
                MediaType = "audiobook",
                Monitored = true
            };

            var model = resource.ToModel();

            Assert.That(model.HardcoverBookId, Is.Null);
            Assert.That(model.RemoteProviderIds, Is.Null);
            Assert.That(model.GoodreadsBookId, Is.Null);
            Assert.That(model.GoodreadsWorkId, Is.Null);
            Assert.That(model.OpenLibraryWorkId, Is.Null);
            Assert.That(model.GoogleBooksId, Is.Null);
        }

        [Test]
        public void should_map_numeric_foreign_book_id_as_facade_dialect()
        {
            var hcResource = new BookResource
            {
                ForeignBookId = "12345",
                MediaType = "audiobook",
                Monitored = true
            };

            var grResource = new BookResource
            {
                ForeignBookId = "231198689",
                MediaType = "ebook",
                Monitored = true
            };

            var hcModel = hcResource.ToModel(new ReadarrFacadeContext("hc", "audiobook", "/readarr/hc/audiobook"));
            var grModel = grResource.ToModel(new ReadarrFacadeContext("gr", "ebook", "/readarr/gr/ebook"));

            Assert.That(hcModel.HardcoverBookId, Is.EqualTo("hc:12345"));
            Assert.That(hcModel.RemoteProviderIds, Is.EquivalentTo(new[] { "hc:12345" }));
            Assert.That(grModel.GoodreadsWorkId, Is.EqualTo("gr:231198689"));
            Assert.That(grModel.RemoteProviderIds, Is.EquivalentTo(new[] { "gr:231198689" }));
        }

        [Test]
        public void should_map_prefixed_foreign_book_id_as_work_id()
        {
            var resource = new BookResource
            {
                ForeignBookId = "gr:231198689",
                MediaType = "ebook",
                Monitored = true
            };

            var model = resource.ToModel();

            Assert.That(model.GoodreadsWorkId, Is.EqualTo("gr:231198689"));
            Assert.That(model.GoodreadsBookId, Is.Null);
            Assert.That(model.RemoteProviderIds, Is.EquivalentTo(new[] { "gr:231198689" }));
        }

        [Test]
        public void should_preserve_explicit_ebook_monitoring_when_mapping_new_book_resource()
        {
            var resource = new BookResource
            {
                Title = "Network Effect",
                MediaType = "ebook",
                Monitored = false,
                AudiobookMonitored = false,
                EbookMonitored = true
            };

            var model = resource.ToModel();

            Assert.That(model.MediaType, Is.EqualTo(BookMediaType.Ebook));
            Assert.That(model.AudiobookMonitored, Is.False);
            Assert.That(model.EbookMonitored, Is.True);
        }

        [Test]
        public void should_keep_legacy_monitored_payload_compatibility_for_new_book_resource()
        {
            var resource = new BookResource
            {
                Title = "Network Effect",
                MediaType = "audiobook",
                Monitored = true
            };

            var model = resource.ToModel();

            Assert.That(model.MediaType, Is.EqualTo(BookMediaType.Audiobook));
            Assert.That(model.AudiobookMonitored, Is.True);
            Assert.That(model.EbookMonitored, Is.False);
        }

        [Test]
        public void should_prefer_explicit_native_side_monitoring_over_legacy_monitored()
        {
            var resource = new BookResource
            {
                Title = "Network Effect",
                MediaType = "audiobook",
                Monitored = true,
                AudiobookMonitored = false
            };

            var model = resource.ToModel();

            Assert.That(model.AudiobookMonitored, Is.False);
        }

        [Test]
        public void should_reject_unknown_media_type_for_new_book_resource()
        {
            var resource = new BookResource
            {
                Title = "Network Effect",
                MediaType = "paperback",
                Monitored = true
            };

            Assert.Throws<BadRequestException>(() => resource.ToModel());
        }

        [Test]
        public void should_map_az_foreign_book_id_to_asin()
        {
            var resource = new BookResource
            {
                ForeignBookId = "az:B018UG5HJY",
                MediaType = "audiobook",
                Monitored = true
            };

            var model = resource.ToModel();

            Assert.That(model.HardcoverBookId, Is.Null);
            Assert.That(model.GoodreadsBookId, Is.Null);
            Assert.That(model.GoodreadsWorkId, Is.Null);
            Assert.That(model.OpenLibraryWorkId, Is.Null);
            Assert.That(model.GoogleBooksId, Is.Null);
            Assert.That(BookEditionIdentity.GetAsin(model), Is.EqualTo("B018UG5HJY"));
        }

	        [Test]
        public void should_expose_prefixed_foreign_book_id_for_hardcover_on_native_surface()
	        {
	            var book = new Book
            {
                Title = "Test",
                HardcoverBookId = "hc:12345",
                Author = new Author(),
                Editions = new System.Collections.Generic.List<Edition>()
            };

            var resource = book.ToResource();

            Assert.That(resource.ForeignBookId, Is.EqualTo("hc:12345"));
        }

        [Test]
        public void should_map_book_added_timestamp_to_resource()
        {
            var added = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
            var book = new Book
            {
                Title = "Test",
                Added = added,
                Author = new Author(),
                Editions = new List<Edition>()
            };

            var resource = book.ToResource();

            Assert.That(resource.Added, Is.EqualTo(added));
        }

        [Test]
        public void should_round_trip_goodreads_search_edition_identity_on_native_surface()
        {
            var book = new Book
            {
                Title = "The Long Way to a Small, Angry Planet",
                GoodreadsWorkId = "gr:22733729",
                MediaType = BookMediaType.Ebook,
                Author = new Author
                {
                    GoodreadsAuthorId = "gr:1306980",
                    Name = "Becky Chambers"
                },
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        ForeignEditionId = "22733729",
                        GoodreadsEditionId = 22733729,
                        Title = "The Long Way to a Small, Angry Planet",
                        Monitored = true
                    }
                }
            };

            var resource = book.ToResource();
            resource.Editions = book.Editions.ToResource();
            var roundTripped = resource.ToModel();

            Assert.Multiple(() =>
            {
                Assert.That(resource.ForeignEditionId, Is.EqualTo("gr:22733729"));
                Assert.That(resource.Editions.Single().ForeignEditionId, Is.EqualTo("gr:22733729"));
                Assert.That(roundTripped.Editions.Single().ForeignEditionId, Is.EqualTo("gr:22733729"));
                Assert.That(roundTripped.Editions.Single().GoodreadsEditionId, Is.EqualTo(22733729));
            });
        }

        [Test]
        public void should_omit_bare_native_edition_identity_without_typed_provider_identity()
        {
            var originalConfiguration = LogManager.Configuration;
            try
            {
                var logs = ConfigureLogging();
                var edition = new Edition
                {
                    Id = 42,
                    Title = "Unresolved Edition",
                    ForeignEditionId = "12345"
                };

                var resource = edition.ToResource();

                Assert.That(resource.ForeignEditionId, Is.EqualTo(string.Empty));
                Assert.That(logs.Logs.Any(log =>
                    log.StartsWith("Warn|", StringComparison.Ordinal) &&
                    log.Contains("[NativeIdentity]", StringComparison.Ordinal) &&
                    log.Contains("localEditionId=42", StringComparison.Ordinal)), Is.True);
            }
            finally
            {
                LogManager.Configuration = originalConfiguration;
                LogManager.ReconfigExistingLoggers();
            }
        }

        [Test]
        public void should_derive_native_goodreads_edition_identity_from_typed_identity()
        {
            var resource = new Edition
            {
                ForeignEditionId = "12345",
                GoodreadsEditionId = 67890
            }.ToResource();

            Assert.That(resource.ForeignEditionId, Is.EqualTo("gr:67890"));
        }

        [TestCase("gr:22733729")]
        [TestCase("gr:22733729-ebook")]
        [TestCase("ol:OL123M")]
        [TestCase("gb:abc123")]
        [TestCase("az:B00ABC1234")]
        [TestCase("hc:edition:30643037-ebook")]
        [TestCase("az:B00ABC1234-audiobook")]
        public void should_preserve_canonical_native_foreign_edition_id(string foreignEditionId)
        {
            var resource = new Edition
            {
                ForeignEditionId = foreignEditionId
            }.ToResource();

            Assert.That(resource.ForeignEditionId, Is.EqualTo(foreignEditionId));
        }

        [Test]
        public void should_expose_bare_foreign_book_id_for_readarr_facades()
        {
            var book = new Book
            {
                Title = "Test",
                HardcoverBookId = "hc:12345",
                GoodreadsWorkId = "gr:231198689",
                Author = new Author(),
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        ForeignEditionId = "gr:199400410-ebook",
                        Monitored = true
                    }
                }
            };

            var hcResource = book.ToResource(new BookResourceMappingOptions { FacadeContext = new ReadarrFacadeContext("hc", "ebook", "/readarr/hc/ebook") });
            var grResource = book.ToResource(new BookResourceMappingOptions { FacadeContext = new ReadarrFacadeContext("gr", "ebook", "/readarr/gr/ebook") });

            Assert.That(hcResource.ForeignBookId, Is.EqualTo("12345"));
            Assert.That(grResource.ForeignBookId, Is.EqualTo("231198689"));
            Assert.That(grResource.ForeignEditionId, Is.EqualTo("199400410"));
        }

        [Test]
        public void should_omit_unaddressable_facade_editions_from_embedded_resources()
        {
            var editions = new List<Edition>
            {
                new Edition
                {
                    Id = 1,
                    Title = "Goodreads Edition",
                    ForeignEditionId = "gr:199400410-ebook"
                },
                new Edition
                {
                    Id = 2,
                    Title = "Hardcover Edition",
                    ForeignEditionId = "hc:edition:30643037-ebook"
                }
            };

            var hcResources = editions.ToResource(new ReadarrFacadeContext("hc", "ebook", "/readarr/hc/ebook"));
            var grResources = editions.ToResource(new ReadarrFacadeContext("gr", "ebook", "/readarr/gr/ebook"));

            Assert.That(hcResources.Select(e => e.Id), Is.EqualTo(new[] { 2 }));
            Assert.That(hcResources.Single().ForeignEditionId, Is.EqualTo("30643037"));
            Assert.That(grResources.Select(e => e.Id), Is.EqualTo(new[] { 1 }));
            Assert.That(grResources.Single().ForeignEditionId, Is.EqualTo("199400410"));
        }

        [Test]
        public void should_log_one_aggregate_warning_for_facade_book_identity_gaps()
        {
            var originalConfiguration = LogManager.Configuration;
            try
            {
                var logs = ConfigureLogging();

                BookResourceMapper.WarnFacadeIdentityGaps(new[]
                {
                    new BookResource { Id = 1, ForeignBookId = string.Empty },
                    new BookResource { Id = 2, ForeignBookId = null },
                    new BookResource { Id = 3, ForeignBookId = "12345" }
                },
                new ReadarrFacadeContext("hc", "ebook", "/readarr/hc/ebook"),
                "book response");

                Assert.That(logs.Logs, Has.Count.EqualTo(1));
                Assert.That(logs.Logs.Single(), Does.Contain("Warn|"));
                Assert.That(logs.Logs.Single(), Does.Contain("Emitted 2 book resource(s) without hc identity from book response"));
            }
            finally
            {
                LogManager.Configuration = originalConfiguration;
                LogManager.ReconfigExistingLoggers();
            }
        }

        [Test]
        public void should_preserve_existing_edition_identity_when_mapping_facade_book_update()
        {
            var existing = new Book
            {
                Id = 10,
                MediaType = BookMediaType.Ebook,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = 1481726,
                        BookId = 10,
                        ForeignEditionId = "hc:edition:30643037-ebook",
                        TitleSlug = "the-reawakening-ebook",
                        HardcoverEditionId = "30643037",
                        GoodreadsEditionId = 26836385,
                        Asin = "B000TEST01",
                        AudibleASIN = "B000AUDIO1",
                        Asins = new List<string> { "B000TEST01" },
                        Isbn13 = "9780000000001",
                        Isbn10 = "0000000001",
                        Monitored = false
                    }
                }
            };

            var resource = new BookResource
            {
                Id = 10,
                MediaType = "ebook",
                Monitored = true,
                EbookMonitored = false,
                Editions = new List<EditionResource>
                {
                    new EditionResource
                    {
                        Id = 1481726,
                        BookId = 10,
                        ForeignEditionId = "30643037",
                        Monitored = true
                    }
                }
            };

            var model = resource.ToModel(existing, new ReadarrFacadeContext("hc", "ebook", "/readarr/hc/ebook"));
            var edition = model.Editions.Single();

            Assert.That(model.EbookMonitored, Is.True);
            Assert.That(edition.Monitored, Is.True);
            Assert.That(edition.ForeignEditionId, Is.EqualTo("hc:edition:30643037-ebook"));
            Assert.That(edition.TitleSlug, Is.EqualTo("the-reawakening-ebook"));
            Assert.That(edition.HardcoverEditionId, Is.EqualTo("30643037"));
            Assert.That(edition.GoodreadsEditionId, Is.EqualTo(26836385));
            Assert.That(edition.Asin, Is.EqualTo("B000TEST01"));
            Assert.That(edition.AudibleASIN, Is.EqualTo("B000AUDIO1"));
            Assert.That(edition.Asins, Is.EqualTo(new[] { "B000TEST01" }));
            Assert.That(edition.Isbn13, Is.EqualTo("9780000000001"));
            Assert.That(edition.Isbn10, Is.EqualTo("0000000001"));
        }

        [Test]
        public void should_not_expose_edition_id_as_foreign_book_id()
        {
            var book = new Book
            {
                Title = "Test",
                GoodreadsWorkId = "gr:231198689",
                Author = new Author(),
                Editions = new System.Collections.Generic.List<Edition>
                {
                    new Edition
                    {
                        GoodreadsEditionId = 199400410,
                        Monitored = true
                    }
                }
            };

            var resource = book.ToResource();

            Assert.That(resource.ForeignBookId, Is.EqualTo("gr:231198689"));
        }

        [Test]
        public void should_expose_az_foreign_book_id_for_asin_only_book()
        {
            var book = new Book
            {
                Title = "Test",
                MediaType = BookMediaType.Audiobook,
                Author = new Author(),
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Asin = "B018UG5HJY",
                        AudibleASIN = "B999999999",
                        Monitored = true
                    }
                }
            };

            var resource = book.ToResource();

            Assert.That(resource.ForeignBookId, Is.EqualTo("az:B018UG5HJY"));
        }

        [Test]
        public void should_prefer_work_foreign_book_id_over_asin_fallback()
        {
            var book = new Book
            {
                Title = "Test",
                GoodreadsWorkId = "gr:231198689",
                MediaType = BookMediaType.Audiobook,
                Author = new Author(),
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Asin = "B018UG5HJY",
                        Monitored = true
                    }
                }
            };

            var resource = book.ToResource();

            Assert.That(resource.ForeignBookId, Is.EqualTo("gr:231198689"));
        }

        [Test]
        public void should_prefer_remote_work_alias_over_asin_fallback()
        {
            var book = new Book
            {
                Title = "Test",
                MediaType = BookMediaType.Audiobook,
                RemoteProviderIds = new HashSet<string> { "gr:231198689" },
                Author = new Author(),
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Asin = "B018UG5HJY",
                        Monitored = true
                    }
                }
            };

            var resource = book.ToResource();

            Assert.That(resource.ForeignBookId, Is.EqualTo("gr:231198689"));
        }

        [Test]
        public void should_expose_hardcover_alias_from_remote_provider_ids_for_seerr_scan()
        {
            var book = new Book
            {
                Title = "Test",
                RemoteProviderIds = new System.Collections.Generic.HashSet<string> { "gr:231198689", "hc:714600" },
                Author = new Author(),
                Editions = new System.Collections.Generic.List<Edition>()
            };

            var resource = book.ToResource();

            Assert.That(resource.ForeignBookId, Is.EqualTo("hc:714600"));
        }

        [Test]
        public void should_join_narrator_names_and_not_fabricate_full_cast()
        {
            var book = new Book
            {
                Title = "Test",
                MediaType = BookMediaType.Audiobook,
                Author = new Author { SortNameLastFirst = "Test, Author" },
                Editions = new System.Collections.Generic.List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Monitored = true,
                        ManualAdd = true,
                        Narrator = "Full Cast",
                        NarratorNames = new System.Collections.Generic.List<string> { "Narrator A", "Narrator B", "Narrator C" }
                    }
                }
            };

            var resource = book.ToResource();

            Assert.That(resource.Narrator, Is.EqualTo("Narrator A, Narrator B, Narrator C"));
        }

        [Test]
        public void should_use_raw_narrator_when_names_missing()
        {
            var book = new Book
            {
                Title = "Test",
                MediaType = BookMediaType.Audiobook,
                Author = new Author { SortNameLastFirst = "Test, Author" },
                Editions = new System.Collections.Generic.List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Monitored = true,
                        ManualAdd = true,
                        Narrator = "Full Cast"
                    }
                }
            };

            var resource = book.ToResource();

            Assert.That(resource.Narrator, Is.EqualTo("Full Cast"));
        }

        [Test]
        public void should_not_backfill_edition_fields_from_book_level()
        {
            var bookLevelRatings = new Ratings { Votes = 10, Value = 4.5m };

            var book = new Book
            {
                Title = "Test",
                MediaType = BookMediaType.Ebook,
                PageCount = 123,
                Ratings = bookLevelRatings,
                Images = new System.Collections.Generic.List<CoreMediaCover>
                {
                    new CoreMediaCover { CoverType = MediaCoverTypes.Cover, Url = "https://example.invalid/book.jpg" }
                },
                Author = new Author { SortNameLastFirst = "Test, Author" },
                Editions = new System.Collections.Generic.List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Monitored = true,
                        PageCount = 0,
                        Ratings = null,
                        Images = null
                    }
                }
            };

            var resource = book.ToResource();

            Assert.That(resource.PageCount, Is.EqualTo(0));
            Assert.That(resource.Ratings, Is.Not.Null);
            Assert.That(resource.Ratings.Votes, Is.EqualTo(0));
            Assert.That(resource.Images, Is.Not.Null);
            Assert.That(resource.Images, Is.Empty);
        }

        [Test]
        public void should_round_trip_edition_format_and_provider_identity_fields()
        {
            var edition = new Edition
            {
                Id = 10,
                BookId = 20,
                ForeignEditionId = "hc-edition:30",
                TitleSlug = "tailored-realities",
                Isbn10 = "1234567890",
                Isbn13 = "9781234567890",
                Asin = "B0DPGKGG9R",
                Asins = new List<string> { "B0DPGKGG9R", "B0DPQWYBF1" },
                GoodreadsEditionId = 222034397,
                HardcoverEditionId = "hc-ed:1644075",
                OpenLibraryEditionId = "ol:OL123M",
                ReadingFormatId = 2,
                EditionFormat = "Audiobook",
                EditionInfo = "Unabridged",
                AudibleASIN = "B0DPGKGG9R",
                GoogleBooksEditionId = "gb:abc123",
                Title = "Tailored Realities",
                Language = "eng"
            };

            var resource = edition.ToResource();
            var model = resource.ToModel();

            Assert.That(resource.ReadingFormatId, Is.EqualTo(2));
            Assert.That(resource.GoodreadsEditionId, Is.EqualTo(222034397));
            Assert.That(resource.Asins, Is.EquivalentTo(new[] { "B0DPGKGG9R", "B0DPQWYBF1" }));
            Assert.That(model.Isbn10, Is.EqualTo("1234567890"));
            Assert.That(model.Isbn13, Is.EqualTo("9781234567890"));
            Assert.That(model.Asin, Is.EqualTo("B0DPGKGG9R"));
            Assert.That(model.Asins, Is.EquivalentTo(new[] { "B0DPGKGG9R", "B0DPQWYBF1" }));
            Assert.That(model.GoodreadsEditionId, Is.EqualTo(222034397));
            Assert.That(model.HardcoverEditionId, Is.EqualTo("hc-ed:1644075"));
            Assert.That(model.OpenLibraryEditionId, Is.EqualTo("ol:OL123M"));
            Assert.That(model.ReadingFormatId, Is.EqualTo(2));
            Assert.That(model.EditionFormat, Is.EqualTo("Audiobook"));
            Assert.That(model.EditionInfo, Is.EqualTo("Unabridged"));
            Assert.That(model.AudibleASIN, Is.EqualTo("B0DPGKGG9R"));
            Assert.That(model.GoogleBooksEditionId, Is.EqualTo("gb:abc123"));
        }

        [Test]
        public void should_use_monitored_edition_for_displayed_foreign_edition_id()
        {
            var book = new Book
            {
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                Author = new Author { SortNameLastFirst = "Herbert, Frank" },
                Editions = new System.Collections.Generic.List<Edition>
                {
                    new Edition { Id = 2, ForeignEditionId = "gr:2", Title = "Monitored Edition", Monitored = true, ManualAdd = false },
                    new Edition { Id = 1, ForeignEditionId = "gr:1", Title = "Manual Edition", Monitored = false, ManualAdd = true }
                }
            };

            var resource = book.ToResource();

            Assert.That(resource.ForeignEditionId, Is.EqualTo("gr:2"));
            Assert.That(resource.Title, Is.EqualTo("Monitored Edition"));
        }

        [Test]
        public void should_hide_narrator_for_unpinned_audiobook_without_files()
        {
            var book = new Book
            {
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = true,
                Author = new Author { SortNameLastFirst = "Herbert, Frank" },
                Editions = new System.Collections.Generic.List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Monitored = true,
                        Title = "Dune",
                        Narrator = "Scott Brick",
                        NarratorNames = new System.Collections.Generic.List<string> { "Scott Brick" }
                    }
                }
            };

            var resource = book.ToResource();

            Assert.That(resource.Narrator, Is.Null);
            Assert.That(resource.NarratorNames, Is.Empty);
            Assert.That(resource.AvailableNarrators, Does.Contain("Scott Brick"));
        }

        [Test]
        public void should_show_narrator_for_pinned_audiobook_without_files()
        {
            var book = new Book
            {
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = false,
                Author = new Author { SortNameLastFirst = "Herbert, Frank" },
                Editions = new System.Collections.Generic.List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Monitored = true,
                        Title = "Dune",
                        Narrator = "Scott Brick"
                    }
                }
            };

            var resource = book.ToResource();

            Assert.That(resource.Narrator, Is.EqualTo("Scott Brick"));
        }

        [Test]
        public void should_not_fallback_to_lowest_id_when_no_monitored_edition_exists()
        {
            var book = new Book
            {
                Title = "Dune",
                MediaType = BookMediaType.Ebook,
                Author = new Author { SortNameLastFirst = "Herbert, Frank" },
                Editions = new System.Collections.Generic.List<Edition>
                {
                    new Edition { Id = 9, ForeignEditionId = "later", Title = "Later Edition", Monitored = false, ManualAdd = false },
                    new Edition { Id = 3, ForeignEditionId = "earlier", Title = "Earlier Edition", Monitored = false, ManualAdd = false }
                }
            };

            var resource = book.ToResource();

            Assert.That(resource.ForeignEditionId, Is.Null);
            Assert.That(resource.Title, Is.EqualTo("Dune"));
        }

        [Test]
        public void lean_mapping_should_show_narrator_when_statistics_have_files_without_bookfiles()
        {
            var book = new Book
            {
                Id = 10,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = true,
                Author = new Author { SortNameLastFirst = "Herbert, Frank" },
                Editions = new System.Collections.Generic.List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Monitored = true,
                        Title = "Dune",
                        NarratorNames = new System.Collections.Generic.List<string> { "Scott Brick" }
                    }
                }
            };

            var resource = book.ToResource(BookResourceMappingOptions.Lean(new BookStatistics
            {
                BookId = 10,
                BookFileCount = 1,
                BookCount = 1,
                TotalBookCount = 1
            }));

            Assert.That(resource.HasFiles, Is.True);
            Assert.That(resource.Statistics.BookFileCount, Is.EqualTo(1));
            Assert.That(resource.Narrator, Is.EqualTo("Scott Brick"));
            Assert.That(resource.NarratorNames, Is.EquivalentTo(new[] { "Scott Brick" }));
        }

        [Test]
        public void lean_mapping_should_omit_bulk_only_payload_fields_by_default()
        {
            var book = new Book
            {
                Id = 11,
                Title = "Dune",
                Overview = "Book overview",
                MediaType = BookMediaType.Ebook,
                Author = new Author { Id = 7, SortNameLastFirst = "Herbert, Frank" },
                Links = new System.Collections.Generic.List<Links>
                {
                    new Links { Name = "Hardcover", Url = "https://hardcover.app/books/1" }
                },
                Editions = new System.Collections.Generic.List<Edition>
                {
                    new Edition
                    {
                        Id = 1,
                        Monitored = true,
                        Title = "Dune",
                        Overview = "Edition overview"
                    }
                }
            };

            var resource = book.ToResource(BookResourceMappingOptions.Lean(new BookStatistics
            {
                BookId = 11,
                BookCount = 1,
                TotalBookCount = 1
            }));

            Assert.That(resource.Author, Is.Null);
            Assert.That(resource.Overview, Is.Null);
            Assert.That(resource.Links, Is.Null);
            Assert.That(resource.Title, Is.EqualTo("Dune"));
            Assert.That(resource.Statistics, Is.Not.Null);
        }

        private static MemoryTarget ConfigureLogging()
        {
            var memoryTarget = new MemoryTarget("memory")
            {
                Layout = "${level}|${logger}|${message}"
            };

            var config = new LoggingConfiguration();
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, memoryTarget, "Chaptarr.Api.V1.Books.*");
            LogManager.Configuration = config;
            LogManager.ReconfigExistingLoggers();

            return memoryTarget;
        }
    }
}
