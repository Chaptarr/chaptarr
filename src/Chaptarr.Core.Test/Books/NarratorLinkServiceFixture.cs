using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Repositories;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    [NonParallelizable]
    public class NarratorLinkServiceFixture
    {
        private sealed class Harness
        {
            public List<NarratorMetadata> Metadata { get; } = new();
            public List<Narrator> Narrators { get; } = new();
            public List<EditionNarratorLink> Links { get; } = new();
            public Dictionary<string, int> Calls { get; } = new();
            public NarratorLinkService Service { get; }

            public Harness()
            {
                Service = new NarratorLinkService(
                    InterfaceProxy.Proxy<IEditionNarratorLinkRepository>((method, args) =>
                    {
                        Count(method.Name);
                        if (method.Name == "GetByEditionIds")
                        {
                            var ids = ((List<int>)args[0]).ToHashSet();
                            var links = Links.Where(l => ids.Contains(l.EditionId)).ToList();
                            foreach (var link in links) link.Narrator = Narrators.Single(n => n.Id == link.NarratorId);
                            return links;
                        }

                        if (method.Name == "ApplyChanges")
                        {
                            var ids = ((List<int>)args[0]).ToHashSet();
                            Links.RemoveAll(l => ids.Contains(l.Id));
                            foreach (var link in (IList<EditionNarratorLink>)args[1])
                            {
                                link.Id = 3000 + Links.Count;
                                Links.Add(link);
                            }

                            return null;
                        }

                        throw new NotSupportedException(method.Name);
                    }),
                    InterfaceProxy.Proxy<IBookNarratorLinkRepository>((method, args) => throw new NotSupportedException(method.Name)),
                    InterfaceProxy.Proxy<INarratorMetadataRepository>((method, args) =>
                    {
                        Count(method.Name);
                        if (method.Name == "FindByProviderIds")
                        {
                            var gr = ((IEnumerable<string>)args[0]).ToHashSet();
                            var hc = ((IEnumerable<string>)args[1]).ToHashSet();
                            return Metadata.Where(m => gr.Contains(m.GoodreadsNarratorId) || hc.Contains(m.HardcoverNarratorId)).ToList();
                        }

                        if (method.Name == "Get" && args[0] is IEnumerable<int> requestedIds)
                        {
                            var ids = requestedIds.ToHashSet();
                            return Metadata.Where(m => ids.Contains(m.Id)).ToList();
                        }

                        if (method.Name == "InsertMany")
                        {
                            foreach (var metadata in (IList<NarratorMetadata>)args[0])
                            {
                                metadata.Id = 1000 + Metadata.Count;
                                Metadata.Add(metadata);
                            }

                            return null;
                        }

                        if (method.Name == "UpdateMany")
                        {
                            return null;
                        }

                        throw new NotSupportedException(method.Name);
                    }),
                    InterfaceProxy.Proxy<INarratorRepository>((method, args) =>
                    {
                        Count(method.Name);
                        if (method.Name == "GetNarratorsByMetadataId")
                        {
                            var ids = ((IEnumerable<int>)args[0]).ToHashSet();
                            return Narrators.Where(n => ids.Contains(n.NarratorMetadataId)).ToList();
                        }

                        if (method.Name == "FindByCleanNames")
                        {
                            var names = ((IEnumerable<string>)args[0]).ToHashSet();
                            return Narrators.Where(n => names.Contains(n.CleanName)).ToList();
                        }

                        if (method.Name == "InsertMany")
                        {
                            foreach (var narrator in (IList<Narrator>)args[0])
                            {
                                narrator.Id = 2000 + Narrators.Count;
                                Narrators.Add(narrator);
                            }

                            return null;
                        }

                        throw new NotSupportedException(method.Name);
                    }),
                    LogManager.GetCurrentClassLogger());
            }

            public Narrator Add(int id, string goodreadsId = null, string hardcoverId = null, string name = "Shared Narrator")
            {
                var metadata = new NarratorMetadata
                {
                    Id = id + 100,
                    Name = name,
                    GoodreadsNarratorId = goodreadsId,
                    HardcoverNarratorId = hardcoverId
                };
                var narrator = new Narrator { Id = id, NarratorMetadataId = metadata.Id, CleanName = name.CleanNarratorName(), Metadata = metadata };
                Metadata.Add(metadata);
                Narrators.Add(narrator);
                return narrator;
            }

            private void Count(string method)
            {
                Calls[method] = Calls.GetValueOrDefault(method) + 1;
            }
        }

        private static Edition Edition(int id, string gr = null, string hc = null, string name = "Shared Narrator") => new()
        {
            Id = id,
            BookId = 10,
            NarratorCredits = new List<NarratorCredit> { new() { Name = name, GoodreadsNarratorId = gr, HardcoverNarratorId = hc } }
        };

        [TestCase(false)]
        [TestCase(true)]
        public void ambiguous_name_only_credit_must_not_choose_or_create_a_person(bool reverse)
        {
            var harness = new Harness();
            harness.Add(1, "gr:101");
            harness.Add(2, "gr:102");
            if (reverse)
            {
                harness.Narrators.Reverse();
            }

            harness.Service.UpsertEditionNarratorLinks(new[] { Edition(11) });

            Assert.That(harness.Links, Is.Empty);
            Assert.That(harness.Narrators.Count, Is.EqualTo(2));
            Assert.That(harness.Calls.GetValueOrDefault("UpdateMany"), Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void provider_preload_must_not_overwrite_name_ambiguity(bool reverse)
        {
            var harness = new Harness();
            harness.Add(1, "gr:101");
            harness.Add(2, "gr:102");
            var editions = new[] { Edition(11), Edition(12, "gr:102") };

            harness.Service.UpsertEditionNarratorLinks(reverse ? editions.Reverse().ToArray() : editions);

            Assert.That(harness.Links.Select(l => (l.EditionId, l.NarratorId)), Is.EquivalentTo(new[] { (12, 2) }));
        }

        [Test]
        public void explicit_provider_identity_can_be_created_without_promoting_an_ambiguous_person()
        {
            var harness = new Harness();
            harness.Add(1, "gr:101");
            harness.Add(2, "gr:102");

            harness.Service.UpsertEditionNarratorLinks(new[] { Edition(11, hc: "hc:501"), Edition(12, hc: "hc:501") });

            Assert.That(harness.Narrators.Count, Is.EqualTo(3));
            Assert.That(harness.Metadata.Take(2).Select(m => m.HardcoverNarratorId), Is.All.Null);
            Assert.That(harness.Links.Select(l => l.NarratorId).Distinct().Count(), Is.EqualTo(1));
            Assert.That(harness.Links[0].NarratorId, Is.EqualTo(harness.Narrators.Last().Id));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void conflicting_new_identities_must_not_claim_the_first_anonymous_person(bool reverse)
        {
            var harness = new Harness();
            harness.Add(1);
            var editions = new[] { Edition(11, "gr:101"), Edition(12, "gr:102"), Edition(13) };

            harness.Service.UpsertEditionNarratorLinks(reverse ? editions.Reverse().ToArray() : editions);

            Assert.That(harness.Metadata[0].GoodreadsNarratorId, Is.Null);
            Assert.That(harness.Links.Select(l => l.EditionId), Is.EquivalentTo(new[] { 11, 12 }));
            Assert.That(harness.Links.Select(l => l.NarratorId).Distinct().Count(), Is.EqualTo(2));
            Assert.That(harness.Narrators.Count, Is.EqualTo(3));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void complete_provider_bridge_must_resolve_before_weaker_credits(bool reverse)
        {
            var harness = new Harness();
            var editions = new[] { Edition(11, "gr:101"), Edition(12, hc: "hc:501"), Edition(13, "gr:101", "hc:501"), Edition(14) };

            harness.Service.UpsertEditionNarratorLinks(reverse ? editions.Reverse().ToArray() : editions);

            Assert.That(harness.Narrators.Count, Is.EqualTo(1));
            Assert.That(harness.Metadata.Single().GoodreadsNarratorId, Is.EqualTo("gr:101"));
            Assert.That(harness.Metadata.Single().HardcoverNarratorId, Is.EqualTo("hc:501"));
            Assert.That(harness.Links.Count, Is.EqualTo(4));
            Assert.That(harness.Links.Select(l => l.NarratorId).Distinct().Count(), Is.EqualTo(1));
        }

        [Test]
        public void contradictory_provider_ids_must_not_link_to_either_person()
        {
            var harness = new Harness();
            harness.Add(1, "gr:101", "hc:501");
            harness.Add(2, "gr:102", "hc:502");

            harness.Service.UpsertEditionNarratorLinks(new[] { Edition(11, "gr:101", "hc:502") });

            Assert.That(harness.Links, Is.Empty);
            Assert.That(harness.Narrators.Count, Is.EqualTo(2));
            Assert.That(harness.Calls.GetValueOrDefault("UpdateMany"), Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void conflicting_provider_bridges_must_not_promote_a_first_winner(bool reverse)
        {
            var harness = new Harness();
            harness.Add(1, "gr:101");
            var editions = new[] { Edition(11, "gr:101", "hc:501"), Edition(12, "gr:101", "hc:502") };

            harness.Service.UpsertEditionNarratorLinks(reverse ? editions.Reverse().ToArray() : editions);

            Assert.That(harness.Metadata.Single().HardcoverNarratorId, Is.Null);
            Assert.That(harness.Links, Is.Empty);
            Assert.That(harness.Calls.GetValueOrDefault("UpdateMany"), Is.Zero);
        }

        [Test]
        public void consistent_provider_proof_must_keep_each_identity_and_credit_role()
        {
            var harness = new Harness();
            harness.Add(1, "gr:101");
            harness.Add(2, "gr:102");
            var edition = Edition(11, "gr:101");
            edition.NarratorCredits.Add(new NarratorCredit { Name = "Shared Narrator", GoodreadsNarratorId = "gr:102", Order = 1, Role = "Full Cast" });

            harness.Service.UpsertEditionNarratorLinks(new[] { edition });

            Assert.That(harness.Links.Select(l => (l.NarratorId, l.IsPrimary, l.Role)), Is.EquivalentTo(new[] { (1, true, "Narrator"), (2, false, "Full Cast") }));
        }

        [Test]
        public void unique_name_fallback_must_promote_once_and_reuse_new_provider_keys()
        {
            var harness = new Harness();
            harness.Add(1);

            harness.Service.UpsertEditionNarratorLinks(new[] { Edition(11, "gr:101", "hc:501"), Edition(12, hc: "hc:501", name: "Another Credit Spelling") });

            Assert.That(harness.Narrators.Count, Is.EqualTo(1));
            Assert.That(harness.Links.Select(l => l.NarratorId), Is.EqualTo(new[] { 1, 1 }));
            Assert.That(harness.Calls["UpdateMany"], Is.EqualTo(1));
        }

        [Test]
        public void provider_metadata_without_local_instance_must_create_one_shell()
        {
            var harness = new Harness();
            harness.Metadata.Add(new NarratorMetadata { Id = 101, Name = "Shared Narrator", GoodreadsNarratorId = "gr:101" });

            harness.Service.UpsertEditionNarratorLinks(new[] { Edition(11, "gr:101"), Edition(12, "gr:101") });

            Assert.That(harness.Metadata.Count, Is.EqualTo(1));
            Assert.That(harness.Narrators.Count, Is.EqualTo(1));
            Assert.That(harness.Links.Count, Is.EqualTo(2));
        }

        [Test]
        public void a_large_credit_batch_must_keep_queries_and_writes_at_the_batch_boundary()
        {
            var harness = new Harness();

            harness.Service.UpsertEditionNarratorLinks(Enumerable.Range(1, 200).Select(id => Edition(id, $"gr:{id}", name: $"Narrator {id}")).ToArray());

            Assert.That(harness.Links.Count, Is.EqualTo(200));
            Assert.That(harness.Calls["FindByProviderIds"], Is.EqualTo(1));
            Assert.That(harness.Calls["GetNarratorsByMetadataId"], Is.EqualTo(1));
            Assert.That(harness.Calls["FindByCleanNames"], Is.EqualTo(1));
            Assert.That(harness.Calls["InsertMany"], Is.EqualTo(2));
            Assert.That(harness.Calls["GetByEditionIds"], Is.EqualTo(1));
            Assert.That(harness.Calls["ApplyChanges"], Is.EqualTo(1));
            Assert.That(harness.Calls.GetValueOrDefault("UpdateMany"), Is.Zero);
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void repeated_database_batch_must_preserve_personas_without_per_row_metadata_queries(bool postgres, bool bulkNameOnly)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var author = new AuthorRepository(database, events, logger).Insert(new Author
                {
                    Name = "Narrator fixture author", CleanName = "narratorfixtureauthor", SortName = "narratorfixtureauthor",
                    TitleSlug = "narrator-fixture-author", Path = "/narrator-fixture", Added = DateTime.UtcNow
                });
                var book = new BookRepository(database, events).Insert(new Book
                {
                    Title = "Narrator fixture book", CleanTitle = "narratorfixturebook", TitleSlug = "narrator-fixture-book",
                    AuthorId = author.Id, MediaType = BookMediaType.Audiobook, Added = DateTime.UtcNow
                });
                var metadataRepository = new NarratorMetadataRepository(database, events, logger);
                var narratorRepository = new NarratorRepository(database, events);
                var metadata = new List<NarratorMetadata>
                {
                    new() { Name = "Shared Narrator", GoodreadsNarratorId = "gr:101" },
                    new() { Name = "Shared Narrator", GoodreadsNarratorId = "gr:102" }
                };
                var sampleSize = bulkNameOnly ? 1001 : 100;
                if (bulkNameOnly)
                {
                    metadata.AddRange(Enumerable.Range(800, sampleSize).Select(id => new NarratorMetadata { Name = $"Narrator {id}", GoodreadsNarratorId = $"gr:{id}" }));
                }

                metadataRepository.InsertMany(metadata);
                var narrators = metadata.Select(m => new Narrator
                {
                    NarratorMetadataId = m.Id, Metadata = m, CleanName = m.Name.CleanNarratorName(), Added = DateTime.UtcNow
                }).ToList();
                narratorRepository.InsertMany(narrators);
                var editions = new[] { Edition(0), Edition(0, "gr:102"), Edition(0, hc: "hc:501") }
                    .Concat(Enumerable.Range(800, sampleSize).Select(id => Edition(0, bulkNameOnly ? null : $"gr:{id}", name: $"Narrator {id}"))).ToArray();
                foreach (var edition in editions)
                {
                    edition.BookId = book.Id;
                    edition.Title = book.Title;
                    edition.Narrator = edition.NarratorCredits[0].Name;
                    edition.NarratorNames = new List<string> { edition.Narrator };
                    edition.Monitored = ReferenceEquals(edition, editions[0]);
                }

                new EditionRepository(database, events).InsertMany(editions);
                var links = new EditionNarratorLinkRepository(database, events);
                var originalLink = links.Insert(new EditionNarratorLink { EditionId = editions[0].Id, NarratorId = narrators[0].Id, IsPrimary = true, Role = "Narrator" });

                var statements = new List<string>();
                var observed = InterfaceProxy.Proxy<IMainDatabase>((method, args) =>
                    method.Name == nameof(IMainDatabase.OpenConnection)
                        ? CommandObserver.Wrap(database.OpenConnection(), command => statements.Add(command.CommandText), includeWrites: true)
                        : method.Invoke(database, args));
                var service = new NarratorLinkService(new EditionNarratorLinkRepository(observed, events),
                    new BookNarratorLinkRepository(observed, events), new NarratorMetadataRepository(observed, events, logger),
                    new NarratorRepository(observed, events), logger);
                for (var pass = 0; pass < 2; pass++)
                {
                    statements.Clear();
                    var timer = Stopwatch.StartNew();
                    service.UpsertEditionNarratorLinks(pass == 0 ? editions : editions.Reverse().ToArray());
                    timer.Stop();
                    var reads = statements.Count(sql => sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase));
                    TestContext.WriteLine($"{(postgres ? "PostgreSQL" : "SQLite")}: pass={pass + 1}, editions={editions.Length}, bulk_name_only={bulkNameOnly}, SELECTs={reads}, elapsed_ms={timer.Elapsed.TotalMilliseconds:F2}");
                    var stored = links.All().ToList();
                    var nameBatches = postgres ? 1 : (int)Math.Ceiling((sampleSize + 1) / (double)SqliteVariableLimit.MaxParameters);
                    var editionBatches = postgres ? 1 : (int)Math.Ceiling(editions.Length / (double)SqliteVariableLimit.MaxParameters);
                    Assert.That(reads, Is.EqualTo(4 + editionBatches + nameBatches - 1 + (bulkNameOnly && pass == 0 ? nameBatches : 0)));
                    Assert.That(stored.Count, Is.EqualTo(sampleSize + 3));
                    Assert.That(stored.Single(link => link.EditionId == editions[0].Id).Id, Is.EqualTo(originalLink.Id));
                    Assert.That(stored.Single(link => link.EditionId == editions[1].Id).NarratorId, Is.EqualTo(narrators[1].Id));
                    Assert.That(narratorRepository.All().Count(), Is.EqualTo(sampleSize + 3));
                    Assert.That(metadataRepository.Get(metadata.Take(2).Select(m => m.Id)).Select(m => m.HardcoverNarratorId), Is.All.Null);
                    Assert.That(new EditionRepository(database, events).Get(editions[0].Id).NarratorNames, Is.EqualTo(new[] { "Shared Narrator" }));
                }

                service.RebuildBookNarratorLinks(new[] { book.Id });
                Assert.That(new BookNarratorLinkRepository(database, events).All().Count(), Is.EqualTo(sampleSize + 3));
            });
        }
    }
}
