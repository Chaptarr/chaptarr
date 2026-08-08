using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.Profiles.Metadata;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorRemoteBookNormalizationFixture
    {
        private sealed class RecordingMetadataProfileService : IMetadataProfileService
        {
            public Dictionary<BookMediaType, int> FilteredCounts { get; } = new();
            public Dictionary<BookMediaType, int> LastProfileIds { get; } = new();
            public Func<Author, int, List<Book>> FilterOverride { get; set; }

            public MetadataProfile Add(MetadataProfile profile) => throw new NotImplementedException();
            public void Update(MetadataProfile profile) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public List<MetadataProfile> All() => throw new NotImplementedException();
            public MetadataProfile Get(int id) => new MetadataProfile { Id = id, Name = $"Profile {id}" };
            public bool Exists(int id) => true;

            public List<Book> FilterBooks(Author input, int profileId)
            {
                var books = input?.Books?.ToList() ?? new List<Book>();
                var mediaType = books.FirstOrDefault()?.MediaType ?? BookMediaType.Audiobook;
                FilteredCounts[mediaType] = books.Count;
                LastProfileIds[mediaType] = profileId;
                return FilterOverride != null ? FilterOverride(input, profileId) : books;
            }
        }

        private sealed class StubImportListExclusionService : IImportListExclusionService
        {
            private readonly List<ImportListExclusion> _exclusions;

            public StubImportListExclusionService()
                : this(Array.Empty<ImportListExclusion>())
            {
            }

            public StubImportListExclusionService(params string[] excludedIds)
            {
                _exclusions = (excludedIds ?? Array.Empty<string>())
                    .Select(id => new ImportListExclusion { ForeignId = id, Name = id })
                    .ToList();
            }

            public StubImportListExclusionService(params ImportListExclusion[] exclusions)
            {
                _exclusions = exclusions?.ToList() ?? new List<ImportListExclusion>();
            }

            public ImportListExclusion Add(ImportListExclusion importListExclusion) => throw new NotImplementedException();
            public List<ImportListExclusion> All() => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public void Delete(List<int> ids) => throw new NotImplementedException();
            public void Delete(string foreignId) => throw new NotImplementedException();
            public ImportListExclusion Get(int id) => throw new NotImplementedException();
            public ImportListExclusion FindByForeignId(string foreignId) => throw new NotImplementedException();
            public ImportListExclusion Update(ImportListExclusion importListExclusion) => throw new NotImplementedException();

            public List<ImportListExclusion> FindByForeignId(List<string> foreignIds)
            {
                var idSet = new HashSet<string>(foreignIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

                return _exclusions
                    .Where(exclusion => idSet.Contains(exclusion.ForeignId))
                    .GroupBy(exclusion => $"{exclusion.ForeignId}:{(int?)exclusion.MediaType}")
                    .Select(group => group.First())
                    .ToList();
            }
        }

        [Test]
        public void normalize_remote_books_should_preserve_overlapping_work_pockets_after_metadata_filtering_and_retention()
        {
            var metadataProfiles = new RecordingMetadataProfileService();
            var logger = LogManager.GetCurrentClassLogger();

            var localAuthor = new Author
            {
                Id = 99,
                Name = "Author",
                AudiobookMetadataProfileId = 1
            };

            var first = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "First",
                HardcoverBookId = "hc:1",
                GoodreadsWorkId = "gr:1",
                Editions = new List<Edition>
                {
                    BuildEdition("audio-en", 2, "en"),
                    BuildEdition("ebook-en", 3, "en")
                }
            };

            var duplicate = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "Duplicate",
                HardcoverBookId = "hc:2",
                GoodreadsWorkId = "gr:1",
                Editions = new List<Edition>
                {
                    BuildEdition("audio-fr", 2, "fr")
                }
            };

            var distinct = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "Distinct",
                HardcoverBookId = "hc:3",
                GoodreadsWorkId = "gr:3",
                Editions = new List<Edition>
                {
                    BuildEdition("audio-en-2", 2, "en")
                }
            };

            var result = RefreshAuthorService.NormalizeRemoteBooks(
                localAuthor,
                new[] { first, duplicate, distinct },
                Array.Empty<Series>(),
                metadataProfiles,
                new StubImportListExclusionService(),
                logger,
                audiobookMetadataProfileIdOverride: null,
                ebookMetadataProfileIdOverride: null,
                editionSelector: new EditionSelector(logger),
                retainEditions: true,
                logContext: "test");

            Assert.Multiple(() =>
            {
                Assert.That(metadataProfiles.FilteredCounts[BookMediaType.Audiobook], Is.EqualTo(3), "metadata filtering must see every pocket exactly as the server sent it (pre-coalesce)");
                Assert.That(result.Select(book => book.HardcoverBookId), Is.EquivalentTo(new[] { "hc:1", "hc:2", "hc:3" }), "overlapping Goodreads work IDs must not create a local union");
                Assert.That(result.Single(book => book.HardcoverBookId == "hc:1").Editions.Select(e => e.ForeignEditionId), Is.EquivalentTo(new[] { "audio-en", "ebook-en" }));
                Assert.That(result.Single(book => book.HardcoverBookId == "hc:2").Editions.Select(e => e.ForeignEditionId), Is.EquivalentTo(new[] { "audio-fr" }));
                Assert.That(result.Single(book => book.HardcoverBookId == "hc:3").Editions.Select(e => e.ForeignEditionId), Is.EquivalentTo(new[] { "audio-en-2" }));
            });
        }

        [Test]
        public void normalize_remote_books_should_keep_allowed_language_pocket_whole_without_union()
        {
            var metadataProfiles = new RecordingMetadataProfileService
            {
                FilterOverride = (author, profileId) => author.Books
                    .Select(book =>
                    {
                        book.Editions = book.Editions
                            .Where(edition => edition.Language == "en")
                            .ToList();

                        return book;
                    })
                    .Where(book => book.Editions.Any())
                    .ToList()
            };

            var logger = LogManager.GetCurrentClassLogger();

            var localAuthor = new Author
            {
                Id = 99,
                Name = "Author",
                AudiobookMetadataProfileId = 1
            };

            // Richer by raw edition count, but entirely the wrong language: under the contract this pocket
            // dies BY THE PROFILE (filter-first), and the allowed-language pocket survives whole. Pre-filter
            // collapse would have kept this pocket and produced "no editions survived".
            var wrongLanguageRepresentative = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "Shared Work",
                HardcoverBookId = "hc:1",
                GoodreadsWorkId = "gr:1",
                Editions = new List<Edition>
                {
                    BuildEdition("audio-fr", 2, "fr"),
                    BuildEdition("audio-fr-2", 2, "fr"),
                    BuildEdition("ebook-fr", 3, "fr")
                }
            };

            var allowedLanguagePocket = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "Shared Work",
                HardcoverBookId = "hc:2",
                GoodreadsWorkId = "gr:1",
                Editions = new List<Edition>
                {
                    BuildEdition("audio-en", 2, "en")
                }
            };

            var result = RefreshAuthorService.NormalizeRemoteBooks(
                localAuthor,
                new[] { wrongLanguageRepresentative, allowedLanguagePocket },
                Array.Empty<Series>(),
                metadataProfiles,
                new StubImportListExclusionService(),
                logger,
                audiobookMetadataProfileIdOverride: null,
                ebookMetadataProfileIdOverride: null,
                editionSelector: new EditionSelector(logger),
                retainEditions: true,
                logContext: "test");

            Assert.Multiple(() =>
            {
                Assert.That(metadataProfiles.FilteredCounts[BookMediaType.Audiobook], Is.EqualTo(2), "both pockets reach the filter exactly as the server sent them");
                Assert.That(result, Has.Count.EqualTo(1));
                Assert.That(result[0].HardcoverBookId, Is.EqualTo("hc:2"), "the allowed-language pocket survives as itself — not as editions grafted onto the dropped pocket");
                Assert.That(result[0].Editions.Select(e => e.ForeignEditionId), Is.EquivalentTo(new[] { "audio-en" }), "no editions from the profile-dropped duplicate pocket may be unioned in");
            });
        }

        [Test]
        public void normalize_remote_books_should_apply_refresh_exclusions_consistently()
        {
            var metadataProfiles = new RecordingMetadataProfileService();
            var logger = LogManager.GetCurrentClassLogger();

            var localAuthor = new Author
            {
                Name = "Author",
                AudiobookMetadataProfileId = 1
            };

            var kept = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "Keep",
                HardcoverBookId = "hc:10",
                Editions = new List<Edition> { BuildEdition("keep-audio", 2, "en") }
            };

            var excluded = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "Exclude",
                HardcoverBookId = "hc:20",
                Editions = new List<Edition> { BuildEdition("exclude-audio", 2, "en") }
            };

            var result = RefreshAuthorService.NormalizeRemoteBooks(
                localAuthor,
                new[] { kept, excluded },
                Array.Empty<Series>(),
                metadataProfiles,
                new StubImportListExclusionService("hc:20"),
                logger,
                audiobookMetadataProfileIdOverride: null,
                ebookMetadataProfileIdOverride: null,
                editionSelector: new EditionSelector(logger),
                retainEditions: true,
                logContext: "test");

            Assert.That(result.Select(book => book.HardcoverBookId), Is.EquivalentTo(new[] { "hc:10" }));
        }

        [Test]
        public void normalize_remote_books_should_skip_books_with_no_editions()
        {
            var metadataProfiles = new RecordingMetadataProfileService();
            var logger = LogManager.GetCurrentClassLogger();

            var localAuthor = new Author
            {
                Name = "Author",
                AudiobookMetadataProfileId = 1
            };

            var malformed = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "Broken",
                HardcoverBookId = "hc:404",
                Editions = new List<Edition>()
            };

            var valid = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "Valid",
                HardcoverBookId = "hc:405",
                Editions = new List<Edition> { BuildEdition("valid-audio", 2, "en") }
            };

            var result = RefreshAuthorService.NormalizeRemoteBooks(
                localAuthor,
                new[] { malformed, valid },
                Array.Empty<Series>(),
                metadataProfiles,
                new StubImportListExclusionService(),
                logger,
                audiobookMetadataProfileIdOverride: null,
                ebookMetadataProfileIdOverride: null,
                editionSelector: new EditionSelector(logger),
                retainEditions: true,
                logContext: "test");

            Assert.That(result.Select(book => book.HardcoverBookId), Is.EquivalentTo(new[] { "hc:405" }));
        }

        [Test]
        public void normalize_remote_books_should_keep_representative_ebook_when_audiobook_has_no_audio_edition()
        {
            var metadataProfiles = new RecordingMetadataProfileService();
            var logger = LogManager.GetCurrentClassLogger();

            var localAuthor = new Author
            {
                Name = "Author",
                AudiobookMetadataProfileId = 1
            };

            var contaminated = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "Wrong Format",
                HardcoverBookId = "hc:406",
                Editions = new List<Edition> { BuildEdition("ebook-only", 3, "en") }
            };

            var valid = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "Valid",
                HardcoverBookId = "hc:407",
                Editions = new List<Edition>
                {
                    BuildEdition("valid-audio", 2, "en"),
                    BuildEdition("valid-ebook", 3, "en")
                }
            };

            var result = RefreshAuthorService.NormalizeRemoteBooks(
                localAuthor,
                new[] { contaminated, valid },
                Array.Empty<Series>(),
                metadataProfiles,
                new StubImportListExclusionService(),
                logger,
                audiobookMetadataProfileIdOverride: null,
                ebookMetadataProfileIdOverride: null,
                editionSelector: new EditionSelector(logger),
                retainEditions: true,
                logContext: "test");

            Assert.Multiple(() =>
            {
                Assert.That(result.Select(book => book.HardcoverBookId), Is.EquivalentTo(new[] { "hc:406", "hc:407" }));
                Assert.That(result.Single(book => book.HardcoverBookId == "hc:406").Editions.Select(e => e.ForeignEditionId), Is.EquivalentTo(new[] { "ebook-only" }));
                Assert.That(result.Single(book => book.HardcoverBookId == "hc:407").Editions.Select(e => e.ForeignEditionId), Is.EquivalentTo(new[] { "valid-audio", "valid-ebook" }));
            });
        }

        [Test]
        public void normalize_remote_books_should_respect_media_scoped_exclusions()
        {
            var metadataProfiles = new RecordingMetadataProfileService();
            var logger = LogManager.GetCurrentClassLogger();

            var localAuthor = new Author
            {
                Name = "Author",
                AudiobookMetadataProfileId = 1,
                EbookMetadataProfileId = 2
            };

            var audiobook = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "Shared Work",
                HardcoverBookId = "hc:20",
                GoodreadsWorkId = "gr:20",
                Editions = new List<Edition> { BuildEdition("audio-keep", 2, "en") }
            };

            var ebook = new Book
            {
                MediaType = BookMediaType.Ebook,
                Title = "Shared Work",
                HardcoverBookId = "hc:20",
                GoodreadsWorkId = "gr:20",
                Editions = new List<Edition> { BuildEdition("ebook-keep", 3, "en") }
            };

            var result = RefreshAuthorService.NormalizeRemoteBooks(
                localAuthor,
                new[] { audiobook, ebook },
                Array.Empty<Series>(),
                metadataProfiles,
                new StubImportListExclusionService(new ImportListExclusion
                {
                    ForeignId = "hc:20",
                    Name = "Audio only",
                    MediaType = BookMediaType.Audiobook
                }),
                logger,
                audiobookMetadataProfileIdOverride: null,
                ebookMetadataProfileIdOverride: null,
                editionSelector: new EditionSelector(logger),
                retainEditions: true,
                logContext: "test");

            Assert.That(result.Select(book => book.MediaType), Is.EquivalentTo(new[] { BookMediaType.Ebook }));
        }

        [Test]
        public void normalize_remote_books_should_keep_identifierless_same_title_books_separate()
        {
            var metadataProfiles = new RecordingMetadataProfileService();
            var logger = LogManager.GetCurrentClassLogger();

            var localAuthor = new Author
            {
                Id = 99,
                Name = "Author",
                AudiobookMetadataProfileId = 1
            };

            var first = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "Shared Title",
                Editions = new List<Edition> { BuildEdition("audio-one", 2, "en") }
            };

            var second = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "Shared Title",
                Editions = new List<Edition> { BuildEdition("audio-two", 2, "en") }
            };

            var result = RefreshAuthorService.NormalizeRemoteBooks(
                localAuthor,
                new[] { first, second },
                Array.Empty<Series>(),
                metadataProfiles,
                new StubImportListExclusionService(),
                logger,
                audiobookMetadataProfileIdOverride: null,
                ebookMetadataProfileIdOverride: null,
                editionSelector: new EditionSelector(logger),
                retainEditions: true,
                logContext: "test");

            Assert.Multiple(() =>
            {
                Assert.That(result.Count, Is.EqualTo(2));
                Assert.That(result.SelectMany(book => book.Editions).Select(edition => edition.ForeignEditionId),
                    Is.EquivalentTo(new[] { "audio-one", "audio-two" }));
            });
        }

        [Test]
        public void normalize_remote_books_should_honor_profile_overrides_for_add_paths()
        {
            var metadataProfiles = new RecordingMetadataProfileService();
            var logger = LogManager.GetCurrentClassLogger();

            var localAuthor = new Author
            {
                Id = 99,
                Name = "Author",
                AudiobookMetadataProfileId = 1
            };

            var audiobook = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "Audio",
                HardcoverBookId = "hc:10",
                Editions = new List<Edition> { BuildEdition("audio-en", 2, "en") }
            };

            var result = RefreshAuthorService.NormalizeRemoteBooks(
                localAuthor,
                new[] { audiobook },
                Array.Empty<Series>(),
                metadataProfiles,
                new StubImportListExclusionService(),
                logger,
                audiobookMetadataProfileIdOverride: 7,
                ebookMetadataProfileIdOverride: null,
                editionSelector: new EditionSelector(logger),
                retainEditions: true,
                logContext: "test");

            Assert.Multiple(() =>
            {
                Assert.That(result.Count, Is.EqualTo(1));
                Assert.That(metadataProfiles.LastProfileIds[BookMediaType.Audiobook], Is.EqualTo(7));
            });
        }

        [Test]
        public void normalize_remote_books_should_filter_metadata_profile_before_representative_retention()
        {
            var metadataProfiles = new RecordingMetadataProfileService
            {
                FilterOverride = (input, _) =>
                {
                    foreach (var book in input.Books)
                    {
                        book.Editions = book.Editions
                            .Where(e => e.Language == "eng" && e.Isbn13 != null)
                            .ToList();
                    }

                    return input.Books.Where(book => book.Editions.Any()).ToList();
                }
            };
            var logger = LogManager.GetCurrentClassLogger();

            var localAuthor = new Author
            {
                Id = 99,
                Name = "Author",
                AudiobookMetadataProfileId = 1
            };

            var audiobook = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "Fallback",
                HardcoverBookId = "hc:50",
                Editions = new List<Edition>
                {
                    BuildEdition("fra-audio", 2, "fra", votes: 999, isbn13: "9780000000001"),
                    BuildEdition("eng-ebook-no-isbn", 3, "eng", votes: 999),
                    BuildEdition("eng-ebook-with-isbn", 3, "eng", votes: 1, isbn13: "9780000000002"),
                    BuildEdition("eng-print-with-isbn", 1, "eng", votes: 500, isbn13: "9780000000003")
                }
            };

            var result = RefreshAuthorService.NormalizeRemoteBooks(
                localAuthor,
                new[] { audiobook },
                Array.Empty<Series>(),
                metadataProfiles,
                new StubImportListExclusionService(),
                logger,
                audiobookMetadataProfileIdOverride: null,
                ebookMetadataProfileIdOverride: null,
                editionSelector: new EditionSelector(logger),
                retainEditions: true,
                logContext: "test");

            Assert.That(result.Single().Editions.Select(e => e.ForeignEditionId),
                Is.EquivalentTo(new[] { "eng-ebook-with-isbn" }));
        }

        [Test]
        public void normalize_remote_books_should_drop_book_when_profile_filters_all_editions()
        {
            var metadataProfiles = new RecordingMetadataProfileService
            {
                FilterOverride = (input, _) =>
                {
                    foreach (var book in input.Books)
                    {
                        book.Editions = book.Editions
                            .Where(e => e.Language == "eng")
                            .ToList();
                    }

                    return input.Books.Where(book => book.Editions.Any()).ToList();
                }
            };
            var logger = LogManager.GetCurrentClassLogger();

            var localAuthor = new Author
            {
                Id = 99,
                Name = "Author",
                AudiobookMetadataProfileId = 1
            };

            var audiobook = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "French Only",
                HardcoverBookId = "hc:51",
                Editions = new List<Edition> { BuildEdition("fra-audio", 2, "fra") }
            };

            var result = RefreshAuthorService.NormalizeRemoteBooks(
                localAuthor,
                new[] { audiobook },
                Array.Empty<Series>(),
                metadataProfiles,
                new StubImportListExclusionService(),
                logger,
                audiobookMetadataProfileIdOverride: null,
                ebookMetadataProfileIdOverride: null,
                editionSelector: new EditionSelector(logger),
                retainEditions: true,
                logContext: "test");

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void normalize_remote_books_should_remain_strict_for_metadata_profile_errors()
        {
            var metadataProfiles = new RecordingMetadataProfileService
            {
                FilterOverride = (_, _) => throw new InvalidOperationException("boom")
            };
            var logger = LogManager.GetCurrentClassLogger();

            var localAuthor = new Author
            {
                Id = 99,
                Name = "Author",
                AudiobookMetadataProfileId = 1
            };

            var audiobook = new Book
            {
                MediaType = BookMediaType.Audiobook,
                Title = "Audio",
                HardcoverBookId = "hc:10",
                Editions = new List<Edition> { BuildEdition("audio-en", 2, "en") }
            };

            Assert.Throws<InvalidOperationException>(() => RefreshAuthorService.NormalizeRemoteBooks(
                localAuthor,
                new[] { audiobook },
                Array.Empty<Series>(),
                metadataProfiles,
                new StubImportListExclusionService(),
                logger,
                audiobookMetadataProfileIdOverride: null,
                ebookMetadataProfileIdOverride: null,
                editionSelector: new EditionSelector(logger),
                retainEditions: true,
                logContext: "test"));
        }

        private static Edition BuildEdition(string foreignEditionId, int readingFormatId, string language, int votes = 0, decimal rating = 0m, string isbn13 = null)
        {
            return new Edition
            {
                ForeignEditionId = foreignEditionId,
                ReadingFormatId = readingFormatId,
                Language = language,
                Title = foreignEditionId,
                Format = readingFormatId == 2 ? "audio" : "ebook",
                Isbn13 = isbn13,
                Ratings = new Ratings
                {
                    Votes = votes,
                    Value = rating
                }
            };
        }
    }
}
