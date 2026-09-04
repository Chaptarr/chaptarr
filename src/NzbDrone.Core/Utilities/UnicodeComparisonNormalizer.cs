using System;
using System.Globalization;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Utilities
{
    public sealed class NormalizedMappedText
    {
        public string Text { get; set; }
        public List<(int Start, int End)> SourceSpans { get; set; }
    }

    public static class UnicodeComparisonNormalizer
    {
        private static readonly Regex CollapseWhitespaceRegex = new Regex(@"\s+", RegexOptions.Compiled);

        public static string NormalizeKey(string text, bool stripDiacritics = true)
        {
            return NormalizeInternal(text, preserveWhitespace: false, stripDiacritics);
        }

        public static string NormalizeWords(string text, bool stripDiacritics = true)
        {
            return NormalizeInternal(text, preserveWhitespace: true, stripDiacritics);
        }

        public static NormalizedMappedText NormalizeWordsWithSourceSpans(string text, IReadOnlyList<(int Start, int End)> sourceSpans, bool stripDiacritics = true)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new NormalizedMappedText
                {
                    Text = string.Empty,
                    SourceSpans = new List<(int Start, int End)>()
                };
            }

            if (sourceSpans == null || sourceSpans.Count != text.Length)
            {
                var generatedSpans = new List<(int Start, int End)>(text.Length);
                for (var spanIndex = 0; spanIndex < text.Length; spanIndex++)
                {
                    generatedSpans.Add((spanIndex, spanIndex + 1));
                }

                sourceSpans = generatedSpans;
            }

            // Sanitize first so every remaining surrogate is part of a valid pair - this guarantees the
            // per-scalar Normalize() call below can never throw (Chaptarr/chaptarr#116), without needing
            // a try/catch on this hot RSS-matching path. Length-preserving, so `sourceSpans` (indexed by
            // original UTF-16 code unit) stays valid against the sanitized text.
            text = SanitizeUnpairedSurrogates(text);

            var builder = new StringBuilder(text.Length);
            var spans = new List<(int Start, int End)>();
            var pendingSeparator = false;
            (int Start, int End)? pendingSeparatorSpan = null;

            var i = 0;
            while (i < text.Length)
            {
                // Walk one Unicode scalar value at a time (not one UTF-16 code unit at a time) so that
                // a surrogate pair - e.g. an emoji or a CJK Extension ideograph outside the Basic
                // Multilingual Plane - is normalized as a whole character instead of being split into
                // two lone surrogates, which String.Normalize() rejects with
                // "String contains invalid Unicode code points" (Chaptarr/chaptarr#116).
                var codePointLength = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                    ? 2
                    : 1;

                var sourceSpan = codePointLength == 2
                    ? (sourceSpans[i].Start, sourceSpans[i + 1].End)
                    : sourceSpans[i];

                var decomposed = text.Substring(i, codePointLength).Normalize(NormalizationForm.FormD);
                var letterOrDigitSegment = new StringBuilder(decomposed.Length);

                foreach (var rune in decomposed.EnumerateRunes())
                {
                    var category = Rune.GetUnicodeCategory(rune);

                    if (stripDiacritics &&
                        (category == UnicodeCategory.NonSpacingMark ||
                         category == UnicodeCategory.SpacingCombiningMark ||
                         category == UnicodeCategory.EnclosingMark))
                    {
                        continue;
                    }

                    if (rune.Value == '\uFFFD')
                    {
                        if (builder.Length > 0)
                        {
                            pendingSeparator = true;
                            pendingSeparatorSpan ??= sourceSpan;
                        }

                        continue;
                    }

                    if (Rune.IsLetterOrDigit(rune))
                    {
                        letterOrDigitSegment.Append(Rune.ToLowerInvariant(rune).ToString());
                        continue;
                    }

                    if (IsSeparator(rune, category) && builder.Length > 0)
                    {
                        pendingSeparator = true;
                        pendingSeparatorSpan ??= sourceSpan;
                    }
                }

                if (letterOrDigitSegment.Length == 0)
                {
                    i += codePointLength;
                    continue;
                }

                if (pendingSeparator && builder.Length > 0 && builder[builder.Length - 1] != ' ')
                {
                    builder.Append(' ');
                    spans.Add(pendingSeparatorSpan ?? sourceSpan);
                }

                var normalizedSegment = letterOrDigitSegment.ToString().Normalize(NormalizationForm.FormC);
                foreach (var ch in normalizedSegment)
                {
                    builder.Append(ch);
                    spans.Add(sourceSpan);
                }

                pendingSeparator = false;
                pendingSeparatorSpan = null;
                i += codePointLength;
            }

            return new NormalizedMappedText
            {
                Text = builder.ToString(),
                SourceSpans = spans
            };
        }

        private static string NormalizeInternal(string text, bool preserveWhitespace, bool stripDiacritics)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string normalized;
            try
            {
                normalized = text.Normalize(NormalizationForm.FormD);
            }
            catch (ArgumentException)
            {
                // An unpaired surrogate anywhere in the text makes it ill-formed UTF-16, which
                // String.Normalize() rejects outright (Chaptarr/chaptarr#116). Replace any such code
                // unit with U+FFFD - handled the same as the existing replacement-character case below -
                // and retry once on the now well-formed text.
                normalized = SanitizeUnpairedSurrogates(text).Normalize(NormalizationForm.FormD);
            }

            var builder = new StringBuilder(normalized.Length);
            var pendingSeparator = false;

            foreach (var rune in normalized.EnumerateRunes())
            {
                var category = Rune.GetUnicodeCategory(rune);

                if (stripDiacritics &&
                    (category == UnicodeCategory.NonSpacingMark ||
                     category == UnicodeCategory.SpacingCombiningMark ||
                     category == UnicodeCategory.EnclosingMark))
                {
                    continue;
                }

                if (rune.Value == '\uFFFD')
                {
                    if (preserveWhitespace && builder.Length > 0)
                    {
                        pendingSeparator = true;
                    }

                    continue;
                }

                if (Rune.IsLetterOrDigit(rune))
                {
                    if (preserveWhitespace && pendingSeparator && builder.Length > 0 && builder[builder.Length - 1] != ' ')
                    {
                        builder.Append(' ');
                    }

                    builder.Append(Rune.ToLowerInvariant(rune).ToString());
                    pendingSeparator = false;
                    continue;
                }

                if (preserveWhitespace && IsSeparator(rune, category) && builder.Length > 0)
                {
                    pendingSeparator = true;
                }
            }

            var result = builder.ToString().Normalize(NormalizationForm.FormC);

            if (!preserveWhitespace)
            {
                return result;
            }

            return CollapseWhitespaceRegex.Replace(result, " ").Trim();
        }

        // Replaces any UTF-16 code unit that isn't part of a valid surrogate pair with U+FFFD, so that
        // callers can safely pass the result to String.Normalize() without it throwing on ill-formed
        // input (Chaptarr/chaptarr#116). Leaves well-formed text (including valid surrogate pairs)
        // completely untouched.
        private static string SanitizeUnpairedSurrogates(string text)
        {
            StringBuilder sanitized = null;

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                var isWellFormed = char.IsHighSurrogate(ch)
                    ? i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                    : !char.IsLowSurrogate(ch) || (i > 0 && char.IsHighSurrogate(text[i - 1]));

                if (isWellFormed)
                {
                    sanitized?.Append(ch);
                    continue;
                }

                sanitized ??= new StringBuilder(text, 0, i, text.Length);
                sanitized.Append('\uFFFD');
            }

            return sanitized?.ToString() ?? text;
        }

        private static bool IsSeparator(Rune rune, UnicodeCategory category)
        {
            if (Rune.IsWhiteSpace(rune))
            {
                return true;
            }

            return category == UnicodeCategory.ConnectorPunctuation ||
                   category == UnicodeCategory.DashPunctuation ||
                   category == UnicodeCategory.OpenPunctuation ||
                   category == UnicodeCategory.ClosePunctuation ||
                   category == UnicodeCategory.InitialQuotePunctuation ||
                   category == UnicodeCategory.FinalQuotePunctuation ||
                   category == UnicodeCategory.OtherPunctuation ||
                   category == UnicodeCategory.MathSymbol ||
                   category == UnicodeCategory.CurrencySymbol ||
                   category == UnicodeCategory.ModifierSymbol ||
                   category == UnicodeCategory.OtherSymbol ||
                   category == UnicodeCategory.Control ||
                   category == UnicodeCategory.Format ||
                   category == UnicodeCategory.LineSeparator ||
                   category == UnicodeCategory.ParagraphSeparator;
        }
    }
}
