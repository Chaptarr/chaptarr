using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.History;
using Chaptarr.Core.Test.Common;
using Chaptarr.Core.Test.Download;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Messaging;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using ImportResult = NzbDrone.Core.MediaFiles.BookImport.ImportResult;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.History
{
    [TestFixture]
    [NonParallelizable]
    public class HistoryImportContextFixture
    {
        private sealed class Events : IEventAggregator
        {
            public HistoryService History;
            public DownloadHistoryService Downloads;
            public NotificationService Notifications;
            public int IncompleteEvents;
            public List<Exception> HistoryErrors = new();

            public void PublishEvent<TEvent>(TEvent message) where TEvent : class, IEvent
            {
                if (message is BookGrabbedEvent grabbed)
                {
                    History.Handle(grabbed);
                    Downloads.Handle(grabbed);
                }
                else if (message is BookImportIncompleteEvent incomplete)
                {
                    IncompleteEvents++;
                    try
                    {
                        History.Handle(incomplete);
                    }
                    catch (Exception error)
                    {
                        HistoryErrors.Add(error);
                    }
                    Downloads.Handle(incomplete);
                    Notifications.Handle(incomplete);
                }
            }
        }

        private sealed class Harness
        {
            public readonly Events Events = new();
            public readonly HistoryRepository HistoryRepository;
            public readonly DownloadHistoryRepository DownloadRepository;
            public readonly HistoryService History;
            public readonly DownloadHistoryService Downloads;
            public readonly Author Author;
            public readonly Book Book;
            public readonly Book Other;
            public readonly Book Audio;
            public readonly ICustomFormatCalculationService Formats;
            public readonly HistoryController Controller;
            public readonly DownloadClientItem Item;
            public readonly Logger Logger = LogManager.GetCurrentClassLogger();
            public readonly List<ImportResult> Results = new();
            public int EntityLookups;
            public int? FailInsertForBook;
            public readonly List<BookDownloadMessage> ImportFailureNotifications = new();
            public int NotificationFactoryLookups;
            public int NotificationSuccesses;

            public Harness(IMainDatabase database)
            {
                var authors = new AuthorRepository(database, Events, Logger);
                var books = new BookRepository(database, Events);
                Author = authors.Insert(new Author
                {
                    Name = "Fixture Author", CleanName = "fixtureauthor", SortName = "fixtureauthor",
                    SortNameLastFirst = "Author, Fixture", TitleSlug = "fixture-author",
                    Path = @"C:\Books\Fixture Author".AsOsAgnostic(), Added = DateTime.UtcNow
                });
                Book Add(string title, BookMediaType media) => books.Insert(new Book
                {
                    AuthorId = Author.Id, Author = Author, Title = title, CleanTitle = title.ToLowerInvariant(),
                    TitleSlug = Guid.NewGuid().ToString("N"), MediaType = media, AnyEditionOk = true,
                    Added = DateTime.UtcNow, ReleaseDate = DateTime.UtcNow.AddYears(-1)
                });
                Book = Add("Stored title", BookMediaType.Ebook);
                Other = Add("Other title", BookMediaType.Ebook);
                Audio = Add("Stored title", BookMediaType.Audiobook);
                HistoryRepository = new HistoryRepository(database, Events);
                DownloadRepository = new DownloadHistoryRepository(database, Events);
                var historyRepository = Proxy<IHistoryRepository>((method, args) =>
                {
                    if (method.Name == nameof(IHistoryRepository.Insert) &&
                        ((EntityHistory)args[0]).BookId == FailInsertForBook)
                    {
                        throw new InvalidOperationException("Injected history insertion failure");
                    }

                    if (method.Name == nameof(IHistoryRepository.FindByDownloadId))
                    {
                        EntityLookups++;
                    }

                    return method.Invoke(HistoryRepository, args);
                });
                History = new HistoryService(historyRepository, Logger, DownloadRepository);
                Downloads = new DownloadHistoryService(DownloadRepository, History);
                Events.History = History;
                Events.Downloads = Downloads;
                var notification = Proxy<INotification>((method, args) =>
                {
                    if (method.Name == "get_HasPendingQueue") return false;
                    if (method.Name == "get_Definition")
                    {
                        return new NotificationDefinition { Id = 43 };
                    }

                    if (method.Name == nameof(INotification.OnImportFailure))
                    {
                        ImportFailureNotifications.Add((BookDownloadMessage)args[0]);
                        return null;
                    }

                    throw new AssertionException(method.Name);
                });
                Events.Notifications = new NotificationService(
                    Proxy<INotificationFactory>((method, args) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(INotificationFactory.OnImportFailureEnabled)));
                        NotificationFactoryLookups++;
                        return new List<INotification> { notification };
                    }),
                    Proxy<INotificationStatusService>((method, args) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(INotificationStatusService.RecordSuccess)));
                        Assert.That(args[0], Is.EqualTo(43));
                        NotificationSuccesses++;
                        return null;
                    }), null, Logger);
                Formats = Proxy<ICustomFormatCalculationService>((method, args) =>
                    method.Name == nameof(ICustomFormatCalculationService.ParseCustomFormat)
                        ? new List<CustomFormat>() : throw new AssertionException(method.Name));
                Controller = new HistoryController(new SeriesBookLinkService(new SeriesBookLinkRepository(database, Events)), History, Formats,
                    Proxy<IUpgradableSpecification>((method, args) => false), NoopFailedDownloadService.Instance,
                    Proxy<IAuthorService>((method, args) => authors.Get((int)args[0])))
                {
                    ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
                };
                Item = new DownloadClientItem
                {
                    DownloadId = "4A094048-99BD-4395-869F-18E691A615AD",
                    Title = "Fixture Author - Stored title (epub)", Category = "books",
                    OutputPath = new OsPath(@"C:\Downloads\Stored title.epub".AsOsAgnostic()),
                    Status = DownloadItemStatus.Completed,
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Id = 4, Type = "SABnzbd", Name = "Fixture SAB", Protocol = DownloadProtocol.Usenet
                    }
                };
            }

            public RemoteBook Remote(params Book[] books) => new()
            {
                Author = Author, Books = books.ToList(), ReleaseSource = ReleaseSourceType.InteractiveSearch,
                CustomFormatScore = 17, DownloadAllowed = true,
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.EPUB), ReleaseGroup = "Fixture" },
                Release = new ReleaseInfo
                {
                    Title = Item.Title, Indexer = "Fixture Indexer", Guid = "fixture-release",
                    DownloadProtocol = DownloadProtocol.Usenet, Size = 1234
                }
            };

            public void Grab(RemoteBook remote) => Events.PublishEvent(new BookGrabbedEvent(remote)
            {
                DownloadId = Item.DownloadId, DownloadClientId = 4,
                DownloadClient = "SABnzbd", DownloadClientName = "Fixture SAB"
            });

            public ImportResult Result(Book book, bool imported = false) => new(
                new ImportDecision<LocalBook>(new LocalBook
                {
                    Author = Author, Book = book, Path = Item.OutputPath.FullPath,
                    Quality = new QualityModel(Quality.EPUB)
                }), imported ? ImportResultType.Imported : ImportResultType.Skipped,
                imported ? Array.Empty<string>() : new[] { "Access to destination file is denied" });

            public CompletedDownloadService Completed() => new(Events, History,
                Proxy<IProvideImportItemService>((method, args) => Item.Clone()),
                Proxy<IDownloadedBooksImportService>((method, args) => method.Name == nameof(IDownloadedBooksImportService.ProcessIncludedMedia) ? new List<ImportResult>() : Results),
                Proxy<IDownloadImportModeResolver>((method, args) => ImportMode.Copy),
                Proxy<ITrackedDownloadAlreadyImported>((method, args) => false),
                Logger, NoopDownloadClientFileSnapshotService.Instance);

            public TrackedDownloadService Tracker(RemoteBook remote) => new(
                Proxy<IParsingService>((method, args) => remote), new CacheManager(), History, Events,
                Downloads, Formats, Logger, NoopDownloadClientFileSnapshotService.Instance);

            public TrackedDownload Track(TrackedDownloadService tracker) => tracker.TrackDownload(
                new DownloadClientDefinition { Id = 4, Protocol = DownloadProtocol.Usenet }, Item.Clone());

            public List<HistoryResource> Visible(int? bookId = null) =>
                Controller.GetAuthorHistory(Author.Id, bookId, includeAuthor: true, includeBook: true);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void imported_history_persists_optional_discarded_conversion_note_in_normal_row(bool postgres, bool includeMessage)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                const string note = "Discarded retained conversion output '/library/.chaptarr-conversions/retained/work/Book.m4b' after manually importing the original files";
                var message = new TrackImportedEvent(
                    new LocalBook
                    {
                        Author = h.Author, Book = h.Book, Path = "/downloads/Book.epub",
                        Quality = new QualityModel(Quality.EPUB)
                    },
                    new BookFile { Id = 42, Path = "/library/Book.epub" },
                    new List<BookFile>(), true, h.Item)
                {
                    Message = includeMessage ? note : null
                };

                h.History.Handle(message);

                // Read the serialized data back through the real repository on each engine.
                var rows = h.HistoryRepository.FindByDownloadId(h.Item.DownloadId.ToUpperInvariant());
                Assert.That(rows, Has.Count.EqualTo(1), "The note belongs to the original import insert, not another History row");
                var imported = rows.Single();
                Assert.Multiple(() =>
                {
                    Assert.That(imported.EventType, Is.EqualTo(EntityHistoryEventType.BookFileImported));
                    Assert.That(imported.GetDataValue("ImportedPath"), Is.EqualTo("/library/Book.epub"));
                    Assert.That(EntityHistory.TryGetDataValue(imported.Data, "Message", out var persistedMessage), Is.EqualTo(includeMessage));
                    if (includeMessage) Assert.That(persistedMessage, Is.EqualTo(note));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void real_grab_and_blocked_import_remain_visible_without_duplicate_rows(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var remote = h.Remote(h.Book);
                h.Grab(remote);
                h.Results.Add(h.Result(h.Book));
                var tracked = new TrackedDownload { DownloadItem = h.Item, RemoteBook = remote, State = TrackedDownloadState.ImportPending };
                var completed = h.Completed();
                completed.Import(tracked);
                completed.Import(tracked);

                var bookHistory = h.Visible(h.Book.Id);
                Assert.That(bookHistory.Select(row => row.EventType), Is.EquivalentTo(new[]
                {
                    EntityHistoryEventType.Grabbed, EntityHistoryEventType.BookImportIncomplete
                }));
                Assert.That(h.Visible(), Has.Count.EqualTo(2));
                var grab = bookHistory.Single(row => row.EventType == EntityHistoryEventType.Grabbed);
                Assert.That(grab.DownloadId, Is.EqualTo(h.Item.DownloadId));
                Assert.That(grab.Quality.Quality, Is.EqualTo(Quality.EPUB));
                Assert.That(grab.Data["releaseSource"], Is.EqualTo("InteractiveSearch"));
                Assert.That(grab.Data["downloadClientName"], Is.EqualTo("Fixture SAB"));
                Assert.That(h.DownloadRepository.All(), Has.Count.EqualTo(2));

                remote.Books.Add(h.Other);
                completed.Import(tracked);
                Assert.That(h.Visible(h.Other.Id), Has.Count.EqualTo(1));
                Assert.That(h.Visible(h.Other.Id).Single().EventType, Is.EqualTo(EntityHistoryEventType.BookImportIncomplete));
                Assert.That(h.Visible(h.Book.Id), Has.Count.EqualTo(2));
                Assert.That(h.DownloadRepository.All(), Has.Count.EqualTo(2));
                var lookups = h.EntityLookups;
                h.Events.PublishEvent(new BookImportIncompleteEvent(tracked));
                Assert.That(h.EntityLookups - lookups, Is.EqualTo(1), "All target books share one DownloadId history read");
                Assert.That(h.HistoryRepository.All(), Has.Count.EqualTo(3));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void newly_resolved_import_failure_uses_matched_context_after_verification(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Results.Add(h.Result(h.Book));
                var tracked = new TrackedDownload { DownloadItem = h.Item, State = TrackedDownloadState.ImportPending };
                h.Completed().Import(tracked);

                Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                Assert.That(h.Visible(h.Book.Id), Has.Count.EqualTo(1));
                Assert.That(h.Visible(h.Book.Id).Single().EventType, Is.EqualTo(EntityHistoryEventType.BookImportIncomplete));
                Assert.That(h.Visible(), Has.Count.EqualTo(1));
                Assert.That(h.DownloadRepository.All().Single().BookId, Is.EqualTo(h.Book.Id));
                Assert.That(h.HistoryRepository.All(), Has.None.Matches<EntityHistory>(row => row.EventType == EntityHistoryEventType.Grabbed));
                Assert.That(h.ImportFailureNotifications, Has.Count.EqualTo(1));
                Assert.That(h.ImportFailureNotifications.Single().Message, Does.Contain(h.Item.Title));
                Assert.That(h.NotificationFactoryLookups, Is.EqualTo(1));
                Assert.That(h.NotificationSuccesses, Is.EqualTo(1));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void fresh_tracker_recovers_persisted_unknown_failure_once_with_original_date(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var date = new DateTime(2026, 10, 8, 10, 58, 7, DateTimeKind.Utc).AddTicks(9915192);
                var statuses = new[] { new TrackedDownloadStatusMessage("Stored title.epub", "Access to destination file is denied") };
                var old = h.DownloadRepository.Insert(new DownloadHistory
                {
                    EventType = DownloadHistoryEventType.DownloadImportIncomplete,
                    DownloadId = h.Item.DownloadId, Date = date, SourceTitle = h.Item.OutputPath.FullPath,
                    DownloadClientId = 4, Protocol = DownloadProtocol.Usenet,
                    Data = new Dictionary<string, string>
                    {
                        { "DownloadClient", "SABnzbd" }, { "DownloadClientName", "Fixture SAB" },
                        { "StatusMessages", statuses.ToJson() }
                    }
                });
                var originalData = h.DownloadRepository.Get(old.Id).Data.ToJson();
                date = h.DownloadRepository.Get(old.Id).Date;
                h.Book.Editions = new List<Edition>
                {
                    new EditionRepository(database, h.Events).Insert(new Edition
                    {
                        BookId = h.Book.Id, Title = "Currently selected edition", Monitored = true, ReadingFormatId = 3
                    })
                };
                var tracker = h.Tracker(h.Remote(h.Book, h.Audio));
                var tracked = h.Track(tracker);

                Assert.That(tracked, Is.Not.Null);
                Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                Assert.That(h.Visible(h.Book.Id), Has.Count.EqualTo(1));
                var visible = h.Visible(h.Book.Id).Single();
                Assert.That(visible.EventType, Is.EqualTo(EntityHistoryEventType.BookImportIncomplete));
                Assert.That(visible.Date, Is.EqualTo(date));
                Assert.That(h.HistoryRepository.All().Single().EditionId, Is.Zero);
                Assert.Multiple(() =>
                {
                    Assert.That(visible.SourceTitle, Is.EqualTo(h.Item.Title));
                    Assert.That(h.ImportFailureNotifications, Is.Empty, "History repair is not a new import failure");
                    Assert.That(h.NotificationFactoryLookups, Is.Zero);
                    Assert.That(h.NotificationSuccesses, Is.Zero);
                });
                Assert.That(visible.Data["statusMessages"], Is.EqualTo(statuses.ToJson()));
                Assert.That(h.Visible(h.Audio.Id), Is.Empty);
                Assert.That(h.Visible(), Has.Count.EqualTo(1));
                var repaired = h.DownloadRepository.Get(old.Id);
                Assert.That(repaired.BookId, Is.EqualTo(h.Book.Id));
                Assert.That(repaired.AuthorId, Is.EqualTo(h.Author.Id));
                Assert.That(repaired.Date, Is.EqualTo(date));
                Assert.That(repaired.SourceTitle, Is.EqualTo(old.SourceTitle));
                Assert.That(repaired.Data.ToJson(), Is.EqualTo(originalData));
                Assert.That(repaired.DownloadClientId, Is.EqualTo(4));

                var lookups = h.EntityLookups;
                var events = h.Events.IncompleteEvents;
                for (var i = 0; i < 10; i++)
                {
                    h.Track(tracker);
                }

                Assert.That(h.EntityLookups, Is.EqualTo(lookups), "Blocked polling must not add history recovery queries");
                Assert.That(h.Events.IncompleteEvents, Is.EqualTo(events));
                h.Track(h.Tracker(h.Remote(h.Book, h.Audio)));
                Assert.That(h.HistoryRepository.All(), Has.Count.EqualTo(1));
                Assert.That(h.DownloadRepository.All(), Has.Count.EqualTo(1));
                Assert.That(h.HistoryRepository.All().Single().Id, Is.EqualTo(visible.Id));

                var next = h.DownloadRepository.Insert(new DownloadHistory
                {
                    EventType = DownloadHistoryEventType.DownloadImportIncomplete,
                    DownloadId = h.Item.DownloadId, Date = date.AddMinutes(1), SourceTitle = old.SourceTitle,
                    Data = new Dictionary<string, string>(old.Data)
                });
                h.Track(h.Tracker(h.Remote(h.Book)));
                Assert.That(h.DownloadRepository.Get(next.Id).BookId, Is.EqualTo(h.Book.Id));
                Assert.That(h.HistoryRepository.All(), Has.Count.EqualTo(2), "Distinct persisted occurrences retain their own original dates");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void new_failure_notifies_once_while_restart_history_repair_stays_silent(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Results.Add(h.Result(null));
                var unknown = new TrackedDownload
                {
                    DownloadItem = h.Item, RemoteBook = h.Remote(), State = TrackedDownloadState.ImportPending
                };
                h.Completed().Import(unknown);
                Assert.That(unknown.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                Assert.That(h.ImportFailureNotifications, Has.Count.EqualTo(1));
                Assert.That(h.HistoryRepository.All(), Is.Empty);
                var original = h.DownloadRepository.All().Single();
                Assert.That(original.BookId, Is.Zero);

                var resolved = h.Remote(h.Book, h.Audio);
                var tracker = h.Tracker(resolved);
                Assert.That(h.Track(tracker).State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                for (var i = 0; i < 10; i++)
                {
                    h.Track(tracker);
                }

                h.Track(h.Tracker(resolved));
                Assert.Multiple(() =>
                {
                    Assert.That(h.ImportFailureNotifications, Has.Count.EqualTo(1));
                    Assert.That(h.NotificationFactoryLookups, Is.EqualTo(1));
                    Assert.That(h.NotificationSuccesses, Is.EqualTo(1));
                    var history = h.HistoryRepository.All().Single();
                    Assert.That(history.Date, Is.EqualTo(original.Date));
                    Assert.That(history.SourceTitle, Is.EqualTo(h.Item.Title));
                    Assert.That(history.BookId, Is.EqualTo(h.Book.Id));
                    Assert.That(h.DownloadRepository.All(), Has.Count.EqualTo(1));
                });

                h.Item.DownloadId = Guid.NewGuid().ToString();
                h.Results.Clear();
                h.Results.Add(h.Result(h.Book));
                var newFailure = new TrackedDownload
                {
                    DownloadItem = h.Item, RemoteBook = h.Remote(h.Book), State = TrackedDownloadState.ImportPending
                };
                h.Completed().Import(newFailure);
                Assert.That(newFailure.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                Assert.That(h.ImportFailureNotifications, Has.Count.EqualTo(2));
                Assert.That(h.NotificationFactoryLookups, Is.EqualTo(2));
                Assert.That(h.NotificationSuccesses, Is.EqualTo(2));
                Assert.That(h.HistoryRepository.All(), Has.Count.EqualTo(2));
                Assert.That(h.DownloadRepository.All(), Has.Count.EqualTo(2));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void partial_history_recovery_keeps_unknown_anchor_until_all_targets_are_saved(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var statuses = new[] { new TrackedDownloadStatusMessage("Stored title.epub", "Access denied") };
                var old = h.DownloadRepository.Insert(new DownloadHistory
                {
                    EventType = DownloadHistoryEventType.DownloadImportIncomplete,
                    DownloadId = h.Item.DownloadId, Date = DateTime.UtcNow.AddDays(-1),
                    SourceTitle = h.Item.OutputPath.FullPath, DownloadClientId = 4,
                    Data = new Dictionary<string, string> { { "StatusMessages", statuses.ToJson() } }
                });
                var date = h.DownloadRepository.Get(old.Id).Date;
                h.FailInsertForBook = h.Other.Id;
                var tracker = h.Tracker(h.Remote(h.Book, h.Other));
                Assert.That(h.Track(tracker).State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                Assert.That(h.Events.HistoryErrors, Has.Count.EqualTo(1));
                Assert.That(h.DownloadRepository.Get(old.Id).BookId, Is.Zero);
                Assert.That(h.HistoryRepository.All().Single().BookId, Is.EqualTo(h.Book.Id));
                h.Track(tracker);
                Assert.That(h.DownloadRepository.Get(old.Id).BookId, Is.Zero);
                h.HistoryRepository.Insert(new EntityHistory
                {
                    EventType = EntityHistoryEventType.BookFileRenamed, BookId = h.Book.Id,
                    AuthorId = h.Author.Id, Date = date.AddHours(1), Quality = new QualityModel(Quality.EPUB),
                    SourceTitle = h.Item.Title, DownloadId = h.Item.DownloadId
                });

                h.FailInsertForBook = null;
                Assert.That(h.Track(h.Tracker(h.Remote(h.Book, h.Other))), Is.Not.Null);
                Assert.That(h.DownloadRepository.Get(old.Id).BookId, Is.EqualTo(h.Book.Id));
                var recovered = h.HistoryRepository.All().Where(row => row.EventType == EntityHistoryEventType.BookImportIncomplete).ToList();
                Assert.That(recovered.Select(row => row.BookId), Is.EquivalentTo(new[] { h.Book.Id, h.Other.Id }));
                Assert.That(recovered.Select(row => row.Date), Has.All.EqualTo(date));
                Assert.That(h.HistoryRepository.All(), Has.Count.EqualTo(3));
                Assert.That(h.DownloadRepository.All(), Has.Count.EqualTo(1));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void persisted_known_author_prevents_recovery_under_a_different_author(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var storedAuthor = new AuthorRepository(database, h.Events, h.Logger).Insert(new Author
                {
                    Name = "Stored Author", CleanName = "storedauthor", SortName = "storedauthor",
                    SortNameLastFirst = "Author, Stored", TitleSlug = "stored-author",
                    Path = @"C:\Books\Stored Author".AsOsAgnostic(), Added = DateTime.UtcNow
                });
                var old = h.DownloadRepository.Insert(new DownloadHistory
                {
                    EventType = DownloadHistoryEventType.DownloadImportIncomplete,
                    DownloadId = h.Item.DownloadId, AuthorId = storedAuthor.Id,
                    Date = DateTime.UtcNow.AddDays(-1), SourceTitle = h.Item.OutputPath.FullPath,
                    Data = new Dictionary<string, string>
                    {
                        { "StatusMessages", new[] { new TrackedDownloadStatusMessage("Stored title.epub", "Access denied") }.ToJson() }
                    }
                });

                Assert.That(h.Track(h.Tracker(h.Remote(h.Book))).State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                Assert.That(h.HistoryRepository.All(), Is.Empty);
                Assert.That(h.Visible(), Is.Empty);
                Assert.That(h.Events.HistoryErrors, Is.Empty);
                Assert.That(h.DownloadRepository.Get(old.Id).BookId, Is.Zero);
                Assert.That(h.DownloadRepository.Get(old.Id).AuthorId, Is.EqualTo(storedAuthor.Id));
                Assert.That(h.DownloadRepository.All(), Has.Count.EqualTo(1));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void strict_wrong_import_target_stays_blocked_and_keeps_grabbed_target(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Book.AnyEditionOk = false;
                var remote = h.Remote(h.Book);
                h.Grab(remote);
                h.Results.Add(h.Result(h.Other, imported: true));
                var tracked = new TrackedDownload { DownloadItem = h.Item, RemoteBook = remote, State = TrackedDownloadState.ImportPending };
                h.Completed().Import(tracked);

                Assert.That(tracked.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
                Assert.That(tracked.RemoteBook.Books.Single().Id, Is.EqualTo(h.Book.Id));
                Assert.That(tracked.StatusMessages.SelectMany(status => status.Messages), Does.Contain("STRICT_TARGET_VERIFICATION_FAILED"));
                Assert.That(h.Visible(h.Book.Id), Has.Count.EqualTo(2));
                Assert.That(h.Visible(h.Other.Id), Is.Empty);
            });
        }
    }
}
