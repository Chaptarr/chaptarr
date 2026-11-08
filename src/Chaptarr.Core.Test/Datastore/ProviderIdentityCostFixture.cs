using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Chaptarr.Core.Test.Common;
using Dapper;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Datastore
{
    [NonParallelizable]
    public class ProviderIdentityCostFixture
    {
        private sealed class Snapshot
        {
            public int Id { get; set; }
            public int BookId { get; set; }
            public string Title { get; set; }
            public string[] Ids { get; set; }
        }

        [Test]
        public void reports_batch_projection_timing_and_verifies_identical_lookup_census([Values(false, true)] bool postgres)
        {
            var source = Environment.GetEnvironmentVariable("CHAPTARR_IDENTITY_BENCH_INPUT");
            // CI always exercises a bounded catalog. The optional captured catalog is
            // supplemental scale evidence, never the only test of projection cost.
            var input = source == null
                ? Enumerable.Range(1, 1000).Select(id => new Snapshot { Id = id, BookId = id / 20,
                    Title = "Catalog edition " + id, Ids = new[] { "gr:" + id, "hc:" + id } }).ToList()
                : JsonSerializer.Deserialize<List<Snapshot>>(File.ReadAllText(source));
            DualDatabaseFixture.Run(postgres, database =>
            {
                var writes = 0;
                var reads = 0;
                var underlying = database;
                database = InterfaceProxy.Proxy<IMainDatabase>((method, args) => method.Name == nameof(IMainDatabase.OpenConnection)
                    ? CommandObserver.Wrap(underlying.OpenConnection(), command =>
                    {
                        if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) reads++;
                        else writes++;
                    }, includeWrites: true) : method.Invoke(underlying, args));
                var author = new BasicRepository<Author>(database, null).Insert(new Author { Name = "Captured catalog", CleanName = "captured catalog", Path = "/fixture/catalog", Added = DateTime.UtcNow });
                var books = input.Select(row => row.BookId).Distinct().ToDictionary(id => id, id => new Book { AuthorId = author.Id, Title = "Captured pocket", CleanTitle = "captured pocket", AnyEditionOk = true, Added = DateTime.UtcNow });
                new BookRepository(database, null).InsertMany(books.Values.ToList());
                var rows = input.Select(row => new Edition { BookId = books[row.BookId].Id, Title = row.Title, RemoteProviderIds = row.Ids.ToHashSet(StringComparer.OrdinalIgnoreCase) }).ToList();
                var repository = new EditionRepository(database, null);
                foreach (var group in rows.GroupBy(row => row.BookId)) group.First().Monitored = true;
                var watch = Stopwatch.StartNew();
                repository.InsertMany(rows);
                var insertMs = watch.Elapsed.TotalMilliseconds;
                using var connection = database.OpenConnection();
                var census = connection.Query<string>("SELECT \"EntityId\" || ':' || \"Scope\" || ':' || \"Provider\" || ':' || \"NormalizedProviderId\" FROM \"ProviderAliasIndex\" WHERE \"EntityType\"='Edition' ORDER BY 1").ToArray();
                var stamp = connection.Query<long>("SELECT \"Id\" FROM \"ProviderAliasIndex\" WHERE \"EntityType\"='Edition' ORDER BY \"Id\"").ToArray();
                var loaded = repository.All().ToList();
                watch.Restart();
                repository.UpdateMany(loaded);
                var unchangedMs = watch.Elapsed.TotalMilliseconds;
                Assert.That(connection.Query<long>("SELECT \"Id\" FROM \"ProviderAliasIndex\" WHERE \"EntityType\"='Edition' ORDER BY \"Id\""), Is.EqualTo(stamp), "unchanged saves must not rewrite the projection");
                foreach (var row in loaded) row.RemoteProviderIds = row.RemoteProviderIds.Append("gr:999999999999").ToHashSet(StringComparer.OrdinalIgnoreCase);
                watch.Restart();
                repository.SetFields(loaded, edition => edition.ProviderIds);
                var changedMs = watch.Elapsed.TotalMilliseconds;
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM \"ProviderAliasIndex\" WHERE \"EntityType\"='Edition' AND \"NormalizedProviderId\"='999999999999'"), Is.EqualTo(rows.Count));
                Assert.That(connection.QuerySingle<int>("SELECT COUNT(*) FROM \"ProviderAliasIndex\" WHERE \"EntityType\"='Edition'"), Is.EqualTo(census.Length + rows.Count));
                foreach (var row in loaded) row.RemoteProviderIds = row.RemoteProviderIds.Where(id => id != "gr:999999999999").ToHashSet(StringComparer.OrdinalIgnoreCase);
                repository.SetFields(loaded, edition => edition.ProviderIds);
                Assert.That(connection.Query<string>("SELECT \"EntityId\" || ':' || \"Scope\" || ':' || \"Provider\" || ':' || \"NormalizedProviderId\" FROM \"ProviderAliasIndex\" WHERE \"EntityType\"='Edition' ORDER BY 1"), Is.EqualTo(census));
                var lookup = new ProviderAliasService(new ProviderAliasRepository(database, null));
                var key = input.SelectMany(row => row.Ids).First();
                var samples = new List<double>();
                for (var i = 0; i < 100; i++)
                {
                    watch.Restart();
                    Assert.That(lookup.FindBookIds("edition", new[] { key }), Is.Not.Empty);
                    samples.Add(watch.Elapsed.TotalMilliseconds);
                }
                samples.Sort();
                var lookupMs = samples.Average();
                Assert.That(reads, Is.LessThan(140), "indexed lookups and batch writes must not load editions individually");
                Assert.That(writes, Is.LessThanOrEqualTo(rows.Count * 5 + books.Count + 20), "per-row persistence is existing behavior: five stages plus book setup and bounded selection writes");
                TestContext.Progress.WriteLine($"IDENTITY COST {database.DatabaseType}: editions={rows.Count}, aliases={census.Length}, insert={insertMs:F2}ms, unchanged full save={unchangedMs:F2}ms, changed identity batch={changedMs:F2}ms, indexed lookup mean={lookupMs:F4}ms, p99={samples[98]:F4}ms, max={samples[^1]:F4}ms; reads={reads}, writes={writes}; census identical, unchanged alias rows untouched");
            });
        }
    }
}
