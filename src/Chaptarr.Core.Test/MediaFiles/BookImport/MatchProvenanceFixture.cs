using System.Collections.Generic;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore.Converters;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class MatchProvenanceFixture
    {
        [Test]
        public void clone_for_destination_should_preserve_decision_and_stamp_provider_identity()
        {
            var source = new MatchProvenance
            {
                DecisionId = "decision-1",
                Mode = "Balanced",
                Route = "global/embedded_tags",
                SupportingSignals = new List<MatchSignal>
                {
                    new MatchSignal
                    {
                        Type = "title",
                        Scope = "book",
                        Field = "TITLE",
                        Observed = "Dune",
                        Expected = "Dune"
                    }
                },
                EvidenceValues = new List<MatchEvidenceValue>
                {
                    new MatchEvidenceValue
                    {
                        Source = "embedded_tag",
                        Value = "Dune (Unabridged)",
                        Fields = new List<string> { "TITLE", "MP4:©nam" },
                        Ranges = new List<MatchEvidenceRange>
                        {
                            new MatchEvidenceRange
                            {
                                Start = 0,
                                End = 4,
                                Disposition = "supporting",
                                Type = "title",
                                Scope = "book"
                            }
                        }
                    }
                }
            };
            var author = new Author { HardcoverAuthorId = "123" };
            var book = new Book { HardcoverBookId = "456" };
            var edition = new Edition { HardcoverEditionId = "789" };

            var result = source.CloneForDestination(author, book, edition);

            Assert.That(result.DecisionId, Is.EqualTo("decision-1"));
            Assert.That(result.Mode, Is.EqualTo("Balanced"));
            Assert.That(result.AuthorProviderIds, Does.Contain("hc:123"));
            Assert.That(result.BookProviderIds, Does.Contain("hc:456"));
            Assert.That(result.EditionProviderIds, Does.Contain("hc:789"));
            Assert.That(result.SupportingSignals, Has.Count.EqualTo(1));
            Assert.That(result.SupportingSignals[0], Is.Not.SameAs(source.SupportingSignals[0]));
            Assert.That(result.EvidenceValues, Has.Count.EqualTo(1));
            Assert.That(result.EvidenceValues[0], Is.Not.SameAs(source.EvidenceValues[0]));
            Assert.That(result.EvidenceValues[0].Ranges[0], Is.Not.SameAs(source.EvidenceValues[0].Ranges[0]));
            Assert.That(result.EvidenceValues[0].Value.Substring(0, 4), Is.EqualTo("Dune"));
        }

        [Test]
        public void manual_selection_should_be_explicit_and_not_invent_automatic_evidence()
        {
            var result = MatchProvenance.ManualSelection(
                new Author { GoodreadsAuthorId = "1" },
                new Book { GoodreadsWorkId = "2" },
                new Edition { GoodreadsEditionId = 3, Title = "Dune" });

            Assert.That(result.Mode, Is.EqualTo("Manual"));
            Assert.That(result.Route, Is.EqualTo("manual_selection"));
            Assert.That(result.SupportingSignals, Has.Count.EqualTo(1));
            Assert.That(result.SupportingSignals[0].Type, Is.EqualTo("manual_selection"));
            Assert.That(result.ConflictingSignals, Is.Empty);
            Assert.That(result.NeutralSignals, Is.Empty);
            Assert.That(result.ExcludedSignals, Is.Empty);
        }

        [Test]
        public void embedded_document_should_round_trip_the_four_signal_buckets_through_real_sqlite_ddl()
        {
            SqlMapper.AddTypeHandler(new EmbeddedDocumentConverter<MatchProvenance>());
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            connection.Execute("CREATE TABLE BookFiles (MatchProvenance TEXT NULL);");

            var source = new MatchProvenance
            {
                DecisionId = "decision-roundtrip",
                Mode = "Strict",
                Route = "author_scoped/embedded_tags",
                SupportingSignals = new List<MatchSignal> { new MatchSignal { Type = "title", Field = "TITLE" } },
                ConflictingSignals = new List<MatchSignal> { new MatchSignal { Type = "series_position", Scope = "book" } },
                NeutralSignals = new List<MatchSignal> { new MatchSignal { Type = "duration", Scope = "edition" } },
                ExcludedSignals = new List<MatchSignal> { new MatchSignal { Type = "ignored_tag", Field = "COMMENT" } },
                EvidenceValues = new List<MatchEvidenceValue>
                {
                    new MatchEvidenceValue
                    {
                        Source = "embedded_tag",
                        Value = "Dune #2",
                        Fields = new List<string> { "TITLE" },
                        Ranges = new List<MatchEvidenceRange>
                        {
                            new MatchEvidenceRange
                            {
                                Start = 0,
                                End = 4,
                                Disposition = "supporting",
                                Type = "title",
                                Scope = "book",
                                Detail = "Title proof"
                            },
                            new MatchEvidenceRange
                            {
                                Start = 6,
                                End = 7,
                                Disposition = "conflicting",
                                Type = "series_position",
                                Scope = "book",
                                Detail = "Position conflict"
                            }
                        }
                    }
                }
            };

            connection.Execute("INSERT INTO BookFiles (MatchProvenance) VALUES (@source);", new { source });
            var result = connection.QuerySingle<MatchProvenance>("SELECT MatchProvenance FROM BookFiles;");

            Assert.That(result.DecisionId, Is.EqualTo("decision-roundtrip"));
            Assert.That(result.SupportingSignals.Single().Type, Is.EqualTo("title"));
            Assert.That(result.ConflictingSignals.Single().Type, Is.EqualTo("series_position"));
            Assert.That(result.NeutralSignals.Single().Type, Is.EqualTo("duration"));
            Assert.That(result.ExcludedSignals.Single().Field, Is.EqualTo("COMMENT"));
            Assert.That(result.SchemaVersion, Is.EqualTo(2));
            Assert.That(result.EvidenceValues.Single().Value, Is.EqualTo("Dune #2"));
            Assert.That(result.EvidenceValues.Single().Ranges.Select(range => range.Disposition),
                Is.EquivalentTo(new[] { "supporting", "conflicting" }));
        }
    }
}
