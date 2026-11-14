using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Chaptarr.Api.V1.Books;
using Chaptarr.Core.Test.Common;
using Chaptarr.Http.Middleware;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using NLog;
using Npgsql;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Validation;

using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    [NonParallelizable]
    public class BookControllerStatisticsBatchFixture
    {
        [OneTimeSetUp]
        public void MapTables()
        {
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }
        }

        private sealed class Harness : IDisposable
        {
            public readonly IDbConnection Connection;
            public readonly AuthorStatisticsRepository Repository;
            public readonly AuthorStatisticsService Service;
            public readonly BookController Controller;
            public readonly List<(string Sql, DynamicParameters Parameters)> Commands = new();
            public readonly List<List<BookStatistics>> Results = new();
            public readonly List<double> QueryMilliseconds = new();
            public readonly List<Book> Books = new();
            public readonly List<List<int>> FileBatches = new();

            public Harness(bool postgres)
            {
                var connectionString = Environment.GetEnvironmentVariable("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING");
                if (postgres && string.IsNullOrWhiteSpace(connectionString))
                {
                    Assert.Ignore("Set CHAPTARR_TEST_POSTGRES_CONNECTION_STRING to exercise PostgreSQL statistics");
                }

                Connection = postgres ? new NpgsqlConnection(connectionString) : new SqliteConnection("Data Source=:memory:;Pooling=False");
                Connection.Open();
                var booleanType = postgres ? "BOOLEAN" : "INTEGER";
                var dateType = postgres ? "TIMESTAMP" : "TEXT";
                Connection.Execute($@"
                    CREATE TEMP TABLE ""Authors"" (""Id"" INTEGER PRIMARY KEY);
                    CREATE TEMP TABLE ""Books"" (
                        ""Id"" INTEGER PRIMARY KEY, ""AuthorId"" INTEGER NOT NULL, ""MediaType"" INTEGER,
                        ""AudiobookMonitored"" {booleanType}, ""EbookMonitored"" {booleanType}, ""ReleaseDate"" {dateType});
                    CREATE INDEX ""IX_Books_AuthorId"" ON ""Books"" (""AuthorId"");
                    CREATE INDEX ""IX_Books_AuthorId_Id"" ON ""Books"" (""AuthorId"", ""Id"");
                    CREATE TEMP TABLE ""Editions"" (
                        ""Id"" INTEGER PRIMARY KEY, ""BookId"" INTEGER NOT NULL, ""Monitored"" {booleanType}, ""ReleaseDate"" {dateType});
                    CREATE UNIQUE INDEX ""IX_Editions_OneMonitoredPerBook"" ON ""Editions"" (""BookId"") WHERE ""Monitored"" = TRUE;
                    CREATE INDEX ""IX_Editions_BookId_Monitored_Id"" ON ""Editions"" (""BookId"", ""Monitored"", ""Id"");
                    CREATE INDEX ""IX_Editions_BookId"" ON ""Editions"" (""BookId"");
                    CREATE INDEX ""IX_Editions_Monitored"" ON ""Editions"" (""Monitored"");
                    CREATE TEMP TABLE ""BookFiles"" (""Id"" INTEGER PRIMARY KEY, ""EditionId"" INTEGER NOT NULL, ""Size"" BIGINT);
                    CREATE INDEX ""IX_BookFiles_EditionId"" ON ""BookFiles"" (""EditionId"");
                    INSERT INTO ""Authors"" VALUES (1), (2);
                    INSERT INTO ""Books"" VALUES
                        (1, 1, 0, TRUE, FALSE, '2000-01-01'), (2, 1, 1, FALSE, FALSE, '2000-01-01'),
                        (3, 2, 0, TRUE, FALSE, '2099-01-01'), (4, 2, 1, FALSE, FALSE, NULL),
                        (5, 1, 0, FALSE, FALSE, NULL), (6, 1, 1, FALSE, FALSE, NULL),
                        (7, 2, 0, FALSE, FALSE, NULL), (8, 2, 1, FALSE, FALSE, NULL);
                    INSERT INTO ""Editions"" VALUES
                        (10, 1, TRUE, '2000-01-01'), (11, 1, FALSE, NULL), (20, 2, TRUE, NULL),
                        (30, 3, TRUE, '2099-01-01'), (31, 3, FALSE, '2000-01-01'), (40, 4, TRUE, NULL), (50, 999999, FALSE, NULL);
                    INSERT INTO ""BookFiles"" VALUES (1, 10, 10), (2, 11, 20), (3, 20, 5), (4, 50, 999), (5, 999999, 999);
                ");
                Connection.Execute("INSERT INTO \"Authors\" VALUES (@Id)", Enumerable.Range(3, 5000).Select(id => new { Id = id }));
                Connection.Execute(@"INSERT INTO ""Books"" VALUES (@Id, @AuthorId, 0, TRUE, FALSE, NULL)",
                    Enumerable.Range(9, 20000).Select(id => new { Id = id, AuthorId = 3 + ((id - 9) / 4) }));
                Connection.Execute("INSERT INTO \"Editions\" VALUES (@Id + 100000, @Id, TRUE, NULL)", Enumerable.Range(9, 20000).Select(id => new { Id = id }));
                Connection.Execute("INSERT INTO \"BookFiles\" VALUES (@Id + 100000, @Id + 100000, 100)", Enumerable.Range(9, 20000).Select(id => new { Id = id }));
                Connection.Execute("ANALYZE", commandType: CommandType.Text);

                var lease = Proxy<IDbConnection>((method, args) => method.Name == nameof(IDisposable.Dispose) ? null : method.Invoke(Connection, args));
                var observed = CommandObserver.Wrap(lease, command =>
                {
                    var parameters = new DynamicParameters();
                    foreach (IDataParameter parameter in command.Parameters)
                    {
                        parameters.Add(parameter.ParameterName, parameter.Value);
                    }

                    Commands.Add((command.CommandText, parameters));
                });
                var database = Proxy<IMainDatabase>((method, args) => method.Name switch
                {
                    nameof(IMainDatabase.OpenConnection) => observed,
                    "get_DatabaseType" => postgres ? DatabaseType.PostgreSQL : DatabaseType.SQLite,
                    _ => throw new AssertionException(method.Name)
                });
                Repository = new AuthorStatisticsRepository(database);
                Service = new AuthorStatisticsService(Proxy<IAuthorStatisticsRepository>((method, args) =>
                {
                    var timer = Stopwatch.StartNew();
                    var result = (List<BookStatistics>)method.Invoke(Repository, args);
                    QueryMilliseconds.Add(timer.Elapsed.TotalMilliseconds);
                    Results.Add(result);
                    return result;
                }), new CacheManager());

                foreach (var id in Enumerable.Range(1, 4))
                {
                    var media = id % 2 == 1 ? BookMediaType.Audiobook : BookMediaType.Ebook;
                    Books.Add(new Book
                    {
                        Id = id, AuthorId = id <= 2 ? 1 : 2, Author = new Author { Id = id <= 2 ? 1 : 2, Name = "Statistics author", HardcoverAuthorId = "hc:" + (id <= 2 ? 1 : 2) },
                        Title = "Matched work", TitleSlug = "matched-work-" + id, HardcoverBookId = "hc:1001", MediaType = media,
                        AnyEditionOk = true, AudiobookMonitored = media == BookMediaType.Audiobook,
                        Editions = new List<Edition> { new Edition { Id = id * 10, BookId = id, Title = "Matched edition", ForeignEditionId = "hc:" + (id * 10), Monitored = true } }
                    });
                }

                var files = new List<BookFile>
                {
                    new BookFile { Id = 1, EditionId = 10, Size = 10 }, new BookFile { Id = 2, EditionId = 11, Size = 20 }, new BookFile { Id = 3, EditionId = 20, Size = 5 }
                };
                var bookService = Proxy<IBookService>((method, args) => method.Name switch
                {
                    nameof(IBookService.FindAllByWorkProviderId) => Books.Where(book => book.MediaType == (BookMediaType)args[2]).ToList(),
                    nameof(IBookService.GetBooks) => Books.Where(book => ((IEnumerable<int>)args[0]).Contains(book.Id)).ToList(),
                    _ => throw new AssertionException(method.Name)
                });
                Controller = new BookController(null, bookService, null, null, null,
                    Proxy<ISeriesBookLinkService>((method, args) => new List<SeriesBookLink>()), Service,
                    Proxy<IMediaFileService>((method, args) =>
                    {
                        Assert.That(method.Name, Is.EqualTo(nameof(IMediaFileService.GetFilesByBooks)));
                        FileBatches.Add(((List<int>)args[0]).ToList());
                        return files;
                    }), Proxy<IMapCoversToLocal>((method, args) => null), null, null, null, null, null,
                    new QualityProfileExistsValidator(new TestQualityProfileService()),
                    new MetadataProfileExistsValidator(new TestMetadataProfileService()), LogManager.GetCurrentClassLogger())
                {
                    ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
                };
                Controller.HttpContext.Items[ReadarrFacadeContext.ItemKey] = new ReadarrFacadeContext("hc", null, "/hc");
            }

            public void RecordPlan(bool postgres, string name)
            {
                var command = Commands.Last();
                var explain = postgres ? "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " : "EXPLAIN QUERY PLAN ";
                var plan = Connection.Query(explain + command.Sql, command.Parameters).ToList();
                TestContext.Progress.WriteLine($"STATISTICS {name} {(postgres ? "PostgreSQL" : "SQLite")} returned={Results.Last().Count} sql={command.Sql} plan={JsonSerializer.Serialize(plan)}");
            }

            public void Dispose() => Connection.Dispose();
        }

        [TestCase(false, null, true)]
        [TestCase(true, null, true)]
        [TestCase(false, null, null)]
        [TestCase(true, null, null)]
        [TestCase(false, "audiobook", true)]
        [TestCase(true, "audiobook", true)]
        [TestCase(false, "ebook", null)]
        [TestCase(true, "ebook", null)]
        public void title_slug_candidates_should_load_only_their_authors_once(bool postgres, string mediaType, bool? monitored)
        {
            using var harness = new Harness(postgres);
            var expected = harness.Repository.AuthorStatistics().ToDictionary(stat => stat.BookId);
            harness.Commands.Clear();
            var resources = harness.Controller.GetBooks(null, null, null, titleSlug: "1001", mediaType: mediaType, monitored: monitored);
            harness.RecordPlan(postgres, "titleSlug");
            TestContext.Progress.WriteLine($"STATISTICS COST {(postgres ? "PostgreSQL" : "SQLite")} media={mediaType ?? "all"} monitored={monitored} rows={harness.Results.Single().Count} coldMs={harness.QueryMilliseconds.Single():F3}");
            var candidateIds = harness.Books.Where(book => mediaType == null || (mediaType == "audiobook" ? book.MediaType == BookMediaType.Audiobook : book.MediaType == BookMediaType.Ebook))
                .Where(book => monitored != true || book.IsMonitored() || expected[book.Id].BookFileCount > 0).Select(book => book.Id).ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(resources.Select(resource => resource.Id), Is.EqualTo(candidateIds));
                Assert.That(harness.Commands.Count, Is.EqualTo(1));
                Assert.That(harness.Results.Single().Select(stat => stat.AuthorId).Distinct(), Is.EquivalentTo(new[] { 1, 2 }));
                Assert.That(harness.Results.Single().Count, Is.EqualTo(8), "Unrelated library rows must not be loaded for a requested work");
                Assert.That(harness.FileBatches.Count, Is.EqualTo(1));
                foreach (var resource in resources)
                {
                    var stat = expected[resource.Id];
                    Assert.That(resource.Statistics.BookFileCount, Is.EqualTo(stat.BookFileCount));
                    Assert.That(resource.Statistics.BookCount, Is.EqualTo(stat.BookCount));
                    Assert.That(resource.Statistics.AvailableBookCount, Is.EqualTo(stat.AvailableBookCount));
                    Assert.That(resource.Statistics.TotalBookCount, Is.EqualTo(stat.TotalBookCount));
                    Assert.That(resource.Statistics.SizeOnDisk, Is.EqualTo(stat.SizeOnDisk));
                    Assert.That(resource.HasFiles, Is.EqualTo(stat.BookFileCount > 0));
                }
            });

            harness.Controller.GetBooks(null, null, null, titleSlug: "1001", mediaType: mediaType, monitored: monitored);
            Assert.That(harness.Commands.Count, Is.EqualTo(1), "Repeat lookup must reuse existing author caches");
            harness.Service.AuthorStatistics(1);
            Assert.That(harness.Commands.Count, Is.EqualTo(1), "The batch must populate the existing single-author cache");
            harness.Service.InvalidateAuthorCache(1);
            harness.Controller.GetBooks(null, null, null, titleSlug: "1001", mediaType: mediaType, monitored: monitored);
            Assert.That(harness.Commands.Count, Is.EqualTo(2));
            Assert.That(harness.Results.Last().Select(stat => stat.AuthorId).Distinct(), Is.EqualTo(new[] { 1 }));
        }

        [TestCase(false, true)]
        [TestCase(true, true)]
        [TestCase(false, null)]
        [TestCase(true, null)]
        public void native_book_id_list_should_use_the_same_author_batch(bool postgres, bool? monitored)
        {
            using var harness = new Harness(postgres);
            harness.Controller.HttpContext.Items.Remove(ReadarrFacadeContext.ItemKey);
            var resources = harness.Controller.GetBooks(null, new List<int> { 1, 3 }, null, monitored: monitored);
            Assert.Multiple(() =>
            {
                Assert.That(resources.Select(resource => resource.Id), Is.EqualTo(new[] { 1, 3 }));
                Assert.That(harness.Commands.Count, Is.EqualTo(1));
                Assert.That(harness.Results.Single().Select(stat => stat.AuthorId).Distinct(), Is.EquivalentTo(new[] { 1, 2 }));
                Assert.That(harness.Results.Single().Count, Is.EqualTo(8));
                Assert.That(resources.Select(resource => resource.Statistics.BookFileCount), Is.EqualTo(new[] { 2, 0 }));
            });

            harness.Connection.Execute("UPDATE \"Editions\" SET \"BookId\" = 3 WHERE \"Id\" = 11");
            harness.Service.InvalidateAuthorCache(1);
            harness.Service.InvalidateAuthorCache(2);
            resources = harness.Controller.GetBooks(null, new List<int> { 1, 3 }, null, monitored: monitored);
            Assert.Multiple(() =>
            {
                Assert.That(harness.Commands.Count, Is.EqualTo(2));
                Assert.That(resources.Select(resource => resource.Statistics.BookFileCount), Is.EqualTo(new[] { 1, 1 }));
                Assert.That(resources.Select(resource => resource.Statistics.SizeOnDisk), Is.EqualTo(new long[] { 10, 20 }));
                Assert.That(resources.All(resource => resource.HasFiles), Is.True);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void single_author_statistics_should_not_scan_unrelated_files(bool postgres)
        {
            using var harness = new Harness(postgres);
            var statistics = harness.Repository.AuthorStatistics(1);
            var command = harness.Commands.Single();
            Assert.That(statistics.Select(stat => stat.BookId), Is.EquivalentTo(new[] { 1, 2, 5, 6 }));
            if (postgres)
            {
                var rawPlan = harness.Connection.QuerySingle<string>("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + command.Sql, command.Parameters);
                using var document = JsonDocument.Parse(rawPlan);
                var fileRows = 0.0;
                void CountFileRows(JsonElement node)
                {
                    if (node.TryGetProperty("Relation Name", out var relation) && relation.GetString() == "BookFiles")
                    {
                        fileRows += node.GetProperty("Actual Rows").GetDouble() * node.GetProperty("Actual Loops").GetDouble();
                    }

                    if (node.TryGetProperty("Plans", out var children))
                    {
                        foreach (var child in children.EnumerateArray())
                        {
                            CountFileRows(child);
                        }
                    }
                }

                CountFileRows(document.RootElement[0].GetProperty("Plan"));
                TestContext.Progress.WriteLine($"STATISTICS SINGLE PostgreSQL fileRows={fileRows} plan={rawPlan}");
                Assert.That(fileRows, Is.LessThanOrEqualTo(8), "The selected author must not aggregate the library's other files");
            }
            else
            {
                var plan = harness.Connection.Query("EXPLAIN QUERY PLAN " + command.Sql, command.Parameters)
                    .Select(row => (string)((IDictionary<string, object>)row)["detail"]).ToList();
                TestContext.Progress.WriteLine("STATISTICS SINGLE SQLite plan=" + JsonSerializer.Serialize(plan));
                Assert.That(plan.Any(detail => detail.Contains("SCAN BookFiles")), Is.False, "The selected author must not aggregate the library's other files");
                Assert.That(plan.Any(detail => detail.Contains("IX_BookFiles_EditionId")), Is.True);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void empty_and_parameter_limit_batches_should_not_expand_into_global_queries(bool postgres)
        {
            using var harness = new Harness(postgres);
            var overload = typeof(AuthorStatisticsService).GetMethod(nameof(AuthorStatisticsService.AuthorStatistics), new[] { typeof(IEnumerable<int>) });
            Assert.That(overload, Is.Not.Null, "The existing statistics service needs a bounded collection overload");
            List<AuthorStatistics> Fetch(IEnumerable<int> ids) => (List<AuthorStatistics>)overload.Invoke(harness.Service, new object[] { ids });
            Assert.That(Fetch(Array.Empty<int>()), Is.Empty);
            Assert.That(Fetch(null), Is.Empty);
            Assert.That(harness.Commands, Is.Empty);
            var ids = Enumerable.Range(1, SqliteVariableLimit.MaxParameters + 1).ToArray();
            var result = Fetch(ids.Concat(ids));
            Assert.That(result.Select(stat => stat.AuthorId), Is.EquivalentTo(ids));
            Assert.That(harness.Commands.Count, Is.EqualTo(postgres ? 1 : 2));
            Assert.That(harness.Commands.All(command => command.Parameters.ParameterNames.Count() <= SqliteVariableLimit.MaxParameters + 1), Is.True);
            Fetch(ids);
            Assert.That(harness.Commands.Count, Is.EqualTo(postgres ? 1 : 2));
        }
    }
}
