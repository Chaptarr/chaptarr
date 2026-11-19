using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookSlugQueryFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void collision_lookup_is_indexed_and_bounded_to_candidate_slugs(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = InterfaceProxy.Proxy<IEventAggregator>((m, a) => null);
                var author = new AuthorRepository(database, events, LogManager.GetCurrentClassLogger()).Insert(new Author
                {
                    Name = "Fixture Author", CleanName = "fixtureauthor", SortName = "fixtureauthor", SortNameLastFirst = "authorfixture",
                    TitleSlug = "fixture-author", Path = "/library/fixture", Added = DateTime.UtcNow
                });
                using var connection = database.OpenConnection();
                var censusPath = Environment.GetEnvironmentVariable("CHAPTARR_SLUG_CENSUS");
                var rows = string.IsNullOrEmpty(censusPath)
                    ? Enumerable.Range(1, 12000).Select(i => new Row { Id = i, MediaType = i % 2, TitleSlug = "book-" + i }).ToList()
                    : JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(censusPath));
                if (string.IsNullOrEmpty(censusPath))
                {
                    rows.AddRange(Enumerable.Range(12001, 7500).Select(id => new Row
                        { Id = id, MediaType = id % 2, TitleSlug = "the-unrelated-title-" + id }));
                }
                var nextId = rows.Max(row => row.Id) + 1;
                rows.AddRange(new[]
                {
                    new Row { Id = nextId, MediaType = 0, TitleSlug = "slug-fixture" },
                    new Row { Id = nextId + 1, MediaType = 0, TitleSlug = "slug-fixture_2" },
                    new Row { Id = nextId + 2, MediaType = 1, TitleSlug = "slug-fixture" },
                    new Row { Id = nextId + 3, MediaType = 0, TitleSlug = "slug-fixtureX" },
                    new Row { Id = nextId + 4, MediaType = 0, TitleSlug = "slug-fixture_2-extra" },
                    new Row { Id = nextId + 5, MediaType = 0, TitleSlug = "slug-fixture_other" },
                    new Row { Id = nextId + 6, MediaType = 0, TitleSlug = "slug-fixture_" },
                    new Row { Id = nextId + 7, MediaType = 0, TitleSlug = "王子📚" },
                    new Row { Id = nextId + 8, MediaType = 0, TitleSlug = "王子📚_2" },
                    new Row { Id = nextId + 9, MediaType = 0, TitleSlug = "王子📚_another" }
                });
                foreach (var chunk in rows.Chunk(200))
                {
                    var parameters = new DynamicParameters(new { authorId = author.Id, added = DateTime.UtcNow, any = true });
                    var values = new List<string>();
                    for (var i = 0; i < chunk.Length; i++)
                    {
                        parameters.Add($"id{i}", chunk[i].Id);
                        parameters.Add($"slug{i}", chunk[i].TitleSlug);
                        parameters.Add($"media{i}", chunk[i].MediaType);
                        values.Add($"(@id{i}, @authorId, 'Fixture', 'fixture', @any, @added, @media{i}, @slug{i})");
                    }
                    connection.Execute($"INSERT INTO \"Books\" (\"Id\", \"AuthorId\", \"Title\", \"CleanTitle\", \"AnyEditionOk\", \"Added\", \"MediaType\", \"TitleSlug\") {(postgres ? "OVERRIDING SYSTEM VALUE" : "")} VALUES {string.Join(",", values)}", parameters);
                }
                connection.Execute("ANALYZE \"Books\"");
                var reads = new List<(string Sql, DynamicParameters Parameters)>();
                var observed = InterfaceProxy.Proxy<IMainDatabase>((method, args) => method.Name == nameof(IMainDatabase.OpenConnection)
                    ? CommandObserver.Wrap<IDbConnection>(database.OpenConnection(), command =>
                    {
                        var parameters = new DynamicParameters();
                        foreach (IDataParameter parameter in command.Parameters) parameters.Add(parameter.ParameterName, parameter.Value);
                        reads.Add((command.CommandText, parameters));
                    })
                    : method.Invoke(database, args));
                var repository = new BookRepository(observed, events);
                var times = new List<double>();
                for (var i = 0; i < 20; i++)
                {
                    var timer = Stopwatch.StartNew();
                    var found = repository.FindTitleSlugConflicts(new[] { "slug-fixture", "the", "missing", "王子📚" });
                    times.Add(timer.Elapsed.TotalMilliseconds);
                    var expected = rows.Where(row => new[] { "slug-fixture", "the", "missing", "王子📚" }.Any(slug =>
                        row.TitleSlug == slug || System.Text.RegularExpressions.Regex.IsMatch(row.TitleSlug,
                            "^" + System.Text.RegularExpressions.Regex.Escape(slug) + "_[0-9]+$"))).Select(row => row.Id);
                    Assert.That(found.Select(book => book.Id), Is.EquivalentTo(expected));
                    Assert.That(found.Count, Is.LessThan(25), "Unrelated the-* and missing-* titles must stay in the database.");
                }
                Assert.That(reads, Has.Count.EqualTo(20), "One reader per batch, not per candidate or book.");
                var read = reads[0];
                var plan = postgres
                    ? string.Join("\n", connection.Query<string>("EXPLAIN " + read.Sql, read.Parameters))
                    : string.Join("\n", connection.Query("EXPLAIN QUERY PLAN " + read.Sql, read.Parameters).Select(row => (string)row.detail));
                Assert.That(plan, Does.Contain("IX_Books_MediaType_TitleSlug"));
                Assert.That(plan, Does.Contain(postgres ? "Index Cond:" : "<expr>"), "Use the slug key as well as the media key.");
                times.Sort();
                TestContext.Progress.WriteLine($"Slug query {database.DatabaseType}: {rows.Count} book rows; median={times[times.Count / 2]:F3}ms, p99/max={times.Last():F3}ms; {reads.Count} reads; plan:\n{plan}");
            }, desiredVersion: 127);
        }

        public sealed class Row
        {
            public int Id { get; set; }
            public int MediaType { get; set; }
            public string TitleSlug { get; set; }
        }
    }
}
