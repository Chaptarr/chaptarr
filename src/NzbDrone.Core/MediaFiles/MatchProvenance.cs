using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.MediaFiles
{
    public sealed class MatchProvenance : IEmbeddedDocument
    {
        public const int CurrentSchemaVersion = 2;
        public const string CurrentMatcherVersion = "evidence-v2.7-exact-group-spans";

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string MatcherVersion { get; set; } = CurrentMatcherVersion;
        public string DecisionId { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime MatchedAtUtc { get; set; } = DateTime.UtcNow;
        public string Mode { get; set; }
        public string Route { get; set; }
        public string MatchedVia { get; set; }
        public string SelectionSource { get; set; }
        public string Summary { get; set; }
        public List<string> AuthorProviderIds { get; set; } = new List<string>();
        public List<string> BookProviderIds { get; set; } = new List<string>();
        public List<string> EditionProviderIds { get; set; } = new List<string>();
        public List<MatchSignal> SupportingSignals { get; set; } = new List<MatchSignal>();
        public List<MatchSignal> ConflictingSignals { get; set; } = new List<MatchSignal>();
        public List<MatchSignal> NeutralSignals { get; set; } = new List<MatchSignal>();
        public List<MatchSignal> ExcludedSignals { get; set; } = new List<MatchSignal>();
        public List<MatchEvidenceValue> EvidenceValues { get; set; } = new List<MatchEvidenceValue>();

        public MatchProvenance CloneForDestination(Author author, Book book, Edition edition)
        {
            var clone = Clone();

            clone.AuthorProviderIds = Distinct(AuthorIdentity.GetProviderIdentityTokenList(author));
            clone.BookProviderIds = Distinct(BookEditionIdentity.GetCanonicalWorkProviderIds(book));
            clone.EditionProviderIds = Distinct(BookEditionIdentity.GetEditionProviderIds(edition));
            return clone;
        }

        public MatchProvenance Clone()
        {
            return new MatchProvenance
            {
                SchemaVersion = SchemaVersion,
                MatcherVersion = MatcherVersion,
                DecisionId = DecisionId,
                MatchedAtUtc = MatchedAtUtc,
                Mode = Mode,
                Route = Route,
                MatchedVia = MatchedVia,
                SelectionSource = SelectionSource,
                Summary = Summary,
                AuthorProviderIds = Distinct(AuthorProviderIds),
                BookProviderIds = Distinct(BookProviderIds),
                EditionProviderIds = Distinct(EditionProviderIds),
                SupportingSignals = CloneSignals(SupportingSignals),
                ConflictingSignals = CloneSignals(ConflictingSignals),
                NeutralSignals = CloneSignals(NeutralSignals),
                ExcludedSignals = CloneSignals(ExcludedSignals),
                EvidenceValues = CloneEvidenceValues(EvidenceValues)
            };
        }

        public static MatchProvenance ManualSelection(Author author, Book book, Edition edition)
        {
            var provenance = new MatchProvenance
            {
                Mode = "Manual",
                Route = "manual_selection",
                MatchedVia = "manual_selection",
                SelectionSource = "user_local",
                Summary = "Edition selected manually"
            };

            provenance.SupportingSignals.Add(new MatchSignal
            {
                Type = "manual_selection",
                Scope = "edition",
                Source = "user",
                Expected = edition?.Title,
                Detail = "The user explicitly selected this edition."
            });

            return provenance.CloneForDestination(author, book, edition);
        }

        public static MatchProvenance UserMetadataSelection(Author author, Book book, Edition edition)
        {
            var provenance = new MatchProvenance
            {
                Mode = "Manual",
                Route = "user_metadata_selection",
                MatchedVia = "user_metadata_selection",
                SelectionSource = "user_metadata",
                Summary = "Authoritative metadata edition accepted explicitly by the user"
            };

            provenance.SupportingSignals.Add(new MatchSignal
            {
                Type = "manual_selection",
                Scope = "edition",
                Source = "user",
                Expected = edition?.Title,
                Detail = "The user explicitly accepted this provider edition; tag and duration evidence are diagnostic rather than vetoes."
            });

            return provenance.CloneForDestination(author, book, edition);
        }

        private static List<MatchSignal> CloneSignals(IEnumerable<MatchSignal> signals)
        {
            return (signals ?? Enumerable.Empty<MatchSignal>())
                .Where(signal => signal != null)
                .Select(signal => signal.Clone())
                .ToList();
        }

        private static List<MatchEvidenceValue> CloneEvidenceValues(IEnumerable<MatchEvidenceValue> values)
        {
            return (values ?? Enumerable.Empty<MatchEvidenceValue>())
                .Where(value => value != null)
                .Select(value => value.Clone())
                .ToList();
        }

        private static List<string> Distinct(IEnumerable<string> values)
        {
            return (values ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public sealed class MatchSignal
    {
        public string Type { get; set; }
        public string Scope { get; set; }
        public string Source { get; set; }
        public string Field { get; set; }
        public string Observed { get; set; }
        public string Expected { get; set; }
        public string Detail { get; set; }

        public MatchSignal Clone()
        {
            return new MatchSignal
            {
                Type = Type,
                Scope = Scope,
                Source = Source,
                Field = Field,
                Observed = Observed,
                Expected = Expected,
                Detail = Detail
            };
        }
    }

    public sealed class MatchEvidenceValue
    {
        public string Source { get; set; }
        public string Value { get; set; }
        public List<string> Fields { get; set; } = new List<string>();
        public List<MatchEvidenceRange> Ranges { get; set; } = new List<MatchEvidenceRange>();

        public MatchEvidenceValue Clone()
        {
            return new MatchEvidenceValue
            {
                Source = Source,
                Value = Value,
                Fields = (Fields ?? new List<string>()).ToList(),
                Ranges = (Ranges ?? new List<MatchEvidenceRange>())
                    .Where(range => range != null)
                    .Select(range => range.Clone())
                    .ToList()
            };
        }
    }

    public sealed class MatchEvidenceRange
    {
        public int Start { get; set; }
        public int End { get; set; }
        public string Disposition { get; set; }
        public string Type { get; set; }
        public string Scope { get; set; }
        public string Detail { get; set; }

        public MatchEvidenceRange Clone()
        {
            return new MatchEvidenceRange
            {
                Start = Start,
                End = End,
                Disposition = Disposition,
                Type = Type,
                Scope = Scope,
                Detail = Detail
            };
        }
    }
}
