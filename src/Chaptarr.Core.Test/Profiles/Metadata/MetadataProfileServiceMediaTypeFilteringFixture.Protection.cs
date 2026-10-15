using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.Profiles.Metadata
{
    public partial class MetadataProfileServiceMediaTypeFilteringFixture
    {
        private sealed class CountingProtectionSet : IReadOnlySet<string>
        {
            private readonly HashSet<string> _values;
            public int Enumerations { get; private set; }
            public CountingProtectionSet(IEnumerable<string> values) => _values = new HashSet<string>(values);
            public int Count => _values.Count;
            public bool Contains(string item) => _values.Contains(item);
            public bool IsProperSubsetOf(IEnumerable<string> other) => _values.IsProperSubsetOf(other);
            public bool IsProperSupersetOf(IEnumerable<string> other) => _values.IsProperSupersetOf(other);
            public bool IsSubsetOf(IEnumerable<string> other) => _values.IsSubsetOf(other);
            public bool IsSupersetOf(IEnumerable<string> other) => _values.IsSupersetOf(other);
            public bool Overlaps(IEnumerable<string> other) => _values.Overlaps(other);
            public bool SetEquals(IEnumerable<string> other) => _values.SetEquals(other);
            public IEnumerator<string> GetEnumerator()
            {
                Enumerations++;
                return _values.GetEnumerator();
            }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private static Edition ProtectionEdition(string identity, bool enriched)
        {
            var edition = new Edition { Language = "fra", ReadingFormatId = 2 };
            switch (identity)
            {
                case "hardcover":
                    edition.HardcoverEditionId = "hc:edition:202";
                    break;
                case "goodreads":
                    edition.ForeignEditionId = "gr:123";
                    break;
                case "openlibrary":
                    edition.OpenLibraryEditionId = "OL202M";
                    break;
                case "googlebooks":
                    edition.GoogleBooksEditionId = "gb:202";
                    break;
                case "amazon":
                    edition.ForeignEditionId = enriched ? "hc:edition:456" : "az:B00IDENTITY";
                    edition.Asin = "B00IDENTITY";
                    break;
                case "legacy":
                    edition.ForeignEditionId = "legacy-edition";
                    break;
                case "legacy-withdrawn":
                    edition.ForeignEditionId = "legacy-edition";
                    edition.HardcoverEditionId = enriched ? null : "hc:edition:202";
                    break;
            }

            if (enriched && identity != "hardcover" && identity != "legacy-withdrawn")
            {
                edition.HardcoverEditionId = "hc:edition:456";
            }

            return edition;
        }

        [TestCase("hardcover", false)]
        [TestCase("goodreads", false)]
        [TestCase("openlibrary", false)]
        [TestCase("googlebooks", false)]
        [TestCase("amazon", false)]
        [TestCase("legacy", false)]
        [TestCase("legacy-withdrawn", false)]
        [TestCase("hardcover", true)]
        [TestCase("goodreads", true)]
        [TestCase("openlibrary", true)]
        [TestCase("googlebooks", true)]
        [TestCase("amazon", true)]
        [TestCase("legacy", true)]
        [TestCase("legacy-withdrawn", true)]
        public void profile_protection_should_preserve_local_identity_through_provider_enrichment(string identity, bool fileBacked)
        {
            var profile = CreateProfile(1, allowedLanguages: "eng");
            var localBook = new Book { Id = 1, AuthorId = 1, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook, AnyEditionOk = false };
            var localEdition = ProtectionEdition(identity, false);
            localEdition.Id = 10;
            localEdition.BookId = localBook.Id;
            localEdition.Book = localBook;
            localEdition.ManualAdd = !fileBacked;
            var expected = ProtectionEdition(identity, true);
            var unwanted = new Edition { HardcoverEditionId = "hc:edition:303", Language = "fra", ReadingFormatId = 2 };
            var remote = new Book { HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook, Editions = new List<Edition> { expected, unwanted } };
            var files = fileBacked ? new List<BookFile> { new() { EditionId = localEdition.Id, Edition = localEdition } } : new List<BookFile>();

            var result = CreateService(profile, new Author { Id = 1 }, new List<Book> { localBook }, new List<Edition> { localEdition }, files)
                .FilterBooks(new Author { Id = 1, Books = new List<Book> { remote } }, profile.Id);

            Assert.That(result.Single().Editions, Has.Count.EqualTo(1));
            Assert.That(result.Single().Editions.Single(), Is.SameAs(expected));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        public void profile_protection_should_never_use_unknown_edition_identity(string foreignId)
        {
            var profile = CreateProfile(1, allowedLanguages: "eng");
            var localBook = new Book { Id = 1, AuthorId = 1, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook, AnyEditionOk = false };
            var localEdition = new Edition { Id = 10, BookId = 1, ForeignEditionId = foreignId, ManualAdd = true };
            var remote = new Book
            {
                HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { new() { ForeignEditionId = foreignId, Language = "fra", ReadingFormatId = 2 } }
            };

            var result = CreateService(profile, new Author { Id = 1 }, new List<Book> { localBook }, new List<Edition> { localEdition })
                .FilterBooks(new Author { Id = 1, Books = new List<Book> { remote } }, profile.Id);

            Assert.That(result.SelectMany(book => book.Editions), Is.Empty);
        }

        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void owned_file_should_not_protect_another_work_or_media_with_the_same_legacy_id(BookMediaType unrelatedMedia)
        {
            var profile = CreateProfile(1, allowedLanguages: "eng");
            var localBook = new Book { Id = 1, AuthorId = 1, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook };
            var localEdition = new Edition { Id = 10, BookId = 1, ForeignEditionId = "legacy-shared", Book = localBook };
            Book Remote(string work, BookMediaType media) => new Book
            {
                HardcoverBookId = work, MediaType = media,
                Editions = new List<Edition> { new() { ForeignEditionId = "legacy-shared", Language = "fra" } }
            };
            var owned = Remote("hc:1001", BookMediaType.Audiobook);
            var unrelated = Remote("hc:1002", unrelatedMedia);
            var files = new List<BookFile> { new() { EditionId = 10, Edition = localEdition } };

            var result = CreateService(profile, new Author { Id = 1 }, new List<Book> { localBook }, new List<Edition> { localEdition }, files)
                .FilterBooks(new Author { Id = 1, Books = new List<Book> { owned, unrelated } }, profile.Id);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result.Single(), Is.SameAs(owned));
            Assert.That(result.Single().Editions.Single().ForeignEditionId, Is.EqualTo("legacy-shared"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void owned_asin_alias_should_not_protect_multiple_canonical_editions(bool valueEqualRows)
        {
            var profile = CreateProfile(1, allowedLanguages: "eng");
            var localBook = new Book { Id = 1, AuthorId = 1, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook };
            var localEdition = new Edition { Id = 10, BookId = 1, Asin = "B00SHARED", Book = localBook };
            var remote = new Book
            {
                HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new() { HardcoverEditionId = "hc:edition:202", Asin = "B00SHARED", Language = "fra" },
                    new() { HardcoverEditionId = valueEqualRows ? "hc:edition:202" : "hc:edition:203", Asin = "B00SHARED", Language = "fra" }
                }
            };
            var files = new List<BookFile> { new() { EditionId = 10, Edition = localEdition } };

            var result = CreateService(profile, new Author { Id = 1 }, new List<Book> { localBook }, new List<Edition> { localEdition }, files)
                .FilterBooks(new Author { Id = 1, Books = new List<Book> { remote } }, profile.Id);

            if (valueEqualRows)
            {
                Assert.That(result.SelectMany(book => book.Editions), Is.Not.Empty);
                Assert.That(result.SelectMany(book => book.Editions).All(edition => edition.HardcoverEditionId == "hc:edition:202"), Is.True);
            }
            else
            {
                Assert.That(result.SelectMany(book => book.Editions), Is.Empty);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void profile_protection_should_preserve_duplicate_rows_with_the_same_known_foreign_identity(bool fileBacked)
        {
            var profile = CreateProfile(1, allowedLanguages: "eng");
            var localBook = new Book { Id = 1, AuthorId = 1, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook, AnyEditionOk = false };
            var localEdition = new Edition { Id = 10, BookId = 1, Book = localBook, ForeignEditionId = "gr:123", ManualAdd = !fileBacked };
            var remote = new Book
            {
                HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new() { ForeignEditionId = "gr:123", Title = "First metadata", Language = "fra" },
                    new() { ForeignEditionId = "gr:123", Title = "Updated metadata", Language = "fra" }
                }
            };
            var files = fileBacked ? new List<BookFile> { new() { EditionId = 10, Edition = localEdition } } : new List<BookFile>();

            var result = CreateService(profile, new Author { Id = 1 }, new List<Book> { localBook }, new List<Edition> { localEdition }, files)
                .FilterBooks(new Author { Id = 1, Books = new List<Book> { remote } }, profile.Id);

            Assert.That(result.Single().Editions, Has.Count.EqualTo(2));
            Assert.That(result.Single().Editions.All(edition => edition.ForeignEditionId == "gr:123"), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void stable_identity_should_not_be_weakened_by_a_shared_foreign_asin(bool fileBacked)
        {
            var profile = CreateProfile(1, allowedLanguages: "eng");
            var localBook = new Book { Id = 1, AuthorId = 1, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook, AnyEditionOk = false };
            var localEdition = new Edition
            {
                Id = 10, BookId = 1, Book = localBook, ForeignEditionId = "az:B00SHARED", HardcoverEditionId = "hc:edition:202", Asin = "B00SHARED", ManualAdd = !fileBacked
            };
            var expected = new Edition { ForeignEditionId = "hc:edition:202", HardcoverEditionId = "hc:edition:202", Asin = "B00SHARED", Language = "fra" };
            var remote = new Book
            {
                HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { expected, new() { ForeignEditionId = "hc:edition:203", HardcoverEditionId = "hc:edition:203", Asin = "B00SHARED", Language = "fra" } }
            };
            var files = fileBacked ? new List<BookFile> { new() { EditionId = 10, Edition = localEdition } } : new List<BookFile>();

            var result = CreateService(profile, new Author { Id = 1 }, new List<Book> { localBook }, new List<Edition> { localEdition }, files)
                .FilterBooks(new Author { Id = 1, Books = new List<Book> { remote } }, profile.Id);

            Assert.That(result.Single().Editions, Has.Count.EqualTo(1));
            Assert.That(result.Single().Editions.Single(), Is.SameAs(expected));
        }

        [Test]
        public void shared_asin_should_not_collapse_distinct_known_foreign_identities()
        {
            var profile = CreateProfile(1, allowedLanguages: "eng");
            var localBook = new Book { Id = 1, AuthorId = 1, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook };
            var localEdition = new Edition { Id = 10, BookId = 1, Book = localBook, Asin = "B00SHARED" };
            var remote = new Book
            {
                HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new() { ForeignEditionId = "legacy-one", Asin = "B00SHARED", Language = "fra" },
                    new() { ForeignEditionId = "legacy-two", Asin = "B00SHARED", Language = "fra" }
                }
            };
            var files = new List<BookFile> { new() { EditionId = 10, Edition = localEdition } };

            var result = CreateService(profile, new Author { Id = 1 }, new List<Book> { localBook }, new List<Edition> { localEdition }, files)
                .FilterBooks(new Author { Id = 1, Books = new List<Book> { remote } }, profile.Id);

            Assert.That(result.SelectMany(book => book.Editions), Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void profile_filtering_should_load_catalog_evidence_once_as_work_count_grows(bool protectedFiles)
        {
            foreach (var workCount in new[] { 1, 25 })
            {
                var profile = CreateProfile(1, allowedLanguages: "eng");
                var localBooks = Enumerable.Range(1, workCount).Select(index => new Book
                {
                    Id = index, AuthorId = 1, HardcoverBookId = "hc:" + (1000 + index), MediaType = BookMediaType.Audiobook
                }).ToList();
                var localEditions = localBooks.Select(book => new Edition
                {
                    Id = book.Id, BookId = book.Id, Book = book, HardcoverEditionId = "hc:edition:" + (2000 + book.Id)
                }).ToList();
                var files = protectedFiles ? localEditions.Select(edition => new BookFile { Edition = edition, EditionId = edition.Id }).ToList() : new List<BookFile>();
                var remoteBooks = localBooks.Select(book => new Book
                {
                    HardcoverBookId = book.HardcoverBookId, MediaType = book.MediaType,
                    Editions = new List<Edition> { new() { HardcoverEditionId = "hc:edition:" + (2000 + book.Id), Language = "eng" } }
                }).ToList();
                var authors = new StubAuthorService(new Author { Id = 1 });
                var books = new StubBookService(localBooks);
                var editions = new StubEditionService(localEditions);
                var mediaFiles = new StubMediaFileService(files);
                var service = new NzbDrone.Core.Profiles.Metadata.MetadataProfileService(
                    new StubMetadataProfileRepository(new[] { profile }), authors, books, editions, mediaFiles,
                    null, null, new StubTermMatcherService(), null, NLog.LogManager.GetCurrentClassLogger());
                var timer = System.Diagnostics.Stopwatch.StartNew();

                var result = service.FilterBooks(new Author { Id = 1, Books = remoteBooks }, profile.Id);

                timer.Stop();
                Assert.That(result, Has.Count.EqualTo(workCount));
                Assert.That(new[] { authors.CatalogReads, books.CatalogReads, editions.CatalogReads, mediaFiles.CatalogReads }, Is.EqualTo(new[] { 1, 1, 1, 1 }));
                TestContext.WriteLine("Works={0}; protectedFiles={1}; author/book/edition/file bulk service reads=[{2},{3},{4},{5}]; elapsed={6}ms",
                    workCount, protectedFiles, authors.CatalogReads, books.CatalogReads, editions.CatalogReads, mediaFiles.CatalogReads, timer.ElapsedMilliseconds);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void request_protection_should_enumerate_the_requested_set_once_as_work_count_grows(bool protectWorkIds)
        {
            foreach (var workCount in new[] { 1, 25 })
            {
                var profile = CreateProfile(1, allowedLanguages: "eng");
                profile.SkipMissingDate = true;
                var books = Enumerable.Range(1, workCount).Select(index => new Book
                {
                    HardcoverBookId = "hc:" + (1000 + index), MediaType = BookMediaType.Audiobook,
                    Editions = new List<Edition> { new() { ForeignEditionId = "gr:" + (2000 + index), Language = "fra", ReadingFormatId = 2 } }
                }).ToList();
                var requested = new CountingProtectionSet(books.SelectMany(book => book.Editions).Select(edition => edition.ForeignEditionId));
                var workIds = protectWorkIds ? books.Select(book => book.HardcoverBookId).ToHashSet() : null;

                var result = CreateService(profile).FilterBooks(new Author { Books = books }, profile.Id,
                    new Dictionary<BookMediaType, IReadOnlySet<string>> { [BookMediaType.Audiobook] = requested },
                    workIds == null ? null : new Dictionary<BookMediaType, IReadOnlySet<string>> { [BookMediaType.Audiobook] = workIds });

                Assert.That(result, Has.Count.EqualTo(workCount));
                Assert.That(result.All(book => book.Editions.Count == 1), Is.True);
                Assert.That(requested.Enumerations, Is.LessThanOrEqualTo(1));
                TestContext.WriteLine("Works={0}; requestedIds={1}; suppliedWorkIds={2}; requested-set enumerations={3}",
                    workCount, requested.Count, protectWorkIds, requested.Enumerations);
            }
        }

        [Test]
        public void requested_canonical_edition_should_survive_profile_without_protecting_unknown_ids_or_ebooks()
        {
            var profile = CreateProfile(1, allowedLanguages: "eng");
            profile.SkipMissingDate = true;
            var requested = new Edition { HardcoverEditionId = "hc:edition:202", Language = "fra", ReadingFormatId = 2 };
            var audio = new Book
            {
                HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { requested, new() { HardcoverEditionId = "hc:edition:203", Language = "fra" }, new() { Language = "fra" } }
            };
            var ebook = new Book { HardcoverBookId = "hc:1001", MediaType = BookMediaType.Ebook, Editions = new List<Edition> { new() { HardcoverEditionId = "hc:edition:202", Language = "fra" } } };

            var result = CreateService(profile).FilterBooks(new Author { Books = new List<Book> { audio, ebook } }, profile.Id,
                new Dictionary<BookMediaType, IReadOnlySet<string>> { [BookMediaType.Audiobook] = new HashSet<string> { " HC:EDITION:202 ", "", " " } });

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result.Single(), Is.SameAs(audio));
            Assert.That(result.Single().Editions.Single(), Is.SameAs(requested));
        }
    }
}
