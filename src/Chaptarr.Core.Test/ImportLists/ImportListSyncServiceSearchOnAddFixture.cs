using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using Chaptarr.Core.Test.Books;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.ImportLists.Goodreads;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    public class ImportListSyncServiceSearchOnAddFixture
    {
        private sealed class StubFetchAndParseImportList : IFetchAndParseImportList
        {
            public List<ImportListItemInfo> Items { get; set; } = new();

            public List<ImportListItemInfo> Fetch() => Items;

            public List<ImportListItemInfo> FetchSingleList(ImportListDefinition definition) => Items;
        }

        private sealed class StubImportListExclusionService : IImportListExclusionService
        {
            public List<ImportListExclusion> Exclusions { get; set; } = new();

            public ImportListExclusion Add(ImportListExclusion importListExclusion) => throw new NotImplementedException();
            public List<ImportListExclusion> All() => Exclusions;
            public void Delete(int id) => throw new NotImplementedException();
            public void Delete(List<int> ids) => throw new NotImplementedException();
            public void Delete(string foreignId) => throw new NotImplementedException();
            public ImportListExclusion Get(int id) => throw new NotImplementedException();
            public ImportListExclusion FindByForeignId(string foreignId) => throw new NotImplementedException();
            public List<ImportListExclusion> FindByForeignId(List<string> foreignIds) => throw new NotImplementedException();
            public ImportListExclusion Update(ImportListExclusion importListExclusion) => throw new NotImplementedException();
        }

        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event) where TEvent : class, NzbDrone.Common.Messaging.IEvent
            {
                // No-op for unit tests.
            }
        }

        private sealed class StubCommandQueueManager : IManageCommandQueue
        {
            public List<Command> Pushed { get; } = new();

            public List<CommandModel> PushMany<TCommand>(List<TCommand> commands) where TCommand : Command
            {
                foreach (var cmd in commands ?? new List<TCommand>())
                {
                    Pushed.Add(cmd);
                }

                return new List<CommandModel>();
            }

            public CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified) where TCommand : Command
            {
                Pushed.Add(command);
                return new CommandModel { Name = command?.Name };
            }

            public CommandModel Push(string commandName, DateTime? lastExecutionTime, DateTime? lastStartTime, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified) => throw new NotImplementedException();
            public IEnumerable<CommandModel> Queue(System.Threading.CancellationToken cancellationToken) => throw new NotImplementedException();
            public List<CommandModel> All() => throw new NotImplementedException();
            public CommandModel Get(int id) => throw new NotImplementedException();
            public List<CommandModel> GetStarted() => throw new NotImplementedException();
            public void SetMessage(CommandModel command, string message) => throw new NotImplementedException();
            public void TouchProgress(CommandModel command) => throw new NotImplementedException();
            public void SetResult(CommandModel command, CommandResult result) => throw new NotImplementedException();
            public void Start(CommandModel command) => throw new NotImplementedException();
            public void Complete(CommandModel command, string message) => throw new NotImplementedException();
            public void Fail(CommandModel command, string message, Exception e) => throw new NotImplementedException();
            public void Requeue() => throw new NotImplementedException();
            public void Cancel(int id) => throw new NotImplementedException();
            public void Pause(int id) => throw new NotImplementedException();
            public void Resume(int id) => throw new NotImplementedException();
            public void CleanCommands() => throw new NotImplementedException();
            public System.Threading.CancellationToken GetCancellationToken(int commandId) => throw new NotImplementedException();
            public void RegisterCancellationToken(int commandId, System.Threading.CancellationTokenSource cancellationTokenSource) => throw new NotImplementedException();
            public void UnregisterCancellationToken(int commandId) => throw new NotImplementedException();
        }

        private sealed class StubAuthorLibraryService : IAuthorLibraryService
        {
            public Task<Author> AddAuthorAsync(string providerId, MonitoringConfig config = null) => throw new NotImplementedException();
            public Task<Author> AddAuthorMonitoringBookAsync(string authorProviderId, string bookProviderId) => throw new NotImplementedException();
            public Task<List<Author>> AddAuthorsMonitoringSeriesAsync(string[] authorProviderIds, string seriesProviderId) => throw new NotImplementedException();
            public Task<Author> RefreshAuthorAsync(int authorId) => throw new NotImplementedException();
            public Task RemoveAuthorAsync(int authorId) => throw new NotImplementedException();
        }

        private sealed class RecordingPendingAuthorImportService : IPendingAuthorImportService
        {
            public List<(string ProviderId, MonitoringConfig Config, string SourceApplication)> Enqueued { get; } = new();

            public Task<int> EnqueueAsync(string providerId, MonitoringConfig config, string sourceApplication)
            {
                Enqueued.Add((providerId, config, sourceApplication));
                return Task.FromResult(Enqueued.Count);
            }

            public List<PendingAuthorImport> GetAll() => new();
            public List<PendingAuthorImport> GetDueForProcessing(int limit = 10) => throw new NotImplementedException();
            public PendingAuthorImport GetByProviderId(string providerId) => throw new NotImplementedException();
            public void UpdateStatus(PendingAuthorImport item, PendingImportStatus status, string error) => throw new NotImplementedException();
            public void ScheduleRetry(PendingAuthorImport item, string error) => throw new NotImplementedException();
            public void Cancel(int id) => throw new NotImplementedException();
            public void RetryNow(int id) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public void CleanupOldCompleted() { }
        }

        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class ImportListFactoryProxy : DispatchProxy
        {
            public ImportListDefinition Definition { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IImportListFactory.Get) &&
                    args?.Length == 1 &&
                    args[0] is int id &&
                    Definition != null &&
                    id == Definition.Id)
                {
                    return Definition;
                }

                if (targetMethod?.Name == nameof(IImportListFactory.GetInstance))
                {
                    return null;
                }

                if (targetMethod?.Name == nameof(IImportListFactory.AutomaticAddEnabled))
                {
                    return new List<IImportList>();
                }

                throw new NotImplementedException($"Test proxy does not implement IImportListFactory.{targetMethod?.Name}");
            }
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public Author Author { get; set; }
            public string AliasProviderId { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.GetAllAuthors))
                {
                    return Author != null ? new List<Author> { Author } : new List<Author>();
                }

                if (targetMethod?.Name == nameof(IAuthorService.FindByProviderId) &&
                    args?.Length == 2)
                {
                    var provider = args[0] as string;
                    var providerId = args[1] as string;

                    if (Author != null &&
                        ((string.Equals(provider, "gr", StringComparison.OrdinalIgnoreCase) &&
                          (string.Equals(providerId, "gr:562254", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(providerId, "562254", StringComparison.OrdinalIgnoreCase))) ||
                         (string.Equals(provider, "hc", StringComparison.OrdinalIgnoreCase) &&
                          (string.Equals(providerId, "hc:562254", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(providerId, "562254", StringComparison.OrdinalIgnoreCase))) ||
                         AliasMatches(providerId)))
                    {
                        return Author;
                    }

                    return null;
                }

                if (targetMethod?.Name == nameof(IAuthorService.UpdateAuthor) &&
                    args?.Length == 1 &&
                    args[0] is Author updated)
                {
                    Author = updated;
                    return updated;
                }

                throw new NotImplementedException($"Test proxy does not implement IAuthorService.{targetMethod?.Name}");
            }

            private bool AliasMatches(string providerId)
            {
                if (string.IsNullOrWhiteSpace(AliasProviderId) || string.IsNullOrWhiteSpace(providerId))
                {
                    return false;
                }

                var alias = AliasProviderId.Trim();
                var rawAlias = alias.Contains(":") ? alias.Substring(alias.IndexOf(':') + 1) : alias;
                return string.Equals(providerId, alias, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(providerId, rawAlias, StringComparison.OrdinalIgnoreCase);
            }
        }

        private class RootFolderServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IRootFolderService.GetBestRootFolder) &&
                    args?.Length >= 1 &&
                    args[0] is string path)
                {
                    return new RootFolder { Id = 1, Path = path };
                }

                throw new NotImplementedException($"Test proxy does not implement IRootFolderService.{targetMethod?.Name}");
            }
        }

        private class RootFolderSettingsResolverProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IRootFolderSettingsResolver.ResolveSettings))
                {
                    return new ResolvedRootFolderSettings
                    {
                        QualityProfileId = 1,
                        MetadataProfileId = 1,
                        MonitorExisting = 2,
                        MonitorFuture = false,
                        IsConfigured = true,
                        Source = "Test"
                    };
                }

                throw new NotImplementedException($"Test proxy does not implement IRootFolderSettingsResolver.{targetMethod?.Name}");
            }
        }

        private class BookServiceProxy : DispatchProxy
        {
            public List<Book> Books { get; set; } = new();
            public string AliasProviderId { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetAllBooks) && (args == null || args.Length == 0))
                {
                    return Books;
                }

                if (targetMethod?.Name == nameof(IBookService.GetBooksByAuthor) &&
                    args?.Length == 1 &&
                    args[0] is int authorId)
                {
                    return Books.Where(b => b.AuthorId == authorId).ToList();
                }

                if (targetMethod?.Name == nameof(IBookService.FindByProviderId) &&
                    args?.Length >= 2)
                {
                    var provider = args[0] as string;
                    var providerId = args[1] as string;
                    var mediaType = args.Length >= 3 && args[2] is BookMediaType mt ? mt : (BookMediaType?)null;
                    return FindBookByProviderId(provider, providerId, mediaType);
                }

                if (targetMethod?.Name == nameof(IBookService.UpdateMany) &&
                    args?.Length == 1 &&
                    args[0] is List<Book> update)
                {
                    foreach (var b in update)
                    {
                        var existing = Books.FirstOrDefault(x => x.Id == b.Id);
                        if (existing != null)
                        {
                            existing.AudiobookMonitored = b.AudiobookMonitored;
                            existing.EbookMonitored = b.EbookMonitored;
                        }
                    }
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement IBookService.{targetMethod?.Name}");
            }

            private Book FindBookByProviderId(string provider, string providerId, BookMediaType? mediaType)
            {
                return Books.FirstOrDefault(book =>
                    book != null &&
                    (!mediaType.HasValue || book.MediaType == mediaType.Value) &&
                    (MatchesBookProvider(book, provider, providerId) || AliasMatches(providerId)));
            }

            private static bool MatchesBookProvider(Book book, string provider, string providerId)
            {
                if (book == null || string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(providerId))
                {
                    return false;
                }

                var candidate = provider.Trim().ToLowerInvariant() switch
                {
                    "hc" => book.HardcoverBookId,
                    "gr" => book.GoodreadsWorkId,
                    "ol" => book.OpenLibraryWorkId,
                    _ => null
                };

                return ProviderIdMatches(candidate, providerId);
            }

            private bool AliasMatches(string providerId)
            {
                return ProviderIdMatches(AliasProviderId, providerId);
            }

            private static bool ProviderIdMatches(string candidate, string providerId)
            {
                if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(providerId))
                {
                    return false;
                }

                var normalizedCandidate = candidate.Trim();
                var rawCandidate = normalizedCandidate.Contains(":")
                    ? normalizedCandidate.Substring(normalizedCandidate.IndexOf(':') + 1)
                    : normalizedCandidate;
                var normalizedProviderId = providerId.Trim();
                var rawProviderId = normalizedProviderId.Contains(":")
                    ? normalizedProviderId.Substring(normalizedProviderId.IndexOf(':') + 1)
                    : normalizedProviderId;

                return string.Equals(normalizedProviderId, normalizedCandidate, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(normalizedProviderId, rawCandidate, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(rawProviderId, normalizedCandidate, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(rawProviderId, rawCandidate, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Test]
        public void should_apply_book_exclusion_when_list_uses_provider_alias()
        {
            var definition = new ImportListDefinition
            {
                Id = 10,
                Name = "Goodreads Bookshelves",
                EnableAutomaticAdd = true,
                ShouldMonitor = ImportListMonitorType.SpecificBook,
                ShouldMonitorExisting = true,
                ShouldSearch = true,
                Settings = new GoodreadsBookshelfImportListSettings
                {
                    UserId = "12345678",
                    BookshelfIds = new[] { "to-read" },
                    MonitorAudiobooks = true,
                    MonitorEbooks = false,
                    AudiobookQualityProfileId = 1,
                    EbookQualityProfileId = 1,
                    AudiobookMetadataProfileId = 1,
                    EbookMetadataProfileId = 1,
                    AudiobookRootFolderPath = "/audiobooks",
                    EbookRootFolderPath = "/ebooks"
                }
            };

            var author = new Author
            {
                Id = 1,
                Name = "Alias Author",
                GoodreadsAuthorId = "gr:562254",
                Monitored = false
            };

            var book = new Book
            {
                Id = 101,
                AuthorId = author.Id,
                Author = author,
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:123456",
                Title = "Alias Book",
                AudiobookMonitored = false,
                EbookMonitored = false
            };

            var fetcher = new StubFetchAndParseImportList
            {
                Items = new List<ImportListItemInfo>
                {
                    new()
                    {
                        ImportListId = definition.Id,
                        ImportList = definition.Name,
                        Author = author.Name,
                        AuthorGoodreadsId = author.GoodreadsAuthorId,
                        Book = book.Title,
                        BookGoodreadsId = "gr:999999",
                        EditionGoodreadsId = "gr-ed:777777",
                        ReleaseDate = DateTime.UtcNow.Date
                    }
                }
            };

            var importListFactoryProxy = DispatchProxy.Create<IImportListFactory, ImportListFactoryProxy>();
            ((ImportListFactoryProxy)(object)importListFactoryProxy).Definition = definition;

            var authorServiceProxy = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorServiceProxy).Author = author;

            var bookServiceProxy = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookServiceProxy).Books = new List<Book> { book };
            ((BookServiceProxy)(object)bookServiceProxy).AliasProviderId = "gr:999999";

            var pendingImportService = new RecordingPendingAuthorImportService();
            var commandQueue = new StubCommandQueueManager();

            var service = new ImportListSyncService(
                importListFactory: importListFactoryProxy,
                importListExclusionService: new StubImportListExclusionService
                {
                    Exclusions = new List<ImportListExclusion>
                    {
                        new() { ForeignId = "gr:123456", Name = book.Title }
                    }
                },
                listFetcherAndParser: fetcher,
                bookInfoProxy: DispatchProxy.Create<NzbDrone.Core.MetadataSource.IProvideBookInfo, ThrowingProxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>>(),
                authorService: authorServiceProxy,
                bookService: bookServiceProxy,
                bookRepository: DispatchProxy.Create<IBookRepository, ThrowingProxy<IBookRepository>>(),
                editionService: DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                editionSelector: new EditionSelector(LogManager.GetCurrentClassLogger()),
                authorLibraryService: new StubAuthorLibraryService(),
                pendingAuthorImportService: pendingImportService,
                rootFolderService: DispatchProxy.Create<IRootFolderService, ThrowingProxy<IRootFolderService>>(),
                rootFolderSettingsResolver: DispatchProxy.Create<IRootFolderSettingsResolver, ThrowingProxy<IRootFolderSettingsResolver>>(),
                eventAggregator: new StubEventAggregator(),
                commandQueueManager: commandQueue,
                logger: LogManager.GetCurrentClassLogger(),
                bookIdentityCacheRepository: DispatchProxy.Create<IImportListBookIdentityCacheRepository, ThrowingProxy<IImportListBookIdentityCacheRepository>>());

            service.Execute(new ImportListSyncCommand(definition.Id));

            Assert.That(pendingImportService.Enqueued, Is.Empty);
            Assert.That(commandQueue.Pushed.OfType<BookSearchCommand>(), Is.Empty);
            Assert.That(commandQueue.Pushed.OfType<ProcessPendingImportsCommand>(), Is.Empty);
            Assert.That(author.Monitored, Is.False);
            Assert.That(book.AudiobookMonitored, Is.False);
        }

        [Test]
        public void should_apply_author_exclusion_when_list_uses_provider_alias()
        {
            var definition = new ImportListDefinition
            {
                Id = 9,
                Name = "Goodreads Authors",
                EnableAutomaticAdd = true,
                ShouldMonitor = ImportListMonitorType.EntireAuthor,
                ShouldMonitorExisting = true,
                ShouldSearch = false,
                Settings = new GoodreadsBookshelfImportListSettings
                {
                    UserId = "12345678",
                    BookshelfIds = new[] { "to-read" },
                    AudiobookQualityProfileId = 1,
                    EbookQualityProfileId = 1,
                    AudiobookMetadataProfileId = 1,
                    EbookMetadataProfileId = 1,
                    AudiobookRootFolderPath = "/audiobooks",
                    EbookRootFolderPath = "/ebooks"
                }
            };

            var author = new Author
            {
                Id = 1,
                Name = "Nate Bargatze",
                GoodreadsAuthorId = "gr:562254",
                Monitored = false
            };

            var fetcher = new StubFetchAndParseImportList
            {
                Items = new List<ImportListItemInfo>
                {
                    new()
                    {
                        ImportListId = definition.Id,
                        ImportList = definition.Name,
                        Author = author.Name,
                        AuthorProviderId = "gr:999999"
                    }
                }
            };

            var importListFactoryProxy = DispatchProxy.Create<IImportListFactory, ImportListFactoryProxy>();
            ((ImportListFactoryProxy)(object)importListFactoryProxy).Definition = definition;

            var authorServiceProxy = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorServiceProxy).Author = author;
            ((AuthorServiceProxy)(object)authorServiceProxy).AliasProviderId = "gr:999999";

            var pendingImportService = new RecordingPendingAuthorImportService();
            var commandQueue = new StubCommandQueueManager();

            var service = new ImportListSyncService(
                importListFactory: importListFactoryProxy,
                importListExclusionService: new StubImportListExclusionService
                {
                    Exclusions = new List<ImportListExclusion>
                    {
                        new() { ForeignId = "gr:562254", Name = author.Name }
                    }
                },
                listFetcherAndParser: fetcher,
                bookInfoProxy: DispatchProxy.Create<NzbDrone.Core.MetadataSource.IProvideBookInfo, ThrowingProxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>>(),
                authorService: authorServiceProxy,
                bookService: DispatchProxy.Create<IBookService, BookServiceProxy>(),
                bookRepository: DispatchProxy.Create<IBookRepository, ThrowingProxy<IBookRepository>>(),
                editionService: DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                editionSelector: new EditionSelector(LogManager.GetCurrentClassLogger()),
                authorLibraryService: new StubAuthorLibraryService(),
                pendingAuthorImportService: pendingImportService,
                rootFolderService: DispatchProxy.Create<IRootFolderService, ThrowingProxy<IRootFolderService>>(),
                rootFolderSettingsResolver: DispatchProxy.Create<IRootFolderSettingsResolver, ThrowingProxy<IRootFolderSettingsResolver>>(),
                eventAggregator: new StubEventAggregator(),
                commandQueueManager: commandQueue,
                logger: LogManager.GetCurrentClassLogger(),
                bookIdentityCacheRepository: DispatchProxy.Create<IImportListBookIdentityCacheRepository, ThrowingProxy<IImportListBookIdentityCacheRepository>>());

            service.Execute(new ImportListSyncCommand(definition.Id));

            Assert.That(pendingImportService.Enqueued, Is.Empty);
            Assert.That(commandQueue.Pushed.OfType<ProcessPendingImportsCommand>(), Is.Empty);
            Assert.That(author.Monitored, Is.False);
        }

        [Test]
        public void should_queue_book_search_when_import_list_monitors_existing_book_and_should_search_enabled()
        {
            var definition = new ImportListDefinition
            {
                Id = 1,
                Name = "Goodreads Bookshelves",
                EnableAutomaticAdd = true,
                ShouldMonitor = ImportListMonitorType.SpecificBook,
                ShouldMonitorExisting = true,
                ShouldSearch = true,
                Settings = new GoodreadsBookshelfImportListSettings
                {
                    UserId = "12345678",
                    BookshelfIds = new[] { "to-read" },
                    MonitorAudiobooks = true,
                    MonitorEbooks = false,
                    AudiobookQualityProfileId = 1,
                    EbookQualityProfileId = 1,
                    AudiobookMetadataProfileId = 1,
                    EbookMetadataProfileId = 1,
                    AudiobookRootFolderPath = "/audiobooks",
                    EbookRootFolderPath = "/ebooks"
                }
            };

            var author = new Author
            {
                Id = 1,
                Name = "Nate Bargatze",
                GoodreadsAuthorId = "562254",
                Monitored = false
            };

            var existingBookId = 100;

            var books = new List<Book>
            {
                new()
                {
                    Id = existingBookId,
                    AuthorId = author.Id,
                    Author = author,
                    MediaType = BookMediaType.Audiobook,
                    GoodreadsWorkId = "123",
                    Title = "Big Dumb Eyes",
                    AudiobookMonitored = false,
                    EbookMonitored = false
                }
            };

            var fetcher = new StubFetchAndParseImportList
            {
                Items = new List<ImportListItemInfo>
                {
                    new()
                    {
                        ImportListId = definition.Id,
                        ImportList = definition.Name,
                        Author = author.Name,
                        AuthorGoodreadsId = author.GoodreadsAuthorId,
                        Book = "Big Dumb Eyes",
                        BookGoodreadsId = "123",
                        EditionGoodreadsId = "456",
                        ReleaseDate = DateTime.UtcNow.Date
                    }
                }
            };

            var importListFactoryProxy = DispatchProxy.Create<IImportListFactory, ImportListFactoryProxy>();
            ((ImportListFactoryProxy)(object)importListFactoryProxy).Definition = definition;

            var authorServiceProxy = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorServiceProxy).Author = author;

            var bookServiceProxy = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookServiceProxy).Books = books;

            var commandQueue = new StubCommandQueueManager();

            var service = new ImportListSyncService(
                importListFactory: importListFactoryProxy,
                importListExclusionService: new StubImportListExclusionService(),
                listFetcherAndParser: fetcher,
                bookInfoProxy: DispatchProxy.Create<NzbDrone.Core.MetadataSource.IProvideBookInfo, ThrowingProxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>>(),
                authorService: authorServiceProxy,
                bookService: bookServiceProxy,
                bookRepository: DispatchProxy.Create<IBookRepository, ThrowingProxy<IBookRepository>>(),
                editionService: DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                editionSelector: new EditionSelector(LogManager.GetCurrentClassLogger()),
                authorLibraryService: new StubAuthorLibraryService(),
                pendingAuthorImportService: DispatchProxy.Create<IPendingAuthorImportService, ThrowingProxy<IPendingAuthorImportService>>(),
                rootFolderService: DispatchProxy.Create<IRootFolderService, ThrowingProxy<IRootFolderService>>(),
                rootFolderSettingsResolver: DispatchProxy.Create<IRootFolderSettingsResolver, ThrowingProxy<IRootFolderSettingsResolver>>(),
                eventAggregator: new StubEventAggregator(),
                commandQueueManager: commandQueue,
                logger: LogManager.GetCurrentClassLogger(),
                bookIdentityCacheRepository: DispatchProxy.Create<IImportListBookIdentityCacheRepository, ThrowingProxy<IImportListBookIdentityCacheRepository>>());

            service.Execute(new ImportListSyncCommand(definition.Id));

            var bookSearch = commandQueue.Pushed.OfType<BookSearchCommand>().ToList();
            Assert.That(bookSearch, Has.Count.EqualTo(1));
            Assert.That(bookSearch.Single().BookIds, Contains.Item(existingBookId));
        }

        [Test]
        public void should_queue_new_goodreads_specific_book_author_for_pending_drain()
        {
            var definition = new ImportListDefinition
            {
                Id = 3,
                Name = "Goodreads Bookshelves",
                EnableAutomaticAdd = true,
                ShouldMonitor = ImportListMonitorType.SpecificBook,
                ShouldMonitorExisting = true,
                ShouldSearch = true,
                Settings = new GoodreadsBookshelfImportListSettings
                {
                    UserId = "12345678",
                    BookshelfIds = new[] { "to-read" },
                    MonitorAudiobooks = true,
                    MonitorEbooks = false,
                    AudiobookQualityProfileId = 1,
                    EbookQualityProfileId = 1,
                    AudiobookMetadataProfileId = 1,
                    EbookMetadataProfileId = 1,
                    AudiobookRootFolderPath = "/audiobooks",
                    EbookRootFolderPath = "/ebooks"
                }
            };

            var fetcher = new StubFetchAndParseImportList
            {
                Items = new List<ImportListItemInfo>
                {
                    new()
                    {
                        ImportListId = definition.Id,
                        ImportList = definition.Name,
                        Author = "Nate Bargatze",
                        AuthorGoodreadsId = "562254",
                        Book = "Big Dumb Eyes",
                        BookGoodreadsId = "123",
                        EditionGoodreadsId = "456",
                        ReleaseDate = DateTime.UtcNow.Date
                    }
                }
            };

            var importListFactoryProxy = DispatchProxy.Create<IImportListFactory, ImportListFactoryProxy>();
            ((ImportListFactoryProxy)(object)importListFactoryProxy).Definition = definition;

            var authorServiceProxy = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var bookServiceProxy = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var commandQueue = new StubCommandQueueManager();
            var pendingImportService = new RecordingPendingAuthorImportService();

            var service = new ImportListSyncService(
                importListFactory: importListFactoryProxy,
                importListExclusionService: new StubImportListExclusionService(),
                listFetcherAndParser: fetcher,
                bookInfoProxy: DispatchProxy.Create<NzbDrone.Core.MetadataSource.IProvideBookInfo, ThrowingProxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>>(),
                authorService: authorServiceProxy,
                bookService: bookServiceProxy,
                bookRepository: DispatchProxy.Create<IBookRepository, ThrowingProxy<IBookRepository>>(),
                editionService: DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                editionSelector: new EditionSelector(LogManager.GetCurrentClassLogger()),
                authorLibraryService: new StubAuthorLibraryService(),
                pendingAuthorImportService: pendingImportService,
                rootFolderService: DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>(),
                rootFolderSettingsResolver: DispatchProxy.Create<IRootFolderSettingsResolver, RootFolderSettingsResolverProxy>(),
                eventAggregator: new StubEventAggregator(),
                commandQueueManager: commandQueue,
                logger: LogManager.GetCurrentClassLogger(),
                bookIdentityCacheRepository: DispatchProxy.Create<IImportListBookIdentityCacheRepository, ThrowingProxy<IImportListBookIdentityCacheRepository>>());

            service.Execute(new ImportListSyncCommand(definition.Id));

            Assert.That(pendingImportService.Enqueued, Has.Count.GreaterThanOrEqualTo(1));
            Assert.That(pendingImportService.Enqueued.Select(x => x.ProviderId), Is.All.EqualTo("gr:562254"));

            var finalConfig = pendingImportService.Enqueued.Last().Config;
            Assert.That(finalConfig.AudiobookBooksToMonitor, Is.EqualTo(new[] { "gr:123" }));
            Assert.That(finalConfig.EbookBooksToMonitor, Is.Empty);
            Assert.That(finalConfig.SearchForMissingBooks, Is.True);

            var pendingDrain = commandQueue.Pushed.OfType<ProcessPendingImportsCommand>().Single();
            Assert.That(pendingDrain.ContinueUntilEmpty, Is.True);
            Assert.That(pendingDrain.BatchSize, Is.EqualTo(10));
        }

        [Test]
        public void should_queue_book_search_for_existing_hardcover_book()
        {
            var definition = new ImportListDefinition
            {
                Id = 1,
                Name = "Hardcover Library",
                EnableAutomaticAdd = true,
                ShouldMonitor = ImportListMonitorType.SpecificBook,
                ShouldMonitorExisting = true,
                ShouldSearch = true,
                Settings = new GoodreadsBookshelfImportListSettings
                {
                    UserId = "12345678",
                    BookshelfIds = new[] { "to-read" },
                    MonitorAudiobooks = true,
                    MonitorEbooks = false,
                    AudiobookQualityProfileId = 1,
                    EbookQualityProfileId = 1,
                    AudiobookMetadataProfileId = 1,
                    EbookMetadataProfileId = 1,
                    AudiobookRootFolderPath = "/audiobooks",
                    EbookRootFolderPath = "/ebooks"
                }
            };

            var author = new Author
            {
                Id = 2,
                Name = "Hardcover Author",
                HardcoverAuthorId = "hc:562254",
                Monitored = false
            };

            var existingBookId = 200;

            var books = new List<Book>
            {
                new()
                {
                    Id = existingBookId,
                    AuthorId = author.Id,
                    Author = author,
                    MediaType = BookMediaType.Audiobook,
                    HardcoverBookId = "hc:123",
                    Title = "Hardcover Book",
                    AudiobookMonitored = false,
                    EbookMonitored = false
                }
            };

            var fetcher = new StubFetchAndParseImportList
            {
                Items = new List<ImportListItemInfo>
                {
                    new()
                    {
                        ImportListId = definition.Id,
                        ImportList = definition.Name,
                        Author = author.Name,
                        AuthorProviderId = author.HardcoverAuthorId,
                        Book = "Hardcover Book",
                        BookProviderId = "hc:123",
                        EditionProviderId = "hc-ed:456",
                        ReleaseDate = DateTime.UtcNow.Date
                    }
                }
            };

            var importListFactoryProxy = DispatchProxy.Create<IImportListFactory, ImportListFactoryProxy>();
            ((ImportListFactoryProxy)(object)importListFactoryProxy).Definition = definition;

            var authorServiceProxy = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorServiceProxy).Author = author;

            var bookServiceProxy = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookServiceProxy).Books = books;

            var commandQueue = new StubCommandQueueManager();

            var service = new ImportListSyncService(
                importListFactory: importListFactoryProxy,
                importListExclusionService: new StubImportListExclusionService(),
                listFetcherAndParser: fetcher,
                bookInfoProxy: DispatchProxy.Create<NzbDrone.Core.MetadataSource.IProvideBookInfo, ThrowingProxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>>(),
                authorService: authorServiceProxy,
                bookService: bookServiceProxy,
                bookRepository: DispatchProxy.Create<IBookRepository, ThrowingProxy<IBookRepository>>(),
                editionService: DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                editionSelector: new EditionSelector(LogManager.GetCurrentClassLogger()),
                authorLibraryService: new StubAuthorLibraryService(),
                pendingAuthorImportService: DispatchProxy.Create<IPendingAuthorImportService, ThrowingProxy<IPendingAuthorImportService>>(),
                rootFolderService: DispatchProxy.Create<IRootFolderService, ThrowingProxy<IRootFolderService>>(),
                rootFolderSettingsResolver: DispatchProxy.Create<IRootFolderSettingsResolver, ThrowingProxy<IRootFolderSettingsResolver>>(),
                eventAggregator: new StubEventAggregator(),
                commandQueueManager: commandQueue,
                logger: LogManager.GetCurrentClassLogger(),
                bookIdentityCacheRepository: DispatchProxy.Create<IImportListBookIdentityCacheRepository, ThrowingProxy<IImportListBookIdentityCacheRepository>>());

            service.Execute(new ImportListSyncCommand(definition.Id));

            var bookSearch = commandQueue.Pushed.OfType<BookSearchCommand>().ToList();
            Assert.That(bookSearch, Has.Count.EqualTo(1));
            Assert.That(bookSearch.Single().BookIds, Contains.Item(existingBookId));
        }
    }
}
