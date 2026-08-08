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

            for (var i = 0; i < text.Length; i++)
            {
                var sourceSpan = sourceSpans[i];
                var decomposed = text[i].ToString().Normalize(NormalizationForm.FormD);
                var letterOrDigitSegment = new StringBuilder(decomposed.Length);

                foreach (var ch in decomposed)
                {
                    var category = CharUnicodeInfo.GetUnicodeCategory(ch);

                    if (stripDiacritics &&
                        (category == UnicodeCategory.NonSpacingMark ||
                         category == UnicodeCategory.SpacingCombiningMark ||
                         category == UnicodeCategory.EnclosingMark))
                    {
                        continue;
                    }

                    if (ch == '\uFFFD')
                    {
                        if (builder.Length > 0)
                        {
                            pendingSeparator = true;
                            pendingSeparatorSpan ??= sourceSpan;
                        }

                        continue;
                    }

                    if (char.IsLetterOrDigit(ch))
                    {
                        letterOrDigitSegment.Append(char.ToLowerInvariant(ch));
                        continue;
                    }

                    if (IsSeparator(ch, category) && builder.Length > 0)
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

        private static string NormalizeInternal(string text, bool preserveWhitespace, bool stripDiacritics)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var normalized = text.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);
            var pendingSeparator = false;

            foreach (var ch in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(ch);

                if (stripDiacritics &&
                    (category == UnicodeCategory.NonSpacingMark ||
                     category == UnicodeCategory.SpacingCombiningMark ||
                     category == UnicodeCategory.EnclosingMark))
                {
                    continue;
                }

                if (ch == '\uFFFD')
                {
                    if (preserveWhitespace && builder.Length > 0)
                    {
                        pendingSeparator = true;
                    }

                    continue;
                }

                if (char.IsLetterOrDigit(ch))
                {
                    if (preserveWhitespace && pendingSeparator && builder.Length > 0 && builder[builder.Length - 1] != ' ')
                    {
                        builder.Append(' ');
                    }

                    builder.Append(char.ToLowerInvariant(ch));
                    pendingSeparator = false;
                    continue;
                }

                if (preserveWhitespace && IsSeparator(ch, category) && builder.Length > 0)
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

        private static bool IsSeparator(char ch, UnicodeCategory category)
        {
            if (char.IsWhiteSpace(ch))
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
