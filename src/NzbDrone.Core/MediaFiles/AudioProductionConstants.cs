using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Utilities;

namespace NzbDrone.Core.MediaFiles
{
    public static class AudioProductionConstants
    {
        public const string DetectedDramatizedFullCastType = "Dramatized / Full-Cast";

        internal enum EvidenceKind { Brand, DramatizedPhrase, CastCredit }

        internal sealed record Term(string Text, EvidenceKind Kind, bool Production = true, bool Exact = false, bool EditionOnly = false)
        {
            public string Normalized { get; } = UnicodeComparisonNormalizer.NormalizeWords(Text).Trim();
        }

        // One vocabulary for production evidence and group credits. Edition-only terms do not change acquisition evidence.
        // Cast credits are never filename/folder evidence; Chaptarr generates cast folder names itself.
        // I'll circle back to this to round out production cues in other languages later.
        internal static readonly IReadOnlyDictionary<string, Term[]> ByLanguage = new Dictionary<string, Term[]>
        {
            ["und"] = new[] { new Term("GraphicAudio", EvidenceKind.Brand), new Term("Graphic Audio", EvidenceKind.Brand) },
            ["eng"] = new[]
            {
                new Term("movie in your mind", EvidenceKind.DramatizedPhrase),
                new Term("a movie in your mind", EvidenceKind.DramatizedPhrase),
                new Term("dramatized", EvidenceKind.DramatizedPhrase),
                new Term("dramatised", EvidenceKind.DramatizedPhrase),
                new Term("dramatized adaptation", EvidenceKind.DramatizedPhrase),
                new Term("dramatised adaptation", EvidenceKind.DramatizedPhrase),
                new Term("radio dramatization", EvidenceKind.DramatizedPhrase),
                new Term("radio dramatisation", EvidenceKind.DramatizedPhrase),
                new Term("radio drama", EvidenceKind.DramatizedPhrase),
                new Term("radio theatre", EvidenceKind.DramatizedPhrase, EditionOnly: true),
                new Term("radio theater", EvidenceKind.DramatizedPhrase, EditionOnly: true),
                new Term("dramatization", EvidenceKind.DramatizedPhrase, EditionOnly: true),
                new Term("dramatisation", EvidenceKind.DramatizedPhrase, EditionOnly: true),
                new Term("dramatic reading", EvidenceKind.DramatizedPhrase, EditionOnly: true),
                new Term("audio drama", EvidenceKind.DramatizedPhrase),
                new Term("audio theatre", EvidenceKind.DramatizedPhrase),
                new Term("audio theater", EvidenceKind.DramatizedPhrase),
                new Term("immersive audio experience", EvidenceKind.DramatizedPhrase),
                new Term("sound effects and music", EvidenceKind.DramatizedPhrase),
                new Term("fullcast", EvidenceKind.CastCredit),
                new Term("full cast", EvidenceKind.CastCredit),
                new Term("full cast audio", EvidenceKind.CastCredit),
                new Term("full cast production", EvidenceKind.CastCredit),
                new Term("multicast", EvidenceKind.CastCredit),
                new Term("multi cast", EvidenceKind.CastCredit),
                new Term("cast performance", EvidenceKind.CastCredit),
                new Term("ensemble cast", EvidenceKind.CastCredit),
                new Term("voice cast", EvidenceKind.CastCredit),
                new Term("cast", EvidenceKind.CastCredit, Exact: true),
                new Term("various", EvidenceKind.CastCredit, false, true),
                new Term("cast album", EvidenceKind.CastCredit, false, true)
            },
            ["deu"] = new[]
            {
                new Term("diverse", EvidenceKind.CastCredit, false, true),
                new Term("diverse sprecher", EvidenceKind.CastCredit, false, true)
            },
            ["spa"] = new[] { new Term("varios narradores", EvidenceKind.CastCredit, false, true) },
            ["ita"] = new[] { new Term("narratori vari", EvidenceKind.CastCredit, false, true) }
        };

        private static readonly Term[] Terms = ByLanguage.Values.SelectMany(terms => terms).ToArray();

        internal static bool Matches(string value, bool nameEvidence = false, bool castLabel = false, bool exact = false, EvidenceKind? kind = null, bool titleQualifier = false, bool editionClassification = false)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var normalized = UnicodeComparisonNormalizer.NormalizeWords(value).Trim();
            var bounded = " " + normalized + " ";
            return Terms.Any(term => (!term.EditionOnly || editionClassification) && (!kind.HasValue || term.Kind == kind) && (!nameEvidence || term.Kind != EvidenceKind.CastCredit) &&
                (castLabel || term.Production) && (!titleQualifier || !term.Exact) &&
                (exact || term.Exact ? normalized == term.Normalized : bounded.Contains(" " + term.Normalized + " ", StringComparison.Ordinal)));
        }

        public static readonly string[] AudiobookIndicators = { "audible", "audio", "audiobook", "audio book", "unabridged", "abridged" };
    }
}
