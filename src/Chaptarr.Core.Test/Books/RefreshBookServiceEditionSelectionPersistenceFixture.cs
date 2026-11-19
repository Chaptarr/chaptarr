using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Chaptarr.Api.V1.Books;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class RefreshBookServiceEditionSelectionPersistenceFixture
    {
        public class ServiceProxy : DispatchProxy
        {
            public Func<MethodInfo, object[], object> Handler { get; set; }

            protected override object Invoke(MethodInfo method, object[] args) => Handler(method, args);

            public static T Create<T>(Func<MethodInfo, object[], object> handler)
                where T : class
            {
                var service = DispatchProxy.Create<T, ServiceProxy>();
                ((ServiceProxy)(object)service).Handler = handler;
                return service;
            }
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void new_and_mixed_editions_have_one_persisted_selection(bool postgres, BookMediaType mediaType)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var harness = new RefreshHarness(database, mediaType);
                var remote = harness.RemoteBook(
                    harness.Edition("first", 10), harness.Edition("second", 100), harness.Edition("third", 20));

                harness.Refresh(remote);
                var selected = harness.AssertSelection("second");
                harness.AssertUnchangedRefresh(remote, selected);

                var next = harness.RemoteBook(
                    harness.Edition("second", 100), harness.Edition("fourth", 1000), harness.Edition("fifth", 30));
                harness.Refresh(next);
                harness.AssertSelection("second");
                harness.AssertUnchangedRefresh(next, selected);

                harness.ClearSelection();
                var mixed = harness.RemoteBook(
                    harness.Edition("second", 100), harness.Edition("sixth", 2000), harness.Edition("seventh", 50));
                harness.Refresh(mixed);
                selected = harness.AssertSelection("sixth");
                harness.AssertUnchangedRefresh(mixed, selected);
            });
        }

        [Test]
        public void provider_suggestion_does_not_replace_surviving_selection(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType,
            [Values(false, true)] bool anyEditionOk)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var harness = new RefreshHarness(database, mediaType);
                harness.SetAnyEditionOk(anyEditionOk);
                var selected = harness.Edition("selected", 1, monitored: true);
                var retentionPin = harness.Edition("retained-pin", 2, manualAdd: true);
                harness.Seed(selected, retentionPin);
                var remote = harness.RemoteBook(harness.Edition("selected", 1),
                    harness.Edition("suggestion", 10000, monitored: true));

                harness.Refresh(remote);
                Assert.That(harness.AssertSelection("selected"), Is.EqualTo(selected.Id));
                Assert.That(harness.Editions().Single(edition => edition.Id == retentionPin.Id).ManualAdd, Is.True);
                harness.AssertUnchangedRefresh(remote, selected.Id);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void strict_selected_edition_survives_remote_removal_without_manual_flag(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var harness = new RefreshHarness(database, BookMediaType.Audiobook);
                harness.SetAnyEditionOk(false);
                var selected = harness.Edition("strict", 1, monitored: true);
                harness.Seed(selected);
                var remote = harness.RemoteBook(harness.Edition("suggestion", 10000, monitored: true));

                harness.Refresh(remote);
                Assert.That(harness.AssertSelection("strict"), Is.EqualTo(selected.Id));
                Assert.That(harness.Editions().Single(edition => edition.Id == selected.Id).ManualAdd, Is.False);
                harness.AssertUnchangedRefresh(remote, selected.Id);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void genuine_unsaved_manual_selection_is_not_discarded_as_provider_hint(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var harness = new RefreshHarness(database, BookMediaType.Audiobook);
                harness.Seed(harness.Edition("old", 10000, monitored: true));
                var remote = harness.RemoteBook(harness.Edition("old", 10000),
                    harness.Edition("manual", 1, monitored: true, manualAdd: true));

                harness.Refresh(remote);
                harness.AssertSelection("manual");
                Assert.That(harness.Editions().Single(edition => edition.Monitored).ManualAdd, Is.True);
            });
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void replacing_selected_edition_selects_one_survivor(bool postgres, BookMediaType mediaType)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var harness = new RefreshHarness(database, mediaType);
                harness.Seed(harness.Edition("removed", 1000, monitored: true), harness.Edition("survivor", 10));
                var remote = harness.RemoteBook(harness.Edition("survivor", 10), harness.Edition("new", 100));

                harness.Refresh(remote);
                var selected = harness.AssertSelection("new");
                Assert.That(harness.Editions().Select(e => e.ForeignEditionId), Does.Not.Contain("gr:removed"));
                harness.AssertUnchangedRefresh(remote, selected);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void merging_selected_duplicate_selects_the_retained_pin_and_moves_its_files(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var harness = new RefreshHarness(database, BookMediaType.Audiobook);
                var removed = harness.Edition("duplicate", 10, monitored: true);
                var retained = harness.Edition("duplicate", 10, manualAdd: true);
                harness.Seed(removed, retained);
                var file = harness.AddFile(removed);
                var remote = harness.RemoteBook(harness.Edition("duplicate", 10), harness.Edition("other", 100));

                harness.Refresh(remote);
                var selected = harness.AssertSelection("duplicate");
                Assert.Multiple(() =>
                {
                    Assert.That(selected, Is.EqualTo(retained.Id));
                    Assert.That(harness.Editions().Any(e => e.Id == removed.Id), Is.False);
                    Assert.That(harness.Files.Get(file.Id).EditionId, Is.EqualTo(retained.Id));
                    Assert.That(harness.Editions().Single(e => e.Id == retained.Id).ManualAdd, Is.True);
                });
                harness.AssertUnchangedRefresh(remote, selected);
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void selected_pin_or_file_survives_missing_remote_edition(bool postgres, bool hasFile)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var harness = new RefreshHarness(database, BookMediaType.Audiobook);
                var protectedEdition = harness.Edition("protected", 10, monitored: true, manualAdd: !hasFile);
                harness.Seed(protectedEdition);
                var file = hasFile ? harness.AddFile(protectedEdition) : null;
                var remote = harness.RemoteBook(harness.Edition("new", 1000), harness.Edition("other", 100));

                harness.Refresh(remote);
                var selected = harness.AssertSelection("protected");
                Assert.That(selected, Is.EqualTo(protectedEdition.Id));
                if (file != null)
                {
                    Assert.That(harness.Files.Get(file.Id).EditionId, Is.EqualTo(protectedEdition.Id));
                }

                harness.AssertUnchangedRefresh(remote, selected);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void many_editions_reuse_one_file_snapshot_for_protection_and_selection(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var harness = new RefreshHarness(database, BookMediaType.Audiobook);
                var editions = Enumerable.Range(0, 128).Select(index => harness.Edition("batch-" + index, index, monitored: index == 0)).ToArray();
                harness.Seed(editions);
                var file = harness.AddFile(editions[0]);
                var remote = harness.RemoteBook(editions.Select(RefreshEntityCopy.CloneEdition).ToArray());
                var stopwatch = Stopwatch.StartNew();

                harness.Refresh(remote);
                harness.AssertSelection("batch-0");

                stopwatch.Stop();
                var fileQueries = harness.FileQueries;
                Assert.That(harness.Files.Get(file.Id).EditionId, Is.EqualTo(editions[0].Id));
                TestContext.WriteLine("Provider={0}, editions={1}, book-file service reads={2}, edition-file service reads={3}, file SELECTs={4}, refresh elapsed={5}ms",
                    postgres ? "PostgreSQL" : "SQLite", editions.Length, harness.FileReads, harness.EditionFileReads, fileQueries, stopwatch.ElapsedMilliseconds);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void unchanged_edition_alias_gain_and_withdrawal_survive_full_book_refresh_and_reload(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var harness = new RefreshHarness(database, BookMediaType.Audiobook);
                var seed = harness.Edition("19236677", 10, monitored: true);
                seed.GoodreadsEditionId = 19236677;
                harness.Seed(seed);
                var remoteEdition = RefreshEntityCopy.CloneEdition(harness.Editions().Single());
                remoteEdition.RemoteProviderIds = new HashSet<string> { "gr:44767458" };
                var repository = new EditionRepository(database, null);
                harness.Refresh(harness.RemoteBook(remoteEdition));
                var first = repository.FindAllByProviderIds("gr", new[] { "44767458" })["44767458"].Single();
                remoteEdition.RemoteProviderIds = new HashSet<string> { "gr:44767459" };
                harness.Refresh(harness.RemoteBook(remoteEdition));
                Assert.That(repository.FindAllByProviderIds("gr", new[] { "44767458" })["44767458"], Is.Empty);
                Assert.That(repository.FindAllByProviderIds("gr", new[] { "44767459" })["44767459"].Single().Id, Is.EqualTo(first.Id));
                remoteEdition.RemoteProviderIds.Clear();
                harness.Refresh(harness.RemoteBook(remoteEdition));
                Assert.That(repository.FindAllByProviderIds("gr", new[] { "44767459" })["44767459"], Is.Empty);
                Assert.That(harness.Editions().Single().Id, Is.EqualTo(first.Id));
            });
        }

        private sealed class RefreshHarness
        {
            private readonly BookRepository _books;
            private readonly EditionService _editions;
            private readonly RefreshBookService _refresh;
            private readonly Book _book;
            private readonly Author _author;
            private readonly List<BookUpdatedEvent> _events = new();
            private readonly int _nativeFormat;
            private int _fileReads;
            private int _editionFileReads;
            private int _fileQueries;

            public MediaFileRepository Files { get; }
            public int FileReads => _fileReads;
            public int EditionFileReads => _editionFileReads;
            public int FileQueries => _fileQueries;

            public RefreshHarness(IMainDatabase database, BookMediaType mediaType)
            {
                var originalDatabase = database;
                database = ServiceProxy.Create<IMainDatabase>((method, args) => method.Name == nameof(IMainDatabase.OpenConnection)
                    ? CommandObserver.Wrap(originalDatabase.OpenConnection(), command =>
                    {
                        if (command.CommandText.Contains("FROM \"BookFiles\"", StringComparison.OrdinalIgnoreCase))
                        {
                            _fileQueries++;
                        }
                    })
                    : method.Invoke(originalDatabase, args));
                var logger = LogManager.GetCurrentClassLogger();
                var events = ServiceProxy.Create<IEventAggregator>((method, args) =>
                {
                    if (args[0] is BookUpdatedEvent bookEvent)
                    {
                        _events.Add(bookEvent);
                    }

                    return null;
                });
                _books = new BookRepository(database, events);
                _editions = new EditionService(new EditionRepository(database, events), events, logger);
                Files = new MediaFileRepository(database, events);
                _nativeFormat = mediaType == BookMediaType.Audiobook ? 2 : 3;
                _author = new AuthorRepository(database, events, logger).Insert(new Author
                {
                    Name = "George R.R. Martin", CleanName = "georgerrmartin", SortName = "martingeorgerr",
                    TitleSlug = "george-rr-martin", GoodreadsAuthorId = "gr:346732",
                    Path = @"C:\Books\George R.R. Martin".AsOsAgnostic(), Added = DateTime.UtcNow
                });
                _book = _books.Insert(new Book
                {
                    Title = "Dangerous Women",
                    CleanTitle = "dangerous women",
                    ForeignEditionId = "gr:19236677",
                    GoodreadsWorkId = "gr:19236677",
                    HardcoverBookId = "hc:103815",
                    TitleSlug = "dangerous-women-" + mediaType,
                    MediaType = mediaType,
                    AnyEditionOk = true,
                    Added = DateTime.UtcNow,
                    Author = _author,
                    AuthorId = _author.Id
                });
                var books = ServiceProxy.Create<IBookService>((method, args) => method.Name switch
                {
                    nameof(IBookService.UpdateMany) => Save((List<Book>)args[0]),
                    nameof(IBookService.GetBook) => Load(),
                    _ => throw new NotImplementedException(method.Name)
                });
                var media = ServiceProxy.Create<IMediaFileService>((method, args) =>
                {
                    if (method.Name == nameof(IMediaFileService.GetFilesByBook))
                    {
                        _fileReads++;
                        return Files.GetFilesByBook((int)args[0]);
                    }

                    if (method.Name == nameof(IMediaFileService.GetFilesByEdition))
                    {
                        _editionFileReads++;
                        return Files.GetFilesByEdition((int)args[0]);
                    }

                    throw new NotImplementedException(method.Name);
                });
                var tags = ServiceProxy.Create<IMetadataTagService>((method, args) => null);
                _refresh = new RefreshBookService(books, null, null, _editions, null, null,
                    new RefreshEditionService(_editions, tags, null, database, logger), media, null, events, null,
                    new EditionSelector(logger), new EditionMetadataProfileFilter(new TestTermMatcherService()), null, logger, mainDatabase: null);
            }

            private object Save(List<Book> books)
            {
                _books.UpdateMany(books);
                return null;
            }

            private Book Load()
            {
                var book = _books.Get(_book.Id);
                book.Author = _author;
                book.Editions = Editions();
                return book;
            }

            public Edition Edition(string key, int votes, bool monitored = false, bool manualAdd = false) => new()
            {
                BookId = _book.Id, ForeignEditionId = "gr:" + key, Title = _book.Title,
                ReadingFormatId = _nativeFormat, Language = "eng", Ratings = new Ratings { Votes = votes },
                Monitored = monitored, ManualAdd = manualAdd
            };

            public Book RemoteBook(params Edition[] editions)
            {
                var remote = RefreshEntityCopy.CloneBook(_book, includeEditions: false);
                remote.Author = null;
                remote.Editions = editions.ToList();
                return remote;
            }

            public void Seed(params Edition[] editions) => _editions.InsertMany(editions.ToList());

            public void SetAnyEditionOk(bool anyEditionOk)
            {
                _book.AnyEditionOk = anyEditionOk;
                _books.Update(_book);
            }

            public void ClearSelection()
            {
                var editions = Editions();
                editions.ForEach(edition => edition.Monitored = false);
                _editions.UpdateMany(editions);
            }

            public BookFile AddFile(Edition edition) => Files.Insert(new BookFile
            {
                EditionId = edition.Id, Path = @"C:\Books\Dangerous Women.m4b".AsOsAgnostic(),
                Size = 100, DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow,
                Quality = new QualityModel(), MediaInfo = new MediaInfoModel()
            });

            public List<Edition> Editions() => _editions.GetEditionRowsByBook(new[] { _book.Id });

            public void Refresh(Book remote)
            {
                _fileReads = 0;
                _editionFileReads = 0;
                _fileQueries = 0;
                _events.Clear();
                _refresh.RefreshBookInfo(Load(), new List<Book> { remote }, new Author(), false);
                Assert.That(Editions().Count(e => e.Monitored), Is.EqualTo(1), "persisted surviving editions must contain one selection");
                Assert.That(_editionFileReads, Is.Zero, "selection/protection must reuse the book file snapshot");
                Assert.That(_fileReads, Is.LessThanOrEqualTo(1));
                Assert.That(_fileQueries, Is.LessThanOrEqualTo(1), "lazy materialization must also reuse one file query");
                foreach (var bookEvent in _events)
                {
                    Assert.That(bookEvent.Book.Editions.ToResource().Count(e => e.Monitored), Is.EqualTo(1));
                }
            }

            public int AssertSelection(string key)
            {
                var resources = Editions().ToResource();
                Assert.That(resources.Where(e => e.Monitored).Select(e => e.ForeignEditionId), Is.EqualTo(new[] { "gr:" + key }));
                return resources.Single(e => e.Monitored).Id;
            }

            public void AssertUnchangedRefresh(Book remote, int selectedId)
            {
                var before = Editions().OrderBy(e => e.Id).Select(e => (e.Id, e.Monitored, e.ManualAdd)).ToList();
                Refresh(remote);
                Assert.That(Editions().OrderBy(e => e.Id).Select(e => (e.Id, e.Monitored, e.ManualAdd)), Is.EqualTo(before));
                Assert.That(Editions().Single(e => e.Monitored).Id, Is.EqualTo(selectedId));
                Assert.That(_events, Is.Empty);
            }
        }
    }
}
