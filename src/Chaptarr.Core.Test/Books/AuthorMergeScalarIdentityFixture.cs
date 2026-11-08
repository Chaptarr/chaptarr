using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using Newtonsoft.Json;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource.BookInfo.V5;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    [NonParallelizable]
    public class AuthorMergeScalarIdentityFixture
    {
        private sealed class Harness : IDisposable
        {
            private static readonly string[] DependentTables = { "Books", "AuthorSeries", "MetadataFiles", "OtherFiles", "DownloadHistory", "ExtraFiles", "Blacklist", "History", "PendingReleases" };
            private static readonly string[] SnapshotTables = DependentTables.Concat(new[] { "Authors", "AuthorSyncMetadata", "ProviderAliasIndex", "ImportListExclusions", "PendingAuthorImport" }).ToArray();
            private readonly LogFactory _factory = new();
            private readonly NLog.Targets.MemoryTarget _log = new() { Layout = "${message}|${exception:format=ToString}" };
            public readonly IMainDatabase Database;
            public readonly AuthorRepository Authors;
            public readonly ProviderAliasSeed Aliases;
            public readonly ImportListExclusionRepository ExclusionRows;
            public readonly ImportListExclusionService Exclusions;
            public readonly AuthorService Service;
            public readonly Author Source;
            public readonly Author Survivor;
            public readonly string ScalarField;
            public readonly string OwnedId;
            public readonly string Canonical;
            public readonly PendingAuthorImportRepository Requests;
            public readonly PendingAuthorImport Request;
            public readonly List<object> Events = new();
            public readonly List<string> TransactionalAuthorWrites = new();
            public int TransactionalBookInserts;
            public int TransactionalHistoryInserts;
            public bool ProbeSourceCache;
            public bool ProbeAllAuthorCache;
            public readonly List<int[]> AllAuthorCacheObservations = new();
            public readonly List<Author> SourceCacheObservations = new();
            public readonly List<(bool SourceExists, string Scalar, int[] Owners, string[] Aliases, Author OldAuthor)> Observations = new();
            private readonly RefreshAuthorService _refresh;
            public Func<string, RefreshResult> Fetch = id => RefreshResult.Error("fixture has no published response");
            public Author Reconcile(Author local, Author remote) => (Author)typeof(RefreshAuthorService)
                .GetMethod("ReconcilePublishedAuthorIdentity", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Invoke(_refresh, new object[] { local, remote });
            public Author FetchCanonical(Author local)
            {
                var args = new object[] { local, false, null, false, null };
                typeof(RefreshAuthorService).GetMethod("GetSkyhookData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(_refresh, args);
                return (Author)args[0];
            }

            public Harness(IMainDatabase database, string provider = "az", bool globalFirst = false, bool secondaryOnly = false)
            {
                Database = database;
                var config = new NLog.Config.LoggingConfiguration(_factory);
                config.AddRuleForAllLevels(_log);
                _factory.Configuration = config;
                var logger = _factory.GetLogger("author-merge");
                PendingAuthorImportService pending = null;
                var events = Proxy<IEventAggregator>((method, args) =>
                {
                    var evt = args[0];
                    Events.Add(evt);
                    if (evt is AuthorEditedEvent edited && edited.Author.Id == Survivor.Id)
                    {
                        var stored = Authors.Get(Survivor.Id);
                        using var connection = Database.OpenConnection();
                        Observations.Add((Authors.Find(Source.Id) != null,
                            ScalarField == null ? null : (string)typeof(Author).GetProperty(ScalarField).GetValue(stored),
                            DependentTables.Select(table => connection.Query<int>($"SELECT DISTINCT \"AuthorId\" FROM \"{table}\"").Single()).ToArray(),
                            Aliases.GetAliases("Author", Survivor.Id, "author").Select(alias => $"{alias.Provider}:{alias.NormalizedProviderId}").OrderBy(id => id).ToArray(),
                            edited.OldAuthor));
                        if (ProbeSourceCache)
                        {
                            try { SourceCacheObservations.Add(Service.GetAuthor(Source.Id)); }
                            catch (ModelNotFoundException) { SourceCacheObservations.Add(null); }
                        }
                        if (ProbeAllAuthorCache) AllAuthorCacheObservations.Add(Service.GetAllAuthors().Select(author => author.Id).ToArray());
                        pending.Handle(edited);
                    }
                    if (evt is AuthorDeletedEvent deleted) pending.Handle(deleted);
                    return null;
                });
                Authors = new AuthorRepository(database, events, logger);
                Aliases = new ProviderAliasSeed(database, events);
                ExclusionRows = new ImportListExclusionRepository(database, events);
                Exclusions = new ImportListExclusionService(ExclusionRows, logger);
                ScalarField = provider switch
                {
                    "gr" => nameof(Author.GoodreadsAuthorId),
                    "az" => nameof(Author.AudnexusAuthorId), "ol" => nameof(Author.OpenLibraryAuthorId),
                    "gb" => nameof(Author.GoogleBooksAuthorId), _ => null
                };
                OwnedId = provider switch
                {
                    "gr" => "gr:8001", "az" => "az:B000APO0PQ",
                    "ol" => "ol:OL8001A", "gb" => "gb:AUTHOR8001", _ => null
                };
                Source = CreateAuthor("hc:1221318", null, 1);
                if (ScalarField != null) typeof(Author).GetProperty(ScalarField).SetValue(Source, OwnedId);
                Authors.Update(Source);
                Survivor = CreateAuthor("hc:71527", null, 2);
                Canonical = Survivor.HardcoverAuthorId;
                Aliases.SeedAliases("Author", Source.Id, "author", AuthorIdentity.GetProviderIdentityTokens(Source).Append("ol:OLSOURCESECONDARYA"));
                Aliases.SeedAliases("Author", Survivor.Id, "author", AuthorIdentity.GetProviderIdentityTokens(Survivor).Append("gb:SURVIVORSECONDARY"));
                Aliases.SeedAliases("Author", Source.Id, "legacy", new[] { "gb:LEGACYSOURCE" });
                Aliases.SeedAliases("Author", Survivor.Id, "legacy", new[] { "ol:LEGACYTARGET" });
                // This fixture's served blueprint explicitly retains these source IDs.
                // Legacy-only IDs are not served and must disappear after refresh.
                var servedIds = Aliases.GetAliases("Author", new[] { Source.Id, Survivor.Id }, "author")
                    .Select(alias => $"{alias.Provider}:{alias.NormalizedProviderId}").ToHashSet(StringComparer.OrdinalIgnoreCase);
                Fetch = _ => RefreshResult.Updated(new Author { Name = Survivor.Name, HardcoverAuthorId = Canonical,
                    GoodreadsAuthorId = Survivor.GoodreadsAuthorId ?? Source.GoodreadsAuthorId,
                    OpenLibraryAuthorId = Survivor.OpenLibraryAuthorId ?? Source.OpenLibraryAuthorId,
                    GoogleBooksAuthorId = Survivor.GoogleBooksAuthorId ?? Source.GoogleBooksAuthorId,
                    AudnexusAuthorId = Survivor.AudnexusAuthorId ?? Source.AudnexusAuthorId, RemoteProviderIds = servedIds });
                var sourceExclusionId = secondaryOnly ? "ol:OLSOURCESECONDARYA" : Source.HardcoverAuthorId;
                var sourceGlobal = new ImportListExclusion { ForeignId = sourceExclusionId, Name = Source.Name };
                var sourceScoped = new ImportListExclusion { ForeignId = sourceExclusionId, Name = Source.Name, MediaType = BookMediaType.Ebook };
                ExclusionRows.Insert(globalFirst ? sourceGlobal : sourceScoped);
                ExclusionRows.Insert(globalFirst ? sourceScoped : sourceGlobal);
                ExclusionRows.Insert(new ImportListExclusion { ForeignId = Canonical, Name = Survivor.Name, MediaType = BookMediaType.Audiobook });
                var books = new BookRepository(database, events);
                var book = books.Insert(new Book { AuthorId = Source.Id, Title = "Merge fixture work", CleanTitle = "mergefixturework", TitleSlug = Guid.NewGuid().ToString("N"), HardcoverBookId = "hc:80018001", Added = DateTime.UtcNow, MediaType = BookMediaType.Audiobook });
                using (var connection = database.OpenConnection())
                {
                    var values = new { AuthorId = Source.Id, BookId = book.Id, Date = DateTime.UtcNow };
                    connection.Execute(@"INSERT INTO ""AuthorSeries"" (""AuthorId"", ""SeriesId"") VALUES (@AuthorId, 8001);
                        INSERT INTO ""MetadataFiles"" (""AuthorId"", ""Consumer"", ""Type"", ""RelativePath"", ""LastUpdated"", ""Extension"") VALUES (@AuthorId, 'fixture', 0, 'metadata.nfo', @Date, '.nfo');
                        INSERT INTO ""OtherFiles"" (""AuthorId"", ""RelativePath"", ""Added"", ""LastUpdated"", ""Extension"") VALUES (@AuthorId, 'other.txt', @Date, @Date, '.txt');
                        INSERT INTO ""DownloadHistory"" (""AuthorId"", ""BookId"", ""EventType"", ""DownloadId"", ""SourceTitle"", ""Date"") VALUES (@AuthorId, @BookId, 0, 'fixture-download', 'fixture', @Date);
                        INSERT INTO ""ExtraFiles"" (""AuthorId"", ""BookId"", ""BookFileId"", ""RelativePath"", ""Extension"", ""Added"", ""LastUpdated"") VALUES (@AuthorId, @BookId, 8001, 'extra.txt', '.txt', @Date, @Date);
                        INSERT INTO ""Blacklist"" (""AuthorId"", ""SourceTitle"", ""Quality"", ""Date"", ""BookIds"") VALUES (@AuthorId, 'fixture', '{}', @Date, '[]');
                        INSERT INTO ""History"" (""AuthorId"", ""BookId"", ""Quality"", ""Date"", ""SourceTitle"") VALUES (@AuthorId, @BookId, '{}', @Date, 'fixture');
                        INSERT INTO ""PendingReleases"" (""AuthorId"", ""Title"", ""Added"", ""Release"", ""Reason"") VALUES (@AuthorId, 'fixture', @Date, '{}', 0);", values);
                }
                var syncRows = new AuthorSyncMetadataRepository(database, events);
                syncRows.Insert(new AuthorSyncMetadata { AuthorId = Source.Id, ExternalAuthorId = Source.HardcoverAuthorId, ETag = "source-etag", LastSyncAttempt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), LastSuccessfulSync = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), LastSyncStatus = SyncStatus.Success, SyncFailureCount = 2 });
                syncRows.Insert(new AuthorSyncMetadata { AuthorId = Survivor.Id, ExternalAuthorId = Canonical, LastSyncAttempt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), SyncFailureCount = 1 });
                Requests = new PendingAuthorImportRepository(database, events);
                Request = Requests.Insert(new PendingAuthorImport { ProviderId = Source.HardcoverAuthorId, ProviderPrefix = "hc", AuthorName = Source.Name, RequestedBy = "ImportList:Fixture", AudiobookStatus = PendingImportStatus.Pending, EbookStatus = PendingImportStatus.NotRequested, OverallStatus = PendingImportStatus.Pending, CreatedAt = DateTime.UtcNow, NextAttemptAt = DateTime.UtcNow, Version = 1, AudiobookBooksToMonitor = "[\"hc:80018001\"]", AudiobookRootFolderPath = @"C:\Audio" });
                Service = new AuthorService(Authors, events, null, null, null, new CacheManager(), books, null, logger, Exclusions, null, null, database, Aliases);
                pending = new PendingAuthorImportService(Requests, Service, events, logger, Aliases);
                var bookService = Proxy<IBookService>((method, args) => method.Name == nameof(IBookService.GetBooksByAuthor)
                    ? books.GetBooksByAuthorId((int)args[0]) : null);
                var editionService = Proxy<IEditionService>((method, args) => method.Name == nameof(IEditionService.GetEditionsByBook)
                    ? new BasicRepository<Edition>(database, events).All().Where(edition => ((IEnumerable<int>)args[0]).Contains(edition.BookId)).ToList() : null);
                var authorInfo = Proxy<IProvideAuthorInfo>((method, args) => method.Name == nameof(IProvideAuthorInfo.RefreshAuthorInfo) ? Fetch((string)args[0]) : null);
                var historyRows = new HistoryRepository(database, events);
                var identityHistory = Proxy<IHistoryRepository>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IHistoryRepository.InsertMany)));
                    Assert.That(args.Length, Is.EqualTo(3));
                    AssertTransactionConnection((IDbConnection)args[1], (IDbTransaction)args[2]);
                    TransactionalHistoryInserts++;
                    try { return method.Invoke(historyRows, args); }
                    catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException != null)
                    {
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                        throw;
                    }
                });
                _refresh = new RefreshAuthorService(MetadataHealthGateStub.Ready(), authorInfo, Service, bookService, editionService, null, null, null, events, null, null, identityHistory, null, null, null, null,
                    Exclusions, new AuthorSyncMetadataService(syncRows, logger), null, null, logger, mainDatabase: database,
                    bookRepository: Proxy<IBookRepository>((method, args) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(IBookRepository.InsertMany)));
                        Assert.That(args.Length, Is.EqualTo(3));
                        AssertTransactionConnection((IDbConnection)args[1], (IDbTransaction)args[2]);
                        TransactionalBookInserts++;
                        return method.Invoke(books, args);
                    }), authorRepository: Proxy<IAuthorRepository>((method, args) =>
                    {
                        if (args.Length == 3 && (method.Name == nameof(IAuthorRepository.InsertMany) || method.Name == nameof(IAuthorRepository.Update)))
                        {
                            Assert.That(args[1], Is.Not.Null);
                            AssertTransactionConnection((IDbConnection)args[1], (IDbTransaction)args[2]);
                            TransactionalAuthorWrites.Add(method.Name);
                        }
                        return method.Invoke(Authors, args);
                    }));
            }

            private static void AssertTransactionConnection(IDbConnection connection, IDbTransaction transaction)
            {
                // Query observation wraps connections but delegates BeginTransaction unchanged.
                // Compare the underlying connection instead of rejecting its instrumentation proxy.
                while (connection is CommandObserver observer)
                {
                    Assert.That(observer.Target, Is.InstanceOf<IDbConnection>());
                    connection = (IDbConnection)observer.Target;
                }
                Assert.That(transaction, Is.Not.Null);
                Assert.That(transaction.Connection, Is.SameAs(connection));
            }

            private Author CreateAuthor(string hardcover, string goodreads, int tag) => Authors.Insert(new Author
            {
                Name = "Lee Child", CleanName = "leechild", SortName = "leechild", TitleSlug = Guid.NewGuid().ToString("N"),
                Path = @"C:\Audio\Lee Child", Added = DateTime.UtcNow, HardcoverAuthorId = hardcover, GoodreadsAuthorId = goodreads,
                AudiobookRootFolderPath = @"C:\Audio", EbookRootFolderPath = @"C:\Ebooks", AudiobookQualityProfileId = 1, EbookQualityProfileId = 1,
                AudiobookMetadataProfileId = 1, EbookMetadataProfileId = 1, AudiobookMonitored = false, EbookMonitored = false, Tags = new() { tag }
            });

            public HashSet<string> Merge(string destination = null)
            {
                var response = new V5AuthorChangesResponse { Merged = new List<V5MergedAuthor> { new() { From = Source.HardcoverAuthorId, To = destination ?? Canonical } } };
                var authors = Authors.All().ToList();
                return (HashSet<string>)typeof(RefreshAuthorService).GetMethod("ApplyServerMerges", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(_refresh, new object[] { response, authors });
            }

            public string Snapshot()
            {
                using var connection = Database.OpenConnection();
                return JsonConvert.SerializeObject(SnapshotTables.ToDictionary(table => table,
                    table => connection.Query($"SELECT * FROM \"{table}\" ORDER BY \"Id\"").ToArray()));
            }

            public void Abort(bool postgres, string stage)
            {
                var (table, operation, condition) = stage switch
                {
                    "author-save" => ("Authors", "UPDATE", $"NEW.\"Id\" = {Survivor.Id}"),
                    "alias-save" => ("ProviderAliasIndex", "DELETE", $"OLD.\"EntityType\" = 'Author' AND OLD.\"EntityId\" = {Survivor.Id}"),
                    "sync-save" => ("AuthorSyncMetadata", "UPDATE", $"NEW.\"AuthorId\" = {Survivor.Id}"),
                    "exclusion-delete" => ("ImportListExclusions", "DELETE", $"OLD.\"ForeignId\" = '{Canonical}' AND OLD.\"MediaType\" IS NOT NULL"),
                    _ => ("Authors", "DELETE", $"OLD.\"Id\" = {Source.Id}")
                };
                using var connection = Database.OpenConnection();
                connection.Execute(postgres
                    ? $@"CREATE FUNCTION ""ScalarMergeAbort""() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF {condition} THEN RAISE EXCEPTION 'author merge fixture abort {stage}'; END IF; RETURN {(operation == "UPDATE" ? "NEW" : "OLD")}; END; $$;
                         CREATE TRIGGER ""ScalarMergeAbort"" BEFORE {operation} ON ""{table}"" FOR EACH ROW EXECUTE FUNCTION ""ScalarMergeAbort""();"
                    : $@"CREATE TRIGGER ""ScalarMergeAbort"" BEFORE {operation} ON ""{table}"" WHEN {condition} BEGIN SELECT RAISE(ABORT, 'author merge fixture abort {stage}'); END;");
            }

            public string LogDump => string.Join(Environment.NewLine, _log.Logs);

            public void AssertSuccess()
            {
                var stored = Authors.Get(Survivor.Id);
                var sync = new AuthorSyncMetadataRepository(Database, null).All().Single();
                using var connection = Database.OpenConnection();
                Assert.Multiple(() =>
                {
                    Assert.That(Authors.Find(Source.Id), Is.Null);
                    Assert.That(stored.Tags, Is.EquivalentTo(new[] { 1, 2 }));
                    if (ScalarField != null) Assert.That(typeof(Author).GetProperty(ScalarField).GetValue(stored), Is.EqualTo(OwnedId));
                    foreach (var table in DependentTables) Assert.That(connection.Query<int>($"SELECT DISTINCT \"AuthorId\" FROM \"{table}\"").Single(), Is.EqualTo(Survivor.Id), table);
                    Assert.That(Aliases.FindAuthorIds(AuthorIdentity.GetProviderIdentityTokens(Source).Append("ol:OLSOURCESECONDARYA").Append("gb:SURVIVORSECONDARY")), Is.EqualTo(new[] { Survivor.Id }));
                    Assert.That(Aliases.GetAliases("Author", Source.Id, "author"), Is.Empty);
                    Assert.That(Aliases.GetAliases("Author", Source.Id, "legacy"), Is.Empty);
                    Assert.That(Aliases.GetAliases("Author", Survivor.Id, "legacy").Select(alias => $"{alias.Provider}:{alias.NormalizedProviderId}"), Is.Empty);
                    Assert.That(Exclusions.FindByForeignId(new List<string> { Canonical }).Select(row => row.MediaType), Is.EqualTo(new BookMediaType?[] { null }));
                    Assert.That(Requests.Get(Request.Id).IsActive(), Is.True);
                    Assert.That(sync.AuthorId, Is.EqualTo(Survivor.Id));
                    Assert.That(sync.ExternalAuthorId, Is.EqualTo(Canonical));
                    Assert.That(sync.ETag, Is.EqualTo("source-etag"));
                    Assert.That(sync.SyncFailureCount, Is.EqualTo(2));
                    Assert.That(Events.OfType<AuthorDeletedEvent>(), Is.Empty);
                    Assert.That(Observations, Has.Count.EqualTo(1));
                });
                var observation = Observations.Single();
                Assert.Multiple(() =>
                {
                    Assert.That(observation.SourceExists, Is.False);
                    Assert.That(observation.Scalar, Is.EqualTo(OwnedId));
                    Assert.That(observation.Owners, Is.All.EqualTo(Survivor.Id));
                    Assert.That(observation.Aliases, Does.Contain("ol:OLSOURCESECONDARYA"));
                    Assert.That(observation.Aliases, Does.Contain("gb:SURVIVORSECONDARY"));
                    Assert.That(observation.OldAuthor.Tags, Is.EquivalentTo(new[] { 2 }));
                    if (ScalarField != null) Assert.That(typeof(Author).GetProperty(ScalarField).GetValue(observation.OldAuthor), Is.Null);
                });
            }

            public void AssertAbort(string before, string stage)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(_log.Logs.Any(entry => entry.Contains("author merge fixture abort " + stage)), Is.True, string.Join(Environment.NewLine, _log.Logs));
                    // Only operational retry state changes after the ownership transaction
                    // rolls back. IDs, ETag, last success and every dependent row stay exact.
                    var oldState = Newtonsoft.Json.Linq.JObject.Parse(before);
                    var newState = Newtonsoft.Json.Linq.JObject.Parse(Snapshot());
                    foreach (var state in new[] { oldState, newState })
                    {
                        var sourceSync = state["AuthorSyncMetadata"].Single(row => (int)row["AuthorId"] == Source.Id);
                        foreach (var field in new[] { "LastSyncAttempt", "LastSyncStatus", "LastHttpStatus", "SyncFailureCount", "LastError", "LastSyncDurationMs", "NextSyncNotBefore" })
                            ((Newtonsoft.Json.Linq.JObject)sourceSync).Remove(field);
                    }
                    Assert.That(newState.ToString(), Is.EqualTo(oldState.ToString()));
                    var retry = new AuthorSyncMetadataRepository(Database, null).FindByAuthorId(Source.Id);
                    Assert.That(retry.LastSyncStatus, Is.EqualTo(SyncStatus.Failed));
                    Assert.That(retry.NextSyncNotBefore, Is.InRange(DateTime.UtcNow.AddSeconds(40), DateTime.UtcNow.AddMinutes(7)));
                    Assert.That(retry.LastError, Does.Contain("author merge fixture abort " + stage));
                    Assert.That(Events.OfType<AuthorEditedEvent>(), Is.Empty);
                    Assert.That(Events.OfType<AuthorDeletedEvent>(), Is.Empty);
                    Assert.That(Requests.Get(Request.Id).IsActive(), Is.True);
                });
            }

            public void Dispose() => _factory.Dispose();
        }

        [Test]
        public void actual_bulk_merge_transfers_unique_scalars_and_publishes_committed_state(
            [Values(false, true)] bool postgres, [Values("gr", "az", "ol", "gb", "none")] string provider)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new Harness(database, provider);
                Assert.That(h.Merge(), Is.Empty, h.LogDump);
                h.AssertSuccess();
                Assert.That(h.Merge(), Is.Empty, h.LogDump);
                Assert.That(h.Events.OfType<AuthorEditedEvent>().Count(), Is.EqualTo(1));
            });
        }

        [Test]
        public void late_merge_failure_rolls_back_all_owned_data_without_events(
            [Values(false, true)] bool postgres, [Values("az", "none")] string provider,
            [Values("source-delete", "author-save", "alias-save", "sync-save", "exclusion-delete")] string stage)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new Harness(database, provider);
                h.Abort(postgres, stage);
                var before = h.Snapshot();
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    Assert.That(h.Merge(), Is.EquivalentTo(new[] { h.Source.HardcoverAuthorId, h.Canonical }));
                    h.AssertAbort(before, stage);
                }
            });
        }

        [Test]
        public void exclusion_scope_precedence_sees_earlier_writes_in_same_merge(
            [Values(false, true)] bool postgres, [Values(false, true)] bool globalFirst)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new Harness(database, "none", globalFirst);
                Assert.That(h.Merge(), Is.Empty, h.LogDump);
                h.AssertSuccess();
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void stored_secondary_alias_exclusion_is_copied_with_the_merge(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new Harness(database, "none", secondaryOnly: true);
                Assert.That(h.Merge(), Is.Empty, h.LogDump);
                h.AssertSuccess();
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void borrowed_writer_contexts_never_commit_or_publish_events(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new Harness(database, "none");
                var before = h.Snapshot();
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    var author = h.Authors.Get(h.Survivor.Id);
                    author.GoodreadsAuthorId = "gr:8002";
                    author.Tags = new() { 77 };
                    h.Authors.Update(author, connection, transaction);
                    h.Aliases.SeedAliases("Author", author.Id, "author", new[] { "gr:8002", "gb:BORROWEDTRANSACTION" }, connection, transaction);
                    h.Exclusions.AddAuthorExclusions(new[] { author }, null, connection, transaction);
                    Assert.That(h.ExclusionRows.FindByForeignId(new List<string> { h.Canonical }, connection, transaction).Select(row => row.MediaType), Is.EqualTo(new BookMediaType?[] { null }));
                    Assert.That(h.Snapshot(), Is.EqualTo(before));
                    Assert.That(h.Events, Is.Empty);
                    transaction.Rollback();
                }
                Assert.That(h.Snapshot(), Is.EqualTo(before));
                Assert.That(h.Events, Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ordinary_update_keeps_existing_alias_and_event_behavior(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new Harness(database, "none");
                var author = h.Authors.Get(h.Survivor.Id);
                author.Tags = new() { 3 };
                h.Service.UpdateAuthor(author);
                Assert.That(h.Authors.Get(author.Id).Tags, Is.EquivalentTo(new[] { 3 }));
                Assert.That(h.Aliases.FindAuthorIds(new[] { "gb:SURVIVORSECONDARY" }), Is.EqualTo(new[] { author.Id }));
                Assert.That(h.Observations, Has.Count.EqualTo(1));
                Assert.That(h.Observations.Single().SourceExists, Is.True);
                Assert.That(h.Observations.Single().OldAuthor.Tags, Is.EquivalentTo(new[] { 2 }));
                Assert.That(h.Events.OfType<AuthorDeletedEvent>(), Is.Empty);
            });
        }

        [Test]
        public void survivor_before_image_is_fresh_on_success_and_failure([Values(false, true)] bool postgres, [Values(false, true)] bool failure)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new Harness(database);
                Assert.That(h.Service.GetAuthor(h.Survivor.Id).Tags, Is.EquivalentTo(new[] { 2 }));
                var external = h.Authors.Get(h.Survivor.Id);
                external.Tags = new() { 2, 3 };
                h.Authors.Update(external);
                if (failure) h.Abort(postgres, "alias-save");
                var before = h.Snapshot();
                var blocked = h.Merge();
                if (failure)
                {
                    Assert.That(blocked, Is.EquivalentTo(new[] { h.Source.HardcoverAuthorId, h.Canonical }));
                    h.AssertAbort(before, "alias-save");
                    Assert.That(h.Service.GetAuthor(h.Survivor.Id).Tags, Is.EquivalentTo(new[] { 2, 3 }));
                }
                else
                {
                    Assert.That(blocked, Is.Empty, h.LogDump);
                    Assert.That(h.Observations.Single().OldAuthor.Tags, Is.EquivalentTo(new[] { 2, 3 }));
                    Assert.That(h.Service.GetAuthor(h.Survivor.Id).Tags, Is.EquivalentTo(new[] { 1, 2, 3 }));
                    Assert.That(h.Observations.Single().SourceExists, Is.False);
                }
            });
        }

        [Test]
        public void deleted_source_cache_is_invalidated_only_after_commit(
            [Values(false, true)] bool postgres, [Values(false, true)] bool failure)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new Harness(database);
                var source = h.Service.GetAuthor(h.Source.Id);
                Assert.That(source.Id, Is.EqualTo(h.Source.Id));
                h.ProbeSourceCache = true;
                if (failure) h.Abort(postgres, "alias-save");
                var before = h.Snapshot();
                var blocked = h.Merge();
                if (failure)
                {
                    Assert.That(blocked, Is.EquivalentTo(new[] { h.Source.HardcoverAuthorId, h.Canonical }));
                    h.AssertAbort(before, "alias-save");
                    Assert.That(h.Service.GetAuthor(h.Source.Id), Is.SameAs(source));
                    Assert.That(h.SourceCacheObservations, Is.Empty);
                }
                else
                {
                    Assert.That(blocked, Is.Empty, h.LogDump);
                    Assert.That(h.SourceCacheObservations, Is.EqualTo(new Author[] { null }));
                    Assert.Throws<ModelNotFoundException>(() => h.Service.GetAuthor(h.Source.Id));
                    Assert.Throws<ModelNotFoundException>(() => h.Service.GetAuthor(h.Source.Id));
                }
            });
        }

        [Test]
        public void author_list_cached_during_merge_is_invalidated_only_after_commit(
            [Values(false, true)] bool postgres, [Values(false, true)] bool failure)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                Harness h = null;
                List<Author> populated = null;
                var observed = Proxy<IMainDatabase>((method, args) => method.Name == nameof(IMainDatabase.OpenConnection)
                    ? CommandObserver.Wrap<IDbConnection>(database.OpenConnection(), command =>
                    {
                        if (populated == null && command.CommandText.Contains("SELECT * FROM \"AuthorSyncMetadata\"") && command.CommandText.Contains("@SourceId OR"))
                            populated = h.Service.GetAllAuthors();
                    })
                    : method.Invoke(database, args));
                using var harness = new Harness(observed);
                h = harness;
                h.ProbeAllAuthorCache = true;
                if (failure) h.Abort(postgres, "alias-save");
                var before = h.Snapshot();
                var blocked = h.Merge();
                Assert.That(populated.Select(author => author.Id), Is.EquivalentTo(new[] { h.Source.Id, h.Survivor.Id }));
                if (failure)
                {
                    Assert.That(blocked, Is.EquivalentTo(new[] { h.Source.HardcoverAuthorId, h.Canonical }));
                    h.AssertAbort(before, "alias-save");
                    Assert.That(h.Service.GetAllAuthors(), Is.SameAs(populated));
                    Assert.That(h.AllAuthorCacheObservations, Is.Empty);
                }
                else
                {
                    Assert.That(blocked, Is.Empty, h.LogDump);
                    Assert.Multiple(() =>
                    {
                        Assert.That(h.AllAuthorCacheObservations.Single(), Is.EqualTo(new[] { h.Survivor.Id }));
                        Assert.That(h.Service.GetAllAuthors().Select(author => author.Id), Is.EqualTo(new[] { h.Survivor.Id }));
                    });
                }
            });
        }

        [Test]
        public void merge_identity_and_sync_reads_remain_set_based(
            [Values(false, true)] bool postgres, [Values(1, 64)] int aliasCount)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var statements = new List<string>();
                var observed = Proxy<IMainDatabase>((method, args) => method.Name == nameof(IMainDatabase.OpenConnection)
                    ? CommandObserver.Wrap<IDbConnection>(database.OpenConnection(), command => statements.Add(command.CommandText))
                    : method.Invoke(database, args));
                using var h = new Harness(observed);
                var extraAliases = Enumerable.Range(1, aliasCount).Select(id => $"ol:OLBATCH{id}A").ToArray();
                h.Aliases.SeedAliases("Author", h.Source.Id, "author", h.Aliases.GetAliases("Author", h.Source.Id, "author")
                    .Select(alias => $"{alias.Provider}:{alias.NormalizedProviderId}").Concat(extraAliases));
                statements.Clear();
                Assert.That(h.Merge(), Is.Empty, h.LogDump);
                Assert.Multiple(() =>
                {
                    Assert.That(statements.Count(sql => sql.Contains("SELECT * FROM \"ProviderAliasIndex\"") && sql.Contains("@SourceId OR")), Is.Zero);
                    Assert.That(statements.Count(sql => sql.Contains("SELECT * FROM \"AuthorSyncMetadata\"") && sql.Contains("@SourceId OR")), Is.EqualTo(1));
                    Assert.That(h.Aliases.FindAuthorIds(extraAliases), Is.Empty, "unserved IDs must not be unioned into the published snapshot");
                    Assert.That(h.Aliases.GetAliases("Author", h.Survivor.Id, "author").Select(alias => $"{alias.Provider}:{alias.NormalizedProviderId}").Intersect(extraAliases), Is.Empty);
                });
                h.AssertSuccess();
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void validation_precedes_any_merge_callback_write(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new Harness(database);
                var before = h.Snapshot();
                var author = h.Authors.Get(h.Survivor.Id);
                author.GoodreadsAuthorId = "hc:8002";
                var called = false;
                Assert.Throws<InvalidOperationException>(() => h.Service.UpdateAuthor(author, (connection, transaction) => called = true));
                Assert.That(called, Is.False);
                Assert.That(h.Snapshot(), Is.EqualTo(before));
                Assert.That(h.Events, Is.Empty);
            });
        }

        [Test]
        public void published_provider_set_merges_on_fetch_and_rebinds_the_refresh_caller([Values(false, true)] bool postgres, [Values(false, true)] bool survivorFirst)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new Harness(database);
                var remote = new Author { Name = "Lee Child", HardcoverAuthorId = h.Canonical,
                    RemoteProviderIds = AuthorIdentity.MergeProviderIdentityTokens(h.Source, h.Survivor), Books = new List<Book>() };
                h.Fetch = _ => RefreshResult.Updated(remote, "new-etag");
                var original = h.Service.GetAuthor(h.Source.Id);
                var author = h.FetchCanonical(survivorFirst ? h.Service.GetAuthor(h.Survivor.Id) : original);
                Assert.That(author.Id, Is.EqualTo(h.Survivor.Id), h.LogDump);
                Assert.That(h.Authors.Find(h.Source.Id), Is.Null, h.LogDump);
                Assert.That(h.Events.OfType<LibraryIdentityChangedEvent>().Count(), Is.EqualTo(1));
                Assert.That(h.FetchCanonical(original).Id, Is.EqualTo(author.Id), h.LogDump);
                Assert.That(h.Events.OfType<LibraryIdentityChangedEvent>().Count(), Is.EqualTo(1));
                Assert.That(h.LogDump, Does.Not.Contain("Failed to refresh author info"));
            });
        }

        [Test]
        public void author_monitoring_is_or_but_primary_settings_and_unselected_books_stay([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new Harness(database);
                h.Source.AudiobookMonitored = true;
                h.Source.EbookMonitored = false;
                h.Source.AudiobookQualityProfileId = 123;
                h.Source.AudiobookRootFolderPath = @"C:\OtherAudio";
                h.Source.AudiobookPath = @"C:\OtherAudio\Lee Child";
                h.Source.AudiobookTags = new() { 99 };
                h.Source.EbookTags = new() { 7 };
                h.Survivor.AudiobookTags = new() { 5 };
                h.Survivor.EbookTags = new() { 6 };
                h.Survivor.EbookMonitored = true;
                h.Survivor.SyncMonitoredAcrossFormats = false;
                h.Authors.Update(h.Source);
                h.Authors.Update(h.Survivor);
                Assert.That(h.Merge(), Is.Empty, h.LogDump);
                var result = h.Authors.Get(h.Survivor.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(result.AudiobookMonitored, Is.True);
                    Assert.That(result.EbookMonitored, Is.True);
                    Assert.That(result.AudiobookQualityProfileId, Is.EqualTo(1));
                    Assert.That(result.AudiobookRootFolderPath, Is.EqualTo(@"C:\Audio"));
                    Assert.That(result.AudiobookTags, Is.EquivalentTo(new[] { 5, 99 }));
                    Assert.That(result.EbookTags, Is.EquivalentTo(new[] { 6, 7 }));
                    Assert.That(result.SyncMonitoredAcrossFormats, Is.False);
                    Assert.That(new BookRepository(database, null).All().All(book => !book.AudiobookMonitored && !book.EbookMonitored), Is.True);
                    Assert.That(h.Events.OfType<AuthorEditedEvent>().Single().IdentityReconciliation, Is.True);
                    var history = new HistoryRepository(database, null).All().Single(row => row.EventType == EntityHistoryEventType.AuthorsMerged);
                    Assert.That(history.AuthorId, Is.EqualTo(h.Survivor.Id));
                    Assert.That(history.Data["details"], Does.Contain("123"));
                });
                new NzbDrone.Core.Housekeeping.Housekeepers.CleanupOrphanedHistoryItems(database).Clean();
                Assert.That(new HistoryRepository(database, null).All().Count(row => row.EventType == EntityHistoryEventType.AuthorsMerged), Is.EqualTo(1));
            });
        }

        [Test]
        public void canonical_alias_can_merge_and_fetch_does_not_hold_either_author_lock([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new Harness(database);
                h.Survivor.GoodreadsAuthorId = "gr:90210";
                h.Survivor.RemoteProviderIds = AuthorIdentity.GetProviderIdentityTokens(h.Survivor);
                h.Authors.Update(h.Survivor);
                var fetch = h.Fetch;
                h.Fetch = id =>
                {
                    Assert.That(id, Is.EqualTo("gr:90210"));
                    var free = System.Threading.Tasks.Task.Run(() =>
                    {
                        foreach (var authorId in new[] { h.Source.Id, h.Survivor.Id })
                        {
                            var gate = BookService.GetWantedEditionLock(authorId);
                            if (!System.Threading.Monitor.TryEnter(gate, TimeSpan.FromSeconds(1))) return false;
                            System.Threading.Monitor.Exit(gate);
                        }
                        return true;
                    }).GetAwaiter().GetResult();
                    Assert.That(free, Is.True, "network work must precede ordered author write locks");
                    return fetch(id);
                };
                Assert.That(h.Merge("gr:90210"), Is.Empty, h.LogDump);
                Assert.That(h.Authors.Find(h.Source.Id), Is.Null);
            });
        }

        [Test]
        public void author_split_moves_positive_work_evidence_without_changing_book_ids_or_file_paths([Values(false, true)] bool postgres, [Values(false, true)] bool unavailable)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new Harness(database);
                var book = new BookRepository(database, null).All().Single();
                var edition = new BasicRepository<Edition>(database, null).Insert(new Edition { BookId = book.Id, Title = "Killing Floor", ForeignEditionId = "gr:78129", GoodreadsEditionId = 78129, Monitored = true, ManualAdd = true });
                var file = new BasicRepository<BookFile>(database, null).Insert(new BookFile { EditionId = edition.Id, Path = "/library/Lee Child/Killing Floor.m4b", Size = 100, Quality = new QualityModel(), MediaInfo = new(), Modified = DateTime.UtcNow, DateAdded = DateTime.UtcNow });
                var published = new Author { Name = h.Source.Name, HardcoverAuthorId = h.Source.HardcoverAuthorId, Books = new List<Book>() };
                var other = new Author { Name = h.Survivor.Name, HardcoverAuthorId = h.Canonical, AudnexusAuthorId = h.Source.AudnexusAuthorId,
                    Books = new List<Book> { new() { HardcoverBookId = book.HardcoverBookId, Editions = new List<Edition> { edition } } } };
                h.Fetch = _ => unavailable ? RefreshResult.Error("offline") : RefreshResult.Updated(other);
                if (unavailable) Assert.Throws<System.Reflection.TargetInvocationException>(() => h.Reconcile(h.Service.GetAuthor(h.Source.Id), published));
                else h.Reconcile(h.Service.GetAuthor(h.Source.Id), published);
                var expected = unavailable ? h.Source.Id : h.Survivor.Id;
                using var connection = database.OpenConnection();
                Assert.Multiple(() =>
                {
                    Assert.That(new BookRepository(database, null).Get(book.Id).AuthorId, Is.EqualTo(expected), h.LogDump);
                    Assert.That(new BasicRepository<BookFile>(database, null).Get(file.Id).Path, Is.EqualTo(file.Path));
                    Assert.That(new BasicRepository<BookFile>(database, null).Get(file.Id).EditionId, Is.EqualTo(edition.Id));
                    Assert.That(new BasicRepository<Edition>(database, null).Get(edition.Id).ManualAdd, Is.True);
                    Assert.That(connection.QuerySingle<int>("SELECT \"AuthorId\" FROM \"DownloadHistory\""), Is.EqualTo(expected));
                    Assert.That(h.Events.OfType<LibraryIdentityChangedEvent>().Count(), Is.EqualTo(unavailable ? 0 : 1));
                    Assert.That(h.Authors.Count(), Is.EqualTo(2));
                    Assert.That(h.TransactionalAuthorWrites, Is.EqualTo(unavailable ? Array.Empty<string>() : new[] { "Update" }));
                    Assert.That(h.TransactionalHistoryInserts, Is.EqualTo(unavailable ? 0 : 1));
                });
            });
        }
        [Test]
        public void merge_inherits_only_an_unconfigured_format_and_never_queues_setup_or_search([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new Harness(database);
                h.Survivor.EbookRootFolderPath = null;
                h.Survivor.EbookPath = null;
                h.Source.EbookRootFolderPath = "/ebooks";
                h.Source.EbookPath = "/ebooks/Lee Child";
                h.Source.EbookQualityProfileId = 23;
                h.Source.EbookMetadataProfileId = 24;
                h.Source.EbookTags = new() { 4 };
                h.Source.EbookMonitorNewItems = NewItemMonitorTypes.None;
                h.Source.EbookMonitored = true;
                h.Authors.Update(h.Survivor);
                h.Authors.Update(h.Source);
                Assert.That(h.Merge(), Is.Empty, h.LogDump);
                var merged = h.Authors.Get(h.Survivor.Id);
                Assert.That(merged.EbookPath, Is.EqualTo(h.Source.EbookPath));
                Assert.That(merged.EbookQualityProfileId, Is.EqualTo(23));
                Assert.That(merged.EbookTags, Is.EquivalentTo(new[] { 4 }));
                Assert.That(merged.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                var handler = new AuthorEditedService(Proxy<NzbDrone.Core.Messaging.Commands.IManageCommandQueue>((method, args) => throw new AssertionException("Merge must not start setup/search")), LogManager.GetCurrentClassLogger());
                foreach (var message in h.Events.OfType<AuthorEditedEvent>()) handler.Handle(message);
            });
        }

        [Test]
        public void split_creates_a_new_author_and_routes_a_pinned_edition_by_its_published_work([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new Harness(database);
                var books = new BookRepository(database, null);
                var original = books.All().Single();
                var editions = new BasicRepository<Edition>(database, null);
                var retained = editions.Insert(new Edition { BookId = original.Id, Title = "First Work", ForeignEditionId = "gr:100", GoodreadsEditionId = 100 });
                var moved = editions.Insert(new Edition { BookId = original.Id, Title = "Different Work", ForeignEditionId = "gr:200", GoodreadsEditionId = 200, Monitored = true, ManualAdd = true });
                var file = new BasicRepository<BookFile>(database, null).Insert(new BookFile { EditionId = moved.Id, Path = "/old-folder/book.m4b", Size = 100, Quality = new(), MediaInfo = new(), Modified = DateTime.UtcNow, DateAdded = DateTime.UtcNow });
                var remoteWork = new Book { HardcoverBookId = "hc:7654321", Title = "Different Work", CleanTitle = "differentwork", MediaType = original.MediaType, Editions = new() { moved } };
                var published = new Author { Name = h.Source.Name, HardcoverAuthorId = h.Source.HardcoverAuthorId,
                    Books = new() { new() { HardcoverBookId = original.HardcoverBookId, MediaType = original.MediaType, Editions = new() { retained } } } };
                var other = new Author { Name = "New Separate Author", HardcoverAuthorId = "hc:9876543", AudnexusAuthorId = h.Source.AudnexusAuthorId, Books = new() { remoteWork } };
                var cached = h.Service.GetAuthor(h.Source.Id);
                h.Fetch = _ =>
                {
                    var stored = h.Authors.Get(h.Source.Id);
                    stored.AudiobookQualityProfileId = 88;
                    h.Authors.Update(stored);
                    return RefreshResult.Updated(other);
                };
                h.Reconcile(cached, published);
                var separate = h.Authors.All().Single(author => author.HardcoverAuthorId == other.HardcoverAuthorId);
                var newBook = books.Get(editions.Get(moved.Id).BookId);
                Assert.Multiple(() =>
                {
                    Assert.That(books.Get(original.Id).AuthorId, Is.EqualTo(h.Source.Id));
                    Assert.That(editions.Get(retained.Id).BookId, Is.EqualTo(original.Id));
                    Assert.That(newBook.AuthorId, Is.EqualTo(separate.Id));
                    Assert.That(newBook.HardcoverBookId, Is.EqualTo(remoteWork.HardcoverBookId));
                    Assert.That(newBook.AudiobookMonitored, Is.EqualTo(original.AudiobookMonitored));
                    Assert.That(editions.Get(moved.Id).ManualAdd, Is.True);
                    Assert.That(new BasicRepository<BookFile>(database, null).Get(file.Id).Path, Is.EqualTo(file.Path));
                    Assert.That(new BasicRepository<BookFile>(database, null).Get(file.Id).EditionId, Is.EqualTo(moved.Id));
                    Assert.That(h.TransactionalAuthorWrites, Is.EqualTo(new[] { "InsertMany" }));
                    Assert.That(h.TransactionalBookInserts, Is.EqualTo(1));
                    Assert.That(h.TransactionalHistoryInserts, Is.EqualTo(1));
                    Assert.That(separate.AddOptions.Monitor, Is.Null);
                    Assert.That(separate.AudiobookQualityProfileId, Is.EqualTo(88));
                    Assert.That(h.Authors.Get(h.Source.Id).AudiobookQualityProfileId, Is.EqualTo(88));
                    Assert.That(separate.AudiobookRootFolderPath, Is.EqualTo(h.Source.AudiobookRootFolderPath));
                    Assert.That(h.Authors.Get(h.Source.Id).AudnexusAuthorId, Is.Null);
                    Assert.That(h.Events.OfType<AuthorAddedEvent>(), Is.Empty);
                    Assert.That(h.Events.OfType<LibraryIdentityChangedEvent>().Single().IsSplit, Is.True);
                });
            });
        }

        [Test]
        public void split_failure_rolls_back_ownership_aliases_and_audit([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new Harness(database);
                var book = new BookRepository(database, null).All().Single();
                var published = new Author { Name = h.Source.Name, HardcoverAuthorId = h.Source.HardcoverAuthorId, Books = new() };
                var other = new Author { Name = h.Survivor.Name, HardcoverAuthorId = h.Canonical, AudnexusAuthorId = h.Source.AudnexusAuthorId,
                    Books = new() { new() { HardcoverBookId = book.HardcoverBookId } } };
                h.Fetch = _ => RefreshResult.Updated(other);
                using var connection = database.OpenConnection();
                connection.Execute(postgres
                    ? @"CREATE FUNCTION split_abort() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'split audit failed'; END; $$;
                        CREATE TRIGGER split_abort BEFORE INSERT ON ""History"" FOR EACH ROW EXECUTE FUNCTION split_abort();"
                    : @"CREATE TRIGGER split_abort BEFORE INSERT ON ""History"" BEGIN SELECT RAISE(ABORT, 'split audit failed'); END;");
                var before = h.Snapshot();
                Assert.That(() => h.Reconcile(h.Service.GetAuthor(h.Source.Id), published), Throws.Exception.With.InnerException.Message.Contains("split audit failed"));
                Assert.That(h.Snapshot(), Is.EqualTo(before));
                Assert.That(h.TransactionalHistoryInserts, Is.EqualTo(1));
                Assert.That(h.Events, Is.Empty);
                Assert.That(h.Service.GetAuthor(h.Source.Id).AudnexusAuthorId, Is.EqualTo(h.Source.AudnexusAuthorId));
            });
        }

        [Test]
        public void split_cannot_treat_conflicting_edition_owners_as_missing_evidence([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new Harness(database);
                var book = new BookRepository(database, null).All().Single();
                var edition = new BasicRepository<Edition>(database, null).Insert(new Edition
                    { BookId = book.Id, Title = "Killing Floor", ForeignEditionId = "gr:78129", GoodreadsEditionId = 78129, Monitored = true });
                var published = new Author { Name = h.Source.Name, HardcoverAuthorId = h.Source.HardcoverAuthorId,
                    Books = new() { new() { HardcoverBookId = "hc:another-work", Editions = new() { edition } } } };
                var other = new Author { Name = h.Survivor.Name, HardcoverAuthorId = h.Canonical, AudnexusAuthorId = h.Source.AudnexusAuthorId,
                    Books = new() { new() { HardcoverBookId = book.HardcoverBookId, Editions = new() { edition } } } };
                h.Fetch = _ => RefreshResult.Updated(other);
                h.Reconcile(h.Service.GetAuthor(h.Source.Id), published);
                Assert.That(new BookRepository(database, null).Get(book.Id).AuthorId, Is.EqualTo(h.Source.Id));
                Assert.That(h.Events, Is.Empty);
                Assert.That(h.LogDump, Does.Contain("published authors claim the same edition"));
            });
        }

        [Test]
        public void split_persists_the_sources_current_canonical_id_before_later_metadata_refresh([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new Harness(database);
                var book = new BookRepository(database, null).All().Single();
                var published = new Author { Name = h.Source.Name, HardcoverAuthorId = "hc:9876543", Books = new() };
                var other = new Author { Name = h.Survivor.Name, HardcoverAuthorId = h.Canonical,
                    RemoteProviderIds = AuthorIdentity.GetProviderIdentityTokens(h.Source),
                    Books = new() { new() { HardcoverBookId = book.HardcoverBookId } } };
                h.Fetch = _ => RefreshResult.Updated(other);
                h.Reconcile(h.Service.GetAuthor(h.Source.Id), published);
                Assert.That(h.Authors.Get(h.Source.Id).HardcoverAuthorId, Is.EqualTo(published.HardcoverAuthorId));
                Assert.That(h.Aliases.FindAuthorIds(new[] { published.HardcoverAuthorId }), Is.EqualTo(new[] { h.Source.Id }));
                Assert.That(new BookRepository(database, null).Get(book.Id).AuthorId, Is.EqualTo(h.Survivor.Id));
            });
        }

        [Test]
        public void three_author_split_uses_remaining_editions_for_the_last_owner(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var h = new Harness(database);
                var books = new BookRepository(database, null);
                var original = books.All().Single();
                original.MediaType = media;
                original.AnyEditionOk = false;
                original.WantedNarratorId = 42;
                original.LastSearchTime = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
                books.Update(original);
                var editions = new BasicRepository<Edition>(database, null);
                var first = editions.Insert(new Edition { BookId = original.Id, Title = "First work", ForeignEditionId = "gr:100", GoodreadsEditionId = 100, ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 3 });
                var last = editions.Insert(new Edition { BookId = original.Id, Title = "Second work", ForeignEditionId = "gr:200", GoodreadsEditionId = 200, ReadingFormatId = first.ReadingFormatId, Monitored = true, ManualAdd = true });
                var files = new BasicRepository<BookFile>(database, null);
                var firstFile = files.Insert(new BookFile { EditionId = first.Id, Path = "/scattered/first", Size = 100, Quality = new(), MediaInfo = new(), Modified = DateTime.UtcNow, DateAdded = DateTime.UtcNow });
                var lastFile = files.Insert(new BookFile { EditionId = last.Id, Path = "/elsewhere/second", Size = 200, Quality = new(), MediaInfo = new(), Modified = DateTime.UtcNow, DateAdded = DateTime.UtcNow });
                var published = new Author { Name = h.Source.Name, HardcoverAuthorId = h.Source.HardcoverAuthorId, Books = new() };
                var ownerA = new Author { Name = h.Survivor.Name, HardcoverAuthorId = h.Canonical, AudnexusAuthorId = h.Source.AudnexusAuthorId,
                    Books = new() { new() { Title = first.Title, CleanTitle = "firstwork", HardcoverBookId = "hc:7654321", MediaType = media, Editions = new() { first } } } };
                var ownerB = new Author { Name = "Third author", HardcoverAuthorId = "hc:9876543", OpenLibraryAuthorId = "ol:OLSOURCESECONDARYA",
                    Books = new() { new() { Title = last.Title, CleanTitle = "secondwork", HardcoverBookId = "hc:7654322", MediaType = media, Editions = new() { last } } } };
                h.Fetch = id => RefreshResult.Updated(id.StartsWith("az:", StringComparison.Ordinal) ? ownerA : ownerB);
                Assert.DoesNotThrow(() => h.Reconcile(h.Service.GetAuthor(h.Source.Id), published));
                var finalOwner = h.Authors.All().Single(author => author.HardcoverAuthorId == ownerB.HardcoverAuthorId);
                var saved = books.Get(original.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(books.All(), Has.Count.EqualTo(2));
                    Assert.That(saved.AuthorId, Is.EqualTo(finalOwner.Id));
                    Assert.That(saved.WantedNarratorId, Is.EqualTo(42));
                    Assert.That(saved.AnyEditionOk, Is.False);
                    Assert.That(saved.LastSearchTime, Is.EqualTo(original.LastSearchTime));
                    Assert.That(editions.Get(last.Id).BookId, Is.EqualTo(original.Id));
                    Assert.That(editions.Get(last.Id).ManualAdd, Is.True);
                    Assert.That(books.Get(editions.Get(first.Id).BookId).AuthorId, Is.EqualTo(h.Survivor.Id));
                    Assert.That(files.Get(firstFile.Id).EditionId, Is.EqualTo(first.Id));
                    Assert.That(files.Get(lastFile.Id).EditionId, Is.EqualTo(last.Id));
                    Assert.That(files.Get(firstFile.Id).Path, Is.EqualTo(firstFile.Path));
                    Assert.That(files.Get(lastFile.Id).Path, Is.EqualTo(lastFile.Path));
                    Assert.That(h.Events.OfType<LibraryIdentityChangedEvent>().Count(), Is.EqualTo(2));
                });
            });
        }
    }
}
