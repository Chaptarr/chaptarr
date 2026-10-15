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
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    [NonParallelizable]
    public class AuthorMergeScalarIdentityFixture
    {
        private sealed class Harness : IDisposable
        {
            private static readonly string[] DependentTables = { "Books", "AuthorSeries", "MetadataFiles", "OtherFiles", "DownloadHistory", "ExtraFiles", "Blacklist", "History", "PendingReleases", "author_trigrams" };
            private static readonly string[] SnapshotTables = DependentTables.Concat(new[] { "Authors", "AuthorSyncMetadata", "ProviderAliasIndex", "ImportListExclusions", "PendingAuthorImport" }).ToArray();
            private readonly LogFactory _factory = new();
            private readonly NLog.Targets.MemoryTarget _log = new() { Layout = "${message}|${exception:format=ToString}" };
            public readonly IMainDatabase Database;
            public readonly AuthorRepository Authors;
            public readonly ProviderAliasService Aliases;
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
            public bool ProbeSourceCache;
            public bool ProbeAllAuthorCache;
            public readonly List<int[]> AllAuthorCacheObservations = new();
            public readonly List<Author> SourceCacheObservations = new();
            public readonly List<(bool SourceExists, string Scalar, int[] Owners, string[] Aliases, Author OldAuthor)> Observations = new();
            private readonly RefreshAuthorService _refresh;

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
                    if (evt is AuthorEditedEvent edited)
                    {
                        var stored = Authors.Get(Survivor.Id);
                        using var connection = Database.OpenConnection();
                        Observations.Add((Authors.Find(Source.Id) != null,
                            ScalarField == null ? null : (string)typeof(Author).GetProperty(ScalarField).GetValue(stored),
                            DependentTables.Select(table => connection.QuerySingle<int>($"SELECT \"AuthorId\" FROM \"{table}\"")).ToArray(),
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
                Aliases = new ProviderAliasService(new ProviderAliasRepository(database, events));
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
                Aliases.ReplaceAliases("Author", Source.Id, "author", AuthorIdentity.GetProviderIdentityTokens(Source).Append("ol:OLSOURCESECONDARYA"));
                Aliases.ReplaceAliases("Author", Survivor.Id, "author", AuthorIdentity.GetProviderIdentityTokens(Survivor).Append("gb:SURVIVORSECONDARY"));
                Aliases.ReplaceAliases("Author", Source.Id, "legacy", new[] { "gb:LEGACYSOURCE" });
                Aliases.ReplaceAliases("Author", Survivor.Id, "legacy", new[] { "ol:LEGACYTARGET" });
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
                        INSERT INTO ""PendingReleases"" (""AuthorId"", ""Title"", ""Added"", ""Release"", ""Reason"") VALUES (@AuthorId, 'fixture', @Date, '{}', 0);
                        INSERT INTO ""author_trigrams"" (""AuthorId"", ""Trigram"") VALUES (@AuthorId, 'lee');", values);
                }
                var syncRows = new AuthorSyncMetadataRepository(database, events);
                syncRows.Insert(new AuthorSyncMetadata { AuthorId = Source.Id, ExternalAuthorId = Source.HardcoverAuthorId, ETag = "source-etag", LastSyncAttempt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), LastSuccessfulSync = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), LastSyncStatus = SyncStatus.Success, SyncFailureCount = 2 });
                syncRows.Insert(new AuthorSyncMetadata { AuthorId = Survivor.Id, ExternalAuthorId = Canonical, LastSyncAttempt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), SyncFailureCount = 1 });
                Requests = new PendingAuthorImportRepository(database, events);
                Request = Requests.Insert(new PendingAuthorImport { ProviderId = Source.HardcoverAuthorId, ProviderPrefix = "hc", AuthorName = Source.Name, RequestedBy = "ImportList:Fixture", AudiobookStatus = PendingImportStatus.Pending, EbookStatus = PendingImportStatus.NotRequested, OverallStatus = PendingImportStatus.Pending, CreatedAt = DateTime.UtcNow, NextAttemptAt = DateTime.UtcNow, Version = 1, AudiobookBooksToMonitor = "[\"hc:80018001\"]", AudiobookRootFolderPath = @"C:\Audio" });
                Service = new AuthorService(Authors, events, null, null, null, new CacheManager(), books, null, logger, Exclusions, null, null, database, Aliases);
                pending = new PendingAuthorImportService(Requests, Service, events, logger, Aliases);
                _refresh = new RefreshAuthorService(null, Service, null, null, null, null, null, events, null, null, null, null, null, null, null,
                    Exclusions, new AuthorSyncMetadataService(syncRows, logger), null, null, logger, mainDatabase: database);
            }

            private Author CreateAuthor(string hardcover, string goodreads, int tag) => Authors.Insert(new Author
            {
                Name = "Lee Child", CleanName = "leechild", SortName = "leechild", TitleSlug = Guid.NewGuid().ToString("N"),
                Path = @"C:\Audio\Lee Child", Added = DateTime.UtcNow, HardcoverAuthorId = hardcover, GoodreadsAuthorId = goodreads,
                AudiobookRootFolderPath = @"C:\Audio", EbookRootFolderPath = @"C:\Ebooks", AudiobookQualityProfileId = 1, EbookQualityProfileId = 1,
                AudiobookMetadataProfileId = 1, EbookMetadataProfileId = 1, AudiobookMonitored = false, EbookMonitored = false, Tags = new() { tag }
            });

            public HashSet<string> Merge()
            {
                var response = new V5AuthorChangesResponse { Merged = new List<V5MergedAuthor> { new() { From = Source.HardcoverAuthorId, To = Canonical } } };
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
                    foreach (var table in DependentTables) Assert.That(connection.QuerySingle<int>($"SELECT \"AuthorId\" FROM \"{table}\""), Is.EqualTo(Survivor.Id), table);
                    Assert.That(Aliases.FindAuthorIds(AuthorIdentity.GetProviderIdentityTokens(Source).Append("ol:OLSOURCESECONDARYA").Append("gb:SURVIVORSECONDARY")), Is.EqualTo(new[] { Survivor.Id }));
                    Assert.That(Aliases.GetAliases("Author", Source.Id, "author"), Is.Empty);
                    Assert.That(Aliases.GetAliases("Author", Source.Id, "legacy"), Is.Empty);
                    Assert.That(Aliases.GetAliases("Author", Survivor.Id, "legacy").Select(alias => $"{alias.Provider}:{alias.NormalizedProviderId}"), Is.EquivalentTo(new[] { "gb:LEGACYSOURCE", "ol:LEGACYTARGET" }));
                    Assert.That(Exclusions.FindByForeignId(Canonical).MediaType, Is.Null);
                    Assert.That(Exclusions.FindByForeignId(new List<string> { Canonical }), Has.Count.EqualTo(1));
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
                    Assert.That(Snapshot(), Is.EqualTo(before));
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
                    h.Aliases.ReplaceAliases("Author", author.Id, "author", new[] { "gr:8002", "gb:BORROWEDTRANSACTION" }, connection, transaction);
                    h.Exclusions.AddAuthorExclusions(new[] { author }, null, connection, transaction);
                    Assert.That(h.ExclusionRows.All(connection, transaction).Where(row => row.ForeignId == h.Canonical).Select(row => row.MediaType), Is.EqualTo(new BookMediaType?[] { null }));
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
                        if (populated == null && command.CommandText.Contains("SELECT * FROM \"ProviderAliasIndex\"") && command.CommandText.Contains("@SourceId OR"))
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
                h.Aliases.ReplaceAliases("Author", h.Source.Id, "author", h.Aliases.GetAliases("Author", h.Source.Id, "author")
                    .Select(alias => $"{alias.Provider}:{alias.NormalizedProviderId}").Concat(extraAliases));
                statements.Clear();
                Assert.That(h.Merge(), Is.Empty, h.LogDump);
                Assert.Multiple(() =>
                {
                    Assert.That(statements.Count(sql => sql.Contains("SELECT * FROM \"ProviderAliasIndex\"") && sql.Contains("@SourceId OR")), Is.EqualTo(1));
                    Assert.That(statements.Count(sql => sql.Contains("SELECT * FROM \"AuthorSyncMetadata\"") && sql.Contains("@SourceId OR")), Is.EqualTo(1));
                    Assert.That(h.Aliases.FindAuthorIds(extraAliases), Is.EqualTo(new[] { h.Survivor.Id }));
                    Assert.That(h.Aliases.GetAliases("Author", h.Survivor.Id, "author").Select(alias => $"{alias.Provider}:{alias.NormalizedProviderId}"), Is.SupersetOf(extraAliases));
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
    }
}
