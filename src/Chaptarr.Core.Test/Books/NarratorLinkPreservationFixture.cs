using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
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
    public class NarratorLinkPreservationFixture
    {
        private sealed class Harness
        {
            public IMainDatabase Database { get; }
            public Book Book { get; }
            public NarratorMetadataRepository Metadata { get; }
            public NarratorRepository Narrators { get; }
            public EditionRepository Editions { get; }
            public EditionNarratorLinkRepository Links { get; }
            public BookNarratorLinkRepository BookLinks { get; }
            public NarratorLinkService Service { get; }

            public Harness(IMainDatabase database)
            {
                Database = database;
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var logger = LogManager.GetCurrentClassLogger();
                var author = new AuthorRepository(database, events, logger).Insert(new Author
                {
                    Name = "Narrator preservation author", CleanName = "narratorpreservationauthor",
                    SortName = "narratorpreservationauthor", TitleSlug = "narrator-preservation-author",
                    Path = @"C:\Books\Narrator preservation author".AsOsAgnostic(), Added = DateTime.UtcNow
                });
                Book = new BookRepository(database, events).Insert(new Book
                {
                    Title = "Narrator preservation book", CleanTitle = "narratorpreservationbook",
                    TitleSlug = "narrator-preservation-book", AuthorId = author.Id,
                    MediaType = BookMediaType.Audiobook, Added = DateTime.UtcNow
                });
                Metadata = new NarratorMetadataRepository(database, events, logger);
                Narrators = new NarratorRepository(database, events);
                Editions = new EditionRepository(database, events);
                Links = new EditionNarratorLinkRepository(database, events);
                BookLinks = new BookNarratorLinkRepository(database, events);
                Service = new NarratorLinkService(Links, BookLinks, Metadata, Narrators, logger);
            }

            public Narrator AddNarrator(string name, string gr, string hc = null)
            {
                var metadata = Metadata.Insert(new NarratorMetadata { Name = name, GoodreadsNarratorId = gr, HardcoverNarratorId = hc });
                return Narrators.Insert(new Narrator
                {
                    NarratorMetadataId = metadata.Id, Metadata = metadata, CleanName = name.CleanNarratorName(), Added = DateTime.UtcNow
                });
            }

            public Edition AddEdition(params NarratorCredit[] credits)
            {
                return Editions.Insert(new Edition
                {
                    BookId = Book.Id, Title = Book.Title, Narrator = credits.FirstOrDefault()?.Name,
                    NarratorNames = credits.Select(c => c.Name).ToList(), NarratorCredits = credits.ToList(), Monitored = true
                });
            }

            public EditionNarratorLink Link(Edition edition, Narrator narrator, bool primary = true, string role = "Narrator")
                => Links.Insert(new EditionNarratorLink { EditionId = edition.Id, NarratorId = narrator.Id, IsPrimary = primary, Role = role });

            public void RejectInsertion(bool postgres, string table, int narratorId)
            {
                using var connection = Database.OpenConnection();
                connection.Execute(postgres
                    ? $"ALTER TABLE \"{table}\" ADD CONSTRAINT reject_narrator_insert CHECK (\"NarratorId\" <> {narratorId})"
                    : $"CREATE TRIGGER reject_narrator_insert BEFORE INSERT ON \"{table}\" WHEN NEW.\"NarratorId\" = {narratorId} BEGIN SELECT RAISE(ABORT, 'injected narrator projection failure'); END");
            }
        }

        private static NarratorCredit Credit(string name = "Shared Narrator", string gr = null, string hc = null)
            => new() { Name = name, GoodreadsNarratorId = gr, HardcoverNarratorId = hc, IsPrimary = true, Role = "Narrator" };

        [TestCase(false)]
        [TestCase(true)]
        public void an_inconclusive_refresh_must_keep_each_specific_existing_relation(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var first = h.AddNarrator("Shared Narrator", "gr:101");
                var second = h.AddNarrator("Shared Narrator", "gr:102");
                var edition = h.AddEdition(Credit());
                var links = new[] { h.Link(edition, first, false, "Voice Actor"), h.Link(edition, second) };
                h.Service.RebuildBookNarratorLinks(new[] { h.Book.Id });

                h.Service.UpsertEditionNarratorLinks(new[] { edition });
                h.Service.RebuildBookNarratorLinks(new[] { h.Book.Id });
                h.Service.UpsertEditionNarratorLinks(new[] { edition });

                Assert.Multiple(() =>
                {
                    Assert.That(h.Links.All().Select(l => l.Id), Is.EquivalentTo(links.Select(l => l.Id)));
                    Assert.That(h.Links.Get(links[0].Id).Role, Is.EqualTo("Voice Actor"));
                    Assert.That(h.Links.Get(links[0].Id).IsPrimary, Is.False);
                    Assert.That(h.BookLinks.All().Select(l => l.NarratorId), Is.EquivalentTo(new[] { first.Id, second.Id }));
                    Assert.That(h.Narrators.Count(), Is.EqualTo(2));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void mixed_credits_must_keep_unresolved_relations_and_remove_only_uncredited_people(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var retained = h.AddNarrator("Shared Narrator", "gr:101");
                h.AddNarrator("Shared Narrator", "gr:102");
                var removed = h.AddNarrator("Removed Narrator", "gr:103");
                var resolved = h.AddNarrator("Resolved Narrator", "gr:104");
                var edition = h.AddEdition(Credit(), Credit("Resolved Narrator", "gr:104"));
                var old = h.Link(edition, retained, false, "Voice Actor");
                h.Link(edition, removed);

                h.Service.UpsertEditionNarratorLinks(new[] { edition });
                h.Service.RebuildBookNarratorLinks(new[] { h.Book.Id });

                Assert.Multiple(() =>
                {
                    Assert.That(h.Links.Get(old.Id).NarratorId, Is.EqualTo(retained.Id));
                    Assert.That(h.Links.All().Select(l => l.NarratorId), Is.EquivalentTo(new[] { retained.Id, resolved.Id }));
                    Assert.That(h.BookLinks.All().Select(l => l.NarratorId), Is.EquivalentTo(new[] { retained.Id, resolved.Id }));
                    Assert.That(h.Narrators.Count(), Is.EqualTo(4));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void unresolved_provider_conflict_must_keep_existing_relations_without_changing_identity(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var first = h.AddNarrator("Shared Narrator", "gr:101", "hc:501");
                var second = h.AddNarrator("Shared Narrator", "gr:102", "hc:502");
                var edition = h.AddEdition(Credit(gr: "gr:101", hc: "hc:502"));
                var old = new[] { h.Link(edition, first, false, "Voice Actor"), h.Link(edition, second) };

                h.Service.UpsertEditionNarratorLinks(new[] { edition });
                h.Service.RebuildBookNarratorLinks(new[] { h.Book.Id });
                h.Service.UpsertEditionNarratorLinks(new[] { edition });

                Assert.Multiple(() =>
                {
                    Assert.That(h.Links.All().Select(l => l.Id), Is.EquivalentTo(old.Select(l => l.Id)));
                    Assert.That(h.Links.Get(old[0].Id).Role, Is.EqualTo("Voice Actor"));
                    Assert.That(h.Links.Get(old[0].Id).IsPrimary, Is.False);
                    Assert.That(h.BookLinks.All().Select(l => l.NarratorId), Is.EquivalentTo(new[] { first.Id, second.Id }));
                    Assert.That(h.Narrators.Count(), Is.EqualTo(2));
                    Assert.That(h.Metadata.Get(first.NarratorMetadataId).GoodreadsNarratorId, Is.EqualTo("gr:101"));
                    Assert.That(h.Metadata.Get(first.NarratorMetadataId).HardcoverNarratorId, Is.EqualTo("hc:501"));
                    Assert.That(h.Metadata.Get(second.NarratorMetadataId).GoodreadsNarratorId, Is.EqualTo("gr:102"));
                    Assert.That(h.Metadata.Get(second.NarratorMetadataId).HardcoverNarratorId, Is.EqualTo("hc:502"));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void a_name_only_companion_must_not_erase_relations_when_provider_evidence_is_unresolved(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var first = h.AddNarrator("Shared Narrator", "gr:101", "hc:501");
                var second = h.AddNarrator("Shared Narrator", "gr:102", "hc:502");
                var edition = h.AddEdition(Credit(), Credit(gr: "gr:101", hc: "hc:502"));
                var old = new[] { h.Link(edition, first, false, "Voice Actor"), h.Link(edition, second) };

                h.Service.UpsertEditionNarratorLinks(new[] { edition });
                h.Service.RebuildBookNarratorLinks(new[] { h.Book.Id });
                h.Service.UpsertEditionNarratorLinks(new[] { edition });

                Assert.Multiple(() =>
                {
                    Assert.That(h.Links.All().Select(l => l.Id), Is.EquivalentTo(old.Select(l => l.Id)));
                    Assert.That(h.Links.Get(old[0].Id).Role, Is.EqualTo("Voice Actor"));
                    Assert.That(h.Links.Get(old[0].Id).IsPrimary, Is.False);
                    Assert.That(h.BookLinks.All().Select(l => l.NarratorId), Is.EquivalentTo(new[] { first.Id, second.Id }));
                    Assert.That(h.Narrators.Count(), Is.EqualTo(2));
                    Assert.That(h.Metadata.Get(first.NarratorMetadataId).GoodreadsNarratorId, Is.EqualTo("gr:101"));
                    Assert.That(h.Metadata.Get(first.NarratorMetadataId).HardcoverNarratorId, Is.EqualTo("hc:501"));
                    Assert.That(h.Metadata.Get(second.NarratorMetadataId).GoodreadsNarratorId, Is.EqualTo("gr:102"));
                    Assert.That(h.Metadata.Get(second.NarratorMetadataId).HardcoverNarratorId, Is.EqualTo("hc:502"));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void an_unresolved_provider_conflict_must_keep_a_single_relation_without_rewriting_identity(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var previous = h.AddNarrator("Previous Narrator", "gr:101", "hc:501");
                var edition = h.AddEdition(Credit("Current Narrator", "gr:101", "hc:502"));
                var old = h.Link(edition, previous, false, "Voice Actor");

                h.Service.UpsertEditionNarratorLinks(new[] { edition });
                h.Service.RebuildBookNarratorLinks(new[] { h.Book.Id });
                h.Service.UpsertEditionNarratorLinks(new[] { edition });

                Assert.Multiple(() =>
                {
                    Assert.That(h.Links.All().Single().Id, Is.EqualTo(old.Id));
                    Assert.That(h.Links.Get(old.Id).Role, Is.EqualTo("Voice Actor"));
                    Assert.That(h.Links.Get(old.Id).IsPrimary, Is.False);
                    Assert.That(h.BookLinks.All().Single().NarratorId, Is.EqualTo(previous.Id));
                    Assert.That(h.Narrators.Count(), Is.EqualTo(1));
                    Assert.That(h.Metadata.Get(previous.NarratorMetadataId).Name, Is.EqualTo("Previous Narrator"));
                    Assert.That(h.Metadata.Get(previous.NarratorMetadataId).GoodreadsNarratorId, Is.EqualTo("gr:101"));
                    Assert.That(h.Metadata.Get(previous.NarratorMetadataId).HardcoverNarratorId, Is.EqualTo("hc:501"));
                });
            });
        }

        [TestCase(false, 0, 1)]
        [TestCase(true, 0, 1)]
        [TestCase(false, 2, 0)]
        [TestCase(true, 2, 0)]
        public void a_definite_current_primary_must_demote_a_retained_relation_without_changing_its_identity(bool postgres, int leadOrder, int retainedOrder)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var previous = h.AddNarrator("Shared Narrator", "gr:101");
                h.AddNarrator("Shared Narrator", "gr:102");
                var lead = h.AddNarrator("Current Lead", "gr:104");
                var unresolvedCredit = Credit();
                unresolvedCredit.IsPrimary = false;
                unresolvedCredit.Order = retainedOrder;
                var leadCredit = Credit("Current Lead", "gr:104");
                leadCredit.Order = leadOrder;
                var edition = h.AddEdition(unresolvedCredit, leadCredit);
                var old = h.Link(edition, previous, true, "Voice Actor");

                h.Service.UpsertEditionNarratorLinks(new[] { edition });
                h.Service.RebuildBookNarratorLinks(new[] { h.Book.Id });

                Assert.Multiple(() =>
                {
                    Assert.That(h.Links.Get(old.Id).NarratorId, Is.EqualTo(previous.Id));
                    Assert.That(h.Links.Get(old.Id).Role, Is.EqualTo("Voice Actor"));
                    Assert.That(h.Links.Get(old.Id).IsPrimary, Is.False);
                    Assert.That(h.Links.All().Single(link => link.IsPrimary).NarratorId, Is.EqualTo(lead.Id));
                    Assert.That(h.BookLinks.All().Single(link => link.IsPrimary).NarratorId, Is.EqualTo(lead.Id));
                    Assert.That(h.Metadata.Get(previous.NarratorMetadataId).GoodreadsNarratorId, Is.EqualTo("gr:101"));
                    Assert.That(h.Narrators.Count(), Is.EqualTo(3));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void a_retained_primary_demotion_must_roll_back_when_the_new_lead_cannot_be_inserted(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var previous = h.AddNarrator("Shared Narrator", "gr:101");
                h.AddNarrator("Shared Narrator", "gr:102");
                var lead = h.AddNarrator("Current Lead", "gr:104");
                var unresolvedCredit = Credit();
                unresolvedCredit.IsPrimary = false;
                unresolvedCredit.Order = 1;
                var edition = h.AddEdition(unresolvedCredit, Credit("Current Lead", "gr:104"));
                var old = h.Link(edition, previous, true, "Voice Actor");
                h.RejectInsertion(postgres, "EditionNarratorLink", lead.Id);

                Assert.Catch<Exception>(() => h.Service.UpsertEditionNarratorLinks(new[] { edition }));
                Assert.Multiple(() =>
                {
                    Assert.That(h.Links.All().Single().Id, Is.EqualTo(old.Id));
                    Assert.That(h.Links.Get(old.Id).IsPrimary, Is.True);
                    Assert.That(h.Links.Get(old.Id).Role, Is.EqualTo("Voice Actor"));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void an_unresolved_earliest_primary_must_not_be_replaced_by_a_later_resolved_credit(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var previous = h.AddNarrator("Shared Narrator", "gr:101");
                h.AddNarrator("Shared Narrator", "gr:102");
                var later = h.AddNarrator("Later Credit", "gr:104");
                var laterCredit = Credit("Later Credit", "gr:104");
                laterCredit.Order = 1;
                var edition = h.AddEdition(Credit(), laterCredit);
                var old = h.Link(edition, previous, true, "Voice Actor");

                h.Service.UpsertEditionNarratorLinks(new[] { edition });

                Assert.That(h.Links.Get(old.Id).IsPrimary, Is.True);
                Assert.That(h.Links.All().Single(link => link.NarratorId == later.Id).IsPrimary, Is.False);
                Assert.That(h.Links.All().Select(link => link.NarratorId), Is.EquivalentTo(new[] { previous.Id, later.Id }));
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void an_uncertain_lead_must_preserve_the_resolved_existing_primary_through_replacement(bool postgres, bool unresolvedCompanion)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var first = h.AddNarrator("First Narrator", "gr:101");
                var second = h.AddNarrator("Second Narrator", "gr:102");
                if (unresolvedCompanion)
                {
                    h.AddNarrator("Second Narrator", "gr:103");
                }

                var edition = h.AddEdition(Credit("First Narrator", "gr:101"),
                    Credit("Second Narrator", unresolvedCompanion ? null : "gr:102"));
                h.Link(edition, first);
                h.Link(edition, second, false);
                h.Service.RebuildBookNarratorLinks(new[] { h.Book.Id });

                h.Service.UpsertEditionNarratorLinks(new[] { edition });
                h.Service.RebuildBookNarratorLinks(new[] { h.Book.Id });
                h.Service.UpsertEditionNarratorLinks(new[] { edition });
                h.Service.RebuildBookNarratorLinks(new[] { h.Book.Id });

                Assert.Multiple(() =>
                {
                    Assert.That(h.Links.All().Single(link => link.NarratorId == first.Id).IsPrimary, Is.True);
                    Assert.That(h.Links.All().Single(link => link.NarratorId == second.Id).IsPrimary, Is.False);
                    Assert.That(h.Links.All().Select(link => link.NarratorId), Is.EquivalentTo(new[] { first.Id, second.Id }));
                    Assert.That(h.BookLinks.All().Single(link => link.IsPrimary).NarratorId, Is.EqualTo(first.Id));
                    Assert.That(h.Narrators.Count(), Is.EqualTo(unresolvedCompanion ? 3 : 2));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void a_proven_new_lead_must_replace_the_resolved_old_primary(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var first = h.AddNarrator("First Narrator", "gr:101");
                var second = h.AddNarrator("Second Narrator", "gr:102");
                var firstCredit = Credit("First Narrator", "gr:101");
                firstCredit.IsPrimary = false;
                firstCredit.Order = 1;
                var edition = h.AddEdition(firstCredit, Credit("Second Narrator", "gr:102"));
                h.Link(edition, first);
                h.Link(edition, second, false);

                h.Service.UpsertEditionNarratorLinks(new[] { edition });
                h.Service.RebuildBookNarratorLinks(new[] { h.Book.Id });

                Assert.Multiple(() =>
                {
                    Assert.That(h.Links.All().Single(link => link.NarratorId == first.Id).IsPrimary, Is.False);
                    Assert.That(h.Links.All().Single(link => link.NarratorId == second.Id).IsPrimary, Is.True);
                    Assert.That(h.BookLinks.All().Single(link => link.IsPrimary).NarratorId, Is.EqualTo(second.Id));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void tied_resolved_first_primary_credits_must_not_choose_either_person(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var first = h.AddNarrator("First Narrator", "gr:101");
                var second = h.AddNarrator("Second Narrator", "gr:102");
                var edition = h.AddEdition(Credit("First Narrator", "gr:101"), Credit("Second Narrator", "gr:102"));

                h.Service.UpsertEditionNarratorLinks(new[] { edition });

                Assert.That(h.Links.All().Select(link => link.IsPrimary), Is.All.False);
                Assert.That(h.Links.All().Select(link => link.NarratorId), Is.EquivalentTo(new[] { first.Id, second.Id }));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void tied_unresolved_and_resolved_first_credits_must_keep_the_existing_primary(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var previous = h.AddNarrator("Shared Narrator", "gr:101");
                h.AddNarrator("Shared Narrator", "gr:102");
                var other = h.AddNarrator("Other Credit", "gr:104");
                var edition = h.AddEdition(Credit(), Credit("Other Credit", "gr:104"));
                var old = h.Link(edition, previous, true, "Voice Actor");

                h.Service.UpsertEditionNarratorLinks(new[] { edition });

                Assert.Multiple(() =>
                {
                    Assert.That(h.Links.Get(old.Id).IsPrimary, Is.True);
                    Assert.That(h.Links.Get(old.Id).Role, Is.EqualTo("Voice Actor"));
                    Assert.That(h.Links.All().Single(link => link.NarratorId == other.Id).IsPrimary, Is.False);
                    Assert.That(h.Links.All().Select(link => link.NarratorId), Is.EquivalentTo(new[] { previous.Id, other.Id }));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void missing_primary_and_first_order_must_keep_the_existing_book_fallback(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var first = h.AddNarrator("First Narrator", "gr:101");
                var second = h.AddNarrator("Second Narrator", "gr:102");
                var firstCredit = Credit("First Narrator", "gr:101");
                firstCredit.IsPrimary = false;
                firstCredit.Order = 3;
                var secondCredit = Credit("Second Narrator", "gr:102");
                secondCredit.IsPrimary = false;
                secondCredit.Order = 4;
                var edition = h.AddEdition(firstCredit, secondCredit);

                h.Service.UpsertEditionNarratorLinks(new[] { edition });
                h.Service.RebuildBookNarratorLinks(new[] { h.Book.Id });

                Assert.That(h.Links.All().Select(link => link.IsPrimary), Is.All.False);
                Assert.That(h.BookLinks.All().Single(link => link.IsPrimary).NarratorId, Is.EqualTo(first.Id));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void uncertain_primary_preservation_must_reuse_the_loaded_ready_batch(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var first = h.AddNarrator("First Narrator", "gr:101");
                var second = h.AddNarrator("Second Narrator", "gr:102");
                var measurements = new List<(int Size, int Readers, int NonQueries, int Scalars, int Preserved)>();
                foreach (var size in new[] { 1, 50 })
                {
                    var editions = Enumerable.Range(0, size).Select(_ =>
                        h.AddEdition(Credit("First Narrator", "gr:101"), Credit("Second Narrator", "gr:102"))).ToArray();
                    foreach (var edition in editions)
                    {
                        h.Link(edition, first);
                        h.Link(edition, second, false);
                    }

                    var readers = 0;
                    var nonQueries = 0;
                    var scalars = 0;
                    var observed = InterfaceProxy.Proxy<IMainDatabase>((method, args) =>
                    {
                        if (method.Name != nameof(IMainDatabase.OpenConnection)) return method.Invoke(database, args);
                        var connection = database.OpenConnection();
                        return InterfaceProxy.Proxy<IDbConnection>((connectionMethod, connectionArgs) =>
                        {
                            var result = connectionMethod.Invoke(connection, connectionArgs);
                            if (result is not IDbCommand command) return result;
                            return InterfaceProxy.Proxy<IDbCommand>((commandMethod, commandArgs) =>
                            {
                                if (commandMethod.Name == nameof(IDbCommand.ExecuteReader)) readers++;
                                if (commandMethod.Name == nameof(IDbCommand.ExecuteNonQuery)) nonQueries++;
                                if (commandMethod.Name == nameof(IDbCommand.ExecuteScalar)) scalars++;
                                return commandMethod.Invoke(command, commandArgs);
                            });
                        });
                    });
                    var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                    var logger = LogManager.GetCurrentClassLogger();
                    var service = new NarratorLinkService(new EditionNarratorLinkRepository(observed, events),
                        new BookNarratorLinkRepository(observed, events), new NarratorMetadataRepository(observed, events, logger),
                        new NarratorRepository(observed, events), logger);
                    var timer = Stopwatch.StartNew();

                    service.UpsertEditionNarratorLinks(editions);

                    var ids = editions.Select(edition => edition.Id).ToHashSet();
                    var primaryCount = h.Links.All().Count(link => ids.Contains(link.EditionId) && link.IsPrimary && link.NarratorId == first.Id);
                    TestContext.WriteLine($"UNCERTAIN_PRIMARY postgres={postgres} editions={size} reader_commands={readers} nonquery_commands={nonQueries} scalar_commands={scalars} all_commands={readers + nonQueries + scalars} preserved={primaryCount} elapsed_ms={timer.Elapsed.TotalMilliseconds:F3}");
                    measurements.Add((size, readers, nonQueries, scalars, primaryCount));
                }

                Assert.Multiple(() =>
                {
                    foreach (var measurement in measurements)
                    {
                        Assert.That(measurement.Preserved, Is.EqualTo(measurement.Size));
                    }

                    Assert.That(measurements[1].Readers, Is.EqualTo(measurements[0].Readers));
                    Assert.That(measurements[1].Scalars, Is.EqualTo(measurements[0].Scalars));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void primary_demotion_must_use_one_set_update_for_a_ready_edition_batch(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var previous = h.AddNarrator("Shared Narrator", "gr:101");
                h.AddNarrator("Shared Narrator", "gr:102");
                var lead = h.AddNarrator("Current Lead", "gr:104");
                var editions = Enumerable.Range(0, 50).Select(_ =>
                {
                    var unresolvedCredit = Credit();
                    unresolvedCredit.IsPrimary = false;
                    unresolvedCredit.Order = 1;
                    return h.AddEdition(unresolvedCredit, Credit("Current Lead", "gr:104"));
                }).ToArray();
                var old = editions.Select(edition => h.Link(edition, previous)).ToList();
                var statements = new List<string>();
                var observed = InterfaceProxy.Proxy<IMainDatabase>((method, args) =>
                {
                    if (method.Name != nameof(IMainDatabase.OpenConnection)) return method.Invoke(database, args);
                    var connection = database.OpenConnection();
                    return InterfaceProxy.Proxy<IDbConnection>((connectionMethod, connectionArgs) =>
                    {
                        var result = connectionMethod.Invoke(connection, connectionArgs);
                        if (result is not IDbCommand command) return result;
                        return InterfaceProxy.Proxy<IDbCommand>((commandMethod, commandArgs) =>
                        {
                            if (commandMethod.Name is nameof(IDbCommand.ExecuteReader) or nameof(IDbCommand.ExecuteNonQuery) or nameof(IDbCommand.ExecuteScalar))
                                statements.Add(command.CommandText);
                            return commandMethod.Invoke(command, commandArgs);
                        });
                    });
                });
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var logger = LogManager.GetCurrentClassLogger();
                var service = new NarratorLinkService(new EditionNarratorLinkRepository(observed, events),
                    new BookNarratorLinkRepository(observed, events), new NarratorMetadataRepository(observed, events, logger),
                    new NarratorRepository(observed, events), logger);
                var timer = Stopwatch.StartNew();

                service.UpsertEditionNarratorLinks(editions);

                var primaryUpdates = statements.Count(sql => sql.StartsWith(@"UPDATE ""EditionNarratorLink"" SET ""IsPrimary""", StringComparison.Ordinal));
                TestContext.WriteLine($"NARRATOR_PRIMARY editions={editions.Length} primary_updates={primaryUpdates} elapsed_ms={timer.Elapsed.TotalMilliseconds:F3}");
                Assert.Multiple(() =>
                {
                    Assert.That(primaryUpdates, Is.EqualTo(1));
                    Assert.That(h.Links.All().Where(link => link.IsPrimary).Count(), Is.EqualTo(editions.Length));
                    Assert.That(h.Links.All().Where(link => link.IsPrimary).Select(link => link.NarratorId), Is.All.EqualTo(lead.Id));
                    Assert.That(h.Links.All().Where(link => link.NarratorId == previous.Id).Select(link => link.Id), Is.EquivalentTo(old.Select(link => link.Id)));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void an_inconclusive_provider_credit_must_keep_only_its_existing_relation_after_a_name_change(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var previous = h.AddNarrator("Previous Narrator", "gr:101");
                h.AddNarrator("Other Narrator", "gr:101");
                var edition = h.AddEdition(Credit("Current Narrator", "gr:101"));
                var old = h.Link(edition, previous, false, "Voice Actor");

                h.Service.UpsertEditionNarratorLinks(new[] { edition });
                h.Service.RebuildBookNarratorLinks(new[] { h.Book.Id });

                Assert.Multiple(() =>
                {
                    Assert.That(h.Links.All().Single().Id, Is.EqualTo(old.Id));
                    Assert.That(h.BookLinks.All().Single().NarratorId, Is.EqualTo(previous.Id));
                    Assert.That(h.Narrators.Count(), Is.EqualTo(2));
                    Assert.That(h.Metadata.Get(previous.NarratorMetadataId).Name, Is.EqualTo("Previous Narrator"));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void a_folded_name_collision_must_keep_the_existing_relation_without_merging_people(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var previous = h.AddNarrator("José Narrator", "gr:101");
                h.AddNarrator("Jose Narrator", "gr:102");
                var edition = h.AddEdition(Credit("Jose Narrator"));
                var old = h.Link(edition, previous);

                h.Service.UpsertEditionNarratorLinks(new[] { edition });
                h.Service.RebuildBookNarratorLinks(new[] { h.Book.Id });

                Assert.Multiple(() =>
                {
                    Assert.That(h.Links.All().Single().Id, Is.EqualTo(old.Id));
                    Assert.That(h.BookLinks.All().Single().NarratorId, Is.EqualTo(previous.Id));
                    Assert.That(h.Narrators.Count(), Is.EqualTo(2));
                    Assert.That(h.Metadata.Get(previous.NarratorMetadataId).Name, Is.EqualTo("José Narrator"));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void resolved_provider_identity_must_replace_a_different_homonym_relation(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var previous = h.AddNarrator("Shared Narrator", "gr:101");
                var current = h.AddNarrator("Shared Narrator", "gr:102");
                var edition = h.AddEdition(Credit(gr: "gr:102"));
                h.Link(edition, previous);

                h.Service.UpsertEditionNarratorLinks(new[] { edition });
                h.Service.RebuildBookNarratorLinks(new[] { h.Book.Id });

                Assert.That(h.Links.All().Single().NarratorId, Is.EqualTo(current.Id));
                Assert.That(h.BookLinks.All().Single().NarratorId, Is.EqualTo(current.Id));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void empty_transient_credits_on_a_loaded_edition_must_not_mean_cast_removal(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var narrator = h.AddNarrator("Shared Narrator", "gr:101");
                var edition = h.AddEdition(Credit(gr: "gr:101"));
                var link = h.Link(edition, narrator);
                var loaded = h.Editions.Get(edition.Id);
                Assert.That(loaded.NarratorCredits, Is.Empty);

                h.Service.UpsertEditionNarratorLinks(new[] { loaded });

                Assert.That(h.Links.All().Single().Id, Is.EqualTo(link.Id));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void an_edition_projection_insert_failure_must_roll_back_its_deletions(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var previous = h.AddNarrator("Previous Narrator", "gr:101");
                var current = h.AddNarrator("Current Narrator", "gr:102");
                var edition = h.AddEdition(Credit("Current Narrator", "gr:102"));
                var old = h.Link(edition, previous);
                h.RejectInsertion(postgres, "EditionNarratorLink", current.Id);

                Assert.That(() => h.Service.UpsertEditionNarratorLinks(new[] { edition }), Throws.Exception);

                Assert.That(h.Links.All().Single().Id, Is.EqualTo(old.Id));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void a_book_projection_failure_must_keep_old_book_links_without_reverting_edition_links(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var previous = h.AddNarrator("Previous Narrator", "gr:101");
                var current = h.AddNarrator("Current Narrator", "gr:102");
                var edition = h.AddEdition(Credit("Current Narrator", "gr:102"));
                h.Link(edition, previous);
                h.Service.RebuildBookNarratorLinks(new[] { h.Book.Id });
                var old = h.BookLinks.All().Single();
                h.RejectInsertion(postgres, "BookNarratorLink", current.Id);

                h.Service.UpsertEditionNarratorLinks(new[] { edition });
                Assert.That(() => h.Service.RebuildBookNarratorLinks(new[] { h.Book.Id }), Throws.Exception);

                Assert.That(h.BookLinks.All().Single().Id, Is.EqualTo(old.Id));
                Assert.That(h.Links.All().Single().NarratorId, Is.EqualTo(current.Id));
            });
        }
    }
}
