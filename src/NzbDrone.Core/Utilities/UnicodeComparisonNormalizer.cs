using System;
using System.Collections.Generic;
using System.Globalization;
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
        // Keep Roman numeric interpretation in the standard canonical range and spelling.
        private static readonly Regex CanonicalRomanNumeralRegex = new Regex(
            @"^(?=[ivxlcdm]+$)m{0,3}(?:cm|cd|d?c{0,3})(?:xc|xl|l?x{0,3})(?:ix|iv|v?i{0,3})$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static IEnumerable<(string Value, int Index, int Length)> WordSpans(string text, bool preserveFtsConnectors = false, bool includePossessive = false, bool splitNumbers = false)
        {
            var index = 0;
            while (index < (text?.Length ?? 0))
            {
                if (!Rune.TryGetRuneAt(text, index, out var rune) || !IsWordCharacter(rune))
                {
                    index += rune.Utf16SequenceLength;
                    continue;
                }

                var start = index;
                var numeric = Rune.IsNumber(rune);
                do
                {
                    index += rune.Utf16SequenceLength;
                    if (index + 1 < text.Length &&
                        ((preserveFtsConnectors && (text[index] == '-' || text[index] == '.')) ||
                         (text[index] == '.' && Rune.IsNumber(rune))) &&
                        Rune.TryGetRuneAt(text, index + 1, out var next) && IsWordCharacter(next) &&
                        (preserveFtsConnectors || Rune.IsNumber(next)))
                    {
                        index++;
                    }
                }
                while (index < text.Length && Rune.TryGetRuneAt(text, index, out rune) &&
                       (IsWordCharacter(rune) || IsCombiningMark(rune)) &&
                       (!splitNumbers || IsCombiningMark(rune) || Rune.IsNumber(rune) == numeric));

                if (includePossessive && index + 1 < text.Length &&
                    (text[index] == '\'' || text[index] == '\u2018' || text[index] == '\u2019') &&
                    (text[index + 1] == 's' || text[index + 1] == 'S'))
                {
                    index += 2;
                }

                yield return (text.Substring(start, index - start), start, index - start);
            }
        }

        public static string NormalizeKey(string text) => Normalize(text, false, false, false, null).Text;

        public static string NormalizeWords(string text) => Normalize(text, true, false, false, null).Text;

        // Keep punctuation for callers whose parsers give it meaning, using the same character fold as stored keys.
        public static string NormalizeText(string text) => Normalize(text, true, true, false, null).Text;

        public static NormalizedMappedText NormalizeWordsWithSourceSpans(string text, IReadOnlyList<(int Start, int End)> sourceSpans)
            => Normalize(text, true, false, true, sourceSpans);

        internal static NormalizedMappedText NormalizeTextWithSourceSpans(string text)
            => Normalize(text, true, true, true, null);

        private static NormalizedMappedText Normalize(string text, bool preserveWhitespace, bool preservePunctuation, bool mapSource, IReadOnlyList<(int Start, int End)> sourceSpans)
        {
            var builder = new StringBuilder(text?.Length ?? 0);
            var spans = mapSource ? new List<(int Start, int End)>() : null;
            (int Start, int End)? separator = null;
            var suppliedSpans = sourceSpans != null && sourceSpans.Count == text?.Length;

            var nextWordEnd = 0;
            for (var index = 0; index < (text?.Length ?? 0);)
            {
                Rune.TryGetRuneAt(text, index, out var rune);
                var length = rune.Utf16SequenceLength;
                string folded = null;
                if (index >= nextWordEnd && IsWordCharacter(rune))
                {
                    nextWordEnd = index + length;
                    var asciiWord = rune.IsAscii;
                    while (nextWordEnd < text.Length && Rune.TryGetRuneAt(text, nextWordEnd, out var next) &&
                           (IsWordCharacter(next) || IsCombiningMark(next)))
                    {
                        asciiWord &= next.IsAscii;
                        nextWordEnd += next.Utf16SequenceLength;
                    }

                    var wholeEnd = nextWordEnd;
                    if (Rune.IsNumber(rune) && wholeEnd + 1 < text.Length &&
                        char.IsWhiteSpace(text[wholeEnd]) && (index == 0 || (text[index - 1] != '.' && text[index - 1] != '/')) &&
                        TryReadFraction(text, wholeEnd + 1, out var fraction, out var fractionEnd) &&
                        decimal.TryParse(NormalizeText(text.Substring(index, wholeEnd - index)),
                            NumberStyles.None, CultureInfo.InvariantCulture, out var whole) && whole < decimal.MaxValue)
                    {
                        folded = (whole + fraction).ToString(CultureInfo.InvariantCulture);
                        length = fractionEnd - index;
                    }

                    if (folded == null && asciiWord)
                    {
                        if (separator.HasValue)
                        {
                            builder.Append(' ');
                            spans?.Add(separator.Value);
                            separator = null;
                        }

                        for (; index < nextWordEnd; index++)
                        {
                            builder.Append(char.ToLowerInvariant(text[index]));
                            spans?.Add(suppliedSpans ? sourceSpans[index] : (index, index + 1));
                        }

                        continue;
                    }
                }

                if (folded == null && rune.Value >= 0x2160 && rune.Value <= 0x217f)
                {
                    var end = index + length;
                    while (end < text.Length && text[end] >= '\u2160' && text[end] <= '\u217f')
                    {
                        end++;
                    }

                    // Fold presentation glyphs to their letter sequence; contextual parsers decide whether it is numeric.
                    folded = text.Substring(index, end - index).Normalize(NormalizationForm.FormKC);
                    length = end - index;
                }
                else if (folded == null && !rune.IsAscii && Rune.IsNumber(rune) && Rune.GetNumericValue(rune) != -1)
                {
                    folded = ((decimal)Rune.GetNumericValue(rune)).ToString(CultureInfo.InvariantCulture);
                }

                if (Rune.IsNumber(rune) && index + length < text.Length && Rune.TryGetRuneAt(text, index + length, out var fractionRune))
                {
                    var fraction = Rune.GetNumericValue(fractionRune);
                    if (fraction > 0 && fraction < 1 &&
                        decimal.TryParse(folded ?? rune.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var whole))
                    {
                        folded = (whole + (decimal)fraction).ToString(CultureInfo.InvariantCulture);
                        length += fractionRune.Utf16SequenceLength;
                    }
                }

                if (folded == null && !rune.IsAscii)
                {
                    var decomposed = rune.ToString().Normalize(NormalizationForm.FormD);
                    var clean = new StringBuilder(decomposed.Length);
                    foreach (var part in decomposed.EnumerateRunes())
                    {
                        if (!IsCombiningMark(part) || (preservePunctuation && Rune.GetUnicodeCategory(part) != UnicodeCategory.NonSpacingMark))
                        {
                            clean.Append(part.ToString());
                        }
                    }

                    folded = clean.ToString().Normalize(NormalizationForm.FormC);
                }

                while (index + length < text.Length && Rune.TryGetRuneAt(text, index + length, out var mark) && IsCombiningMark(mark))
                {
                    if (preservePunctuation && Rune.GetUnicodeCategory(mark) != UnicodeCategory.NonSpacingMark)
                    {
                        folded = (folded ?? rune.ToString()) + mark.ToString();
                    }

                    length += mark.Utf16SequenceLength;
                }

                var sourceSpan = suppliedSpans
                    ? (sourceSpans[index].Start, sourceSpans[index + length - 1].End)
                    : (index, index + length);
                if (folded == null)
                {
                    Append(rune, sourceSpan, rune.Value == '.' && builder.Length > 0 && char.IsAsciiDigit(builder[builder.Length - 1]) &&
                        index + length < text.Length && Rune.TryGetRuneAt(text, index + length, out var next) && Rune.IsNumber(next));
                }
                else
                {
                    foreach (var part in folded.EnumerateRunes())
                    {
                        if (!IsCombiningMark(part) || (preservePunctuation && Rune.GetUnicodeCategory(part) != UnicodeCategory.NonSpacingMark))
                        {
                            Append(part, sourceSpan, part.Value == '.');
                        }
                    }
                }

                index += length;
            }

            return new NormalizedMappedText { Text = builder.ToString(), SourceSpans = spans };

            void Append(Rune rune, (int Start, int End) sourceSpan, bool numericPoint)
            {
                if (IsWordCharacter(rune) || preservePunctuation || (preserveWhitespace && numericPoint))
                {
                    if (separator.HasValue)
                    {
                        builder.Append(' ');
                        spans?.Add(separator.Value);
                        separator = null;
                    }

                    var lower = Rune.ToLowerInvariant(rune);
                    if (lower.IsBmp)
                    {
                        builder.Append(lower.Value == '\uFFFD' ? ' ' : (char)lower.Value);
                        spans?.Add(sourceSpan);
                    }
                    else
                    {
                        Span<char> characters = stackalloc char[2];
                        lower.EncodeToUtf16(characters);
                        builder.Append(characters);
                        spans?.Add(sourceSpan);
                        spans?.Add(sourceSpan);
                    }
                }
                else if (preserveWhitespace && builder.Length > 0 && !IsCombiningMark(rune))
                {
                    // Unclassified characters must separate evidence instead of joining unrelated words.
                    separator ??= sourceSpan;
                }
            }
        }

        private static bool TryReadFraction(string text, int start, out decimal fraction, out int end)
        {
            fraction = 0;
            end = start;
            while (end < text.Length && Rune.TryGetRuneAt(text, end, out var rune) &&
                   (Rune.IsNumber(rune) || rune.Value == '/'))
            {
                end += rune.Utf16SequenceLength;
            }

            if (end == start || (end < text.Length && Rune.TryGetRuneAt(text, end, out var following) &&
                (IsWordCharacter(following) || IsCombiningMark(following) ||
                 (following.Value == '.' && end + 1 < text.Length && Rune.TryGetRuneAt(text, end + 1, out var next) && Rune.IsNumber(next)))))
            {
                return false;
            }

            var parts = NormalizeText(text.Substring(start, end - start)).Split('/');
            if (!decimal.TryParse(parts[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out fraction))
            {
                return false;
            }

            if (parts.Length == 2 && fraction == decimal.Truncate(fraction) &&
                decimal.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var denominator) && denominator > 0)
            {
                fraction = (decimal)((double)fraction / (double)denominator);
            }
            else if (parts.Length != 1)
            {
                return false;
            }

            return fraction > 0 && fraction < 1;
        }

        internal static bool TryParseRomanNumeral(string raw, out int value)
        {
            value = 0;
            if (!IsCanonicalRomanNumeral(raw))
            {
                return false;
            }

            raw = raw.Trim().ToUpperInvariant();
            long total = 0;
            var previous = 0;
            for (var index = raw.Length - 1; index >= 0; index--)
            {
                var current = raw[index] switch { 'I' => 1, 'V' => 5, 'X' => 10, 'L' => 50, 'C' => 100, 'D' => 500, 'M' => 1000, _ => 0 };
                if (current == 0)
                {
                    return false;
                }

                total += current < previous ? -current : current;
                previous = current < previous ? previous : current;
            }

            if (total <= 0 || total > int.MaxValue)
            {
                return false;
            }

            value = (int)total;
            return true;
        }

        internal static bool IsCanonicalRomanNumeral(string raw)
        {
            return !string.IsNullOrWhiteSpace(raw) && CanonicalRomanNumeralRegex.IsMatch(raw.Trim());
        }

        private static bool IsWordCharacter(Rune rune) => Rune.IsLetter(rune) || Rune.IsNumber(rune);

        private static bool IsCombiningMark(Rune rune) => Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    }
}
