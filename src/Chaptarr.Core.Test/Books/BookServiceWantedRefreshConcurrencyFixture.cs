using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Profiles.Metadata;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookServiceWantedRefreshConcurrencyFixture
    {
        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, true, false)]
        [TestCase(false, false, true)]
        [TestCase(true, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, true, true)]
        public void wanted_selection_should_not_report_success_with_no_persisted_selected_edition(bool postgres, bool interleave, bool authorRefresh)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var profileRows = new MetadataProfileRepository(database, events);
                var profile = profileRows.Insert(new MetadataProfile
                {
                    Name = "Audit profile", AllowedLanguages = "", MinPopularity = 0,
                    SkipMissingDate = false, Ignored = new List<string>()
                });
                var authors = new AuthorRepository(database, events, logger);
                var rootPath = @"C:\Audio".AsOsAgnostic();
                var author = authors.Insert(new Author
                {
                    Name = "Audit Author", Overview = "", CleanName = "auditauthor", TitleSlug = "audit-author",
                    HardcoverAuthorId = "hc:9001", Path = @"C:\Audio\Audit Author".AsOsAgnostic(),
                    AudiobookRootFolderPath = rootPath, AudiobookMetadataProfileId = profile.Id, AudiobookQualityProfileId = 1, Added = DateTime.UtcNow
                });
                var authorService = InterfaceProxy.Proxy<IAuthorService>((method, args) => method.Name switch
                {
                    nameof(IAuthorService.GetAuthor) => authors.Get((int)args[0]),
                    nameof(IAuthorService.UpdateAuthor) => authors.Update((Author)args[0]),
                    nameof(IAuthorService.FindByProviderId) => authors.Get(author.Id),
                    nameof(IAuthorService.GetExistingAuthors) => authors.Get((IEnumerable<int>)args[0]).ToList(),
                    nameof(IAuthorService.GetAllAuthors) => authors.All().ToList(),
                    nameof(IAuthorService.GetAuthors) => authors.Get((IEnumerable<int>)args[0]).ToList(),
                    _ => throw new AssertionException(method.Name)
                });
                var root = new RootFolder { Path = rootPath, FolderType = FolderType.Audiobook };
                var roots = InterfaceProxy.Proxy<IRootFolderService>((method, _) => method.Name == nameof(IRootFolderService.All)
                    ? new List<RootFolder> { root } : throw new AssertionException(method.Name));
                Action beforeBatch = null;
                var files = InterfaceProxy.Proxy<IMediaFileService>((method, _) =>
                {
                    if (method.Name == nameof(IMediaFileService.GetFilesByBooks)) beforeBatch?.Invoke();
                    if (method.Name is nameof(IMediaFileService.GetFilesByBooks) or nameof(IMediaFileService.GetFilesByBook) or nameof(IMediaFileService.GetFilesByAuthor))
                        return new List<BookFile>();
                    throw new AssertionException(method.Name);
                });
                var bookRows = new BookRepository(database, events);
                var editionRows = new EditionRepository(database, events);
                var editions = new EditionService(editionRows, events, logger);
                var books = new BookService(bookRows, editions, events, authorService, files, roots,
                    new SeriesBookLinkRepository(database, events), null, logger);
                var source = bookRows.Insert(new Book
                {
                    AuthorId = author.Id, Title = "Audit Work", CleanTitle = "auditwork", TitleSlug = "audit-work",
                    HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook, AnyEditionOk = true,
                    Monitored = true, AudiobookMonitored = true, Added = DateTime.UtcNow,
                    AddOptions = new AddBookOptions { AddType = BookAddType.Automatic }
                });
                var first = editionRows.Insert(new Edition
                {
                    BookId = source.Id, Title = source.Title, TitleSlug = "first", ForeignEditionId = "hc:edition:2001-audiobook",
                    HardcoverEditionId = "2001", ReadingFormatId = 2, Narrator = "Stephen Fry",
                    NarratorNames = new List<string> { "Stephen Fry" }, Monitored = true
                });
                var requested = editionRows.Insert(new Edition
                {
                    BookId = source.Id, Title = source.Title, TitleSlug = "requested", ForeignEditionId = "hc:edition:2002-audiobook",
                    HardcoverEditionId = "2002", ReadingFormatId = 2, Narrator = "Jim Dale",
                    NarratorNames = new List<string> { "Jim Dale" }, Monitored = false
                });
                var refreshEditions = new RefreshEditionService(editions,
                    InterfaceProxy.Proxy<IMetadataTagService>((_, _) => null), null, database, logger);
                var refresh = new RefreshBookService(books, authorService, roots, editions, null, null, refreshEditions,
                    files, null, events, null, new EditionSelector(logger), null, null, logger, mainDatabase: null);
                var profileService = new MetadataProfileService(profileRows, authorService, books, editions, files,
                    null, roots, new TestTermMatcherService(), events, logger);
                var config = InterfaceProxy.Proxy<IConfigService>((method, _) => method.ReturnType == typeof(bool) ? false :
                    method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
                var refreshAuthors = new RefreshAuthorService(MetadataHealthGateStub.Ready(), null, authorService, books, editions, profileService, refresh,
                    null, events, null, files, new global::NzbDrone.Core.History.HistoryRepository(database, events), roots, null, null, config, null, null, null, null,
                    logger, editionSelector: new EditionSelector(logger), mainDatabase: database, bookRepository: new BookRepository(database, events), authorRepository: new AuthorRepository(database, events, logger));
                var failures = new List<Exception>();
                Dictionary<int, Book> result;
                if (interleave)
                {
                    using var writerSnapshotReady = new ManualResetEventSlim();
                    using var allowWriter = new ManualResetEventSlim();
                    using var refreshStarted = new ManualResetEventSlim();
                    var staleLocal = bookRows.Get(source.Id);
                    staleLocal.Author = author;
                    staleLocal.Editions = editionRows.FindByBook(new[] { source.Id }).ToList();
                    staleLocal.BookFiles = new List<BookFile>();
                    var remote = RefreshEntityCopy.CloneBook(staleLocal, includeEditions: false, includeSeriesLinks: false);
                    remote.Author = author;
                    remote.Editions = new List<Edition> { RefreshEntityCopy.CloneEdition(first) };
                    beforeBatch = () =>
                    {
                        beforeBatch = null;
                        writerSnapshotReady.Set();
                        Assert.That(allowWriter.Wait(TimeSpan.FromSeconds(15)), Is.True, "test must release the writer");
                    };
                    var writerTask = Task.Run(() => books.AddWantedEditions(new List<Edition> { requested }, (_, error) => failures.Add(error)));
                    Task<bool> refreshTask = null;
                    try
                    {
                        Assert.That(writerSnapshotReady.Wait(TimeSpan.FromSeconds(15)), Is.True);
                        refreshTask = Task.Run(() =>
                        {
                            refreshStarted.Set();
                            if (!authorRefresh)
                            {
                                return refresh.RefreshBookInfo(staleLocal, new List<Book> { remote }, author, false);
                            }

                            return refreshAuthors.ReconcileAuthorBlob(author, new Author
                            {
                                Id = author.Id, Name = author.Name, CleanName = author.CleanName, TitleSlug = author.TitleSlug,
                                Overview = author.Overview, HardcoverAuthorId = author.HardcoverAuthorId,
                                Books = new List<Book> { remote }, Series = new List<Series>()
                            });
                        });
                        Assert.That(refreshStarted.Wait(TimeSpan.FromSeconds(15)), Is.True);
                        var refreshFinishedBeforeWriter = refreshTask.Wait(TimeSpan.FromSeconds(2));
                        TestContext.WriteLine($"Refresh completed while writer held selection lock: {refreshFinishedBeforeWriter}");
                        allowWriter.Set();
                        Assert.That(writerTask.Wait(TimeSpan.FromSeconds(15)), Is.True);
                        Assert.That(refreshTask.Wait(TimeSpan.FromSeconds(15)), Is.True);
                        result = writerTask.GetAwaiter().GetResult();
                    }
                    finally
                    {
                        allowWriter.Set();
                        Task.WaitAll(refreshTask == null ? new Task[] { writerTask } : new Task[] { writerTask, refreshTask }, TimeSpan.FromSeconds(15));
                    }
                }
                else
                {
                    result = books.AddWantedEditions(new List<Edition> { requested }, (_, error) => failures.Add(error));
                }
                var saved = bookRows.Get(source.Id);
                var savedEditions = editionRows.FindByBook(new[] { source.Id }).ToList();
                TestContext.WriteLine($"Provider={(postgres ? "PostgreSQL" : "SQLite")}, interleave={interleave}, reported={result.Count}, returnedRows={string.Join(',', result.Values.Select(book => book.Id))}, failures={failures.Count}, AnyEditionOk={saved.AnyEditionOk}, editions={savedEditions.Count}, selected={savedEditions.Count(edition => edition.Monitored)}, manual={savedEditions.Count(edition => edition.ManualAdd)}");
                Assert.That(result.Count == 0 || savedEditions.Count(edition => edition.Monitored) == 1, Is.True,
                    "a successful wanted request must have one persisted selected edition");
                Assert.That(result.Count, Is.EqualTo(1));
                Assert.That(result.Values.Single().Id, Is.EqualTo(source.Id));
                Assert.That(savedEditions.Single(edition => edition.Monitored).Id, Is.EqualTo(requested.Id));
                Assert.That(saved.AnyEditionOk, Is.False);
                Assert.That(savedEditions.Single(edition => edition.Monitored).ManualAdd, Is.True);
                Assert.That(failures, Is.Empty);
            });
        }
    }
}
