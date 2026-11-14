using System;
using NzbDrone.Core.MediaFiles;
using Chaptarr.Core.Test.Common;
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

            public List<ImportListItemInfo> FetchSingleList(IImportList importList) => Items;
        }

        private sealed class StubImportListExclusionService : IImportListExclusionService
        {
            public List<ImportListExclusion> Exclusions { get; set; } = new();

            public void ValidateAuthorExclusions(IEnumerable<Author> authors) => throw new NotImplementedException();
            public void AddAuthorExclusions(IEnumerable<Author> authors, BookMediaType? mediaType, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new System.NotSupportedException();
            public void AddAuthorExclusions(IEnumerable<Author> authors, BookMediaType? mediaType = null) => throw new NotImplementedException();
            public ImportListExclusion Add(ImportListExclusion importListExclusion) => throw new NotImplementedException();
            public List<ImportListExclusion> All() => Exclusions;
            public void Delete(int id) => throw new NotImplementedException();
            public void Delete(List<int> ids) => throw new NotImplementedException();
            public ImportListExclusion Get(int id) => throw new NotImplementedException();
            public List<ImportListExclusion> FindByForeignId(List<string> foreignIds, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new System.NotSupportedException();
            public List<ImportListExclusion> FindByForeignId(List<string> foreignIds) => Exclusions.Where(exclusion => foreignIds.Contains(exclusion.ForeignId)).ToList();
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
            public int? RejectSearchNumber { get; set; }
            public bool ReturnNullOnRejection { get; set; }
            private int _searches;

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
                if (command is BookSearchCommand && ++_searches == RejectSearchNumber)
                {
                    if (ReturnNullOnRejection)
                    {
                        return null;
                    }

                    throw new InvalidOperationException("Queue is full");
                }

                command.Trigger = trigger;
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
            public bool CancelIfQueued(int id) => throw new NotImplementedException();
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

            public PendingAuthorImport Get(int id) => GetAll().Find(item => item.Id == id);
            public NzbDrone.Core.Datastore.PagingSpec<PendingAuthorImport> GetPaged(NzbDrone.Core.Datastore.PagingSpec<PendingAuthorImport> pagingSpec) => throw new NotImplementedException();
            public List<PendingAuthorImport> GetAll() => new();
            public List<PendingAuthorImport> GetDueForProcessing(int limit = 10) => throw new NotImplementedException();
            public PendingAuthorImport GetByProviderId(string providerId) => throw new NotImplementedException();
            public bool UpdateStatus(PendingAuthorImport item, PendingImportStatus status, string error, long? expectedVersion = null) => throw new NotImplementedException();
            public bool SaveProgress(PendingAuthorImport item, PendingAuthorImport previous) => throw new NotImplementedException();
            public void ScheduleRetry(PendingAuthorImport item, string error) => throw new NotImplementedException();
            public bool IsActiveRequest(int id, long version) => true;
            public void Cancel(int id) => throw new NotImplementedException();
            public bool CancelIfQueued(int id) => throw new NotImplementedException();
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
                if (targetMethod?.Name == nameof(IImportListFactory.GetInstance))
                {
                    return InterfaceProxy.Proxy<IImportList>((method, _) => method.Name == "get_Definition"
                        ? Definition : throw new NotImplementedException(method.Name));
                }

                if (targetMethod?.Name == nameof(IImportListFactory.Get) &&
                    args?.Length == 1 &&
                    args[0] is int id &&
                    Definition != null &&
                    id == Definition.Id)
                {
                    return Definition;
                }

                throw new NotImplementedException($"Test proxy does not implement IImportListFactory.{targetMethod?.Name}");
            }
        }

        private class WorkInfoProxy : DispatchProxy
        {
            public Book Book { get; set; }
            public Author Author { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == "GetWorkInfo")
                {
                    return Tuple.Create(Author.GoodreadsAuthorId, Book, new List<Author> { Author });
                }

                throw new NotImplementedException($"Test proxy does not implement IProvideBookInfo.{targetMethod?.Name}");
            }
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public Author Author { get; set; }
            public string AliasProviderId { get; set; }
            public int ProviderLookupCount { get; private set; }
            public List<Author> AdditionalAuthors { get; } = new();
            public List<(int AuthorId, string MediaType, bool Monitored)> MonitoringWrites { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.GetAllAuthors))
                {
                    return (Author != null ? new List<Author> { Author } : new List<Author>()).Concat(AdditionalAuthors).ToList();
                }

                if (targetMethod?.Name == nameof(IAuthorService.FindByProviderId) &&
                    args?.Length == 2)
                {
                    ProviderLookupCount++;
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

                if (targetMethod?.Name == nameof(IAuthorService.SetMediaTypeMonitoring))
                {
                    MonitoringWrites.Add(((int)args[0], (string)args[1], (bool)args[2]));
                    return null;
                }

                if (targetMethod?.Name == nameof(IAuthorService.UpdateAuthor))
                {
                    throw new AssertionException("Import lists must not save the sync-start author snapshot");
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
            public int LookupCount { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IRootFolderService.GetBestRootFolder) &&
                    args?.Length >= 1 &&
                    args[0] is string path)
                {
                    LookupCount++;
                    return new RootFolder { Id = 1, Path = path };
                }

                throw new NotImplementedException($"Test proxy does not implement IRootFolderService.{targetMethod?.Name}");
            }
        }

        private class RootFolderSettingsResolverProxy : DispatchProxy
        {
            public bool? Monitored { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IRootFolderSettingsResolver.ResolveSettings))
                {
                    return new ResolvedRootFolderSettings
                    {
                        QualityProfileId = 1,
                        MetadataProfileId = 1,
                        Monitored = Monitored,
                        MonitorExistingBooks = false,
                        MonitorNewItems = NzbDrone.Core.Books.NewItemMonitorTypes.None,
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
            public int UpdateCount { get; private set; }
            public int CatalogReadCount { get; private set; }
            public int BatchLookupCount { get; private set; }
            public int ProviderLookupCount { get; private set; }
            public Func<List<Edition>, List<Book>, Dictionary<int, Book>> WantedEditions { get; set; }
            public bool FailWantedRequest { get; set; }
            public bool IncompleteBatch { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.FindBookIdsByProviderIds))
                {
                    BatchLookupCount++;
                    if (IncompleteBatch)
                    {
                        var requested = ((IEnumerable<(string Scope, string Provider, string Id)>)args[0]).Distinct().ToList();
                        foreach (var key in requested)
                        {
                            ((Action<(string Scope, string Provider, string Id)>)args[1])?.Invoke(key);
                        }
                        return requested.ToDictionary(key => key, _ => new List<int>());
                    }
                    return ((IEnumerable<(string Scope, string Provider, string Id)>)args[0]).Distinct().ToDictionary(key => key,
                        key => Books.Where(book => key.Scope == "work" ? MatchesBookProvider(book, key.Provider, key.Id) || AliasMatches(key.Id) :
                            book.Editions?.Any(edition => BookEditionIdentity.EditionMatchesProviderId(edition, key.Id)) == true)
                            .Select(book => book.Id).ToList());
                }

                if (targetMethod?.Name == nameof(IBookService.AddWantedEditions))
                {
                    if (FailWantedRequest)
                    {
                        Assert.That(args[1], Is.Not.Null);
                        ((Action<Edition, Exception>)args[1])(((List<Edition>)args[0])[0], new InvalidOperationException("Narration unavailable"));
                        return new Dictionary<int, Book>();
                    }

                    return WantedEditions((List<Edition>)args[0], Books);
                }

                if (targetMethod?.Name == nameof(IBookService.GetImportListBookIdentities))
                {
                    return Books;
                }

                if (targetMethod?.Name == nameof(IBookService.GetBooksByAuthor) &&
                    args?.Length == 2 &&
                    args[0] is int authorId)
                {
                    CatalogReadCount++;
                    return Books.Where(b => b.AuthorId == authorId).ToList();
                }

                if (targetMethod?.Name == nameof(IBookService.FindByProviderId) &&
                    args?.Length >= 2)
                {
                    ProviderLookupCount++;
                    var provider = args[0] as string;
                    var providerId = args[1] as string;
                    var mediaType = args.Length >= 3 && args[2] is BookMediaType mt ? mt : (BookMediaType?)null;
                    return FindBookByProviderId(provider, providerId, mediaType);
                }

                if (targetMethod?.Name == nameof(IBookService.SetAddOptions))
                {
                    return null;
                }

                if (targetMethod?.Name == nameof(IBookService.GetExistingBooks))
                {
                    var ids = ((IEnumerable<int>)args[0]).ToHashSet();
                    return Books.Where(book => ids.Contains(book.Id)).ToList();
                }

                if (targetMethod?.Name == nameof(IBookService.GetBookIds))
                {
                    return Books.Where(book => book.HasFiles).Select(book => book.Id).ToList();
                }

                if (targetMethod?.Name == nameof(IBookService.MonitorRequestedBooks))
                {
                    UpdateCount++;
                    var ids = ((IEnumerable<int>)args[0]).ToHashSet();
                    foreach (var book in Books.Where(book => ids.Contains(book.Id)))
                    {
                        book.SetMonitoredForMediaType((string)args[1], true);
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

        private sealed class FailOnAuthorLoad : NzbDrone.Core.Datastore.LazyLoaded<Author>
        {
            public override void LazyLoad() => throw new AssertionException("Import-list mapping must reuse the loaded author index");
        }

        private sealed class FailOnEditionLoad : NzbDrone.Core.Datastore.LazyLoaded<List<Edition>>
        {
            public override void LazyLoad() => throw new AssertionException("Import-list indexing must load editions in a batch");
        }

        private class BatchEditionServiceProxy : DispatchProxy
        {
            public List<Edition> Editions { get; set; } = new();
            public int RowOnlyLookupCount { get; private set; }
            public int HydratedBatchLookupCount { get; private set; }
            public HashSet<int> RequestedBookIds { get; private set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IEditionService.GetEditionRowsByBook) &&
                    args?[0] is IEnumerable<int> bookIds)
                {
                    RowOnlyLookupCount++;
                    var ids = bookIds.ToHashSet();
                    RequestedBookIds.UnionWith(ids);
                    return Editions.Where(edition => ids.Contains(edition.BookId)).ToList();
                }

                if (targetMethod?.Name == nameof(IEditionService.GetEditionsByBook))
                {
                    HydratedBatchLookupCount++;
                    throw new AssertionException("Import-list indexing must use edition-only rows");
                }

                throw new NotImplementedException($"Test proxy does not implement IEditionService.{targetMethod?.Name}");
            }
        }

        private static NzbDrone.Core.MetadataSource.IProvideBookInfo AttestedMetadata(IEnumerable<ImportListItemInfo> reports)
        {
            var sources = reports.Select(report => new ImportListItemInfo
            {
                Author = report.Author, AuthorProviderId = ImportListProviderIdHelper.Normalize(report.AuthorProviderId, "gr"),
                Book = report.Book, BookProviderId = ImportListProviderIdHelper.Normalize(report.BookProviderId, "gr"),
                EditionProviderId = BookEditionIdentity.NormalizeRequestedEditionProviderId(ImportListProviderIdHelper.Normalize(report.EditionProviderId, "gr")),
                EditionHasNarration = report.EditionHasNarration, EditionNarratorNames = report.EditionNarratorNames?.ToList(),
                HardcoverReadingFormatId = report.HardcoverReadingFormatId
            }).ToList();
            return InterfaceProxy.Proxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>((method, args) =>
            {
                var editionSource = method.Name == "GetEditionInfo";
                Assert.That(method.Name, Is.AnyOf("GetWorkInfo", "GetEditionInfo"));
                var id = (string)args[0];
                var report = sources.First(item => editionSource
                    ? BookEditionIdentity.NormalizeRequestedEditionProviderId(item.EditionProviderId) == BookEditionIdentity.NormalizeRequestedEditionProviderId(id)
                    : ImportListProviderIdHelper.Normalize(item.BookProviderId, "gr") == id);
                var authorId = ImportListProviderIdHelper.Normalize(report.AuthorProviderId, "gr");
                var workId = ImportListProviderIdHelper.Normalize(report.BookProviderId, "gr");
                var author = new Author { Name = report.Author,
                    HardcoverAuthorId = authorId?.StartsWith("hc:") == true ? authorId : null,
                    GoodreadsAuthorId = authorId?.StartsWith("gr:") == true ? authorId : null };
                var book = new Book { Title = report.Book, Author = author, MediaType = (BookMediaType)args[1],
                    HardcoverBookId = workId?.StartsWith("hc:") == true ? workId : null,
                    GoodreadsWorkId = workId?.StartsWith("gr:") == true ? workId : null };
                if (editionSource)
                {
                    book.Editions = new List<Edition> { new() { ForeignEditionId = BookEditionIdentity.NormalizeRequestedEditionProviderId(id),
                        ReadingFormatId = report.EditionHasNarration == true ? 2 : report.HardcoverReadingFormatId ?? 1,
                        NarratorNames = report.EditionNarratorNames?.ToList() } };
                }
                return Tuple.Create(authorId, book, new List<Author> { author });
            });
        }

        private static ImportListSyncService BuildExistingAuthorService(
            ImportListDefinition definition,
            Author author,
            List<Book> books,
            List<ImportListItemInfo> items,
            StubCommandQueueManager commandQueue = null,
            IEditionService editionService = null,
            IBookService bookServiceOverride = null,
            IPendingAuthorImportService pendingImports = null,
            IAuthorService authorServiceOverride = null,
            List<ImportListExclusion> exclusions = null, NzbDrone.Core.MetadataSource.IProvideBookInfo bookInfoOverride = null,
            IImportListBookIdentityCacheRepository identityCache = null, IRootFolderService roots = null, Logger logger = null,
            IImportListFactory factoryOverride = null)
        {
            var importListFactory = factoryOverride ?? DispatchProxy.Create<IImportListFactory, ImportListFactoryProxy>();
            if (factoryOverride == null)
            {
                ((ImportListFactoryProxy)(object)importListFactory).Definition = definition;
            }

            var authorService = authorServiceOverride ?? DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Author = author;

            var bookService = bookServiceOverride ?? DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)(object)bookService).Books = books ?? new List<Book>();

            return new ImportListSyncService(
                importListFactory: importListFactory,
                importListExclusionService: new StubImportListExclusionService { Exclusions = exclusions ?? new List<ImportListExclusion>() },
                listFetcherAndParser: new StubFetchAndParseImportList { Items = items ?? new List<ImportListItemInfo>() },
                bookInfoProxy: bookInfoOverride ?? DispatchProxy.Create<NzbDrone.Core.MetadataSource.IProvideBookInfo, ThrowingProxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>>(),
                authorService: authorService,
                bookService: bookService,
                editionService: editionService ?? DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                pendingAuthorImportService: pendingImports ?? new RecordingPendingAuthorImportService(),
                rootFolderService: roots ?? DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>(),
                rootFolderSettingsResolver: DispatchProxy.Create<IRootFolderSettingsResolver, RootFolderSettingsResolverProxy>(),
                eventAggregator: new StubEventAggregator(),
                commandQueueManager: commandQueue ?? new StubCommandQueueManager(),
                logger: logger ?? LogManager.GetCurrentClassLogger(),
                bookIdentityCacheRepository: identityCache ?? DispatchProxy.Create<IImportListBookIdentityCacheRepository, ThrowingProxy<IImportListBookIdentityCacheRepository>>());
        }

        [Test]
        public void local_lookup_should_load_only_requested_editions_and_keep_all_work_identity_controls()
        {
            var books = Enumerable.Range(1, 25)
                .Select(index => new Book
                {
                    Id = index,
                    AuthorId = 1,
                    MediaType = BookMediaType.Ebook,
                    HardcoverBookId = $"hc:{index}",
                    LazyEditions = new FailOnEditionLoad()
                })
                .ToList();
            var editionService = DispatchProxy.Create<IEditionService, BatchEditionServiceProxy>();
            ((BatchEditionServiceProxy)(object)editionService).Editions = books
                .Select(book => new Edition { BookId = book.Id, Asin = $"B{book.Id:D9}" })
                .ToList();
            var service = BuildExistingAuthorService(null, null, books, new List<ImportListItemInfo>(),
                editionService: editionService);

            var buildLookup = typeof(ImportListSyncService).GetMethod("BuildImportListLocalLookup",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var lookup = buildLookup.Invoke(service, new object[] { new HashSet<int> { 1, 25 }, 0, null });
            var findBook = lookup.GetType().GetMethod("FindBook");

            Assert.Multiple(() =>
            {
                Assert.That(((BatchEditionServiceProxy)(object)editionService).RowOnlyLookupCount, Is.EqualTo(1));
                Assert.That(((BatchEditionServiceProxy)(object)editionService).HydratedBatchLookupCount, Is.Zero);
                Assert.That(((BatchEditionServiceProxy)(object)editionService).RequestedBookIds, Is.EquivalentTo(new[] { 1, 25 }));
                Assert.That(findBook.Invoke(lookup, new object[] { "az:B000000002" }), Is.Null);
                Assert.That(findBook.Invoke(lookup, new object[] { "hc:2" }), Is.SameAs(books[1]));
                Assert.That(findBook.Invoke(lookup, new object[] { "az:B000000001" }), Is.SameAs(books[0]));
                Assert.That(findBook.Invoke(lookup, new object[] { "az:B000000025" }), Is.SameAs(books[24]));
            });
        }

        [Test]
        public void should_not_enable_an_existing_author_when_the_list_monitor_choice_is_none()
        {
            var definition = new ImportListDefinition
            {
                Id = 20,
                Name = "Do not monitor",
                EnableAutomaticAdd = true,
                ShouldMonitor = ImportListMonitorType.None,
                ShouldMonitorExisting = true
            };
            var author = new Author
            {
                Id = 1,
                Name = "Existing Author",
                GoodreadsAuthorId = "gr:562254",
                AudiobookMonitored = false,
                EbookMonitored = false,
                Monitored = false
            };
            var books = new List<Book>
            {
                new()
                {
                    Id = 101,
                    AuthorId = author.Id,
                    Author = author,
                    MediaType = BookMediaType.Audiobook,
                    AudiobookMonitored = false,
                    EbookMonitored = false
                }
            };
            var service = BuildExistingAuthorService(
                definition,
                author,
                books,
                new List<ImportListItemInfo>
                {
                    new()
                    {
                        ImportListId = definition.Id,
                        ImportList = definition.Name,
                        Author = author.Name,
                        AuthorProviderId = author.GoodreadsAuthorId
                    }
                });

            service.Execute(new ImportListSyncCommand(definition.Id));

            Assert.Multiple(() =>
            {
                Assert.That(author.AudiobookMonitored, Is.False);
                Assert.That(author.EbookMonitored, Is.False);
                Assert.That(author.Monitored, Is.False);
                Assert.That(books.Single().AudiobookMonitored, Is.False);
            });
        }

        [Test]
        public void should_leave_existing_catalog_alone_when_list_contains_only_an_author()
        {
            var definition = new ImportListDefinition
            {
                Id = 21,
                Name = "Monitor the author",
                EnableAutomaticAdd = true,
                ShouldMonitor = ImportListMonitorType.EntireAuthor,
                ShouldMonitorExisting = true,
                Settings = new GoodreadsBookshelfImportListSettings
                {
                    MonitorAudiobooks = true,
                    MonitorEbooks = false
                }
            };
            var author = new Author
            {
                Id = 1,
                Name = "Existing Author",
                GoodreadsAuthorId = "gr:562254",
                AudiobookMonitored = false,
                EbookMonitored = false,
                Monitored = false
            };
            var books = new List<Book>
            {
                new()
                {
                    Id = 101,
                    AuthorId = author.Id,
                    Author = author,
                    MediaType = BookMediaType.Audiobook,
                    AudiobookMonitored = false,
                    EbookMonitored = false
                },
                new()
                {
                    Id = 102,
                    AuthorId = author.Id,
                    Author = author,
                    MediaType = BookMediaType.Ebook,
                    AudiobookMonitored = false,
                    EbookMonitored = false
                }
            };
            var service = BuildExistingAuthorService(
                definition,
                author,
                books,
                new List<ImportListItemInfo>
                {
                    new()
                    {
                        ImportListId = definition.Id,
                        ImportList = definition.Name,
                        Author = author.Name,
                        AuthorProviderId = author.GoodreadsAuthorId
                    }
                });

            service.Execute(new ImportListSyncCommand(definition.Id));

            Assert.Multiple(() =>
            {
                Assert.That(author.AudiobookMonitored, Is.False);
                Assert.That(author.EbookMonitored, Is.False);
                Assert.That(author.Monitored, Is.False);
                Assert.That(books.Single(book => book.MediaType == BookMediaType.Audiobook).AudiobookMonitored, Is.False);
                Assert.That(books.Single(book => book.MediaType == BookMediaType.Ebook).EbookMonitored, Is.False);
            });
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
                editionService: DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
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
                editionService: DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
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
        public void distinct_resolved_sources_should_do_no_provider_requests_or_cache_writes_on_an_unchanged_sync()
        {
            var (list, author, _, _) = ExistingListScenario();
            list.Settings = new GoodreadsBookshelfImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false };
            var local = new Book { Id = 101, AuthorId = author.Id, Author = author, GoodreadsWorkId = "gr:123", MediaType = BookMediaType.Audiobook };
            var reports = Enumerable.Range(0, 100).Select(index => new ImportListItemInfo { ImportListId = list.Id,
                AuthorProviderId = author.GoodreadsAuthorId, BookProviderId = "gr:123", EditionProviderId = "gr:" + (8000 + index), Book = "Listed Work" }).ToList();
            var calls = 0;
            var metadata = InterfaceProxy.Proxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>((method, args) =>
            {
                System.Threading.Interlocked.Increment(ref calls);
                var source = new Book { GoodreadsWorkId = "gr:123", Editions = new List<Edition> { new() { ForeignEditionId = (string)args[0], ReadingFormatId = 1 } } };
                return Tuple.Create(author.GoodreadsAuthorId, source, new List<Author> { author });
            });
            var cache = new System.Collections.Concurrent.ConcurrentDictionary<string, ImportListBookIdentityCache>();
            var writes = 0;
            var batchReads = 0;
            var identities = InterfaceProxy.Proxy<IImportListBookIdentityCacheRepository>((method, args) =>
            {
                if (method.Name == nameof(IImportListBookIdentityCacheRepository.FindBySourceProviderIds))
                {
                    batchReads++;
                    return ((IEnumerable<string>)args[0]).Where(cache.ContainsKey).Select(id => cache[id]).ToList();
                }
                if (method.Name == nameof(IImportListBookIdentityCacheRepository.UpsertBySourceProviderId))
                {
                    System.Threading.Interlocked.Increment(ref writes);
                    var mapping = (ImportListBookIdentityCache)args[0];
                    cache[mapping.SourceProviderId] = mapping;
                    return mapping;
                }
                throw new AssertionException(method.Name);
            });
            var pending = new RecordingPendingAuthorImportService();
            var service = BuildExistingAuthorService(list, author, new List<Book> { local }, reports,
                pendingImports: pending, bookInfoOverride: metadata, identityCache: identities);
            service.Execute(new ImportListSyncCommand(list.Id));
            Assert.That(calls, Is.EqualTo(100));
            Assert.That(writes, Is.EqualTo(100));
            reports.ForEach(report => report.EditionHasNarration = null);
            service.Execute(new ImportListSyncCommand(list.Id));
            Assert.That(calls, Is.EqualTo(100));
            Assert.That(writes, Is.EqualTo(100));
            Assert.That(batchReads, Is.EqualTo(2));
            Assert.That(pending.Enqueued, Is.Empty);
            TestContext.WriteLine("100 distinct resolved sources: first sync 100 metadata requests +100 upserts; unchanged sync 0 metadata requests +0 upserts +1 batch cache read");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_queue_book_search_when_import_list_makes_existing_book_eligible_and_should_search_enabled(bool bookWasAlreadyMonitored)
        {
            var definition = new ImportListDefinition
            {
                Id = 1,
                Name = "Goodreads Bookshelves",
                EnableAutomaticAdd = true,
                ShouldMonitor = ImportListMonitorType.SpecificBook,
                ShouldMonitorExisting = true,
                ShouldSearch = true,
                MonitoringOptions = new ImportListMonitoringOptions { ResumePausedAuthors = true },
                Settings = new GoodreadsBookshelfImportListSettings
                {
                    UserId = "12345678",
                    BookshelfIds = new[] { "to-read" },
                    MonitorAudiobooks = true,
                    MonitorEbooks = true,
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
                Monitored = false,
                AudiobookMonitored = false,
                EbookMonitored = false
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
                    AudiobookMonitored = bookWasAlreadyMonitored,
                    EbookMonitored = false,
                    Editions = new List<Edition> { new() { Id = 456, BookId = existingBookId, GoodreadsEditionId = 456, ReadingFormatId = 1 } }
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
                editionService: DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                pendingAuthorImportService: DispatchProxy.Create<IPendingAuthorImportService, ThrowingProxy<IPendingAuthorImportService>>(),
                rootFolderService: DispatchProxy.Create<IRootFolderService, ThrowingProxy<IRootFolderService>>(),
                rootFolderSettingsResolver: DispatchProxy.Create<IRootFolderSettingsResolver, ThrowingProxy<IRootFolderSettingsResolver>>(),
                eventAggregator: new StubEventAggregator(),
                commandQueueManager: commandQueue,
                logger: LogManager.GetCurrentClassLogger(),
                bookIdentityCacheRepository: DispatchProxy.Create<IImportListBookIdentityCacheRepository, ThrowingProxy<IImportListBookIdentityCacheRepository>>());

            service.Execute(new ImportListSyncCommand(definition.Id));

            var bookSearch = commandQueue.Pushed.OfType<BookSearchCommand>().ToList();
            Assert.That(bookSearch, Has.Count.EqualTo(bookWasAlreadyMonitored ? 0 : 1));
            if (!bookWasAlreadyMonitored)
            {
                Assert.That(bookSearch.Single().BookIds, Contains.Item(existingBookId));
                Assert.That(bookSearch.Single().Trigger, Is.Not.EqualTo(CommandTrigger.Manual));
            }

            Assert.That(author.AudiobookMonitored, Is.True);
            Assert.That(author.EbookMonitored, Is.False,
                "A specific audiobook request must not enable the list's other configured media side.");
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
                MonitoringOptions = new ImportListMonitoringOptions { AudiobookMonitored = true },
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
                bookInfoProxy: AttestedMetadata(fetcher.Items),
                authorService: authorServiceProxy,
                bookService: bookServiceProxy,
                editionService: DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
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
            Assert.That(finalConfig.AudiobookMonitored, Is.True);
            Assert.That(finalConfig.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.None));
            Assert.That(finalConfig.AudiobookBooksToMonitor, Is.EqualTo(new[] { "gr:123" }));
            Assert.That(finalConfig.EbookMonitored, Is.Null);
            Assert.That(finalConfig.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.None));
            Assert.That(finalConfig.CreateEbook, Is.False);
            Assert.That(finalConfig.EbookBooksToMonitor, Is.Null);
            Assert.That(finalConfig.SearchForMissingBooks, Is.True);
            Assert.That(finalConfig.AudiobookBooksToSearch, Is.Null);

            var pendingDrain = commandQueue.Pushed.OfType<ProcessPendingImportsCommand>().Single();
            Assert.That(pendingDrain.ContinueUntilEmpty, Is.True);
            Assert.That(pendingDrain.BatchSize, Is.EqualTo(10));
        }

        [Test]
        public void generic_list_full_selection_update_keeps_list_provenance_for_media_scoped_exclusion()
        {
            const string authorProviderId = "gr:58";
            const string bookProviderId = "gr:312460";
            const string importListName = "Generic Favorites";

            var definition = new ImportListDefinition
            {
                Id = 31,
                Name = importListName,
                EnableAutomaticAdd = true,
                ShouldMonitor = ImportListMonitorType.SpecificBook,
                ShouldMonitorExisting = true,
                Settings = new NzbDrone.Core.ImportLists.LazyLibrarianImport.LazyLibrarianImportSettings
                {
                    MonitorAudiobooks = true,
                    MonitorEbooks = true,
                    AudiobookRootFolderPath = @"C:\library".AsOsAgnostic(),
                    EbookRootFolderPath = @"C:\library".AsOsAgnostic(),
                    AudiobookQualityProfileId = 1,
                    EbookQualityProfileId = 1,
                    AudiobookMetadataProfileId = 1,
                    EbookMetadataProfileId = 1
                }
            };
            var author = new Author { Name = "Frank Herbert", GoodreadsAuthorId = authorProviderId };
            var book = new Book { Title = "Dune", GoodreadsWorkId = bookProviderId, Author = author };
            var report = new ImportListItemInfo
            {
                ImportListId = definition.Id,
                ImportList = definition.Name,
                Author = author.Name,
                Book = book.Title,
                BookProviderId = bookProviderId,
                ReleaseDate = DateTime.UtcNow.Date
            };

            var importListFactory = DispatchProxy.Create<IImportListFactory, ImportListFactoryProxy>();
            ((ImportListFactoryProxy)(object)importListFactory).Definition = definition;
            var pendingImportService = new RecordingPendingAuthorImportService();
            var bookInfoProxy = DispatchProxy.Create<NzbDrone.Core.MetadataSource.IProvideBookInfo, WorkInfoProxy>();
            ((WorkInfoProxy)(object)bookInfoProxy).Book = book;
            ((WorkInfoProxy)(object)bookInfoProxy).Author = author;
            var commandQueue = new StubCommandQueueManager();

            var service = new ImportListSyncService(
                importListFactory: importListFactory,
                importListExclusionService: new StubImportListExclusionService
                {
                    Exclusions = new List<ImportListExclusion>
                    {
                        new() { ForeignId = authorProviderId, Name = author.Name, MediaType = BookMediaType.Ebook }
                    }
                },
                listFetcherAndParser: new StubFetchAndParseImportList { Items = new List<ImportListItemInfo> { report } },
                bookInfoProxy: bookInfoProxy,
                authorService: DispatchProxy.Create<IAuthorService, AuthorServiceProxy>(),
                bookService: DispatchProxy.Create<IBookService, BookServiceProxy>(),
                editionService: DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                pendingAuthorImportService: pendingImportService,
                rootFolderService: DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>(),
                rootFolderSettingsResolver: DispatchProxy.Create<IRootFolderSettingsResolver, RootFolderSettingsResolverProxy>(),
                eventAggregator: new StubEventAggregator(),
                commandQueueManager: commandQueue,
                logger: LogManager.GetCurrentClassLogger(),
                bookIdentityCacheRepository: DispatchProxy.Create<IImportListBookIdentityCacheRepository, ThrowingProxy<IImportListBookIdentityCacheRepository>>());

            service.Execute(new ImportListSyncCommand(definition.Id));

            Assert.That(report.AuthorProviderId, Is.EqualTo(authorProviderId), "the missing author identity is supplied by the mapping pass before it is queued");
            Assert.That(pendingImportService.Enqueued, Has.Count.EqualTo(1), "the mapped author is queued with the complete book selection");
            Assert.That(pendingImportService.Enqueued.Select(x => x.ProviderId), Is.All.EqualTo(authorProviderId));

            var finalRequest = pendingImportService.Enqueued.Last();
            Assert.Multiple(() =>
            {
                Assert.That(finalRequest.Config.RequestedBy, Is.EqualTo($"ImportList:{importListName}"));
                Assert.That(finalRequest.SourceApplication, Is.EqualTo($"ImportList:{importListName}"));
                Assert.That(finalRequest.Config.CreateAudiobook, Is.True);
                Assert.That(finalRequest.Config.CreateEbook, Is.False);
                Assert.That(finalRequest.Config.AudiobookBooksToMonitor, Is.EqualTo(new[] { bookProviderId }));
                Assert.That(finalRequest.Config.EbookBooksToMonitor, Is.Empty, "an excluded side must not leave a book request waiting in pending completion");
            });
        }

        [Test]
        public void should_inherit_the_author_gate_from_the_root_independently_of_initial_book_monitoring()
        {
            var definition = new ImportListDefinition
            {
                Id = 4,
                Name = "Goodreads Bookshelves",
                EnableAutomaticAdd = true,
                ShouldMonitor = ImportListMonitorType.None,
                MonitorNewItems = NewItemMonitorTypes.All,
                MonitoringOptions = new ImportListMonitoringOptions
                {
                    AudiobookMonitor = MonitorTypes.None,
                    AudiobookMonitorNewItems = NewItemMonitorTypes.All
                },
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
                        AuthorGoodreadsId = "562254"
                    }
                }
            };

            var importListFactory = DispatchProxy.Create<IImportListFactory, ImportListFactoryProxy>();
            ((ImportListFactoryProxy)(object)importListFactory).Definition = definition;
            var pendingImportService = new RecordingPendingAuthorImportService();
            var rootFolderSettingsResolver = DispatchProxy.Create<IRootFolderSettingsResolver, RootFolderSettingsResolverProxy>();
            ((RootFolderSettingsResolverProxy)(object)rootFolderSettingsResolver).Monitored = true;

            var service = new ImportListSyncService(
                importListFactory: importListFactory,
                importListExclusionService: new StubImportListExclusionService(),
                listFetcherAndParser: fetcher,
                bookInfoProxy: DispatchProxy.Create<NzbDrone.Core.MetadataSource.IProvideBookInfo, ThrowingProxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>>(),
                authorService: DispatchProxy.Create<IAuthorService, AuthorServiceProxy>(),
                bookService: DispatchProxy.Create<IBookService, BookServiceProxy>(),
                editionService: DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                pendingAuthorImportService: pendingImportService,
                rootFolderService: DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>(),
                rootFolderSettingsResolver: rootFolderSettingsResolver,
                eventAggregator: new StubEventAggregator(),
                commandQueueManager: new StubCommandQueueManager(),
                logger: LogManager.GetCurrentClassLogger(),
                bookIdentityCacheRepository: DispatchProxy.Create<IImportListBookIdentityCacheRepository, ThrowingProxy<IImportListBookIdentityCacheRepository>>());

            service.Execute(new ImportListSyncCommand(definition.Id));

            var config = pendingImportService.Enqueued.Last().Config;
            Assert.Multiple(() =>
            {
                Assert.That(config.AudiobookMonitored, Is.True);
                Assert.That(config.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.None));
                Assert.That(config.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
                Assert.That(config.EbookMonitored, Is.Null);
            });
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
                    EbookMonitored = false,
                    Editions = new List<Edition> { new() { Id = 456, BookId = existingBookId, HardcoverEditionId = "hc:456", ReadingFormatId = 1 } }
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
                editionService: DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
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
        private static (ImportListDefinition list, Author author, List<Book> books, List<ImportListItemInfo> reports) ExistingListScenario()
        {
            var list = new ImportListDefinition
            {
                Id = 1,
                Name = "Selected ebooks",
                EnableAutomaticAdd = true,
                ShouldMonitor = ImportListMonitorType.SpecificBook,
                ShouldMonitorExisting = true,
                ShouldSearch = true,
                MonitoringOptions = new ImportListMonitoringOptions { AddMissingAuthors = false },
                Settings = new GoodreadsBookshelfImportListSettings { MonitorAudiobooks = false, MonitorEbooks = true }
            };
            var author = new Author { Id = 1, GoodreadsAuthorId = "gr:562254", Name = "Existing Author", AudiobookMonitored = false, EbookMonitored = false };
            var books = Enumerable.Range(1, 3).Select(id => new Book
            {
                Id = id, Author = author, AuthorId = author.Id, GoodreadsWorkId = $"gr:{id}",
                MediaType = BookMediaType.Ebook, EbookMonitored = false
            }).ToList();
            books.Add(new Book { Id = 4, Author = author, AuthorId = author.Id, GoodreadsWorkId = "gr:1", MediaType = BookMediaType.Audiobook, AudiobookMonitored = true });
            var reports = books.Take(2).Select(book => new ImportListItemInfo
            {
                ImportListId = list.Id, Author = author.Name, Book = $"Listed book {book.Id}", AuthorProviderId = author.GoodreadsAuthorId, BookProviderId = book.GoodreadsWorkId
            }).ToList();
            return (list, author, books, reports);
        }

        [TestCase(false, false, true)]
        [TestCase(true, false, true)]
        [TestCase(false, true, false)]
        public void listed_narrations_are_batched_before_generic_row_selection_and_search_the_returned_copy(bool competingSelected, bool competingPinned, bool selectedPinned)
        {
            var (list, author, _, _) = ExistingListScenario();
            list.Settings = new NzbDrone.Core.ImportLists.Hardcover.Library.HardcoverLibraryImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false };
            var first = new Book { Id = 101, AuthorId = author.Id, Author = author, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { new() { Id = 301, BookId = 101, HardcoverEditionId = "hc:202", ReadingFormatId = 2, Narrator = "Stephen Fry", Monitored = competingSelected, ManualAdd = competingPinned } } };
            var second = new Book { Id = 102, AuthorId = author.Id, Author = author, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { new() { Id = 302, BookId = 102, HardcoverEditionId = "hc:202", ReadingFormatId = 2, Narrator = "Stephen Fry", Monitored = true, ManualAdd = selectedPinned } } };
            var reports = new List<ImportListItemInfo> { new() { ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId, BookProviderId = "hc:1001", EditionProviderId = "hc-ed:202", Author = author.Name, Book = "Listed narration" } };
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var proxy = (BookServiceProxy)books;
            var calls = 0;
            proxy.WantedEditions = (editions, candidates) =>
            {
                calls++;
                Assert.That(editions.Select(edition => edition.Id), Is.EqualTo(new[] { 302 }));
                Assert.That(candidates.Select(book => book.Id), Is.EquivalentTo(new[] { 101, 102 }));
                second.SetMonitored(true);
                return new Dictionary<int, Book> { [302] = second };
            };
            var queue = new StubCommandQueueManager();
            var service = BuildExistingAuthorService(list, author, new List<Book> { first, second }, reports, queue, bookServiceOverride: books);
            service.Execute(new ImportListSyncCommand(list.Id));
            service.Execute(new ImportListSyncCommand(list.Id));
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(first.IsMonitored(), Is.False);
            Assert.That(queue.Pushed.OfType<BookSearchCommand>().Single().BookIds, Is.EqualTo(new[] { 102 }));
            Assert.That(author.AudiobookMonitored, Is.False);
        }

        [Test]
        public void existing_only_list_should_batch_selected_rows_without_import_refresh_or_opposite_format_changes()
        {
            var (list, author, books, reports) = ExistingListScenario();
            var commands = new StubCommandQueueManager();
            var pending = new RecordingPendingAuthorImportService();
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var recorder = (BookServiceProxy)(object)bookService;
            var service = BuildExistingAuthorService(list, author, books, reports, commands,
                bookServiceOverride: bookService, pendingImports: pending);

            service.Execute(new ImportListSyncCommand(list.Id));
            service.Execute(new ImportListSyncCommand(list.Id));

            Assert.Multiple(() =>
            {
                Assert.That(books.Take(2).All(book => book.EbookMonitored), Is.True);
                Assert.That(books[2].EbookMonitored, Is.False);
                Assert.That(books[3].AudiobookMonitored, Is.True);
                Assert.That(author.EbookMonitored, Is.False);
                Assert.That(author.AudiobookMonitored, Is.False);
                Assert.That(pending.Enqueued, Is.Empty);
                Assert.That(recorder.UpdateCount, Is.EqualTo(1));
                Assert.That(recorder.CatalogReadCount, Is.Zero);
                Assert.That(commands.Pushed, Has.Count.EqualTo(1));
                Assert.That(commands.Pushed.All(command => command is BookSearchCommand search && search.BookIds.Count == 2 && search.Trigger != CommandTrigger.Manual), Is.True);
            });

            books[0].EbookMonitored = false;
            service.Execute(new ImportListSyncCommand(list.Id));
            Assert.That(books[0].EbookMonitored, Is.True);
            Assert.That(commands.Pushed, Has.Count.EqualTo(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void keep_existing_off_should_leave_rows_alone_and_resume_only_when_requested(bool resume)
        {
            var (list, author, books, reports) = ExistingListScenario();
            list.ShouldMonitorExisting = false;
            list.MonitoringOptions.ResumePausedAuthors = resume;
            var commands = new StubCommandQueueManager();
            var service = BuildExistingAuthorService(list, author, books, reports, commands);
            service.Execute(new ImportListSyncCommand(list.Id));
            Assert.Multiple(() =>
            {
                Assert.That(books.Take(3).Any(book => book.EbookMonitored), Is.False);
                Assert.That(books[3].AudiobookMonitored, Is.True);
                Assert.That(author.EbookMonitored, Is.EqualTo(resume));
                Assert.That(author.AudiobookMonitored, Is.False);
                Assert.That(commands.Pushed, Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void failed_search_queue_should_retain_only_unqueued_book_requests_for_next_sync(bool returnNull)
        {
            var (list, author, books, reports) = ExistingListScenario();
            var commands = new StubCommandQueueManager { RejectSearchNumber = 1, ReturnNullOnRejection = returnNull };
            var service = BuildExistingAuthorService(list, author, books, reports, commands);
            Assert.DoesNotThrow(() => service.Execute(new ImportListSyncCommand(list.Id)));
            Assert.That(books[0].AddOptions.SearchForNewBook, Is.True);
            Assert.That(books[1].AddOptions.SearchForNewBook, Is.True);
            service.Execute(new ImportListSyncCommand(list.Id));
            Assert.That(commands.Pushed.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(books[1].AddOptions.SearchForNewBook, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void add_missing_authors_should_control_import_with_valid_root_defaults(bool addMissingAuthors)
        {
            var (list, _, _, reports) = ExistingListScenario();
            list.MonitoringOptions.AddMissingAuthors = addMissingAuthors;
            ((DualMediaImportListSettings)list.Settings).EbookRootFolderPath = @"C:\library".AsOsAgnostic();
            var pending = new RecordingPendingAuthorImportService();
            var commands = new StubCommandQueueManager();
            var service = BuildExistingAuthorService(list, null, new List<Book>(), reports, commands, pendingImports: pending, bookInfoOverride: AttestedMetadata(reports));
            service.Execute(new ImportListSyncCommand(list.Id));
            Assert.That(pending.Enqueued.Any(), Is.EqualTo(addMissingAuthors));
            Assert.That(commands.Pushed.OfType<ProcessPendingImportsCommand>().Any(), Is.EqualTo(addMissingAuthors));
            Assert.That(pending.Enqueued.All(item => item.Config.CreateEbook && !item.Config.CreateAudiobook), Is.True);
        }

        [Test]
        public void converted_none_should_leave_rows_and_author_gates_alone()
        {
            var (list, author, books, reports) = ExistingListScenario();
            list.ShouldMonitor = ImportListMonitorType.None;
            list.ShouldMonitorExisting = false;
            list.MonitoringOptions = new ImportListMonitoringOptions { AddMissingAuthors = false, ResumePausedAuthors = false };
            var commands = new StubCommandQueueManager();
            BuildExistingAuthorService(list, author, books, reports, commands).Execute(new ImportListSyncCommand(list.Id));
            Assert.Multiple(() =>
            {
                Assert.That(books.Take(3).Any(book => book.EbookMonitored), Is.False);
                Assert.That(author.EbookMonitored, Is.False);
                Assert.That(author.AudiobookMonitored, Is.False);
                Assert.That(commands.Pushed, Is.Empty);
            });
        }

        [TestCase(true)]
        [TestCase(false)]
        [TestCase(null)]
        public void cached_narration_should_be_rechecked_after_author_refresh(bool? refreshedNarration)
        {
            var (list, author, _, _) = ExistingListScenario();
            list.ShouldSearch = false;
            list.Settings = new GoodreadsBookshelfImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false };
            var local = new Book { Id = 101, AuthorId = author.Id, Author = author, GoodreadsWorkId = "gr:123", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new() { Id = 301, BookId = 101, GoodreadsEditionId = 777, ReadingFormatId = 2, Narrator = "Stephen Fry", Monitored = true, ManualAdd = true },
                    new() { Id = 302, BookId = 101, GoodreadsEditionId = 789, ReadingFormatId = 2, Narrator = "Jim Dale" }
                } };
            ImportListItemInfo Report() => new() { ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId,
                BookProviderId = local.GoodreadsWorkId, EditionProviderId = "gr-ed:456", Book = "Listed Work" };
            var reports = new List<ImportListItemInfo> { Report() };
            var cached = new ImportListBookIdentityCache { SourceProviderId = "edition:gr:456", BookProviderId = local.GoodreadsWorkId,
                AuthorProviderId = author.GoodreadsAuthorId, EditionHasNarration = true, EditionNarratorNames = new List<string> { "Stephen Fry" },
                UpdatedAt = DateTime.UtcNow.AddMinutes(-2) };
            var identities = InterfaceProxy.Proxy<IImportListBookIdentityCacheRepository>((method, args) =>
            {
                if (method.Name == nameof(IImportListBookIdentityCacheRepository.FindBySourceProviderIds)) return new List<ImportListBookIdentityCache> { cached };
                cached = (ImportListBookIdentityCache)args[0];
                return cached;
            });
            var calls = 0;
            var metadata = InterfaceProxy.Proxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>((method, args) =>
            {
                calls++;
                var editions = refreshedNarration.HasValue ? new List<Edition>
                {
                    new() { GoodreadsEditionId = 456, ReadingFormatId = refreshedNarration.Value ? 2 : 4,
                        NarratorNames = refreshedNarration.Value ? new List<string> { "Jim Dale" } : null }
                } : new List<Edition>();
                return Tuple.Create(author.GoodreadsAuthorId, new Book { GoodreadsWorkId = local.GoodreadsWorkId, Editions = editions }, new List<Author> { author });
            });
            var writer = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var selections = new List<int>();
            ((BookServiceProxy)writer).WantedEditions = (editions, _) =>
            {
                selections.AddRange(editions.Select(edition => edition.Id));
                return editions.ToDictionary(edition => edition.Id, _ => local);
            };
            var service = BuildExistingAuthorService(list, author, new List<Book> { local }, reports,
                bookServiceOverride: writer, bookInfoOverride: metadata, identityCache: identities);
            service.Execute(new ImportListSyncCommand(list.Id));
            Assert.That(calls, Is.Zero, "Unchanged author metadata reuses the cached cast");
            Assert.That(selections, Is.EqualTo(new[] { 301 }));

            author.LastInfoSync = DateTime.UtcNow.AddMinutes(-1);
            reports[0] = Report();
            service.Execute(new ImportListSyncCommand(list.Id));
            Assert.Multiple(() =>
            {
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(reports[0].EditionHasNarration, Is.EqualTo(refreshedNarration));
                Assert.That(reports[0].EditionNarratorNames ?? new List<string>(),
                    Is.EqualTo(refreshedNarration == true ? new[] { "Jim Dale" } : Array.Empty<string>()));
                Assert.That(selections, Is.EqualTo(refreshedNarration == true ? new[] { 301, 302 } : new[] { 301 }));
            });
            if (refreshedNarration.HasValue)
            {
                reports[0] = Report();
                service.Execute(new ImportListSyncCommand(list.Id));
                Assert.That(calls, Is.EqualTo(1), "The refreshed evidence is reused on the next unchanged sync");
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void cache_failures_should_warn_once_per_sync_without_aborting_books(bool failBatchRead)
        {
            var (list, author, _, _) = ExistingListScenario();
            list.Settings = new GoodreadsBookshelfImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false };
            var local = new Book { Id = 101, AuthorId = author.Id, Author = author, GoodreadsWorkId = "gr:123", MediaType = BookMediaType.Audiobook };
            var reports = Enumerable.Range(400, 20).Select(id => new ImportListItemInfo
            {
                ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId, BookProviderId = local.GoodreadsWorkId,
                EditionProviderId = $"gr-ed:{id}", Book = "Listed Work"
            }).ToList();
            var cacheCalls = new List<string>();
            var identities = InterfaceProxy.Proxy<IImportListBookIdentityCacheRepository>((method, args) =>
            {
                lock (cacheCalls) cacheCalls.Add(method.Name);
                if (!failBatchRead && method.Name == nameof(IImportListBookIdentityCacheRepository.FindBySourceProviderIds))
                    return new List<ImportListBookIdentityCache>();
                throw new InvalidOperationException("Cache storage unavailable");
            });
            var metadata = InterfaceProxy.Proxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>((method, args) =>
                Tuple.Create(author.GoodreadsAuthorId, new Book { GoodreadsWorkId = local.GoodreadsWorkId, Editions = new List<Edition>
                    { new() { ForeignEditionId = ((string)args[0]).Replace("gr-ed:", "gr:"), ReadingFormatId = 4 } } }, new List<Author> { author }));
            using var logs = new LogFactory();
            var memory = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}|${exception:message}" };
            var logging = new NLog.Config.LoggingConfiguration(logs);
            logging.AddRule(LogLevel.Info, LogLevel.Fatal, memory);
            logs.Configuration = logging;
            BuildExistingAuthorService(list, author, new List<Book> { local }, reports,
                bookInfoOverride: metadata, identityCache: identities, logger: logs.GetLogger("cache-failure-test"))
                .Execute(new ImportListSyncCommand(list.Id));
            Assert.Multiple(() =>
            {
                Assert.That(local.IsMonitored(), Is.True);
                Assert.That(memory.Logs.Count(message => message.StartsWith("Warn|") && message.Contains("source cache")), Is.EqualTo(1));
                Assert.That(memory.Logs.Any(message => message.Contains("Cache storage unavailable")), Is.True);
                Assert.That(memory.Logs.Any(message => message.Contains("20 edition lookups") && message.Contains("elapsed:")), Is.True);
                Assert.That(cacheCalls.Count, Is.EqualTo(failBatchRead ? 1 : 21));
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void duplicate_work_rows_should_select_only_one_per_media_type(bool alreadyMonitored, bool hardcover)
        {
            var (list, author, books, _) = ExistingListScenario();
            var providerId = hardcover ? "hc:123" : "gr:123";
            books = Enumerable.Range(1, 5).Select(id => new Book
            {
                Id = id, Author = author, AuthorId = author.Id, Title = "Listed work",
                MediaType = BookMediaType.Ebook, EbookMonitored = alreadyMonitored && id == 5,
                GoodreadsWorkId = hardcover ? null : providerId,
                HardcoverBookId = hardcover ? providerId : null
            }).ToList();
            books.Add(new Book { Id = 6, Author = author, AuthorId = author.Id, GoodreadsWorkId = providerId,
                HardcoverBookId = hardcover ? providerId : null, MediaType = BookMediaType.Audiobook, AudiobookMonitored = true });
            var reports = new List<ImportListItemInfo> { new() { ImportListId = list.Id, Book = "Listed work", AuthorProviderId = author.GoodreadsAuthorId, BookProviderId = providerId } };
            var commands = new StubCommandQueueManager();
            BuildExistingAuthorService(list, author, books, reports, commands).Execute(new ImportListSyncCommand(list.Id));
            Assert.That(books.Where(book => book.MediaType == BookMediaType.Ebook && book.EbookMonitored).Select(book => book.Id), Is.EqualTo(new[] { alreadyMonitored ? 5 : 1 }));
            Assert.That(books[5].AudiobookMonitored, Is.True);
            Assert.That(commands.Pushed.OfType<BookSearchCommand>().Count(), Is.EqualTo(alreadyMonitored ? 0 : 1));
        }

        [TestCase("gr:999", false)]
        [TestCase("gr:999", true)]
        [TestCase("hc-ed:999", false)]
        [TestCase("hc-ed:999", true)]
        public void edition_only_report_should_resolve_its_owner_without_confusing_a_work_with_the_same_number(string editionId, bool alreadyMonitored)
        {
            var (list, author, _, _) = ExistingListScenario();
            var books = new List<Book>
            {
                new() { Id = 1, Author = author, AuthorId = author.Id, Title = "Unrelated work", GoodreadsWorkId = "gr:999", HardcoverBookId = "hc:999", MediaType = BookMediaType.Ebook },
                new() { Id = 2, Author = author, AuthorId = author.Id, Title = "Listed work", GoodreadsWorkId = "gr:123", MediaType = BookMediaType.Ebook,
                    Editions = new List<Edition> { new() { Id = 10, BookId = 2, GoodreadsEditionId = 999, HardcoverEditionId = "hc:999", Monitored = true } } },
                new() { Id = 3, Author = author, AuthorId = author.Id, Title = "Listed work", GoodreadsWorkId = "gr:123", MediaType = BookMediaType.Ebook, EbookMonitored = alreadyMonitored }
            };
            var report = new ImportListItemInfo { ImportListId = list.Id, Book = "Listed edition", EditionProviderId = editionId };
            var commands = new StubCommandQueueManager();
            BuildExistingAuthorService(list, author, books, new List<ImportListItemInfo> { report }, commands)
                .Execute(new ImportListSyncCommand(list.Id));
            Assert.That(report.BookProviderId, Is.EqualTo("gr:123"));
            Assert.That(books.Where(book => book.EbookMonitored).Select(book => book.Id), Is.EqualTo(new[] { alreadyMonitored ? 3 : 2 }));
            Assert.That(books[1].Editions.Single().Monitored, Is.True);
            Assert.That(commands.Pushed.OfType<BookSearchCommand>().Count(), Is.EqualTo(alreadyMonitored ? 0 : 1));
        }

        [Test]
        public void resume_should_use_the_author_gate_setter_once_per_format()
        {
            var (list, author, books, reports) = ExistingListScenario();
            list.MonitoringOptions.ResumePausedAuthors = true;
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var recorder = (AuthorServiceProxy)(object)authorService;
            var service = BuildExistingAuthorService(list, author, books, reports, authorServiceOverride: authorService);
            service.Execute(new ImportListSyncCommand(list.Id));
            service.Execute(new ImportListSyncCommand(list.Id));
            Assert.That(recorder.MonitoringWrites, Is.EqualTo(new[] { (author.Id, "ebook", true) }));
        }

        [TestCase("hc:123")]
        [TestCase("gr:123")]
        public void missing_work_should_not_match_an_unrelated_edition_with_the_same_number(string providerId)
        {
            var book = new Book
            {
                Id = 1, AuthorId = 1, MediaType = BookMediaType.Ebook,
                HardcoverBookId = "hc:999", GoodreadsWorkId = "gr:999",
                Editions = new List<Edition> { new() { Id = 2, BookId = 1, HardcoverEditionId = "hc:123", GoodreadsEditionId = 123 } }
            };
            var service = BuildExistingAuthorService(null, null, new List<Book> { book }, new List<ImportListItemInfo>());
            var lookup = typeof(ImportListSyncService).GetMethod("BuildImportListLocalLookup", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(service, new object[] { null, 0, null });
            Assert.That(lookup.GetType().GetMethod("FindBook").Invoke(lookup, new object[] { providerId }), Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void active_author_searches_should_be_one_automatic_batch_including_after_explicit_resume(bool resume)
        {
            var (list, author, books, reports) = ExistingListScenario();
            author.EbookMonitored = !resume;
            list.MonitoringOptions.ResumePausedAuthors = resume;
            list.ShouldMonitor = ImportListMonitorType.None;
            var queue = new StubCommandQueueManager();
            var service = BuildExistingAuthorService(list, author, books, reports, queue);
            service.Execute(new ImportListSyncCommand(list.Id));
            service.Execute(new ImportListSyncCommand(list.Id));
            var search = queue.Pushed.OfType<BookSearchCommand>().Single();
            Assert.Multiple(() =>
            {
                Assert.That(search.BookIds, Is.EquivalentTo(new[] { 1, 2 }));
                Assert.That(search.Trigger, Is.Not.EqualTo(CommandTrigger.Manual));
                Assert.That(author.EbookMonitored, Is.True);
                Assert.That(author.AudiobookMonitored, Is.False);
            });
        }

        [TestCase(true, false, 2, true, false)]
        [TestCase(false, false, 2, true, false)]
        [TestCase(false, true, 2, true, false)]
        [TestCase(false, false, 1, true, false)]
        [TestCase(false, false, 3, true, false)]
        [TestCase(false, true, 1, true, false)]
        [TestCase(false, false, 2, false, true)]
        [TestCase(false, true, 2, false, true)]
        [TestCase(false, false, 2, true, true)]
        public void source_requests_should_monitor_enabled_rows_and_only_request_explicit_audio_narrations(bool series, bool owned, int sourceFormat, bool audio, bool ebook)
        {
            var report = series ? ImportListEditionIntentFixture.SeriesItem() : ImportListEditionIntentFixture.HardcoverItem(owned, sourceFormat, audio, ebook);
            Assert.That(report, Is.Not.Null);
            var (list, author, _, _) = ExistingListScenario();
            list.ShouldSearch = false;
            list.Settings = series
                ? new GoodreadsSeriesImportListSettings { MonitorAudiobooks = audio, MonitorEbooks = ebook }
                : new NzbDrone.Core.ImportLists.Hardcover.Library.HardcoverLibraryImportListSettings { MonitorAudiobooks = audio, MonitorEbooks = ebook };
            author.HardcoverAuthorId = "hc:562254";
            var selected = new Edition { Id = 301, BookId = 101, GoodreadsEditionId = 202, HardcoverEditionId = "hc:202", ReadingFormatId = sourceFormat, Narrator = "Selected narrator", Monitored = true, ManualAdd = true };
            var audioBook = new Book { Id = 101, AuthorId = author.Id, Author = author, MediaType = BookMediaType.Audiobook, GoodreadsWorkId = "gr:1001", HardcoverBookId = "hc:1001", Editions = new List<Edition> { selected } };
            var ebookBook = new Book { Id = 102, AuthorId = author.Id, Author = author, MediaType = BookMediaType.Ebook, GoodreadsWorkId = "gr:1001", HardcoverBookId = "hc:1001", Editions = new List<Edition> { new() { Id = 401, BookId = 102, ReadingFormatId = 3, Monitored = true } } };
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var calls = 0;
            ((BookServiceProxy)bookService).WantedEditions = (editions, candidates) =>
            {
                calls++;
                Assert.That(editions.Select(edition => edition.Id), Is.EqualTo(new[] { selected.Id }));
                return new Dictionary<int, Book> { [selected.Id] = audioBook };
            };
            var service = BuildExistingAuthorService(list, author, new List<Book> { audioBook, ebookBook }, new List<ImportListItemInfo> { report }, bookServiceOverride: bookService);
            service.Execute(new ImportListSyncCommand(list.Id));
            service.Execute(new ImportListSyncCommand(list.Id));
            Assert.Multiple(() =>
            {
                Assert.That(audioBook.IsMonitored(), Is.EqualTo(audio));
                Assert.That(ebookBook.IsMonitored(), Is.EqualTo(ebook));
                Assert.That(calls, Is.EqualTo(!series && sourceFormat == 2 && audio ? 2 : 0));
                Assert.That(selected.Monitored, Is.True);
                Assert.That(selected.ManualAdd, Is.True);
                Assert.That(author.AudiobookMonitored, Is.False);
                Assert.That(author.EbookMonitored, Is.False);
            });
        }

        [TestCase(true, false, 2, true, false)]
        [TestCase(false, false, 2, true, false)]
        [TestCase(false, true, 2, true, false)]
        [TestCase(false, false, 1, true, false)]
        [TestCase(false, false, 3, true, false)]
        [TestCase(false, false, 2, false, true)]
        [TestCase(false, true, 2, false, true)]
        [TestCase(false, false, 2, true, true)]
        public void source_requests_should_carry_the_same_intent_into_new_author_imports(bool series, bool owned, int sourceFormat, bool audio, bool ebook)
        {
            var report = series ? ImportListEditionIntentFixture.SeriesItem() : ImportListEditionIntentFixture.HardcoverItem(owned, sourceFormat, audio, ebook);
            Assert.That(report, Is.Not.Null);
            var (list, _, _, _) = ExistingListScenario();
            list.ShouldSearch = false;
            list.MonitoringOptions.AddMissingAuthors = true;
            list.Settings = series
                ? new GoodreadsSeriesImportListSettings { MonitorAudiobooks = audio, MonitorEbooks = ebook, AudiobookRootFolderPath = @"C:\Audio", EbookRootFolderPath = @"C:\Ebooks" }
                : new NzbDrone.Core.ImportLists.Hardcover.Library.HardcoverLibraryImportListSettings { MonitorAudiobooks = audio, MonitorEbooks = ebook, AudiobookRootFolderPath = @"C:\Audio", EbookRootFolderPath = @"C:\Ebooks" };
            var pending = new RecordingPendingAuthorImportService();
            BuildExistingAuthorService(list, null, new List<Book>(), new List<ImportListItemInfo> { report }, pendingImports: pending, bookInfoOverride: AttestedMetadata(new[] { report }))
                .Execute(new ImportListSyncCommand(list.Id));
            Assert.That(pending.Enqueued, Is.Not.Empty);
            if (!series)
            {
                Assert.That(report.EditionProviderId, Is.EqualTo("hc-ed:202"));
                Assert.That(report.EditionHasNarration, Is.False);
            }
            var config = pending.Enqueued.Last().Config;
            Assert.Multiple(() =>
            {
                Assert.That(config.AudiobookBooksToMonitor ?? new List<string>(), Is.EquivalentTo(audio ? new[] { report.BookProviderId } : Array.Empty<string>()));
                Assert.That(config.EbookBooksToMonitor ?? new List<string>(), Is.EquivalentTo(ebook ? new[] { report.BookProviderId } : Array.Empty<string>()));
                Assert.That(config.AudiobookNarrations?.Count ?? 0, Is.Zero);
                if (config.AudiobookNarrations?.Count > 0)
                {
                    Assert.That(config.AudiobookNarrations.Single().EditionProviderId, Is.EqualTo("hc:202"));
                    Assert.That(config.AudiobookNarrations.Single().RequiresEdition, Is.False);
                }
            });
        }

        [TestCase(BookMediaType.Audiobook, false)]
        [TestCase(BookMediaType.Ebook, false)]
        [TestCase(BookMediaType.Audiobook, true)]
        [TestCase(BookMediaType.Ebook, true)]
        public void edition_requests_should_apply_exclusions_to_the_target_side_not_the_source_format(BookMediaType excludedSide, bool excludeAuthor)
        {
            var report = ImportListEditionIntentFixture.HardcoverItem(false, 2);
            var (list, author, _, _) = ExistingListScenario();
            list.ShouldSearch = false;
            list.Settings = new NzbDrone.Core.ImportLists.Hardcover.Library.HardcoverLibraryImportListSettings { MonitorAudiobooks = true, MonitorEbooks = true };
            author.HardcoverAuthorId = report.AuthorProviderId;
            var books = new List<Book>
            {
                new() { Id = 101, AuthorId = author.Id, Author = author, MediaType = BookMediaType.Audiobook, HardcoverBookId = report.BookProviderId,
                    Editions = new List<Edition> { new() { Id = 301, BookId = 101, HardcoverEditionId = "hc:202", ReadingFormatId = 2, Narrator = "Selected narrator", Monitored = true } } },
                new() { Id = 102, AuthorId = author.Id, Author = author, MediaType = BookMediaType.Ebook, HardcoverBookId = report.BookProviderId }
            };
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var calls = 0;
            ((BookServiceProxy)bookService).WantedEditions = (editions, candidates) =>
            {
                calls++;
                return new Dictionary<int, Book> { [301] = books[0] };
            };
            BuildExistingAuthorService(list, author, books, new List<ImportListItemInfo> { report }, bookServiceOverride: bookService,
                exclusions: new List<ImportListExclusion> { new() { ForeignId = excludeAuthor ? report.AuthorProviderId : report.BookProviderId, MediaType = excludedSide } })
                .Execute(new ImportListSyncCommand(list.Id));
            Assert.Multiple(() =>
            {
                Assert.That(books.Single(book => book.MediaType == excludedSide).IsMonitored(), Is.False);
                Assert.That(books.Single(book => book.MediaType != excludedSide).IsMonitored(), Is.True);
                Assert.That(calls, Is.EqualTo(excludedSide == BookMediaType.Audiobook ? 0 : 1));
            });
        }

        [TestCase(BookMediaType.Audiobook, false)]
        [TestCase(BookMediaType.Ebook, false)]
        [TestCase(BookMediaType.Audiobook, true)]
        [TestCase(BookMediaType.Ebook, true)]
        public void queued_shelf_requests_should_not_persist_excluded_book_sides(BookMediaType excludedSide, bool excludeAuthor)
        {
            var report = ImportListEditionIntentFixture.HardcoverItem(false, 2);
            var (list, _, _, _) = ExistingListScenario();
            list.MonitoringOptions.AddMissingAuthors = true;
            list.Settings = new NzbDrone.Core.ImportLists.Hardcover.Library.HardcoverLibraryImportListSettings
            {
                MonitorAudiobooks = true, MonitorEbooks = true,
                AudiobookRootFolderPath = @"C:\Audio", EbookRootFolderPath = @"C:\Ebooks"
            };
            var pending = new RecordingPendingAuthorImportService();
            BuildExistingAuthorService(list, null, new List<Book>(), new List<ImportListItemInfo> { report }, pendingImports: pending, bookInfoOverride: AttestedMetadata(new[] { report }),
                exclusions: new List<ImportListExclusion> { new() { ForeignId = excludeAuthor ? report.AuthorProviderId : report.BookProviderId, MediaType = excludedSide } })
                .Execute(new ImportListSyncCommand(list.Id));
            Assert.That(pending.Enqueued, Is.Not.Empty);
            var config = pending.Enqueued.Last().Config;
            Assert.Multiple(() =>
            {
                Assert.That(config.AudiobookBooksToMonitor?.Count ?? 0, Is.EqualTo(excludedSide == BookMediaType.Audiobook ? 0 : 1));
                Assert.That(config.EbookBooksToMonitor?.Count ?? 0, Is.EqualTo(excludedSide == BookMediaType.Ebook ? 0 : 1));
                Assert.That(report.EditionProviderId, Is.EqualTo("hc-ed:202"));
                Assert.That(report.EditionHasNarration, Is.False);
                Assert.That(config.AudiobookNarrations?.Count ?? 0, Is.Zero);
                Assert.That(config.AudiobookBooksToSearch?.Count ?? 0, Is.EqualTo(excludedSide == BookMediaType.Audiobook ? 0 : 1));
                Assert.That(config.EbookBooksToSearch?.Count ?? 0, Is.EqualTo(excludedSide == BookMediaType.Ebook ? 0 : 1));
            });
        }

        [Test]
        public void new_author_queue_should_keep_both_owned_and_shelf_narration_requests()
        {
            var owned = ImportListEditionIntentFixture.HardcoverItem(true, 2);
            var shelf = ImportListEditionIntentFixture.HardcoverItem(false, 2);
            shelf.EditionProviderId = "hc-ed:203";
            owned.EditionHasNarration = shelf.EditionHasNarration = true;
            owned.EditionNarratorNames = new List<string> { "Jim Dale" };
            shelf.EditionNarratorNames = new List<string> { "Stephen Fry" };
            var (list, _, _, _) = ExistingListScenario();
            list.MonitoringOptions.AddMissingAuthors = true;
            list.Settings = new NzbDrone.Core.ImportLists.Hardcover.Library.HardcoverLibraryImportListSettings
            {
                MonitorAudiobooks = true, MonitorEbooks = false, AudiobookRootFolderPath = @"C:\Audio"
            };
            var pending = new RecordingPendingAuthorImportService();
            BuildExistingAuthorService(list, null, new List<Book>(), new List<ImportListItemInfo> { owned, shelf }, pendingImports: pending, bookInfoOverride: AttestedMetadata(new[] { owned, shelf }))
                .Execute(new ImportListSyncCommand(list.Id));
            Assert.That(pending.Enqueued, Is.Not.Empty);
            Assert.That(pending.Enqueued.Last().Config.AudiobookNarrations.Select(request => request.EditionProviderId),
                Is.EquivalentTo(new[] { "hc:202", "hc:203" }));
            Assert.That(pending.Enqueued.Last().Config.AudiobookNarrations.Single(request => request.EditionProviderId == "hc:202").NarratorNames,
                Is.EqualTo(new[] { "Jim Dale" }));
            Assert.That(pending.Enqueued.Last().Config.AudiobookNarrations.Single(request => request.EditionProviderId == "hc:203").NarratorNames,
                Is.EqualTo(new[] { "Stephen Fry" }));
        }

        [Test]
        public void owned_and_shelf_with_different_audio_editions_should_both_reach_the_shared_writer()
        {
            var owned = ImportListEditionIntentFixture.HardcoverItem(true, 2);
            var shelf = ImportListEditionIntentFixture.HardcoverItem(false, 2);
            shelf.EditionProviderId = "hc-ed:203";
            var (list, author, _, _) = ExistingListScenario();
            list.ShouldSearch = false;
            list.Settings = new NzbDrone.Core.ImportLists.Hardcover.Library.HardcoverLibraryImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false };
            author.HardcoverAuthorId = owned.AuthorProviderId;
            var books = new List<Book>
            {
                new() { Id = 101, AuthorId = author.Id, Author = author, MediaType = BookMediaType.Audiobook, HardcoverBookId = owned.BookProviderId,
                    Editions = new List<Edition> { new() { Id = 301, BookId = 101, HardcoverEditionId = "hc:202", ReadingFormatId = 2, Narrator = "First narrator", Monitored = true, ManualAdd = true } } },
                new() { Id = 102, AuthorId = author.Id, Author = author, MediaType = BookMediaType.Audiobook, HardcoverBookId = owned.BookProviderId,
                    Editions = new List<Edition> { new() { Id = 302, BookId = 102, HardcoverEditionId = "hc:203", ReadingFormatId = 2, Narrator = "Second narrator", Monitored = true, ManualAdd = true } } }
            };
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var calls = 0;
            ((BookServiceProxy)bookService).WantedEditions = (editions, candidates) =>
            {
                calls++;
                Assert.That(editions.Select(edition => edition.Id), Is.EquivalentTo(new[] { 301, 302 }));
                return new Dictionary<int, Book> { [301] = books[0], [302] = books[1] };
            };
            var service = BuildExistingAuthorService(list, author, books, new List<ImportListItemInfo> { owned, shelf }, bookServiceOverride: bookService);
            for (var sync = 0; sync < 3; sync++)
            {
                service.Execute(new ImportListSyncCommand(list.Id));
            }

            Assert.That(calls, Is.EqualTo(3));
            Assert.That(books.All(book => book.IsMonitored()), Is.True);
            Assert.That(books.SelectMany(book => book.Editions).All(edition => edition.Monitored && edition.ManualAdd), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void local_non_audio_narrator_is_not_an_audio_request(bool hardcover)
        {
            var report = hardcover ? ImportListEditionIntentFixture.HardcoverItem(false, 2) : ImportListEditionIntentFixture.SeriesItem();
            report.EditionProviderId = hardcover ? "hc-ed:202" : "gr:202";
            report.HardcoverReadingFormatId = null;
            var (list, author, _, _) = ExistingListScenario();
            list.ShouldSearch = false;
            list.Settings = hardcover
                ? new NzbDrone.Core.ImportLists.Hardcover.Library.HardcoverLibraryImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false }
                : new GoodreadsBookshelfImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false };
            author.HardcoverAuthorId = hardcover ? report.AuthorProviderId : null;
            author.GoodreadsAuthorId = hardcover ? null : report.AuthorProviderId;
            var selected = new Edition { Id = 302, BookId = 101, ReadingFormatId = 2, Narrator = "Selected Narrator", Monitored = true };
            var nonAudio = new Edition { Id = 301, BookId = 101, ReadingFormatId = 1, Narrator = "Other Narrator",
                HardcoverEditionId = hardcover ? "hc:202" : null, GoodreadsEditionId = hardcover ? null : 202 };
            var book = new Book { Id = 101, AuthorId = author.Id, Author = author, MediaType = BookMediaType.Audiobook, AnyEditionOk = true,
                HardcoverBookId = hardcover ? report.BookProviderId : null, GoodreadsWorkId = hardcover ? null : report.BookProviderId,
                Editions = new List<Edition> { selected, nonAudio } };
            var proxy = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var calls = 0;
            ((BookServiceProxy)proxy).WantedEditions = (editions, candidates) => { calls++; return new Dictionary<int, Book> { [301] = book }; };
            BuildExistingAuthorService(list, author, new List<Book> { book }, new List<ImportListItemInfo> { report }, bookServiceOverride: proxy)
                .Execute(new ImportListSyncCommand(list.Id));
            Assert.Multiple(() =>
            {
                Assert.That(calls, Is.Zero);
                Assert.That(book.IsMonitored(), Is.True);
                Assert.That(book.Editions.Single(edition => edition.Monitored), Is.SameAs(selected));
                Assert.That(book.AnyEditionOk, Is.True);
                Assert.That(book.Editions.All(edition => !edition.ManualAdd), Is.True);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void complete_source_mapper_should_verify_primary_ownership(bool hardcover)
        {
            var report = hardcover ? ImportListEditionIntentFixture.HardcoverItem(false, 2) : ImportListEditionIntentFixture.SeriesItem();
            if (hardcover) report.EditionHasNarration = true;
            var (list, _, _, _) = ExistingListScenario();
            var calls = 0;
            var attested = AttestedMetadata(new[] { report });
            var metadata = InterfaceProxy.Proxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>((method, args) =>
            {
                calls++;
                return method.Invoke(attested, args);
            });
            var service = BuildExistingAuthorService(list, null, new List<Book>(), new List<ImportListItemInfo>(), bookInfoOverride: metadata);
            var lookup = Activator.CreateInstance(typeof(ImportListSyncService).GetNestedType("ImportListLocalLookup", BindingFlags.NonPublic), true);
            var stats = Activator.CreateInstance(typeof(ImportListSyncService).GetNestedType("ImportListSyncStats", BindingFlags.NonPublic), true);
            typeof(ImportListSyncService).GetMethod("MapBookReportWithMappings", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(service, new object[] { report, lookup, null, stats, null });
            Assert.That(calls, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void fully_identified_source_request_should_verify_ownership_and_queue_once(bool hardcover)
        {
            var report = hardcover ? ImportListEditionIntentFixture.HardcoverItem(false, 2) : ImportListEditionIntentFixture.SeriesItem();
            if (hardcover) report.EditionHasNarration = true;
            var (list, _, _, _) = ExistingListScenario();
            list.MonitoringOptions.AddMissingAuthors = true;
            list.Settings = hardcover
                ? new NzbDrone.Core.ImportLists.Hardcover.Library.HardcoverLibraryImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false, AudiobookRootFolderPath = @"C:\Audio" }
                : new GoodreadsSeriesImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false, AudiobookRootFolderPath = @"C:\Audio" };
            var calls = 0;
            var attested = AttestedMetadata(new[] { report });
            var metadata = InterfaceProxy.Proxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>((method, args) =>
            {
                calls++;
                return method.Invoke(attested, args);
            });
            var pending = new RecordingPendingAuthorImportService();
            BuildExistingAuthorService(list, null, new List<Book>(), new List<ImportListItemInfo> { report }, pendingImports: pending, bookInfoOverride: metadata)
                .Execute(new ImportListSyncCommand(list.Id));
            Assert.Multiple(() =>
            {
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(pending.Enqueued.Count, Is.EqualTo(1));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void existing_author_missing_listed_editions_should_queue_once_without_searching_another_narration(bool resume)
        {
            var (list, author, _, _) = ExistingListScenario();
            list.Settings = new NzbDrone.Core.ImportLists.Hardcover.Library.HardcoverLibraryImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false };
            list.MonitoringOptions.ResumePausedAuthors = resume;
            author.AudiobookRootFolderPath = @"C:\Audio";
            author.AudiobookQualityProfileId = 3;
            author.AudiobookMetadataProfileId = 4;
            var book = new Book { Id = 101, AuthorId = author.Id, Author = author, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook };
            var reports = new[] { "hc-ed:202", "hc-ed:203" }.Select(id => new ImportListItemInfo
            {
                ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId, BookProviderId = book.HardcoverBookId,
                EditionProviderId = id, HardcoverReadingFormatId = 2, EditionHasNarration = true, Author = author.Name, Book = "Listed narration"
            }).ToList();
            var pending = new RecordingPendingAuthorImportService();
            var commands = new StubCommandQueueManager();
            BuildExistingAuthorService(list, author, new List<Book> { book }, reports, commands, pendingImports: pending)
                .Execute(new ImportListSyncCommand(list.Id));
            var request = pending.Enqueued.Single().Config;
            Assert.That(request.AudiobookNarrations, Has.Count.EqualTo(2));
            Assert.That(request.AudiobookNarrations.All(item => item.RequiresEdition && item.Search), Is.True);
            Assert.That(request.AudiobookBooksToMonitor, Is.EqualTo(new[] { book.HardcoverBookId }));
            Assert.That(request.AudiobookMetadataProfileId, Is.EqualTo(4));
            Assert.That(request.AudiobookQualityProfileId, Is.EqualTo(3));
            Assert.That(author.AudiobookMonitored, Is.EqualTo(resume));
            Assert.That(request.AudiobookMonitored, Is.EqualTo(resume));
            Assert.That(commands.Pushed.OfType<BookSearchCommand>(), Is.Empty);
            Assert.That(book.IsMonitored(), Is.False);
        }

        [Test]
        public void failed_narration_should_not_stop_other_list_books_from_monitoring_and_search()
        {
            var (list, author, _, _) = ExistingListScenario();
            list.Settings = new NzbDrone.Core.ImportLists.Hardcover.Library.HardcoverLibraryImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false };
            var first = new Book { Id = 101, AuthorId = author.Id, Author = author, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { new() { Id = 301, BookId = 101, HardcoverEditionId = "hc:202", ReadingFormatId = 2, Narrator = "Requested Narrator" } } };
            var second = new Book { Id = 102, AuthorId = author.Id, Author = author, HardcoverBookId = "hc:1002", MediaType = BookMediaType.Audiobook };
            var reports = new List<ImportListItemInfo>
            {
                new() { ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId, BookProviderId = first.HardcoverBookId, EditionProviderId = "hc-ed:202", Author = author.Name, Book = "Unavailable narration" },
                new() { ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId, BookProviderId = second.HardcoverBookId, Author = author.Name, Book = "Other book" }
            };
            var proxy = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)proxy).FailWantedRequest = true;
            var queue = new StubCommandQueueManager();
            BuildExistingAuthorService(list, author, new List<Book> { first, second }, reports, queue, bookServiceOverride: proxy)
                .Execute(new ImportListSyncCommand(list.Id));
            Assert.That(first.IsMonitored(), Is.False);
            Assert.That(second.IsMonitored(), Is.True);
            Assert.That(queue.Pushed.OfType<BookSearchCommand>().Single().BookIds, Is.EqualTo(new[] { second.Id }));
        }

        [Test]
        public void keep_off_must_not_pin_copy_or_queue_a_narration()
        {
            var (list, author, _, _) = ExistingListScenario();
            list.ShouldMonitorExisting = false;
            list.Settings = new NzbDrone.Core.ImportLists.Hardcover.Library.HardcoverLibraryImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false, AudiobookRootFolderPath = @"C:\Audio" };
            var report = ImportListEditionIntentFixture.HardcoverItem(false, 2);
            author.HardcoverAuthorId = report.AuthorProviderId;
            var book = new Book { Id = 101, AuthorId = author.Id, Author = author, HardcoverBookId = report.BookProviderId,
                MediaType = BookMediaType.Audiobook, AnyEditionOk = true,
                Editions = new List<Edition> { new() { Id = 301, BookId = 101, HardcoverEditionId = "hc:202", ReadingFormatId = 2, Narrator = "Requested Narrator", Monitored = true } } };
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)books).Books = new List<Book> { book };
            var calls = 0;
            ((BookServiceProxy)books).WantedEditions = (_, _) => { calls++; return new Dictionary<int, Book>(); };
            var pending = new RecordingPendingAuthorImportService();
            BuildExistingAuthorService(list, author, new List<Book> { book }, new List<ImportListItemInfo> { report }, bookServiceOverride: books, pendingImports: pending)
                .Execute(new ImportListSyncCommand(list.Id));
            Assert.That(calls, Is.Zero);
            Assert.That(pending.Enqueued, Is.Empty);
            Assert.That(book.AnyEditionOk, Is.True);
            Assert.That(book.Editions.Any(edition => edition.ManualAdd), Is.False);
        }

        [Test]
        public void repeated_absent_provider_ids_should_be_probed_once_per_sync()
        {
            var (list, _, _, _) = ExistingListScenario();
            list.MonitoringOptions.AddMissingAuthors = false;
            var authors = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var reports = Enumerable.Range(0, 100).Select(_ => new ImportListItemInfo
            {
                ImportListId = list.Id, AuthorProviderId = "gr:987654", BookProviderId = "gr:987654", Author = "Absent Author", Book = "Absent Work"
            }).ToList();
            var service = BuildExistingAuthorService(list, null, new List<Book>(), reports, authorServiceOverride: authors, bookServiceOverride: books, bookInfoOverride: AttestedMetadata(reports));
            service.Execute(new ImportListSyncCommand(list.Id));
            TestContext.WriteLine("100 repeated absent reports: {0} author service probes, {1} book service probes, {2} scoped batch calls",
                ((AuthorServiceProxy)authors).ProviderLookupCount, ((BookServiceProxy)books).ProviderLookupCount, ((BookServiceProxy)books).BatchLookupCount);
            Assert.That(((AuthorServiceProxy)authors).ProviderLookupCount, Is.EqualTo(1));
            Assert.That(((BookServiceProxy)books).ProviderLookupCount, Is.Zero);
            Assert.That(((BookServiceProxy)books).BatchLookupCount, Is.EqualTo(1));
            service.Execute(new ImportListSyncCommand(list.Id));
            Assert.That(((AuthorServiceProxy)authors).ProviderLookupCount, Is.EqualTo(2));
            Assert.That(((BookServiceProxy)books).ProviderLookupCount, Is.Zero);
            Assert.That(((BookServiceProxy)books).BatchLookupCount, Is.EqualTo(2));
        }

        [Test]
        public void incomplete_batch_alias_lookup_should_fall_back_once_without_memoizing_absence()
        {
            var (list, author, books, _) = ExistingListScenario();
            books = books.Take(1).ToList();
            var reports = Enumerable.Range(0, 100).Select(_ => new ImportListItemInfo
            {
                ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId, BookProviderId = "gr:789", Book = "Listed Work"
            }).ToList();
            var proxy = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)proxy).AliasProviderId = "gr:789";
            ((BookServiceProxy)proxy).IncompleteBatch = true;
            BuildExistingAuthorService(list, author, books, reports, bookServiceOverride: proxy, bookInfoOverride: AttestedMetadata(reports)).Execute(new ImportListSyncCommand(list.Id));
            Assert.That(books.Single().IsMonitored(), Is.True);
            Assert.That(((BookServiceProxy)proxy).BatchLookupCount, Is.EqualTo(1));
            Assert.That(((BookServiceProxy)proxy).ProviderLookupCount, Is.EqualTo(1));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        public void edition_narrator_names_should_reach_the_writer_and_survive_cached_replay(bool generic, bool incompleteCache)
        {
            var (list, author, _, _) = ExistingListScenario();
            list.ShouldSearch = false;
            list.Settings = new GoodreadsBookshelfImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false };
            var editions = new List<Edition>
            {
                new() { Id = 301, BookId = 101, GoodreadsEditionId = 777, ReadingFormatId = 2, Narrator = "Jim Dale", Monitored = true, ManualAdd = true },
                new() { Id = 302, BookId = 102, GoodreadsEditionId = 789, ReadingFormatId = 2, Narrator = "Stephen Fry", Monitored = true, ManualAdd = true }
            };
            var books = editions.Select(edition => new Book { Id = edition.BookId, Author = author, AuthorId = author.Id,
                GoodreadsWorkId = "gr:123", MediaType = BookMediaType.Audiobook, AnyEditionOk = false,
                Editions = new List<Edition> { edition } }).ToList();
            ImportListItemInfo Report() => new() { ImportListId = list.Id, Author = author.Name,
                AuthorProviderId = author.GoodreadsAuthorId, Book = "Listed Work", BookProviderId = "gr:123",
                EditionProviderId = "gr-ed:456", IsGenericBookRequest = generic };
            var reports = new List<ImportListItemInfo> { Report() };
            var metadataCalls = 0;
            var metadata = InterfaceProxy.Proxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>((method, args) =>
            {
                metadataCalls++;
                Assert.That(method.Name, Is.EqualTo("GetEditionInfo"));
                var mapped = new Book { GoodreadsWorkId = "gr:123", Title = "Listed Work", Editions = new List<Edition>
                    { new() { GoodreadsEditionId = 456, ReadingFormatId = 2, NarratorNames = new List<string> { "Stephen Fry" } } } };
                return Tuple.Create(author.GoodreadsAuthorId, mapped, new List<Author> { author });
            });
            var cache = new Dictionary<string, ImportListBookIdentityCache>();
            if (incompleteCache)
            {
                cache["edition:gr:456"] = new ImportListBookIdentityCache
                {
                    SourceProviderId = "edition:gr:456", BookProviderId = "gr:123", AuthorProviderId = author.GoodreadsAuthorId,
                    EditionHasNarration = true, UpdatedAt = DateTime.UtcNow
                };
            }
            var identities = InterfaceProxy.Proxy<IImportListBookIdentityCacheRepository>((method, args) =>
            {
                if (method.Name == nameof(IImportListBookIdentityCacheRepository.FindBySourceProviderIds))
                    return ((IEnumerable<string>)args[0]).Where(cache.ContainsKey).Select(key => cache[key]).ToList();
                if (method.Name == nameof(IImportListBookIdentityCacheRepository.FindBySourceProviderId)) return cache.GetValueOrDefault((string)args[0]);
                if (method.Name == nameof(IImportListBookIdentityCacheRepository.UpsertBySourceProviderId))
                {
                    var value = (ImportListBookIdentityCache)args[0];
                    cache[value.SourceProviderId] = value;
                    return value;
                }
                throw new AssertionException(method.Name);
            });
            var writer = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var writerCalls = 0;
            ((BookServiceProxy)writer).WantedEditions = (requested, _) =>
            {
                writerCalls++;
                Assert.That(requested.Single().Id, Is.EqualTo(302));
                return new Dictionary<int, Book> { [302] = books[1] };
            };
            var pending = new RecordingPendingAuthorImportService();
            var service = BuildExistingAuthorService(list, author, books, reports, bookServiceOverride: writer,
                pendingImports: pending, bookInfoOverride: metadata, identityCache: identities);
            for (var sync = 0; sync < 3; sync++)
            {
                reports[0] = Report();
                service.Execute(new ImportListSyncCommand(list.Id));
            }
            Assert.Multiple(() =>
            {
                Assert.That(writerCalls, Is.EqualTo(generic ? 0 : 3));
                Assert.That(metadataCalls, Is.EqualTo(generic ? 0 : 1));
                Assert.That(pending.Enqueued, Is.Empty);
                Assert.That(editions.All(edition => edition.ManualAdd && edition.Monitored), Is.True);
                Assert.That(books.Any(book => book.IsMonitored()), Is.True);
            });
        }

        [Test]
        public void mapping_must_not_borrow_narrator_names_from_a_different_author()
        {
            var (list, author, _, _) = ExistingListScenario();
            list.Settings = new GoodreadsBookshelfImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false };
            var foreignAuthor = new Author { Id = 2, GoodreadsAuthorId = "gr:999", Name = "Different author" };
            var requested = new Book { Id = 101, AuthorId = author.Id, Author = author, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition>
                {
                    new() { Id = 301, BookId = 101, ForeignEditionId = "hc:edition:203", ReadingFormatId = 2, Narrator = "Jim Dale" },
                    new() { Id = 302, BookId = 101, ForeignEditionId = "hc:edition:204", ReadingFormatId = 2, Narrator = "Other Narrator" }
                } };
            var foreign = new Book { Id = 102, AuthorId = foreignAuthor.Id, Author = foreignAuthor, HardcoverBookId = "hc:2001", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { new() { Id = 303, BookId = 102, ForeignEditionId = "hc:edition:999", ReadingFormatId = 2, Narrator = "Other Narrator" } } };
            var report = new ImportListItemInfo { ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId, Author = author.Name,
                BookProviderId = "hc:1001", EditionProviderId = "hc-ed:999", EditionHasNarration = true, EditionNarratorNames = new List<string> { "Jim Dale" }, Book = "Requested work" };
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var selected = new List<int>();
            ((BookServiceProxy)books).WantedEditions = (editions, candidates) =>
            {
                selected.AddRange(editions.Select(edition => edition.Id));
                return editions.ToDictionary(edition => edition.Id, edition => candidates.Single(book => book.Id == edition.BookId));
            };
            BuildExistingAuthorService(list, author, new List<Book> { requested, foreign }, new List<ImportListItemInfo> { report }, bookServiceOverride: books)
                .Execute(new ImportListSyncCommand(list.Id));
            Assert.Multiple(() =>
            {
                Assert.That(report.EditionNarratorNames, Is.EqualTo(new[] { "Jim Dale" }));
                Assert.That(selected, Is.EqualTo(new[] { 301 }));
            });
        }


        [Test]
        public void non_audio_exact_must_not_block_a_compatible_audio_cast()
        {
            var (list, author, _, _) = ExistingListScenario();
            list.Settings = new GoodreadsBookshelfImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false };
            var audio = new Edition { Id = 301, BookId = 101, ForeignEditionId = "hc:edition:203", ReadingFormatId = 2, Narrator = "Jim Dale" };
            var print = new Edition { Id = 302, BookId = 101, ForeignEditionId = "hc:edition:999", ReadingFormatId = 1, Monitored = true, ManualAdd = true };
            var book = new Book { Id = 101, AuthorId = author.Id, Author = author, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook,
                AnyEditionOk = false, Editions = new List<Edition> { audio, print } };
            var report = new ImportListItemInfo { ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId, Author = author.Name,
                BookProviderId = "hc:1001", EditionProviderId = "hc-ed:999", EditionHasNarration = true,
                EditionNarratorNames = new List<string> { "Jim Dale" }, Book = "Requested work" };
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var selected = new List<int>();
            ((BookServiceProxy)books).WantedEditions = (editions, candidates) =>
            {
                selected.AddRange(editions.Select(edition => edition.Id));
                return editions.ToDictionary(edition => edition.Id, edition => candidates.Single(candidate => candidate.Id == edition.BookId));
            };
            var pending = new RecordingPendingAuthorImportService();
            BuildExistingAuthorService(list, author, new List<Book> { book }, new List<ImportListItemInfo> { report },
                bookServiceOverride: books, pendingImports: pending).Execute(new ImportListSyncCommand(list.Id));
            Assert.Multiple(() =>
            {
                Assert.That(selected, Is.EqualTo(new[] { audio.Id }));
                Assert.That(pending.Enqueued, Is.Empty);
            });
        }

        [Test]
        public void remote_primary_ownership_should_not_rewrite_another_local_owners_narration()
        {
            var (list, author, _, _) = ExistingListScenario();
            list.Settings = new GoodreadsBookshelfImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false };
            var foreignAuthor = new Author { Id = 2, GoodreadsAuthorId = "gr:999", Name = "Different author" };
            var local = new Book { Id = 101, AuthorId = author.Id, Author = author, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { new() { Id = 301, BookId = 101, ForeignEditionId = "hc:edition:203", ReadingFormatId = 2, Narrator = "Other Narrator" } } };
            var source = new Book { Author = foreignAuthor, HardcoverBookId = "hc:1001", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { new() { ForeignEditionId = "hc:edition:999", ReadingFormatId = 2, Narrator = "Other Narrator" } } };
            var report = new ImportListItemInfo { ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId, Author = author.Name,
                BookProviderId = "hc:1001", EditionProviderId = "hc-ed:999", Book = "Requested work" };
            var metadata = InterfaceProxy.Proxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>((method, args) =>
                Tuple.Create(foreignAuthor.GoodreadsAuthorId, source, new List<Author> { foreignAuthor }));
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var selected = new List<int>();
            ((BookServiceProxy)books).WantedEditions = (editions, candidates) =>
            {
                selected.AddRange(editions.Select(edition => edition.Id));
                return editions.ToDictionary(edition => edition.Id, edition => candidates.Single(book => book.Id == edition.BookId));
            };
            BuildExistingAuthorService(list, author, new List<Book> { local }, new List<ImportListItemInfo> { report },
                bookServiceOverride: books, bookInfoOverride: metadata).Execute(new ImportListSyncCommand(list.Id));
            Assert.Multiple(() =>
            {
                Assert.That(report.AuthorProviderId, Is.EqualTo(foreignAuthor.GoodreadsAuthorId));
                Assert.That(report.EditionHasNarration, Is.True);
                Assert.That(report.EditionNarratorNames, Is.EqualTo(new[] { "Other Narrator" }));
                Assert.That(selected, Is.Empty);
            });
        }

        [TestCase("hc")]
        [TestCase("gr")]
        public void work_and_edition_list_mapping_keys_must_never_collide(string provider)
        {
            var method = typeof(ImportListSyncService).GetMethod("GetMappingSourceProviderId", BindingFlags.NonPublic | BindingFlags.Static);
            var work = method.Invoke(null, new object[] { new ImportListItemInfo { BookProviderId = provider + ":123" } });
            var edition = method.Invoke(null, new object[] { new ImportListItemInfo { EditionProviderId = provider + ":123" } });
            Assert.That(edition, Is.Not.EqualTo(work));
        }

        [Test]
        public void an_unmapped_edition_must_not_be_used_as_a_work_identifier()
        {
            var method = typeof(ImportListSyncService).GetMethod("GetBookProviderId", BindingFlags.NonPublic | BindingFlags.Instance);
            var service = BuildExistingAuthorService(null, null, new List<Book>(), new List<ImportListItemInfo>());
            Assert.That(method.Invoke(service, new object[] { new ImportListItemInfo { EditionProviderId = "gr:123" } }), Is.Null);
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void resolved_non_narrated_source_should_use_the_generic_row_and_reuse_mapping_across_syncs(int readingFormat)
        {
            var (list, author, _, _) = ExistingListScenario();
            list.Settings = new GoodreadsBookshelfImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false };
            var local = new Book { Id = 101, AuthorId = author.Id, Author = author, GoodreadsWorkId = "gr:123", MediaType = BookMediaType.Audiobook };
            var report = new ImportListItemInfo { ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId, Author = author.Name,
                BookProviderId = local.GoodreadsWorkId, Book = "Listed Work", EditionProviderId = "gr:456", HardcoverReadingFormatId = readingFormat == 2 ? 2 : null };
            var source = new Book { GoodreadsWorkId = local.GoodreadsWorkId, Title = report.Book,
                Editions = new List<Edition> { new() { ForeignEditionId = "gr:456", ReadingFormatId = readingFormat } } };
            var calls = 0;
            var metadata = InterfaceProxy.Proxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>((method, args) =>
            {
                calls++;
                return Tuple.Create(author.GoodreadsAuthorId, source, new List<Author> { author });
            });
            var cache = new Dictionary<string, ImportListBookIdentityCache>();
            var batchReads = 0;
            var identities = InterfaceProxy.Proxy<IImportListBookIdentityCacheRepository>((method, args) =>
            {
                if (method.Name == nameof(IImportListBookIdentityCacheRepository.FindBySourceProviderIds))
                {
                    batchReads++;
                    return ((IEnumerable<string>)args[0]).Distinct().Where(cache.ContainsKey).Select(id => cache[id]).ToList();
                }
                if (method.Name == nameof(IImportListBookIdentityCacheRepository.FindBySourceProviderId)) return cache.GetValueOrDefault((string)args[0]);
                if (method.Name == nameof(IImportListBookIdentityCacheRepository.UpsertBySourceProviderId))
                {
                    var mapping = (ImportListBookIdentityCache)args[0];
                    cache[mapping.SourceProviderId] = mapping;
                    return mapping;
                }
                throw new AssertionException(method.Name);
            });
            var pending = new RecordingPendingAuthorImportService();
            var commands = new StubCommandQueueManager();
            var service = BuildExistingAuthorService(list, author, new List<Book> { local }, new List<ImportListItemInfo> { report }, commands,
                pendingImports: pending, bookInfoOverride: metadata, identityCache: identities);
            service.Execute(new ImportListSyncCommand(list.Id));
            report.EditionHasNarration = null;
            service.Execute(new ImportListSyncCommand(list.Id));
            Assert.Multiple(() =>
            {
                Assert.That(local.IsMonitored(), Is.True);
                Assert.That(cache["edition:gr:456"].EditionHasNarration, Is.False);
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(batchReads, Is.EqualTo(2));
                Assert.That(pending.Enqueued, Is.Empty);
                Assert.That(commands.Pushed.OfType<BookSearchCommand>().ToList(), Has.Count.EqualTo(1));
            });
            local.SetMonitored(false);
            report.EditionHasNarration = null;
            service.Execute(new ImportListSyncCommand(list.Id));
            Assert.That(local.IsMonitored(), Is.True);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(commands.Pushed.OfType<BookSearchCommand>().ToList(), Has.Count.EqualTo(2));
            if (readingFormat == 2)
            {
                source.Editions.Single().Narrator = "Newly supplied narrator";
                author.LastInfoSync = cache["edition:gr:456"].UpdatedAt.AddSeconds(1);
                report.EditionHasNarration = null;
                service.Execute(new ImportListSyncCommand(list.Id));
                Assert.That(calls, Is.EqualTo(2));
                Assert.That(cache["edition:gr:456"].EditionHasNarration, Is.True);
                Assert.That(pending.Enqueued.Single().Config.AudiobookNarrations.Single().RequiresEdition, Is.True);
                Assert.That(commands.Pushed.OfType<BookSearchCommand>().ToList(), Has.Count.EqualTo(2));
            }
        }

        [Test]
        public void unresolved_source_should_monitor_generic_once_and_honor_later_narration_evidence()
        {
            var (list, author, _, _) = ExistingListScenario();
            list.Settings = new GoodreadsBookshelfImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false };
            var local = new Book { Id = 101, AuthorId = author.Id, Author = author, GoodreadsWorkId = "gr:123", MediaType = BookMediaType.Audiobook };
            var report = new ImportListItemInfo { ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId,
                BookProviderId = local.GoodreadsWorkId, EditionProviderId = "gr:456", HardcoverReadingFormatId = 2, Book = "Listed Work" };
            var source = new Book { GoodreadsWorkId = "gr:123", Editions = new List<Edition>() };
            var calls = 0;
            var metadata = InterfaceProxy.Proxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>((method, args) =>
            {
                calls++;
                return Tuple.Create(author.GoodreadsAuthorId, source, new List<Author> { author });
            });
            var pending = new RecordingPendingAuthorImportService();
            var commands = new StubCommandQueueManager();
            var service = BuildExistingAuthorService(list, author, new List<Book> { local }, new List<ImportListItemInfo> { report },
                commands, pendingImports: pending, bookInfoOverride: metadata);
            service.Execute(new ImportListSyncCommand(list.Id));
            service.Execute(new ImportListSyncCommand(list.Id));
            Assert.That(local.IsMonitored(), Is.True);
            Assert.That(pending.Enqueued, Is.Empty);
            Assert.That(commands.Pushed.OfType<BookSearchCommand>().ToList(), Has.Count.EqualTo(1));
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(report.EditionHasNarration, Is.Null);

            source.Editions.Add(new Edition { ForeignEditionId = "gr:456", ReadingFormatId = 2, Narrator = "Requested Narrator" });
            service.Execute(new ImportListSyncCommand(list.Id));
            Assert.That(report.EditionHasNarration, Is.True);
            Assert.That(pending.Enqueued.Single().Config.AudiobookNarrations.Single().EditionProviderId, Is.EqualTo("gr:456"));
            Assert.That(commands.Pushed.OfType<BookSearchCommand>().ToList(), Has.Count.EqualTo(1));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void source_classification_should_require_the_registered_work_alias_not_an_edition_number(bool sameWork)
        {
            var (list, author, _, _) = ExistingListScenario();
            list.Settings = new GoodreadsBookshelfImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false };
            var local = new Book { Id = 101, AuthorId = author.Id, Author = author, GoodreadsWorkId = "gr:123", MediaType = BookMediaType.Audiobook };
            var report = new ImportListItemInfo { ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId,
                BookProviderId = "gr:789", EditionProviderId = "gr:456", Book = "Listed Work" };
            var source = new Book { GoodreadsWorkId = sameWork ? "gr:123" : "gr:456",
                Editions = new List<Edition> { new() { ForeignEditionId = "gr:456", ReadingFormatId = 2, Narrator = "Requested Narrator" } } };
            var metadata = InterfaceProxy.Proxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>((method, args) => Tuple.Create(author.GoodreadsAuthorId, source, new List<Author> { author }));
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)books).AliasProviderId = "gr:789";
            var pending = new RecordingPendingAuthorImportService();
            BuildExistingAuthorService(list, author, new List<Book> { local }, new List<ImportListItemInfo> { report },
                pendingImports: pending, bookInfoOverride: metadata, bookServiceOverride: books).Execute(new ImportListSyncCommand(list.Id));
            Assert.That(report.EditionHasNarration, Is.EqualTo(sameWork ? true : (bool?)null));
            Assert.That(local.IsMonitored(), Is.EqualTo(!sameWork));
            Assert.That(pending.Enqueued.Count, Is.EqualTo(sameWork ? 1 : 0));
            Assert.That(((BookServiceProxy)books).BatchLookupCount, Is.EqualTo(1));
            Assert.That(((BookServiceProxy)books).ProviderLookupCount, Is.Zero);
        }

        [Test]
        public void repeated_source_editions_should_share_one_metadata_lookup_in_a_mapping_cohort()
        {
            var (list, author, _, _) = ExistingListScenario();
            list.Settings = new GoodreadsBookshelfImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false };
            var local = new Book { Id = 101, AuthorId = author.Id, Author = author, GoodreadsWorkId = "gr:123", MediaType = BookMediaType.Audiobook };
            var source = new Book { GoodreadsWorkId = "gr:123", Editions = new List<Edition> { new() { ForeignEditionId = "gr:456", ReadingFormatId = 1 } } };
            var calls = 0;
            var metadata = InterfaceProxy.Proxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>((method, args) =>
            {
                System.Threading.Interlocked.Increment(ref calls);
                return Tuple.Create(author.GoodreadsAuthorId, source, new List<Author> { author });
            });
            var reports = Enumerable.Range(0, 100).Select(_ => new ImportListItemInfo { ImportListId = list.Id,
                AuthorProviderId = author.GoodreadsAuthorId, BookProviderId = "gr:123", EditionProviderId = "gr:456", Book = "Listed Work" }).ToList();
            BuildExistingAuthorService(list, author, new List<Book> { local }, reports, bookInfoOverride: metadata).Execute(new ImportListSyncCommand(list.Id));
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(reports.All(report => report.EditionHasNarration == false), Is.True);
            Assert.That(local.IsMonitored(), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void cached_source_narration_should_not_cross_a_different_parent_identity(bool mismatchedAuthor)
        {
            var (list, author, _, _) = ExistingListScenario();
            list.Settings = new GoodreadsBookshelfImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false };
            var local = new Book { Id = 101, AuthorId = author.Id, Author = author, GoodreadsWorkId = "gr:123", MediaType = BookMediaType.Audiobook };
            var report = new ImportListItemInfo { ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId,
                BookProviderId = "gr:123", EditionProviderId = "gr:456", Book = "Listed Work" };
            var cached = new ImportListBookIdentityCache { SourceProviderId = "edition:gr:456", EditionHasNarration = true,
                BookProviderId = mismatchedAuthor ? "gr:123" : "gr:999", AuthorProviderId = mismatchedAuthor ? "gr:987654" : author.GoodreadsAuthorId };
            var identities = InterfaceProxy.Proxy<IImportListBookIdentityCacheRepository>((method, args) =>
                method.Name == nameof(IImportListBookIdentityCacheRepository.FindBySourceProviderIds) ? new List<ImportListBookIdentityCache> { cached } : args[0]);
            var calls = 0;
            var metadata = InterfaceProxy.Proxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>((method, args) =>
            {
                calls++;
                return Tuple.Create(author.GoodreadsAuthorId, new Book { GoodreadsWorkId = "gr:123" }, new List<Author> { author });
            });
            var pending = new RecordingPendingAuthorImportService();
            BuildExistingAuthorService(list, author, new List<Book> { local }, new List<ImportListItemInfo> { report },
                pendingImports: pending, bookInfoOverride: metadata, identityCache: identities).Execute(new ImportListSyncCommand(list.Id));
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(report.EditionHasNarration, Is.Null);
            Assert.That(local.IsMonitored(), Is.True);
            Assert.That(pending.Enqueued, Is.Empty);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void stale_goodreads_work_should_resolve_via_the_reported_edition_to_the_existing_canonical_media_row(bool sameAuthor)
        {
            var (list, author, _, _) = ExistingListScenario();
            var local = new Book { Id = 101, AuthorId = author.Id, Author = author, Title = "Red Rising",
                HardcoverBookId = "hc:427473", GoodreadsWorkId = "gr:165789509", MediaType = BookMediaType.Ebook };
            var report = new ImportListItemInfo { ImportListId = list.Id, AuthorProviderId = sameAuthor ? author.GoodreadsAuthorId : "gr:987654",
                BookProviderId = "gr:21580644", EditionProviderId = "gr:15839976", Book = local.Title };
            var source = new Book { HardcoverBookId = "hc:427473", GoodreadsWorkId = "gr:165789509", Title = local.Title,
                Editions = new List<Edition> { new() { ForeignEditionId = "gr:15839976", ReadingFormatId = 4 } } };
            var calls = 0;
            var metadata = InterfaceProxy.Proxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>((method, args) =>
            {
                calls++;
                return Tuple.Create(author.GoodreadsAuthorId, source, new List<Author> { author });
            });
            ImportListBookIdentityCache mapping = null;
            var identities = InterfaceProxy.Proxy<IImportListBookIdentityCacheRepository>((method, args) =>
            {
                if (method.Name == nameof(IImportListBookIdentityCacheRepository.FindBySourceProviderIds))
                    return mapping == null ? new List<ImportListBookIdentityCache>() : new List<ImportListBookIdentityCache> { mapping };
                mapping = (ImportListBookIdentityCache)args[0];
                return mapping;
            });
            var queue = new StubCommandQueueManager();
            var pending = new RecordingPendingAuthorImportService();
            var service = BuildExistingAuthorService(list, author, new List<Book> { local }, new List<ImportListItemInfo> { report }, queue,
                bookInfoOverride: metadata, identityCache: identities, pendingImports: pending);
            service.Execute(new ImportListSyncCommand(list.Id));
            Assert.That(report.BookProviderId, Is.EqualTo(sameAuthor ? "hc:427473" : "gr:21580644"));
            Assert.That(local.EbookMonitored, Is.EqualTo(sameAuthor));
            Assert.That(local.GoodreadsWorkId, Is.EqualTo("gr:165789509"));
            Assert.That(queue.Pushed.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(sameAuthor ? new[] { local.Id } : Array.Empty<int>()));
            if (sameAuthor)
            {
                report.BookProviderId = "gr:21580644";
                report.EditionHasNarration = null;
                service.Execute(new ImportListSyncCommand(list.Id));
                Assert.That(report.BookProviderId, Is.EqualTo("hc:427473"));
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(pending.Enqueued, Is.Empty);
                Assert.That(queue.Pushed.OfType<BookSearchCommand>().ToList(), Has.Count.EqualTo(1));
            }
        }

        [Test]
        public void rejected_automatic_batch_should_retain_all_requested_books_including_paused_authors()
        {
            var (list, author, books, reports) = ExistingListScenario();
            author.EbookMonitored = true;
            var pausedAuthor = new Author { Id = 2, GoodreadsAuthorId = "gr:987654", EbookMonitored = false };
            var tail = new Book { Id = 101, AuthorId = pausedAuthor.Id, Author = pausedAuthor, GoodreadsWorkId = "gr:345", MediaType = BookMediaType.Ebook };
            books.Add(tail);
            reports.Add(new ImportListItemInfo { ImportListId = list.Id, AuthorProviderId = pausedAuthor.GoodreadsAuthorId, BookProviderId = tail.GoodreadsWorkId, Book = "Tail Work" });
            var commands = new StubCommandQueueManager { RejectSearchNumber = 1 };
            var authors = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)authors).AdditionalAuthors.Add(pausedAuthor);
            var service = BuildExistingAuthorService(list, author, books, reports, commands, authorServiceOverride: authors);
            Assert.DoesNotThrow(() => service.Execute(new ImportListSyncCommand(list.Id)));
            Assert.That(books.Take(2).All(book => book.AddOptions.SearchForNewBook), Is.True);
            Assert.That(commands.Pushed.OfType<BookSearchCommand>(), Is.Empty);
            Assert.That(tail.AddOptions.SearchForNewBook, Is.True);
            service.Execute(new ImportListSyncCommand(list.Id));
            Assert.That(commands.Pushed.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { 1, 2, tail.Id }));
        }

        [Test]
        public void new_author_list_configs_should_resolve_a_shared_root_once_per_sync()
        {
            var (list, _, _, _) = ExistingListScenario();
            ((DualMediaImportListSettings)list.Settings).EbookRootFolderPath = @"C:\Library".AsOsAgnostic();
            list.MonitoringOptions.AddMissingAuthors = true;
            var reports = Enumerable.Range(0, 100).Select(index => new ImportListItemInfo
            {
                ImportListId = list.Id, AuthorProviderId = "gr:" + (9000 + index), BookProviderId = "gr:" + (7000 + index), Book = "Listed Work"
            }).ToList();
            var roots = DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>();
            var pending = new RecordingPendingAuthorImportService();
            BuildExistingAuthorService(list, null, new List<Book>(), reports, pendingImports: pending, roots: roots, bookInfoOverride: AttestedMetadata(reports)).Execute(new ImportListSyncCommand(list.Id));
            Assert.That(pending.Enqueued, Has.Count.EqualTo(100));
            Assert.That(((RootFolderServiceProxy)roots).LookupCount, Is.EqualTo(1));
        }

        [Test]
        public void retargeting_a_monitored_narration_should_search_once_and_a_compatible_repeat_should_not_search()
        {
            var (list, author, _, _) = ExistingListScenario();
            list.Settings = new GoodreadsBookshelfImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false };
            var previous = new Edition { Id = 301, BookId = 101, ForeignEditionId = "gr:455", ReadingFormatId = 2, Narrator = "Previous Narrator", Monitored = true };
            var requested = new Edition { Id = 302, BookId = 101, ForeignEditionId = "gr:456", ReadingFormatId = 2, Narrator = "Requested Narrator" };
            var local = new Book { Id = 101, AuthorId = author.Id, Author = author, GoodreadsWorkId = "gr:123", MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = true, Editions = new List<Edition> { previous, requested } };
            var report = new ImportListItemInfo { ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId,
                BookProviderId = "gr:123", EditionProviderId = "gr:456", Book = "Listed Work" };
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)books).WantedEditions = (_, _) =>
            {
                previous.Monitored = false;
                requested.Monitored = true;
                return new Dictionary<int, Book> { [requested.Id] = local };
            };
            var commands = new StubCommandQueueManager();
            var service = BuildExistingAuthorService(list, author, new List<Book> { local }, new List<ImportListItemInfo> { report }, commands, bookServiceOverride: books);
            service.Execute(new ImportListSyncCommand(list.Id));
            service.Execute(new ImportListSyncCommand(list.Id));
            Assert.That(commands.Pushed.OfType<BookSearchCommand>().Single().BookIds, Is.EqualTo(new[] { local.Id }));
            Assert.That(requested.Monitored, Is.True);
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        public void actual_positive_source_or_local_narration_should_override_a_false_snapshot(bool sourcePositive, bool localPositive)
        {
            var (list, author, _, _) = ExistingListScenario();
            list.Settings = new GoodreadsBookshelfImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false };
            var selected = new Edition { Id = 301, BookId = 101, ForeignEditionId = "gr:456", ReadingFormatId = 2,
                Narrator = localPositive ? "Requested Narrator" : null };
            var local = new Book { Id = 101, AuthorId = author.Id, Author = author, GoodreadsWorkId = "gr:123", MediaType = BookMediaType.Audiobook,
                Editions = new List<Edition> { selected } };
            var report = new ImportListItemInfo { ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId,
                BookProviderId = "gr:123", EditionProviderId = "gr:456", EditionHasNarration = sourcePositive, Book = "Listed Work" };
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var wanted = 0;
            ((BookServiceProxy)books).WantedEditions = (_, _) => { wanted++; return new Dictionary<int, Book> { [selected.Id] = local }; };
            var pending = new RecordingPendingAuthorImportService();
            BuildExistingAuthorService(list, author, new List<Book> { local }, new List<ImportListItemInfo> { report },
                bookServiceOverride: books, pendingImports: pending).Execute(new ImportListSyncCommand(list.Id));
            Assert.That(report.EditionHasNarration, Is.True);
            Assert.That(wanted, Is.EqualTo(localPositive ? 1 : 0));
            Assert.That(pending.Enqueued.Count, Is.EqualTo(localPositive ? 0 : 1));
            Assert.That(local.IsMonitored(), Is.EqualTo(localPositive));
        }

        [TestCase(BookMediaType.Audiobook, false, false)]
        [TestCase(BookMediaType.Audiobook, false, true)]
        [TestCase(BookMediaType.Audiobook, true, false)]
        [TestCase(BookMediaType.Audiobook, true, true)]
        [TestCase(BookMediaType.Ebook, false, false)]
        [TestCase(BookMediaType.Ebook, false, true)]
        [TestCase(BookMediaType.Ebook, true, false)]
        [TestCase(BookMediaType.Ebook, true, true)]
        [TestCase(BookMediaType.Audiobook, false, true, true)]
        [TestCase(BookMediaType.Audiobook, true, false, true)]
        public void missing_author_media_side_should_follow_add_missing_formats_independently_of_existing_monitoring(
            BookMediaType side, bool addMissing, bool monitorExisting, bool narration = false)
        {
            var (list, author, _, _) = ExistingListScenario();
            list.MonitoringOptions.AddMissingAuthors = addMissing;
            list.ShouldMonitorExisting = monitorExisting;
            list.Settings = new GoodreadsBookshelfImportListSettings
            {
                MonitorAudiobooks = side == BookMediaType.Audiobook, MonitorEbooks = side == BookMediaType.Ebook,
                AudiobookRootFolderPath = @"C:\Audio", EbookRootFolderPath = @"C:\Ebooks",
                AudiobookQualityProfileId = 3, AudiobookMetadataProfileId = 4, EbookQualityProfileId = 5, EbookMetadataProfileId = 6
            };
            var other = new Book { Id = 101, AuthorId = author.Id, Author = author, GoodreadsWorkId = "gr:123",
                MediaType = side == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook };
            var report = new ImportListItemInfo { ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId, Author = author.Name,
                BookProviderId = other.GoodreadsWorkId, Book = "Listed Work", EditionProviderId = narration ? "gr:223" : null,
                EditionHasNarration = narration ? true : null, EditionNarratorNames = narration ? new() { "Requested Narrator" } : null };
            var pending = new RecordingPendingAuthorImportService();
            BuildExistingAuthorService(list, author, new List<Book> { other }, new List<ImportListItemInfo> { report }, pendingImports: pending, bookInfoOverride: AttestedMetadata(new[] { report }))
                .Execute(new ImportListSyncCommand(list.Id));
            Assert.That(pending.Enqueued, Has.Count.EqualTo(addMissing ? 1 : 0));
            if (!addMissing)
            {
                return;
            }

            var config = pending.Enqueued.Single().Config;
            Assert.That(config.CreateAudiobook, Is.EqualTo(side == BookMediaType.Audiobook));
            Assert.That(config.CreateEbook, Is.EqualTo(side == BookMediaType.Ebook));
            Assert.That(side == BookMediaType.Audiobook ? config.AudiobookRootFolderPath : config.EbookRootFolderPath,
                Is.EqualTo(side == BookMediaType.Audiobook ? @"C:\Audio" : @"C:\Ebooks"));
            Assert.That(side == BookMediaType.Audiobook ? config.AudiobookQualityProfileId : config.EbookQualityProfileId,
                Is.EqualTo(side == BookMediaType.Audiobook ? 3 : 5));
            Assert.That(side == BookMediaType.Audiobook ? config.AudiobookMetadataProfileId : config.EbookMetadataProfileId,
                Is.EqualTo(side == BookMediaType.Audiobook ? 4 : 6));
            Assert.That(side == BookMediaType.Audiobook ? config.AudiobookBooksToMonitor : config.EbookBooksToMonitor, Is.EqualTo(new[] { "gr:123" }));
        }

        [TestCase(BookMediaType.Audiobook, false)]
        [TestCase(BookMediaType.Audiobook, true)]
        [TestCase(BookMediaType.Ebook, false)]
        [TestCase(BookMediaType.Ebook, true)]
        public void configured_side_missing_catalog_should_remain_allowed_with_add_off(BookMediaType side, bool existingBooks)
        {
            var (list, author, _, _) = ExistingListScenario();
            list.Settings = new GoodreadsBookshelfImportListSettings
            {
                MonitorAudiobooks = side == BookMediaType.Audiobook,
                MonitorEbooks = side == BookMediaType.Ebook
            };
            if (!existingBooks)
            {
                if (side == BookMediaType.Audiobook)
                {
                    author.AudiobookQualityProfileId = 9;
                    author.AudiobookMetadataProfileId = 8;
                }
                else
                {
                    author.EbookQualityProfileId = 9;
                    author.EbookMetadataProfileId = 8;
                }
            }

            var other = new Book { Id = 101, AuthorId = author.Id, Author = author, GoodreadsWorkId = "gr:999",
                MediaType = existingBooks ? side : side == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook };
            var report = new ImportListItemInfo { ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId, Author = author.Name,
                BookProviderId = "gr:123", Book = "Missing Listed Work" };
            var pending = new RecordingPendingAuthorImportService();
            BuildExistingAuthorService(list, author, new List<Book> { other }, new List<ImportListItemInfo> { report },
                pendingImports: pending, bookInfoOverride: AttestedMetadata(new[] { report })).Execute(new ImportListSyncCommand(list.Id));
            Assert.That(pending.Enqueued, Has.Count.EqualTo(1));
            var config = pending.Enqueued.Single().Config;
            Assert.That(config.CreateAudiobook, Is.EqualTo(side == BookMediaType.Audiobook));
            Assert.That(config.CreateEbook, Is.EqualTo(side == BookMediaType.Ebook));
            Assert.That(side == BookMediaType.Audiobook ? config.AudiobookBooksToMonitor : config.EbookBooksToMonitor,
                Is.EqualTo(new[] { "gr:123" }));
        }

        [Test]
        public void skipped_missing_author_count_should_include_cached_misses_without_repeating_lookups()
        {
            var (list, author, _, _) = ExistingListScenario();
            var reports = new List<ImportListItemInfo>
            {
                new() { ImportListId = list.Id, AuthorProviderId = "gr:999999", Author = "Missing Author" },
                new() { ImportListId = list.Id, AuthorProviderId = "gr:999999", Author = "Repeated Missing Author" },
                new() { ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId, Author = author.Name }
            };
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var pending = new RecordingPendingAuthorImportService();
            using var logs = new LogFactory();
            var memory = new NLog.Targets.MemoryTarget { Layout = "${message}" };
            var logging = new NLog.Config.LoggingConfiguration(logs);
            logging.AddRule(LogLevel.Info, LogLevel.Fatal, memory);
            logs.Configuration = logging;

            BuildExistingAuthorService(list, author, new List<Book>(), reports,
                pendingImports: pending, authorServiceOverride: authorService,
                logger: logs.GetLogger("skipped-missing-author-count"))
                .Execute(new ImportListSyncCommand(list.Id));

            Assert.Multiple(() =>
            {
                Assert.That(memory.Logs.Where(message => message.Contains("missing authors because adding is off")),
                    Is.EqualTo(new[] { $"Import list {list.Name}: skipped 1 missing authors because adding is off" }));
                Assert.That(((AuthorServiceProxy)(object)authorService).ProviderLookupCount, Is.EqualTo(1),
                    "the cached missing author is reused, and the local existing author needs no live lookup");
                Assert.That(pending.Enqueued, Is.Empty);
                Assert.That(author.EbookMonitored, Is.False);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void missing_authors_should_remain_absent_with_add_off(bool narration)
        {
            var (list, author, _, _) = ExistingListScenario();
            list.Settings = new GoodreadsBookshelfImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false };
            var report = new ImportListItemInfo { ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId, Author = author.Name,
                BookProviderId = "gr:123", Book = "Missing Listed Work", EditionProviderId = narration ? "gr:223" : null,
                EditionHasNarration = narration ? true : null, EditionNarratorNames = narration ? new() { "Requested Narrator" } : null };
            var pending = new RecordingPendingAuthorImportService();
            var commands = new StubCommandQueueManager();
            BuildExistingAuthorService(list, null, new List<Book>(), new List<ImportListItemInfo> { report }, commands,
                pendingImports: pending, bookInfoOverride: AttestedMetadata(new[] { report })).Execute(new ImportListSyncCommand(list.Id));
            Assert.That(pending.Enqueued, Is.Empty);
            Assert.That(commands.Pushed, Is.Empty);
        }

        [Test]
        public void missing_generic_and_narrated_requests_should_share_one_catalog_queue_without_inventing_edition_intent()
        {
            var (list, author, _, _) = ExistingListScenario();
            list.Settings = new GoodreadsBookshelfImportListSettings { MonitorAudiobooks = true, MonitorEbooks = true };
            author.AudiobookQualityProfileId = author.EbookQualityProfileId = 9;
            author.AudiobookMetadataProfileId = author.EbookMetadataProfileId = 8;
            var local = new List<Book>
            {
                new() { Id = 101, AuthorId = author.Id, Author = author, GoodreadsWorkId = "gr:999", MediaType = BookMediaType.Audiobook },
                new() { Id = 102, AuthorId = author.Id, Author = author, GoodreadsWorkId = "gr:999", MediaType = BookMediaType.Ebook }
            };
            var reports = new List<ImportListItemInfo>
            {
                new() { ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId, BookProviderId = "gr:123", EditionProviderId = "gr:223", Book = "Automatic source", IsGenericBookRequest = true },
                new() { ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId, BookProviderId = "gr:124", EditionProviderId = "gr:224", Book = "Printed source", EditionHasNarration = false },
                new() { ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId, BookProviderId = "gr:125", EditionProviderId = "gr:225", Book = "Narrated source", EditionHasNarration = true, EditionNarratorNames = new() { "Fixture Narrator" } },
                new() { ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId, BookProviderId = "gr:126", EditionProviderId = "gr:226", Book = "Deferred source" }
            };
            var metadata = AttestedMetadata(reports);
            var pending = new RecordingPendingAuthorImportService();
            BuildExistingAuthorService(list, author, local, reports, pendingImports: pending, bookInfoOverride: metadata)
                .Execute(new ImportListSyncCommand(list.Id));
            Assert.That(pending.Enqueued, Has.Count.EqualTo(1));
            var config = pending.Enqueued.Single().Config;
            Assert.Multiple(() =>
            {
                Assert.That(config.AudiobookBooksToMonitor ?? new List<string>(), Is.EquivalentTo(new[] { "gr:123", "gr:124", "gr:125", "gr:126" }));
                Assert.That(config.EbookBooksToMonitor ?? new List<string>(), Is.EquivalentTo(new[] { "gr:123", "gr:124", "gr:125", "gr:126" }));
                Assert.That(config.AudiobookNarrations.Select(request => request.WorkProviderId), Is.EquivalentTo(new[] { "gr:125" }));
                Assert.That(config.AudiobookNarrations.Single(request => request.WorkProviderId == "gr:125").RequiresEdition, Is.True);
                Assert.That(config.AudiobookNarrations.SingleOrDefault(request => request.WorkProviderId == "gr:126"), Is.Null);
                Assert.That(config.AudiobookBooksToSearch ?? new List<string>(), Is.EquivalentTo(config.AudiobookBooksToMonitor ?? new List<string>()));
                Assert.That(config.EbookBooksToSearch ?? new List<string>(), Is.EquivalentTo(config.EbookBooksToMonitor ?? new List<string>()));
                Assert.That(config.AudiobookMonitorExistingMode, Is.Null);
                Assert.That(config.EbookMonitorExistingMode, Is.Null);
                Assert.That(config.SearchForMissingBooks, Is.False);
            });
        }

        internal static ImportListSyncService BuildMissingMediaSideService(Author author, Book other, BookMediaType side,
            bool monitored, IAuthorService authors, IBookService books, IEditionService editions,
            IPendingAuthorImportService pending, IRootFolderService roots, IManageCommandQueue queue, bool bothSides = false, bool newAuthor = false,
            List<ImportListItemInfo> reports = null, bool keep = true, List<ImportListExclusion> exclusions = null, bool search = true,
            Action<string, BookMediaType> metadataLookup = null, Action<ImportListDefinition> configureList = null, bool addMissingAuthors = true, Logger logger = null)
        {
            var (list, _, _, _) = ExistingListScenario();
            list.MonitoringOptions.AddMissingAuthors = addMissingAuthors;
            list.ShouldMonitorExisting = keep;
            list.ShouldSearch = search;
            list.MonitoringOptions.AudiobookMonitored = monitored;
            list.MonitoringOptions.EbookMonitored = monitored;
            list.MonitoringOptions.AudiobookMonitorNewItems = NewItemMonitorTypes.New;
            list.MonitoringOptions.EbookMonitorNewItems = NewItemMonitorTypes.New;
            list.Settings = new GoodreadsBookshelfImportListSettings
            {
                MonitorAudiobooks = bothSides || side == BookMediaType.Audiobook, MonitorEbooks = bothSides || side == BookMediaType.Ebook,
                AudiobookRootFolderPath = @"C:\Audio".AsOsAgnostic(), EbookRootFolderPath = @"C:\Ebooks".AsOsAgnostic(),
                AudiobookQualityProfileId = 3, AudiobookMetadataProfileId = 4, EbookQualityProfileId = 5, EbookMetadataProfileId = 6
            };
            configureList?.Invoke(list);
            var factory = DispatchProxy.Create<IImportListFactory, ImportListFactoryProxy>();
            ((ImportListFactoryProxy)(object)factory).Definition = list;
            var items = reports ?? new List<ImportListItemInfo> { new()
            {
                ImportListId = list.Id, AuthorProviderId = author.GoodreadsAuthorId, Author = author.Name,
                BookProviderId = other.GoodreadsWorkId, Book = other.Title
            } };
            var metadata = AttestedMetadata(items);
            var bookInfo = InterfaceProxy.Proxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>((method, args) =>
            {
                metadataLookup?.Invoke((string)args[0], (BookMediaType)args[1]);
                Assert.That(args[1], Is.EqualTo(bothSides ? BookMediaType.Audiobook : side));
                return method.Invoke(metadata, args);
            });
            return new ImportListSyncService(factory, new StubImportListExclusionService { Exclusions = exclusions ?? new() },
                new StubFetchAndParseImportList { Items = items }, bookInfo,
                authors, books, editions, pending, roots,
                new RootFolderSettingsResolver(roots),
                new StubEventAggregator(), queue, logger ?? LogManager.GetCurrentClassLogger(),
                DispatchProxy.Create<IImportListBookIdentityCacheRepository, ThrowingProxy<IImportListBookIdentityCacheRepository>>());
        }

        [Test]
        public void completely_excluded_mapped_work_should_not_queue_an_author_or_search()
        {
            var (list, _, _, _) = ExistingListScenario();
            list.MonitoringOptions.AddMissingAuthors = true;
            list.Settings = new GoodreadsBookshelfImportListSettings { MonitorAudiobooks = true, MonitorEbooks = false, AudiobookRootFolderPath = @"C:\Audio" };
            var author = new Author { GoodreadsAuthorId = "gr:789", Name = "Mapped Author" };
            var remote = new Book { GoodreadsWorkId = "gr:123", Title = "Mapped Work",
                Editions = new List<Edition> { new() { ForeignEditionId = "gr:456", ReadingFormatId = 4 } } };
            var report = new ImportListItemInfo { ImportListId = list.Id, EditionProviderId = "gr:456", Book = remote.Title };
            var metadata = InterfaceProxy.Proxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>((method, args) => Tuple.Create(author.GoodreadsAuthorId, remote, new List<Author> { author }));
            var pending = new RecordingPendingAuthorImportService();
            var commands = new StubCommandQueueManager();
            BuildExistingAuthorService(list, null, new List<Book>(), new List<ImportListItemInfo> { report }, commands,
                pendingImports: pending, bookInfoOverride: metadata,
                exclusions: new List<ImportListExclusion> { new() { ForeignId = "gr:123", MediaType = BookMediaType.Audiobook } })
                .Execute(new ImportListSyncCommand(list.Id));
            Assert.That(report.BookProviderId, Is.EqualTo(remote.GoodreadsWorkId));
            Assert.That(report.AuthorProviderId, Is.EqualTo(author.GoodreadsAuthorId));
            Assert.That(pending.Enqueued, Is.Empty);
            Assert.That(commands.Pushed, Is.Empty);
        }

        [TestCase(false, BookMediaType.Audiobook, false, false)]
        [TestCase(false, BookMediaType.Ebook, true, false)]
        [TestCase(false, BookMediaType.Ebook, false, false)]
        [TestCase(true, BookMediaType.Audiobook, false, false)]
        [TestCase(true, BookMediaType.Ebook, true, false)]
        [TestCase(true, BookMediaType.Ebook, false, true)]
        public void primary_routing_should_verify_one_source_for_the_complete_contributor_cohort(bool editionSource, BookMediaType side, bool staleCache, bool conflictingLeader)
        {
            var (list, _, _, _) = ExistingListScenario();
            list.MonitoringOptions.AddMissingAuthors = true;
            list.Settings = new GoodreadsBookshelfImportListSettings
            {
                MonitorAudiobooks = side == BookMediaType.Audiobook,
                MonitorEbooks = side == BookMediaType.Ebook,
                AudiobookRootFolderPath = side == BookMediaType.Audiobook ? @"C:\Books".AsOsAgnostic() : null,
                EbookRootFolderPath = side == BookMediaType.Ebook ? @"C:\Books".AsOsAgnostic() : null
            };
            var primary = new Author { HardcoverAuthorId = "hc:1111", Name = "Selected Primary" };
            var source = new Book { HardcoverBookId = "hc:3333", Title = "Requested Work",
                Author = !editionSource && side == BookMediaType.Ebook && !staleCache ? new Author() : primary,
                MediaType = side, Editions = new List<Edition> { new() { HardcoverEditionId = "hc:4444",
                    ReadingFormatId = side == BookMediaType.Audiobook ? 2 : 4,
                    Narrator = side == BookMediaType.Audiobook ? "Requested Narrator" : null } } };
            var reports = Enumerable.Range(0, 21).Select(index => new ImportListItemInfo
            {
                ImportListId = list.Id, Book = source.Title, Author = "Supplied Contributor",
                BookProviderId = "hc:3333", AuthorProviderId = index % 2 == 0 ? "hc:2222" : "hc:5555",
                EditionProviderId = editionSource ? "hc-ed:4444" : null
            }).ToList();
            if (editionSource && side == BookMediaType.Audiobook)
            {
                reports[1].EditionHasNarration = false;
            }
            if (editionSource)
            {
                reports.Add(new ImportListItemInfo { ImportListId = list.Id, Book = "Different Work",
                    BookProviderId = "hc:9999", AuthorProviderId = "hc:2222", EditionProviderId = "hc-ed:4444" });
            }
            if (conflictingLeader)
            {
                reports.Insert(0, reports[^1]);
                reports.RemoveAt(reports.Count - 1);
            }
            var calls = 0;
            var metadata = InterfaceProxy.Proxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>((method, args) =>
            {
                System.Threading.Interlocked.Increment(ref calls);
                Assert.That(method.Name, Is.EqualTo(editionSource ? "GetEditionInfo" : "GetWorkInfo"));
                Assert.That(args[1], Is.EqualTo(side));
                return Tuple.Create("hc:1111", source, new List<Author> { primary });
            });
            var cache = staleCache ? new ImportListBookIdentityCache
            {
                SourceProviderId = editionSource ? "edition:hc:4444" : "work:hc:3333", BookProviderId = "hc:3333",
                AuthorProviderId = "hc:2222", Author = "Supplied Contributor", EditionHasNarration = false
            } : null;
            var reads = 0;
            var upserts = 0;
            var identities = InterfaceProxy.Proxy<IImportListBookIdentityCacheRepository>((method, args) =>
            {
                if (method.Name == nameof(IImportListBookIdentityCacheRepository.FindBySourceProviderIds))
                {
                    reads++;
                    return cache == null ? new List<ImportListBookIdentityCache>() : new List<ImportListBookIdentityCache> { cache };
                }
                Assert.That(method.Name, Is.EqualTo(nameof(IImportListBookIdentityCacheRepository.UpsertBySourceProviderId)));
                upserts++;
                cache = (ImportListBookIdentityCache)args[0];
                return cache;
            });
            var authors = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var books = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var pending = new RecordingPendingAuthorImportService();
            BuildExistingAuthorService(list, null, new List<Book>(), reports, pendingImports: pending,
                bookInfoOverride: metadata, identityCache: identities, authorServiceOverride: authors,
                bookServiceOverride: books).Execute(new ImportListSyncCommand(list.Id));
            Assert.Multiple(() =>
            {
                Assert.That(reports.Where(report => report.BookProviderId == "hc:3333").Select(report => report.AuthorProviderId), Is.All.EqualTo("hc:1111"));
                Assert.That(reports.Where(report => report.BookProviderId == "hc:3333").Select(report => report.Author), Is.All.EqualTo(primary.Name));
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(reads, Is.EqualTo(1));
                Assert.That(upserts, Is.EqualTo(1));
                if (editionSource && side == BookMediaType.Audiobook)
                {
                    Assert.That(reports.Where(report => report.BookProviderId == "hc:3333").Select(report => report.EditionHasNarration), Is.All.True);
                    Assert.That(reports.Where(report => report.BookProviderId == "hc:3333").All(report => report.EditionNarratorNames.SequenceEqual(new[] { "Requested Narrator" })), Is.True);
                }
                Assert.That(cache?.AuthorProviderId, Is.EqualTo("hc:1111"));
                Assert.That(pending.Enqueued.Select(item => item.ProviderId), Is.EqualTo(new[] { "hc:1111" }));
                Assert.That(((BookServiceProxy)books).CatalogReadCount, Is.Zero);
                Assert.That(((BookServiceProxy)books).BatchLookupCount, Is.EqualTo(1));
                Assert.That(((AuthorServiceProxy)authors).ProviderLookupCount, Is.EqualTo(1));
                if (editionSource)
                {
                    var conflicting = reports.Single(report => report.BookProviderId == "hc:9999");
                    Assert.That(conflicting.AuthorProviderId, Is.Null);
                    Assert.That(conflicting.EditionHasNarration, Is.Null);
                }
            });
            var config = pending.Enqueued.Single().Config;
            Assert.That(side == BookMediaType.Audiobook ? config.AudiobookBooksToMonitor : config.EbookBooksToMonitor,
                Is.EqualTo(new[] { "hc:3333" }));
            Assert.That(config.AuthorName, Is.EqualTo(primary.Name));
            Assert.That(config.AudiobookNarrations?.All(narration => narration.WorkProviderId == "hc:3333"), Is.Not.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void primary_routing_should_reject_an_unproved_remote_work_or_edition(bool editionSource)
        {
            var (list, _, _, _) = ExistingListScenario();
            list.MonitoringOptions.AddMissingAuthors = true;
            var primary = new Author { HardcoverAuthorId = "hc:1111", Name = "Selected Primary" };
            var report = new ImportListItemInfo { ImportListId = list.Id, Book = "Requested Work",
                BookProviderId = "hc:3333", AuthorProviderId = "hc:2222", EditionProviderId = editionSource ? "hc-ed:4444" : null };
            var source = new Book { HardcoverBookId = editionSource ? "hc:3333" : "hc:9999", Author = primary,
                Editions = new List<Edition> { new() { HardcoverEditionId = "hc:9999", ReadingFormatId = 2, Narrator = "Wrong Narrator" } } };
            var calls = 0;
            var metadata = InterfaceProxy.Proxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>((method, args) =>
            {
                calls++;
                return Tuple.Create("hc:1111", source, new List<Author> { primary });
            });
            var pending = new RecordingPendingAuthorImportService();
            var upserts = 0;
            var identities = InterfaceProxy.Proxy<IImportListBookIdentityCacheRepository>((method, args) =>
            {
                if (method.Name == nameof(IImportListBookIdentityCacheRepository.FindBySourceProviderIds)) return new List<ImportListBookIdentityCache>();
                upserts++;
                return args[0];
            });
            BuildExistingAuthorService(list, null, new List<Book>(), new List<ImportListItemInfo> { report },
                pendingImports: pending, bookInfoOverride: metadata, identityCache: identities).Execute(new ImportListSyncCommand(list.Id));
            Assert.Multiple(() =>
            {
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(report.AuthorProviderId, Is.Null);
                Assert.That(report.EditionHasNarration, Is.Null);
                Assert.That(pending.Enqueued, Is.Empty);
                Assert.That(upserts, Is.Zero);
            });
        }

        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void primary_routing_should_use_indexed_same_side_owner_and_aliases_without_metadata(BookMediaType side)
        {
            var (list, primary, _, _) = ExistingListScenario();
            list.Settings = new GoodreadsBookshelfImportListSettings { MonitorAudiobooks = side == BookMediaType.Audiobook, MonitorEbooks = side == BookMediaType.Ebook };
            var other = new Author { Id = 2, GoodreadsAuthorId = "gr:987654", Name = "Opposite Side Owner" };
            var books = new List<Book>
            {
                new() { Id = 101, AuthorId = primary.Id, GoodreadsWorkId = "gr:123", MediaType = side },
                new() { Id = 102, AuthorId = primary.Id, GoodreadsWorkId = "gr:123", MediaType = side },
                new() { Id = 103, AuthorId = other.Id, GoodreadsWorkId = "gr:123", MediaType = side == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook }
            };
            books.ForEach(book => book.LazyAuthor = new FailOnAuthorLoad());
            var authors = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)authors).AdditionalAuthors.Add(other);
            var writer = DispatchProxy.Create<IBookService, BookServiceProxy>();
            ((BookServiceProxy)writer).AliasProviderId = "gr:777";
            var reports = Enumerable.Range(0, 21).Select(_ => new ImportListItemInfo { ImportListId = list.Id,
                Book = "Requested Work", BookProviderId = "gr:777", AuthorProviderId = other.GoodreadsAuthorId, Author = other.Name }).ToList();
            BuildExistingAuthorService(list, primary, books, reports, authorServiceOverride: authors, bookServiceOverride: writer)
                .Execute(new ImportListSyncCommand(list.Id));
            Assert.Multiple(() =>
            {
                Assert.That(reports.Select(report => report.AuthorProviderId), Is.All.EqualTo(primary.GoodreadsAuthorId));
                Assert.That(reports.Select(report => report.Author), Is.All.EqualTo(primary.Name));
                Assert.That(books.Count(book => book.MediaType == side && book.IsMonitored()), Is.EqualTo(1));
                Assert.That(books.Last().IsMonitored(), Is.False);
                Assert.That(((BookServiceProxy)writer).CatalogReadCount, Is.Zero);
                Assert.That(((AuthorServiceProxy)authors).ProviderLookupCount, Is.Zero);
            });
        }
        [TestCase("az:B012345678", BookMediaType.Audiobook, false)]
        [TestCase("az:B012345678", BookMediaType.Audiobook, true)]
        [TestCase("gb:product-id", BookMediaType.Ebook, false)]
        [TestCase("gb:product-id", BookMediaType.Ebook, true)]
        public void primary_routing_should_preserve_exact_product_only_work_surrogates(string productId, BookMediaType side, bool editionSource)
        {
            var (list, _, _, _) = ExistingListScenario();
            list.MonitoringOptions.AddMissingAuthors = true;
            list.Settings = new GoodreadsBookshelfImportListSettings
            {
                MonitorAudiobooks = side == BookMediaType.Audiobook,
                MonitorEbooks = side == BookMediaType.Ebook,
                AudiobookRootFolderPath = side == BookMediaType.Audiobook ? @"C:\Books".AsOsAgnostic() : null,
                EbookRootFolderPath = side == BookMediaType.Ebook ? @"C:\Books".AsOsAgnostic() : null
            };
            var primary = new Author { HardcoverAuthorId = "hc:1111", Name = "Product Primary" };
            var edition = new Edition { ForeignEditionId = productId,
                Asin = side == BookMediaType.Audiobook ? productId : null,
                GoogleBooksEditionId = side == BookMediaType.Ebook ? productId : null, ReadingFormatId = side == BookMediaType.Audiobook ? 2 : 4 };
            var source = new Book { Author = primary, Title = "Product Only Work", MediaType = side, Editions = new List<Edition> { edition } };
            Assert.That(BookIdentity.GetStableWorkProviderIdentityTokens(source), Is.Empty);
            var report = new ImportListItemInfo { ImportListId = list.Id, Book = source.Title, BookProviderId = productId,
                AuthorProviderId = "hc:2222", EditionProviderId = editionSource ? productId : null };
            var calls = 0;
            var metadata = InterfaceProxy.Proxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>((method, args) =>
            {
                calls++;
                Assert.That(args[0], Is.EqualTo(productId));
                return Tuple.Create(primary.HardcoverAuthorId, source, new List<Author> { primary });
            });
            var pending = new RecordingPendingAuthorImportService();
            BuildExistingAuthorService(list, null, new List<Book>(), new List<ImportListItemInfo> { report },
                pendingImports: pending, bookInfoOverride: metadata).Execute(new ImportListSyncCommand(list.Id));
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(report.AuthorProviderId, Is.EqualTo(primary.HardcoverAuthorId));
            Assert.That(pending.Enqueued.Single().ProviderId, Is.EqualTo(primary.HardcoverAuthorId));
            var config = pending.Enqueued.Single().Config;
            Assert.That(side == BookMediaType.Audiobook ? config.AudiobookBooksToMonitor : config.EbookBooksToMonitor, Is.EqualTo(new[] { productId }));
        }

        [TestCase("hc:777")]
        [TestCase("gr:777")]
        public void primary_routing_should_not_prove_a_work_from_a_colliding_edition_only_response(string workId)
        {
            var (list, _, _, _) = ExistingListScenario();
            list.MonitoringOptions.AddMissingAuthors = true;
            var primary = new Author { HardcoverAuthorId = "hc:1111", Name = "Selected Primary" };
            var source = new Book { Author = primary, Editions = new List<Edition> { new() { HardcoverEditionId = "hc:777", GoodreadsEditionId = 777 } } };
            var report = new ImportListItemInfo { ImportListId = list.Id, Book = "Requested Work", BookProviderId = workId, AuthorProviderId = "hc:2222" };
            var metadata = InterfaceProxy.Proxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>((method, args) => Tuple.Create(primary.HardcoverAuthorId, source, new List<Author> { primary }));
            var pending = new RecordingPendingAuthorImportService();
            BuildExistingAuthorService(list, null, new List<Book>(), new List<ImportListItemInfo> { report },
                pendingImports: pending, bookInfoOverride: metadata).Execute(new ImportListSyncCommand(list.Id));
            Assert.That(report.AuthorProviderId, Is.Null);
            Assert.That(pending.Enqueued, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void primary_routing_should_resolve_conflicting_local_owners_once_without_rehoming_rows(bool bothSides)
        {
            var (list, primary, _, _) = ExistingListScenario();
            list.MonitoringOptions.AddMissingAuthors = bothSides;
            list.Settings = new GoodreadsBookshelfImportListSettings
            {
                MonitorAudiobooks = true,
                MonitorEbooks = bothSides,
                AudiobookRootFolderPath = @"C:\Books".AsOsAgnostic(),
                EbookRootFolderPath = bothSides ? @"C:\Books".AsOsAgnostic() : null
            };
            var contributor = new Author { Id = 2, GoodreadsAuthorId = "gr:987654", Name = "Contributor" };
            var books = new List<Book>
            {
                new() { Id = 101, AuthorId = primary.Id, GoodreadsWorkId = "gr:123", MediaType = BookMediaType.Audiobook },
                new() { Id = 102, AuthorId = contributor.Id, GoodreadsWorkId = "gr:123", MediaType = bothSides ? BookMediaType.Ebook : BookMediaType.Audiobook }
            };
            books.ForEach(book => book.LazyAuthor = new FailOnAuthorLoad());
            var authors = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)authors).AdditionalAuthors.Add(contributor);
            var calls = 0;
            var metadata = InterfaceProxy.Proxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>((method, args) =>
            {
                calls++;
                Assert.That(method.Name, Is.EqualTo("GetWorkInfo"));
                return Tuple.Create(primary.GoodreadsAuthorId, new Book { GoodreadsWorkId = "gr:123", Title = "Requested Work", Author = primary }, new List<Author> { primary });
            });
            var reports = Enumerable.Range(0, 21).Select(_ => new ImportListItemInfo { ImportListId = list.Id,
                Book = "Requested Work", BookProviderId = "gr:123", AuthorProviderId = contributor.GoodreadsAuthorId }).ToList();
            var pending = new RecordingPendingAuthorImportService();
            BuildExistingAuthorService(list, primary, books, reports, authorServiceOverride: authors,
                bookInfoOverride: metadata, pendingImports: pending).Execute(new ImportListSyncCommand(list.Id));
            Assert.Multiple(() =>
            {
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(reports.Select(report => report.AuthorProviderId), Is.All.EqualTo(primary.GoodreadsAuthorId));
                Assert.That(books[0].IsMonitored(), Is.True);
                Assert.That(books[1].IsMonitored(), Is.False);
                Assert.That(books[1].AuthorId, Is.EqualTo(contributor.Id));
                Assert.That(pending.Enqueued.Select(item => item.ProviderId), Is.All.EqualTo(primary.GoodreadsAuthorId));
            });
            if (bothSides)
            {
                Assert.That(pending.Enqueued.Single().Config.EbookBooksToMonitor, Is.EqualTo(new[] { "gr:123" }));
            }
            else
            {
                Assert.That(pending.Enqueued, Is.Empty);
            }
        }

        [Test]
        public void primary_routing_should_not_dispatch_unproved_contributor_siblings_after_a_source_error()
        {
            var (list, _, _, _) = ExistingListScenario();
            list.MonitoringOptions.AddMissingAuthors = true;
            var reports = Enumerable.Range(0, 21).Select(_ => new ImportListItemInfo { ImportListId = list.Id,
                Book = "Requested Work", BookProviderId = "hc:3333", AuthorProviderId = "hc:2222" }).ToList();
            var calls = 0;
            var metadata = InterfaceProxy.Proxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>((method, args) =>
            {
                calls++;
                throw new InvalidOperationException("Provider unavailable");
            });
            var pending = new RecordingPendingAuthorImportService();
            BuildExistingAuthorService(list, null, new List<Book>(), reports, bookInfoOverride: metadata,
                pendingImports: pending).Execute(new ImportListSyncCommand(list.Id));
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(reports.Select(report => report.AuthorProviderId), Is.All.Null);
            Assert.That(pending.Enqueued, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void primary_routing_should_isolate_unavailable_list_settings_from_a_healthy_neighbor(bool throws)
        {
            var (list, author, books, reports) = ExistingListScenario();
            reports.Add(new ImportListItemInfo { ImportListId = 2, Book = "Unavailable List Work", BookProviderId = "hc:3333", AuthorProviderId = "hc:2222" });
            var factory = InterfaceProxy.Proxy<IImportListFactory>((method, args) =>
            {
                if (method.Name == nameof(IImportListFactory.GetInstance))
                {
                    return InterfaceProxy.Proxy<IImportList>((providerMethod, _) => providerMethod.Name == "get_Definition"
                        ? list : throw new AssertionException(providerMethod.Name));
                }
                Assert.That(method.Name, Is.EqualTo(nameof(IImportListFactory.Get)));
                if ((int)args[0] == list.Id)
                {
                    return list;
                }
                if (throws)
                {
                    throw new InvalidOperationException("List settings could not load");
                }
                return null;
            });
            var calls = 0;
            var metadata = InterfaceProxy.Proxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>((method, args) =>
            {
                calls++;
                throw new AssertionException("No metadata call is needed for the healthy local rows or unavailable list");
            });
            var pending = new RecordingPendingAuthorImportService();
            BuildExistingAuthorService(list, author, books, reports, factoryOverride: factory,
                bookInfoOverride: metadata, pendingImports: pending).Execute(new ImportListSyncCommand(list.Id));
            Assert.That(books.Take(2).All(book => book.IsMonitored()), Is.True);
            Assert.That(calls, Is.Zero);
            Assert.That(pending.Enqueued, Is.Empty);
            Assert.That(reports.Last().AuthorProviderId, Is.EqualTo("hc:2222"));
        }

        [Test]
        public void primary_routing_should_not_fetch_default_audio_metadata_without_an_enabled_side()
        {
            var (list, _, _, _) = ExistingListScenario();
            list.MonitoringOptions.AddMissingAuthors = true;
            list.Settings = new GoodreadsBookshelfImportListSettings { MonitorAudiobooks = false, MonitorEbooks = false };
            var report = new ImportListItemInfo { ImportListId = list.Id, Book = "Unrequested Work", BookProviderId = "hc:3333", AuthorProviderId = "hc:2222" };
            var calls = 0;
            var metadata = InterfaceProxy.Proxy<NzbDrone.Core.MetadataSource.IProvideBookInfo>((method, args) =>
            {
                calls++;
                throw new AssertionException("Disabled sides must not issue a metadata lookup");
            });
            var pending = new RecordingPendingAuthorImportService();
            BuildExistingAuthorService(list, null, new List<Book>(), new List<ImportListItemInfo> { report },
                bookInfoOverride: metadata, pendingImports: pending).Execute(new ImportListSyncCommand(list.Id));
            Assert.That(calls, Is.Zero);
            Assert.That(pending.Enqueued, Is.Empty);
        }

    }
}
