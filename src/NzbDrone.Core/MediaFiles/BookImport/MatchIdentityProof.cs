using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.MediaFiles.BookImport.Services;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    internal enum MatchIdentityRole
    {
        Author,
        Title,
        ProviderIdentifier
    }

    /// <summary>
    /// One untruncated value that actually proved identity at the matcher decision site.
    /// This is deliberately separate from MatchProvenance: provenance is a persisted,
    /// display-oriented payload and may window long values for the UI.
    /// </summary>
    internal sealed class MatchIdentityProofValue
    {
        public MatchIdentityProofValue(
            MatchIdentityRole role,
            string source,
            string field,
            string observed,
            string expected,
            string scope,
            string detail)
        {
            Role = role;
            Source = source;
            Field = field;
            Observed = observed;
            Expected = expected;
            Scope = scope;
            Detail = detail;
        }

        public MatchIdentityRole Role { get; }
        public string Source { get; }
        public string Field { get; }
        public string Observed { get; }
        public string Expected { get; }
        public string Scope { get; }
        public string Detail { get; }
    }

    /// <summary>
    /// Immutable identity proof retained with an in-memory match decision.
    /// Multipart fast-grouping may only reuse a decision when the same physical
    /// source, logical field, and raw value are present on the member file.
    /// </summary>
    internal sealed class MatchIdentityProof
    {
        public MatchIdentityProof(IEnumerable<MatchIdentityProofValue> values)
        {
            Values = (values ?? Enumerable.Empty<MatchIdentityProofValue>())
                .Where(value => value != null &&
                                !string.IsNullOrWhiteSpace(value.Source) &&
                                !string.IsNullOrWhiteSpace(value.Field) &&
                                !string.IsNullOrWhiteSpace(value.Observed))
                .GroupBy(
                    value => new
                    {
                        value.Role,
                        Source = value.Source.ToLowerInvariant(),
                        Field = value.Field.ToLowerInvariant(),
                        value.Observed
                    })
                .Select(group => group.First())
                .ToList()
                .AsReadOnly();
        }

        public IReadOnlyList<MatchIdentityProofValue> Values { get; }

        public bool Has(MatchIdentityRole role)
        {
            return Values.Any(value => value.Role == role);
        }
    }

    internal static class MatchIdentityProofMembership
    {
        public static bool HasRequiredIdentity(MatchIdentityProof proof)
        {
            return proof != null &&
                   (proof.Has(MatchIdentityRole.ProviderIdentifier) ||
                    (proof.Has(MatchIdentityRole.Author) && proof.Has(MatchIdentityRole.Title)));
        }

        public static MatchIdentityProof CommonProof(IEnumerable<MatchIdentityProof> proofs)
        {
            var all = (proofs ?? Enumerable.Empty<MatchIdentityProof>()).ToList();
            if (all.Count == 0 || all.Any(proof => proof == null))
            {
                return new MatchIdentityProof(Array.Empty<MatchIdentityProofValue>());
            }

            var common = all[0].Values
                .Where(value => all.Skip(1).All(proof => proof.Values.Any(candidate => SameTuple(value, candidate))))
                .ToList();
            return new MatchIdentityProof(common);
        }

        public static MatchIdentityProof Intersect(
            MatchIdentityProof seedProof,
            IDictionary<string, List<string>> embeddedTags,
            IDictionary<string, List<string>> pathTags)
        {
            if (seedProof == null)
            {
                return new MatchIdentityProof(Array.Empty<MatchIdentityProofValue>());
            }

            var matched = new List<MatchIdentityProofValue>();
            foreach (var proofValue in seedProof.Values)
            {
                var sourceTags = string.Equals(proofValue.Source, "embedded_tag", StringComparison.OrdinalIgnoreCase)
                    ? embeddedTags
                    : pathTags;
                if (sourceTags == null ||
                    !sourceTags.TryGetValue(proofValue.Field, out var values) ||
                    values == null ||
                    !values.Any(value => string.Equals(value, proofValue.Observed, StringComparison.Ordinal)))
                {
                    continue;
                }

                matched.Add(proofValue);
            }

            return new MatchIdentityProof(matched);
        }

        private static bool SameTuple(MatchIdentityProofValue left, MatchIdentityProofValue right)
        {
            return left != null && right != null &&
                   left.Role == right.Role &&
                   string.Equals(left.Source, right.Source, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(left.Field, right.Field, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(left.Observed, right.Observed, StringComparison.Ordinal);
        }
    }

    internal static class MatchIdentityProofFactory
    {
        public static MatchIdentityProof FromExpectedIdentity(
            string authorName,
            IEnumerable<string> titleCandidates,
            IDictionary<string, List<string>> tags,
            IContainmentValidator containmentValidator)
        {
            var matchableTags = (tags ?? new Dictionary<string, List<string>>())
                .Where(pair => !TagExclusionPolicy.IsExcludedFromMatching(pair.Key) && pair.Value != null)
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Where(value => !string.IsNullOrWhiteSpace(value)).ToList(),
                    StringComparer.OrdinalIgnoreCase);
            var values = new List<MatchIdentityProofValue>();
            if (containmentValidator == null || matchableTags.Count == 0)
            {
                return new MatchIdentityProof(values);
            }

            if (!string.IsNullOrWhiteSpace(authorName))
            {
                foreach (var field in matchableTags)
                {
                    foreach (var observed in field.Value)
                    {
                        var singleField = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            [field.Key] = new List<string> { observed }
                        };
                        if (containmentValidator.ValidateAuthorInTags(authorName, singleField))
                        {
                            values.Add(new MatchIdentityProofValue(
                                MatchIdentityRole.Author,
                                "embedded_tag",
                                field.Key,
                                observed,
                                authorName,
                                "book",
                                "This field proved the author identity returned by discovery."));
                        }
                    }
                }
            }

            foreach (var title in (titleCandidates ?? Enumerable.Empty<string>())
                         .Where(title => !string.IsNullOrWhiteSpace(title))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var titleEvidence = containmentValidator.GetEditionTitleEvidence(title, matchableTags) ??
                                    Array.Empty<EditionTitleEvidence>();
                foreach (var evidence in titleEvidence.Where(evidence =>
                             evidence != null &&
                             !string.IsNullOrWhiteSpace(evidence.FieldName) &&
                             !string.IsNullOrWhiteSpace(evidence.FieldValue)))
                {
                    values.Add(new MatchIdentityProofValue(
                        MatchIdentityRole.Title,
                        "embedded_tag",
                        evidence.FieldName,
                        evidence.FieldValue,
                        evidence.MatchedTitle ?? title,
                        "book",
                        "This field proved the book identity returned by discovery."));
                }

                if (values.Any(value => value.Role == MatchIdentityRole.Title))
                {
                    break;
                }
            }

            return new MatchIdentityProof(values);
        }
    }
}
