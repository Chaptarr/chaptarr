using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class RefreshBookServiceIdempotentUpdateFixture
    {
        private sealed class StubMediaFileService : IMediaFileService
        {
            private readonly List<BookFile> _files;

            public StubMediaFileService(params BookFile[] files)
            {
                _files = files?.ToList() ?? new List<BookFile>();
            }

            public BookFile Add(BookFile bookFile) => throw new NotImplementedException();
            public void AddMany(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Update(BookFile bookFile) => throw new NotImplementedException();
            public void Update(List<BookFile> bookFiles) => throw new NotImplementedException();
            public void Delete(BookFile bookFile, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public void DeleteMany(List<BookFile> bookFiles, DeleteMediaFileReason reason) => throw new NotImplementedException();
            public List<System.IO.Abstractions.IFileInfo> FilterUnchangedFiles(List<System.IO.Abstractions.IFileInfo> files, FilterFilesType filter) => files;
            public List<BookFile> GetFilesByAuthor(int authorId) => throw new NotImplementedException();
            public List<BookFile> GetFilesByBook(int bookId) => _files.ToList();
            public List<BookFile> GetFilesByBooks(List<int> bookIds) => throw new NotImplementedException();
            public List<BookFile> GetFilesByEdition(int editionId) => throw new NotImplementedException();
            public List<BookFile> GetUnmappedFiles() => throw new NotImplementedException();
            public BookFile Get(int id) => throw new NotImplementedException();
            public List<BookFile> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path) => throw new NotImplementedException();
            public List<BookFile> GetFilesWithBasePath(string path, string mediaType) => throw new NotImplementedException();
            public List<BookFile> GetFileWithPath(List<string> path) => throw new NotImplementedException();
            public BookFile GetFileWithPath(string path) => throw new NotImplementedException();
            public void UpdateMediaInfo(List<BookFile> bookFiles) => throw new NotImplementedException();
            public List<BookFile> GetFilesByAuthorAndMediaType(int authorId, string mediaType) => throw new NotImplementedException();
        }

        private class BookServiceProxy : DispatchProxy
        {
            public int UpdateManyCalls { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.UpdateMany))
                {
                    UpdateManyCalls++;
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement {targetMethod?.Name}");
            }
        }

        private sealed class RecordingRefreshEditionService : IRefreshEditionService
        {
            public List<Edition> Added { get; private set; }
            public List<Edition> Updated { get; private set; }
            public List<Edition> Deleted { get; private set; }
            public List<Edition> UpToDate { get; private set; }

            public bool RefreshEditionInfo(List<Edition> add, List<Edition> update, List<Tuple<Edition, Edition>> merge, List<Edition> delete, List<Edition> upToDate, List<Edition> remoteEditions, bool forceUpdateFileTags)
            {
                Added = add.ToList();
                Updated = update.ToList();
                Deleted = delete.ToList();
                UpToDate = upToDate.ToList();
                return add.Any() || update.Any() || merge.Any() || delete.Any();
            }
        }

        private sealed class TestableRefreshBookService : RefreshBookService
        {
            public TestableRefreshBookService(IMediaFileService mediaFileService, Logger logger)
                : this(mediaFileService, null, null, logger)
            {
            }

            public TestableRefreshBookService(IMediaFileService mediaFileService, IBookService bookService, IRefreshEditionService refreshEditionService, Logger logger)
                : base(bookService: bookService,
                    authorService: null,
                    rootFolderService: null,
                    editionService: null,
                    authorInfo: null,
                    bookInfo: null,
                    refreshEditionService: refreshEditionService,
                    mediaFileService: mediaFileService,
                    historyService: null,
                    eventAggregator: null,
                    checkIfBookShouldBeRefreshed: null,
                    editionSelector: new EditionSelector(logger),
                    editionMetadataProfileFilter: new NzbDrone.Core.Books.Services.EditionMetadataProfileFilter(new TestTermMatcherService()),
                    mediaCoverService: null,
                    logger: logger)
            {
            }

            public UpdateResult UpdateEntityPublic(Book local, Book remote)
            {
                return UpdateEntity(local, remote);
            }
        }

        [Test]
        public void update_entity_should_apply_metadata_when_provider_urls_change()
        {
            var service = new TestableRefreshBookService(new StubMediaFileService(), LogManager.GetCurrentClassLogger());

            var localProviderUrls = new ProviderUrlMap { ["goodreads"] = "https://example.com/local" };
            var remoteProviderUrls = new ProviderUrlMap { ["goodreads"] = "https://example.com/remote" };

            var local = new Book
            {
                Title = "Same",
                MediaType = BookMediaType.Audiobook,
                ProviderUrls = localProviderUrls,
                LastUpdated = new DateTime(2020, 01, 01, 0, 0, 0, DateTimeKind.Utc),
                Monitored = false
            };

            var remote = new Book
            {
                Title = "Same",
                MediaType = BookMediaType.Audiobook,
                ProviderUrls = remoteProviderUrls,
                LastUpdated = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc),
                Monitored = true
            };

            var result = service.UpdateEntityPublic(local, remote);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.EqualTo(RefreshEntityServiceBase<Book, Edition>.UpdateResult.Standard));
                Assert.That(ReferenceEquals(local.ProviderUrls, localProviderUrls), Is.False);
                Assert.That(local.ProviderUrls["goodreads"], Is.EqualTo("https://example.com/remote"));
            });
        }

        [Test]
        public void update_entity_should_converge_when_provider_ids_and_isbns_change()
        {
            var service = new TestableRefreshBookService(new StubMediaFileService(), LogManager.GetCurrentClassLogger());

            var local = new Book
            {
                Title = "Same",
                MediaType = BookMediaType.Audiobook,
                ProviderUrls = new ProviderUrlMap { ["goodreads"] = "https://example.com/goodreads" },
                HardcoverBookId = "hc:1",
                GoodreadsBookId = "gr:10",
                GoodreadsWorkId = "gr:111",
                ASIN = "B000000000",
                ISBN10 = "1427237286",
                ISBN13 = "9781427237286"
            };

            var remote = new Book
            {
                Title = "Same",
                MediaType = BookMediaType.Audiobook,
                ProviderUrls = new ProviderUrlMap { ["goodreads"] = "https://example.com/goodreads" },
                HardcoverBookId = "hc:1",
                GoodreadsBookId = "gr:10",
                GoodreadsWorkId = "gr:222",
                ASIN = "B111111111",
                ISBN10 = "1429997171",
                ISBN13 = "9781429997171"
            };

            var first = service.UpdateEntityPublic(local, remote);
            var second = service.UpdateEntityPublic(local, remote);

            Assert.Multiple(() =>
            {
                Assert.That(first, Is.EqualTo(RefreshEntityServiceBase<Book, Edition>.UpdateResult.Standard));
                Assert.That(local.GoodreadsWorkId, Is.EqualTo(remote.GoodreadsWorkId));
                Assert.That(local.ASIN, Is.Null);
                Assert.That(local.ISBN10, Is.Null);
                Assert.That(local.ISBN13, Is.Null);
                Assert.That(second, Is.EqualTo(RefreshEntityServiceBase<Book, Edition>.UpdateResult.None));
            });
        }

        [Test]
        public void update_entity_should_not_treat_stable_work_alias_overlap_as_provider_split()
        {
            var service = new TestableRefreshBookService(new StubMediaFileService(), LogManager.GetCurrentClassLogger());

            var local = new Book
            {
                Title = "Same",
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:231260754",
                RemoteProviderIds = new HashSet<string>
                {
                    "gr:231260754",
                    "hc:383236"
                }
            };

            var remote = new Book
            {
                Title = "Same",
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:3046572",
                RemoteProviderIds = new HashSet<string>
                {
                    "gr:231260754",
                    "gr:3046572",
                    "hc:383236"
                }
            };

            var result = service.UpdateEntityPublic(local, remote);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.EqualTo(RefreshEntityServiceBase<Book, Edition>.UpdateResult.Standard));
                Assert.That(local.GoodreadsWorkId, Is.EqualTo("gr:3046572"));
                Assert.That(local.RemoteProviderIds, Is.EquivalentTo(new[] { "gr:231260754", "gr:3046572", "hc:383236" }));
            });
        }

        [Test]
        public void update_entity_should_copy_alias_only_remote_provider_ids_without_metadata_change()
        {
            var service = new TestableRefreshBookService(new StubMediaFileService(), LogManager.GetCurrentClassLogger());

            var local = new Book
            {
                Title = "Same",
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:231260754",
                HardcoverBookId = "hc:383236"
            };

            var remote = new Book
            {
                Title = "Same",
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:231260754",
                HardcoverBookId = "hc:383236",
                RemoteProviderIds = new HashSet<string>
                {
                    "gr:231260754",
                    "gr:3046572",
                    "hc:383236"
                }
            };

            var result = service.UpdateEntityPublic(local, remote);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.EqualTo(RefreshEntityServiceBase<Book, Edition>.UpdateResult.None));
                Assert.That(local.RemoteProviderIds, Is.EquivalentTo(new[] { "gr:231260754", "gr:3046572", "hc:383236" }));
                Assert.That(local.RemoteProviderIds, Is.Not.SameAs(remote.RemoteProviderIds));
            });
        }

        [Test]
        public void update_entity_should_replace_local_hardcover_book_id_with_server_identity()
        {
            var logs = ConfigureLogging();
            var service = new TestableRefreshBookService(new StubMediaFileService(), LogManager.GetCurrentClassLogger());

            var local = new Book
            {
                Title = "Same",
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:714600",
                GoodreadsWorkId = "gr:231198689",
                RemoteProviderIds = new HashSet<string>
                {
                    "hc:714600",
                    "gr:231198689"
                }
            };

            var remote = new Book
            {
                Title = "Same",
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:999999",
                GoodreadsWorkId = "gr:231198689",
                RemoteProviderIds = new HashSet<string>
                {
                    "hc:999999",
                    "gr:231198689"
                }
            };

            service.UpdateEntityPublic(local, remote);

            Assert.Multiple(() =>
            {
                Assert.That(local.HardcoverBookId, Is.EqualTo("hc:999999"));
                Assert.That(local.RemoteProviderIds, Is.EquivalentTo(new[] { "hc:999999", "gr:231198689" }));
                Assert.That(logs.Logs.Any(log =>
                    log.Contains("[PROVIDER-ID-DRIFT]", StringComparison.Ordinal) &&
                    log.Contains("hc:714600", StringComparison.Ordinal) &&
                    log.Contains("hc:999999", StringComparison.Ordinal)), Is.True);
            });
        }

        [Test]
        public void update_entity_should_not_warn_when_rotated_primary_id_remains_a_server_alias()
        {
            var logs = ConfigureLogging();
            var service = new TestableRefreshBookService(new StubMediaFileService(), LogManager.GetCurrentClassLogger());

            var local = new Book
            {
                Id = 42,
                Title = "Same",
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:714600",
                RemoteProviderIds = new HashSet<string> { "hc:714600" }
            };

            var remote = new Book
            {
                Title = "Same",
                MediaType = BookMediaType.Audiobook,
                HardcoverBookId = "hc:999999",
                RemoteProviderIds = new HashSet<string> { "hc:714600", "hc:999999" }
            };

            service.UpdateEntityPublic(local, remote);

            Assert.Multiple(() =>
            {
                Assert.That(local.HardcoverBookId, Is.EqualTo("hc:999999"));
                Assert.That(local.RemoteProviderIds, Is.EquivalentTo(new[] { "hc:714600", "hc:999999" }));
                Assert.That(logs.Logs.Any(log => log.Contains("[PROVIDER-ID-DRIFT]", StringComparison.Ordinal)), Is.False);
            });
        }

        [Test]
        public void update_entity_should_clear_local_provider_aliases_when_server_omits_them()
        {
            var service = new TestableRefreshBookService(new StubMediaFileService(), LogManager.GetCurrentClassLogger());

            var local = new Book
            {
                Title = "Same",
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:231198689",
                RemoteProviderIds = new HashSet<string>
                {
                    "hc:714600",
                    "gr:231198689"
                }
            };

            var remote = new Book
            {
                Title = "Same",
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:231198689"
            };

            var result = service.UpdateEntityPublic(local, remote);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.EqualTo(RefreshEntityServiceBase<Book, Edition>.UpdateResult.None));
                Assert.That(local.RemoteProviderIds, Is.Null);
            });
        }

        [Test]
        public void use_metadata_from_should_clone_remote_provider_ids()
        {
            var source = new Book
            {
                RemoteProviderIds = new HashSet<string> { "gr:231260754", "gr:3046572" }
            };

            var target = new Book();

            target.UseMetadataFrom(source);
            target.RemoteProviderIds.Add("hc:383236");

            Assert.That(target.RemoteProviderIds, Is.EquivalentTo(new[] { "gr:231260754", "gr:3046572", "hc:383236" }));
            Assert.That(source.RemoteProviderIds, Is.EquivalentTo(new[] { "gr:231260754", "gr:3046572" }));
            Assert.That(target.RemoteProviderIds, Is.Not.SameAs(source.RemoteProviderIds));
        }

        [Test]
        public void clone_book_should_clone_remote_provider_ids_for_new_child_adds()
        {
            var source = new Book
            {
                Title = "Harry Potter and the Goblet of Fire",
                RemoteProviderIds = new HashSet<string> { "gr:231260754", "gr:3046572", "hc:383236" },
                Author = new Author
                {
                    Name = "J.K. Rowling",
                    RemoteProviderIds = new HashSet<string> { "gr:1077326", "gr:19981845" }
                },
                Editions = new List<Edition>
                {
                    new Edition { Title = "Audio", ForeignEditionId = "hc:123" }
                }
            };

            var clone = RefreshEntityCopy.CloneBook(source, includeEditions: true);
            clone.RemoteProviderIds.Add("gr:57992582");
            clone.Author.RemoteProviderIds.Add("hc:80626");

            Assert.Multiple(() =>
            {
                Assert.That(clone.RemoteProviderIds, Is.EquivalentTo(new[] { "gr:231260754", "gr:3046572", "hc:383236", "gr:57992582" }));
                Assert.That(source.RemoteProviderIds, Is.EquivalentTo(new[] { "gr:231260754", "gr:3046572", "hc:383236" }));
                Assert.That(clone.RemoteProviderIds, Is.Not.SameAs(source.RemoteProviderIds));
                Assert.That(clone.Author.RemoteProviderIds, Is.EquivalentTo(new[] { "gr:1077326", "gr:19981845", "hc:80626" }));
                Assert.That(source.Author.RemoteProviderIds, Is.EquivalentTo(new[] { "gr:1077326", "gr:19981845" }));
                Assert.That(clone.Author.RemoteProviderIds, Is.Not.SameAs(source.Author.RemoteProviderIds));
            });
        }

        [Test]
        public void identical_author_then_work_blueprint_should_not_churn_edition_or_file_link()
        {
            var localEdition = new Edition
            {
                Id = 214,
                BookId = 38728,
                ForeignEditionId = "az:B0H75VCVGG-audiobook",
                Title = "Piranesi",
                Asin = "B0H75HGGRR",
                Asins = new List<string> { "B0H75HGGRR", "B0H75VCVGG" },
                ReadingFormatId = 2,
                Monitored = true
            };
            var file = new BookFile
            {
                Id = 99,
                EditionId = localEdition.Id,
                Edition = localEdition
            };
            var local = new Book
            {
                Id = 38728,
                Title = "Piranesi",
                ForeignEditionId = "hc:175280",
                HardcoverBookId = "hc:175280",
                BaseBookId = "hc:175280",
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = true,
                AudiobookMonitored = true,
                ProviderUrls = new ProviderUrlMap { ["hardcover"] = "https://hardcover.app/books/piranesi" },
                Links = new List<Links> { new Links { Name = "hardcover", Url = "https://hardcover.app/books/piranesi" } },
                Editions = new List<Edition> { localEdition },
                BookFiles = new List<BookFile> { file }
            };
            localEdition.Book = local;

            var remoteEdition = RefreshEntityCopy.CloneEdition(localEdition);
            remoteEdition.Id = 0;
            remoteEdition.BookId = 0;
            remoteEdition.Book = null;
            remoteEdition.Monitored = false;
            var remote = RefreshEntityCopy.CloneBook(local, includeEditions: false);
            remote.Id = 0;
            remote.Editions = new List<Edition> { remoteEdition };
            remote.BookFiles = null;
            remote.Author = null;
            remote.AuthorId = 0;

            var mediaFiles = new StubMediaFileService(file);
            var refreshEditions = new RecordingRefreshEditionService();
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var service = new TestableRefreshBookService(mediaFiles, bookService, refreshEditions, LogManager.GetCurrentClassLogger());

            service.RefreshBookInfo(local, new List<Book> { remote }, new Author(), false);

            Assert.Multiple(() =>
            {
                Assert.That(refreshEditions.Added, Is.Empty);
                Assert.That(refreshEditions.Deleted, Is.Empty);
                Assert.That(refreshEditions.Updated, Is.Empty);
                Assert.That(refreshEditions.UpToDate, Has.Count.EqualTo(1));
                Assert.That(refreshEditions.UpToDate.Single(), Is.SameAs(localEdition));
                Assert.That(localEdition.Id, Is.EqualTo(214));
                Assert.That(localEdition.Monitored, Is.True);
                Assert.That(file.EditionId, Is.EqualTo(214));
                Assert.That(file.Edition, Is.SameAs(localEdition));
            });
        }

        private static MemoryTarget ConfigureLogging()
        {
            var memoryTarget = new MemoryTarget("provider-id-drift-memory")
            {
                Layout = "${level}|${logger}|${message}"
            };

            var config = new LoggingConfiguration();
            config.AddRule(LogLevel.Warn, LogLevel.Fatal, memoryTarget);
            LogManager.Configuration = config;
            LogManager.ReconfigExistingLoggers();

            return memoryTarget;
        }
    }
}
