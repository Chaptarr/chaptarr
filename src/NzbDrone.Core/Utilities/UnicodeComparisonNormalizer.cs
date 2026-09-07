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

        public static string NormalizeKey(string text)
        {
            return NormalizeInternal(text, preserveWhitespace: false);
        }

        public static string NormalizeWords(string text)
        {
            return NormalizeInternal(text, preserveWhitespace: true);
        }

        public static NormalizedMappedText NormalizeWordsWithSourceSpans(string text, IReadOnlyList<(int Start, int End)> sourceSpans)
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
                for (var i = 0; i < text.Length; i++)
                {
                    generatedSpans.Add((i, i + 1));
                }

                sourceSpans = generatedSpans;
            }

            var builder = new StringBuilder(text.Length);
            var spans = new List<(int Start, int End)>();
            var pendingSeparator = false;
            (int Start, int End)? pendingSeparatorSpan = null;

            // Normalize whole characters: splitting an emoji surrogate pair rejects otherwise valid release titles.
            // EnumerateRunes also replaces lone surrogates without shifting the original UTF-16 offsets.
            var sourceIndex = 0;
            foreach (var character in text.EnumerateRunes())
            {
                var sourceSpan = (sourceSpans[sourceIndex].Start, sourceSpans[sourceIndex + character.Utf16SequenceLength - 1].End);
                sourceIndex += character.Utf16SequenceLength;
                var decomposed = character.ToString().Normalize(NormalizationForm.FormD);
                var letterOrDigitSegment = new StringBuilder(decomposed.Length);

                foreach (var rune in decomposed.EnumerateRunes())
                {
                    var category = Rune.GetUnicodeCategory(rune);

                    if (category == UnicodeCategory.NonSpacingMark ||
                        category == UnicodeCategory.SpacingCombiningMark ||
                        category == UnicodeCategory.EnclosingMark)
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
            }

            return new NormalizedMappedText
            {
                Text = builder.ToString(),
                SourceSpans = spans
            };
        }

        private static string NormalizeInternal(string text, bool preserveWhitespace)
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
                // Keep the existing replacement-character policy for malformed UTF-16 as well.
                var sanitized = new StringBuilder(text.Length);
                foreach (var rune in text.EnumerateRunes())
                {
                    sanitized.Append(rune.ToString());
                }

                normalized = sanitized.ToString().Normalize(NormalizationForm.FormD);
            }

            var builder = new StringBuilder(normalized.Length);
            var pendingSeparator = false;

            foreach (var rune in normalized.EnumerateRunes())
            {
                var category = Rune.GetUnicodeCategory(rune);

                if (category == UnicodeCategory.NonSpacingMark ||
                    category == UnicodeCategory.SpacingCombiningMark ||
                    category == UnicodeCategory.EnclosingMark)
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
