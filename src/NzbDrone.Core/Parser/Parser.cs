using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Utilities;

namespace NzbDrone.Core.Parser
{
    public static class Parser
    {
        internal static readonly char[] SubtitleSeparators = { ':', '：' };

        private static readonly Logger Logger = NzbDroneLogger.GetLogger(typeof(Parser));

        private static readonly Regex[] ReportBookTitleRegex = new[]
        {
            //ruTracker - (Genre) [Source]? Author - Discography
            new Regex(@"^(?:\(.+?\))(?:\W*(?:\[(?<source>.+?)\]))?\W*(?<author>.+?)(?: - )(?<discography>Discography|Discografia).+?(?<startyear>\d{4}).+?(?<endyear>\d{4})",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            //Author - Discography with two years
            new Regex(@"^(?<author>.+?)(?: - )(?:.+?)?(?<discography>Discography|Discografia).+?(?<startyear>\d{4}).+?(?<endyear>\d{4})",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            //Author - Discography with end year
            new Regex(@"^(?<author>.+?)(?: - )(?:.+?)?(?<discography>Discography|Discografia).+?(?<endyear>\d{4})",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            //Author Discography with two years
            new Regex(@"^(?<author>.+?)\W*(?<discography>Discography|Discografia).+?(?<startyear>\d{4}).+?(?<endyear>\d{4})",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            //Author Discography with end year
            new Regex(@"^(?<author>.+?)\W*(?<discography>Discography|Discografia).+?(?<endyear>\d{4})",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            //Author Discography
            new Regex(@"^(?<author>.+?)\W*(?<discography>Discography|Discografia)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            //MyAnonaMouse - Title by Author [lang / pdf]
            new Regex(@"^(?<book>.+)\bby\b(?<author>.+?)(?:\[|\()",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            // Title by Author FORMAT
            new Regex(@"^(?<book>.+?)\s+by\s+(?<author>.+?)\s+(?:MP3|M4B|FLAC|AAC|EPUB|MOBI|AZW3|PDF)$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            // Title by Author
            new Regex(@"^(?<book>.+)\s+\bby\b\s+(?<author>.+)$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            //MyAnonaMouse - Audiobook format: Title [Narrator] [Duration] Format
            new Regex(@"^(?<book>.+?)\s*\[.+?\]\s*(?:\[.+?\])?\s*(?:MP3|M4B|FLAC|AAC)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            //MyAnonaMouse - Simple format: Title FORMAT (clean parsed titles)
            new Regex(@"^(?<book>.+?)\s+(?:MP3|M4B|FLAC|AAC|EPUB|MOBI|AZW3|PDF)$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            //ruTracker - (Genre) [Source]? Author - Book - Year
            new Regex(@"^(?:\(.+?\))(?:\W*(?:\[(?<source>.+?)\]))?\W*(?<author>.+?)(?: - )(?<book>.+?)(?: - )(?<releaseyear>\d{4})",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            //Author-Book-Version-Source-Year
            //ex. Imagine Dragons-Smoke And Mirrors-Deluxe Edition-2CD-FLAC-2015-JLM
            new Regex(@"^(?<author>.+?)[-](?<book>.+?)[-](?:[\(|\[]?)(?<version>.+?(?:Edition)?)(?:[\)|\]]?)[-](?<source>\d?CD|WEB).+?(?<releaseyear>\d{4})",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            //Author-Book-Source-Year
            //ex. Dani_Sbert-Togheter-WEB-2017-FURY
            new Regex(@"^(?<author>.+?)[-](?<book>.+?)[-](?<source>\d?CD|WEB).+?(?<releaseyear>\d{4})",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            //Author - Book (Year) Strict
            new Regex(@"^(?:(?<author>.+?)(?: - )+)(?<book>.+?)\W*(?:\(|\[).+?(?<releaseyear>\d{4})",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            //Author - Book (Year)
            new Regex(@"^(?:(?<author>.+?)(?: - )+)(?<book>.+?)\W*(?:\(|\[)(?<releaseyear>\d{4})",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            //Author - Book - Year [something]
            new Regex(@"^(?:(?<author>.+?)(?: - )+)(?<book>.+?)\W*(?: - )(?<releaseyear>\d{4})\W*(?:\(|\[)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            //Author - Book [something] or Author - Book (something)
            new Regex(@"^(?:(?<author>.+?)(?: - )+)(?<book>.+?)\W*(?:\(|\[)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            //Author - Book Year
            new Regex(@"^(?:(?<author>.+?)(?: - )+)(?<book>.+?)\W*(?<releaseyear>\d{4})",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            //Author-Book (Year) Strict
            //Hyphen no space between author and book
            new Regex(@"^(?:(?<author>.+?)(?:-)+)(?<book>.+?)\W*(?:\(|\[).+?(?<releaseyear>\d{4})",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            //Author-Book (Year)
            //Hyphen no space between author and book
            new Regex(@"^(?:(?<author>.+?)(?:-)+)(?<book>.+?)\W*(?:\(|\[)(?<releaseyear>\d{4})",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            //Author-Book [something] or Author-Book (something)
            //Hyphen no space between author and book
            new Regex(@"^(?:(?<author>.+?)(?:-)+)(?<book>.+?)\W*(?:\(|\[)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            //Author-Book-something-Year
            new Regex(@"^(?:(?<author>.+?)(?:-)+)(?<book>.+?)(?:-.+?)(?<releaseyear>\d{4})",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            //Author-Book Year
            //Hyphen no space between author and book
            new Regex(@"^(?:(?<author>.+?)(?:-)+)(?:(?<book>.+?)(?:-)+)(?<releaseyear>\d{4})",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),

            //Author - Year - Book
            // Hypen with no or more spaces between author/book/year
            new Regex(@"^(?:(?<author>.+?)(?:-))(?<releaseyear>\d{4})(?:-)(?<book>[^-]+)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
        };

        private static readonly Regex[] RejectHashedReleasesRegex = new Regex[]
            {
                // Generic match for md5 and mixed-case hashes.
                new Regex(@"^[0-9a-zA-Z]{32}", RegexOptions.Compiled),

                // Generic match for shorter lower-case hashes.
                new Regex(@"^[a-z0-9]{24}$", RegexOptions.Compiled),

                // Format seen on some NZBGeek releases
                // Be very strict with these coz they are very close to the valid 101 ep numbering.
                new Regex(@"^[A-Z]{11}\d{3}$", RegexOptions.Compiled),
                new Regex(@"^[a-z]{12}\d{3}$", RegexOptions.Compiled),

                //Backup filename (Unknown origins)
                new Regex(@"^Backup_\d{5,}S\d{2}-\d{2}$", RegexOptions.Compiled),

                //123 - Started appearing December 2014
                new Regex(@"^123$", RegexOptions.Compiled),

                //abc - Started appearing January 2015
                new Regex(@"^abc$", RegexOptions.Compiled | RegexOptions.IgnoreCase),

                //b00bs - Started appearing January 2015
                new Regex(@"^b00bs$", RegexOptions.Compiled | RegexOptions.IgnoreCase)
            };

        private static readonly Regex PercentRegex = new Regex(@"(?<=\b\d+)%", RegexOptions.Compiled);

        private static readonly Regex FileExtensionRegex = new Regex(@"\.[a-z0-9]{2,4}$",
                                                                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        //TODO Rework this Regex for Music
        private static readonly RegexReplace SimpleTitleRegex = new RegexReplace(@"(?:(480|720|1080|2160|320)[ip]|[xh][\W_]?26[45]|DD\W?5\W1|848x480|1280x720|1920x1080|3840x2160|4096x2160|(8|10)b(it)?)\s*",
                                                                string.Empty,
                                                                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Valid TLDs http://data.iana.org/TLD/tlds-alpha-by-domain.txt
        private static readonly RegexReplace WebsitePrefixRegex = new RegexReplace(@"^(?:\[\s*)?(?:www\.)?[-a-z0-9-]{1,256}\.(?:[a-z]{2,6}\.[a-z]{2,6}|xn--[a-z0-9-]{4,}|[a-z]{2,})\b(?:\s*\]|[ -]{2,})[ -]*",
                                                                string.Empty,
                                                                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly RegexReplace WebsitePostfixRegex = new RegexReplace(@"(?:\[\s*)?(?:www\.)?[-a-z0-9-]{1,256}\.(?:xn--[a-z0-9-]{4,}|[a-z]{2,6})\b(?:\s*\])$",
                                                                string.Empty,
                                                                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AirDateRegex = new Regex(@"^(.*?)(?<!\d)((?<airyear>\d{4})[_.-](?<airmonth>[0-1][0-9])[_.-](?<airday>[0-3][0-9])|(?<airmonth>[0-1][0-9])[_.-](?<airday>[0-3][0-9])[_.-](?<airyear>\d{4}))(?!\d)",
                                                                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SixDigitAirDateRegex = new Regex(@"(?<=[_.-])(?<airdate>(?<!\d)(?<airyear>[1-9]\d{1})(?<airmonth>[0-1][0-9])(?<airday>[0-3][0-9]))(?=[_.-])",
                                                                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly RegexReplace CleanReleaseGroupRegex = new RegexReplace(@"^(.*?[-._ ])|(-(RP|1|NZBGeek|Obfuscated|Scrambled|sample|Pre|postbot|xpost|Rakuv[a-z0-9]*|WhiteRev|BUYMORE|AsRequested|AlternativeToRequested|GEROV|Z0iDS3N|Chamele0n|4P|4Planet))+$",
                                                                string.Empty,
                                                                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly RegexReplace CleanTorrentSuffixRegex = new RegexReplace(@"\[(?:ettv|rartv|rarbg|cttv)\]$",
                                                                string.Empty,
                                                                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MalformedApostropheEntityRegex = new Regex(@"&(?:#)?0*39;",
                                                                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex LeadingUsenetCounterRegex = new Regex(@"^\s*\(\d+/\d+\)\s*",
                                                                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MultipartArticleCounterRegex = new Regex(@"\[\d+/\d+\]",
                                                                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TrailingYEncRegex = new Regex(@"\s+yenc\s*$",
                                                                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex KbpsMetadataRegex = new Regex(@"\s*[\(\[][^)\]]*\bkbps\b[^)\]]*[\)\]]",
                                                                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TrailingPar2VolumeRegex = new Regex(@"\s*\.vol\d+\+\d+\.par2\b",
                                                                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TrailingPar2OrNzbRegex = new Regex(@"\s*\.(?:par2|nzb)\b",
                                                                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ReleaseGroupRegex = new Regex(@"-(?<releasegroup>[a-z0-9]+)(?<!MP3|ALAC|FLAC|WEB)(?:\b|[-._ ])",
                                                                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AnimeReleaseGroupRegex = new Regex(@"^(?:\[(?<subgroup>(?!\s).+?(?<!\s))\](?:_|-|\s|\.)?)",
                                                                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex YearInTitleRegex = new Regex(@"^(?<title>.+?)(?:\W|_)?(?<year>\d{4})",
                                                                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly HashSet<char> WordDelimiters = new HashSet<char>(" .,_-=()[]|\"`'’");
        private static readonly Regex DuplicateSpacesRegex = new Regex(@"\s{2,}", RegexOptions.Compiled);

        private static readonly Regex RequestInfoRegex = new Regex(@"\[.+?\]", RegexOptions.Compiled);


        public static ParsedBookInfo ParseBookTitleWithSearchCriteria(string title, Author author, List<Book> books, List<Book> authorCatalog)
        {
            try
            {
                if (!ValidateBeforeParsing(title))
                {
                    return null;
                }

                var authorName = author.Name == "Various Authors" ? "VA" : author.Name;

                if (Logger.IsTraceEnabled)
                {
                    Logger.Trace("Parsing string '{0}' using search criteria author: '{1}' books: '{2}'",
                                 title,
                                 authorName,
                                 string.Join(", ", books.Select(a => a.Title)));
                }

                var releaseTitle = CleanReleaseTitleForParsing(title);

                var simpleTitle = SimpleTitleRegex.Replace(releaseTitle);

                simpleTitle = WebsitePrefixRegex.Replace(simpleTitle);
                simpleTitle = WebsitePostfixRegex.Replace(simpleTitle);

                simpleTitle = CleanTorrentSuffixRegex.Replace(simpleTitle);

                var bestMatch = ReleaseTitleMatchScorer.FindBestMatch(simpleTitle, authorName, books, null, authorCatalog);

                if (bestMatch?.Book == null || !bestMatch.IsMatch)
                {
                    Logger.Trace("No acceptable title match found using search criteria for '{0}'", title);
                    return null;
                }

                var foundAuthor = author?.Name ?? authorName;
                var foundBook = bestMatch.PrimaryTitle;

                if (string.IsNullOrWhiteSpace(foundBook))
                {
                    foundBook = ReleaseTitleMatchScorer.GetPrimaryBookTitle(bestMatch.Book);
                }

                if (string.IsNullOrWhiteSpace(foundBook))
                {
                    foundBook = bestMatch.Book.Title;
                }

                if (Logger.IsTraceEnabled)
                {
                    Logger.Trace("Search-criteria title match: Author='{0}', Book='{1}', Variant='{2}', Leftovers=[{3}]",
                                 foundAuthor,
                                 foundBook,
                                 bestMatch.MatchedVariant ?? "<none>",
                                 string.Join(", ", bestMatch.MeaningfulLeftovers.Take(8)));
                }

                var result = new ParsedBookInfo
                {
                    AuthorName = foundAuthor,
                    AuthorTitleInfo = GetAuthorTitleInfo(foundAuthor),
                    BookTitle = foundBook
                };

                try
                {
                    result.Quality = QualityParser.ParseQuality(title);
                    Logger.Trace("Quality parsed: {0}", result.Quality);

                    result.ReleaseGroup = ParseReleaseGroup(CleanReleaseTitleForParsing(title, normalizeBrackets: true));

                    Logger.Trace("Release Group parsed: {0}", result.ReleaseGroup);

                    return result;
                }
                catch (InvalidDateException ex)
                {
                    Logger.Debug(ex, ex.Message);
                }
            }
            catch (Exception e)
            {
                if (!title.ToLower().Contains("password") && !title.ToLower().Contains("yenc"))
                {
                    Logger.Error(e, "An error has occurred while trying to parse {0}", title);
                }
            }

            Logger.Trace("Unable to parse {0}", title);
            return null;
        }

        public static ParsedBookInfo ParseBookTitle(string title)
        {
            try
            {
                Logger.Trace("ParseBookTitle called with: '{0}'", title);

                if (!ValidateBeforeParsing(title))
                {
                    return null;
                }

                Logger.Trace("Parsing string '{0}'", title);

                var releaseTitle = CleanReleaseTitleForParsing(title);
                var parseTitle = CleanReleaseTitleForParsing(title, normalizeBrackets: true);

                var simpleTitle = SimpleTitleRegex.Replace(parseTitle);

                // TODO: Quick fix stripping [url] - prefixes.
                simpleTitle = WebsitePrefixRegex.Replace(simpleTitle);
                simpleTitle = WebsitePostfixRegex.Replace(simpleTitle);

                simpleTitle = CleanTorrentSuffixRegex.Replace(simpleTitle);

                var airDateMatch = AirDateRegex.Match(simpleTitle);
                if (airDateMatch.Success)
                {
                    simpleTitle = airDateMatch.Groups[1].Value + airDateMatch.Groups["airyear"].Value + "." + airDateMatch.Groups["airmonth"].Value + "." + airDateMatch.Groups["airday"].Value;
                }

                var sixDigitAirDateMatch = SixDigitAirDateRegex.Match(simpleTitle);
                if (sixDigitAirDateMatch.Success)
                {
                    var airYear = sixDigitAirDateMatch.Groups["airyear"].Value;
                    var airMonth = sixDigitAirDateMatch.Groups["airmonth"].Value;
                    var airDay = sixDigitAirDateMatch.Groups["airday"].Value;

                    if (airMonth != "00" || airDay != "00")
                    {
                        var fixedDate = string.Format("20{0}.{1}.{2}", airYear, airMonth, airDay);

                        simpleTitle = simpleTitle.Replace(sixDigitAirDateMatch.Groups["airdate"].Value, fixedDate);
                    }
                }

                foreach (var regex in ReportBookTitleRegex)
                {
                    var match = regex.Matches(simpleTitle);

                    if (match.Count != 0)
                    {
                        Logger.Trace(regex);
                        try
                        {
                            var result = ParseBookMatchCollection(match, releaseTitle);

                            if (result != null)
                            {
                                result.Quality = QualityParser.ParseQuality(title);
                                Logger.Trace("Quality parsed: {0}", result.Quality);

                                result.ReleaseGroup = ParseReleaseGroup(parseTitle);

                                var subGroup = GetSubGroup(match);
                                if (!subGroup.IsNullOrWhiteSpace())
                                {
                                    result.ReleaseGroup = subGroup;
                                }

                                Logger.Trace("Release Group parsed: {0}", result.ReleaseGroup);

                                result.ReleaseHash = GetReleaseHash(match);
                                if (!result.ReleaseHash.IsNullOrWhiteSpace())
                                {
                                    Logger.Trace("Release Hash parsed: {0}", result.ReleaseHash);
                                }

                                return result;
                            }
                        }
                        catch (InvalidDateException ex)
                        {
                            Logger.Debug(ex, ex.Message);
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (!title.ToLower().Contains("password") && !title.ToLower().Contains("yenc"))
                {
                    Logger.Error(e, "An error has occurred while trying to parse {0}", title);
                }
            }

            Logger.Trace("Unable to parse {0}", title);
            return null;
        }

        public static (string, string) SplitBookTitle(this string book, string author)
        {
            // Strip author from title, eg Tom Clancy: Ghost Protocol
            if (book.StartsWith($"{author}:"))
            {
                book = book.Split(':', 2)[1].Trim();
            }

            var parenthesis = book.IndexOf('(');
            var colon = book.IndexOf(':');

            string[] parts = null;

            if (parenthesis > -1)
            {
                var endParenthesis = book.IndexOf(')', parenthesis);
                if (endParenthesis == -1 || !book.Substring(parenthesis + 1, endParenthesis - parenthesis).Contains(' '))
                {
                    parenthesis = -1;
                }
            }

            if (colon > -1 && parenthesis > -1)
            {
                if (colon < parenthesis)
                {
                    parts = book.Split(':', 2);
                }
                else
                {
                    parts = book.Split('(', 2);
                    parts[1] = parts[1].TrimEnd(')');
                }
            }
            else if (colon > -1)
            {
                parts = book.Split(':', 2);
            }
            else if (parenthesis > -1)
            {
                parts = book.Split('(');
                parts[1] = parts[1].TrimEnd(')');
            }

            if (parts != null)
            {
                return (parts[0].Trim(), parts[1].TrimEnd(':').Trim());
            }

            return (book, string.Empty);
        }

        public static string CleanAuthorName(this string name)
        {
            if (name.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            // Keep numeric strings textual so leading zeroes and signs are retained.
            if (long.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                return name.Trim();
            }

            name = PercentRegex.Replace(UnicodeComparisonNormalizer.NormalizeText(name), "percent");

            return UnicodeComparisonNormalizer.NormalizeKey(name);
        }

        internal static IEqualityComparer<string> NarratorNameComparer { get; } = new NarratorWordComparer();

        private sealed class NarratorWordComparer : IEqualityComparer<string>
        {
            public bool Equals(string left, string right) => string.Equals(
                UnicodeComparisonNormalizer.NormalizeWords(left), UnicodeComparisonNormalizer.NormalizeWords(right), StringComparison.Ordinal);

            public int GetHashCode(string name) => StringComparer.Ordinal.GetHashCode(UnicodeComparisonNormalizer.NormalizeWords(name));
        }

        public static string CleanNarratorName(this string name)
        {
            return name.CleanAuthorName();
        }

        public static string ParseReleaseGroup(string title)
        {
            title = title.Trim();
            title = RemoveFileExtension(title);
            title = WebsitePrefixRegex.Replace(title);

            var animeMatch = AnimeReleaseGroupRegex.Match(title);

            if (animeMatch.Success)
            {
                return animeMatch.Groups["subgroup"].Value;
            }

            title = CleanReleaseGroupRegex.Replace(title);

            var matches = ReleaseGroupRegex.Matches(title);

            if (matches.Count != 0)
            {
                var group = matches.OfType<Match>().Last().Groups["releasegroup"].Value;

                if (int.TryParse(group, out _))
                {
                    return null;
                }

                return group;
            }

            return null;
        }

        public static string RemoveFileExtension(string title)
        {
            title = FileExtensionRegex.Replace(title, m =>
            {
                var extension = m.Value.ToLower();
                if (MediaFiles.MediaFileExtensions.AllExtensions.Contains(extension) || new[] { ".par2", ".nzb" }.Contains(extension))
                {
                    return string.Empty;
                }

                return m.Value;
            });

            return title;
        }

        public static string CleanReleaseTitleForParsing(string title) => CleanReleaseTitleForParsing(title, normalizeBrackets: false);

        private static string CleanReleaseTitleForParsing(string title, bool normalizeBrackets)
        {
            if (title.IsNullOrWhiteSpace())
            {
                return title;
            }

            var cleaned = MalformedApostropheEntityRegex.Replace(title, "'");
            cleaned = WebUtility.HtmlDecode(cleaned);
            if (normalizeBrackets)
            {
                cleaned = TitleBracketCatalog.NormalizeForParsing(cleaned);
            }

            cleaned = LeadingUsenetCounterRegex.Replace(cleaned, string.Empty);
            cleaned = MultipartArticleCounterRegex.Replace(cleaned, " ");
            cleaned = TrailingYEncRegex.Replace(cleaned, string.Empty);
            cleaned = KbpsMetadataRegex.Replace(cleaned, string.Empty);
            cleaned = TrailingPar2VolumeRegex.Replace(cleaned, string.Empty);
            cleaned = TrailingPar2OrNzbRegex.Replace(cleaned, string.Empty);
            cleaned = TitleBracketCatalog.TrimUnclosedTail(cleaned);
            cleaned = RemoveFileExtension(cleaned);
            cleaned = DuplicateSpacesRegex.Replace(cleaned, " ").Trim();

            return cleaned;
        }

        public static string CleanBookTitle(this string book)
        {
            // Stored title keys retain every word; metadata omissions belong to the guarded title comparison.
            return book?.Trim() ?? string.Empty;
        }

        private static AuthorTitleInfo GetAuthorTitleInfo(string title)
        {
            var authorTitleInfo = new AuthorTitleInfo();
            authorTitleInfo.Title = title;

            return authorTitleInfo;
        }

        public static string ParseAuthorName(string title)
        {
            Logger.Trace("Parsing string '{0}'", title);

            var parseResult = ParseBookTitle(title);

            if (parseResult == null)
            {
                return CleanAuthorName(title);
            }

            return parseResult.AuthorName;
        }

        private static ParsedBookInfo ParseBookMatchCollection(MatchCollection matchCollection, string releaseTitle)
        {
            var authorName = matchCollection[0].Groups["author"].Value.Replace('.', ' ').Replace('_', ' ');
            var bookTitle = matchCollection[0].Groups["book"].Value.Replace('.', ' ').Replace('_', ' ');
            var releaseVersion = matchCollection[0].Groups["version"].Value.Replace('.', ' ').Replace('_', ' ');
            authorName = RequestInfoRegex.Replace(authorName, "").Trim(' ');
            bookTitle = RequestInfoRegex.Replace(bookTitle, "").Trim(' ');
            releaseVersion = RequestInfoRegex.Replace(releaseVersion, "").Trim(' ');

            int.TryParse(matchCollection[0].Groups["releaseyear"].Value, out var releaseYear);

            ParsedBookInfo result;

            result = new ParsedBookInfo
            {
                ReleaseTitle = releaseTitle
            };

            result.AuthorName = authorName;
            result.BookTitle = bookTitle;
            result.AuthorTitleInfo = GetAuthorTitleInfo(result.AuthorName);
            result.ReleaseDate = releaseYear.ToString();
            result.ReleaseVersion = releaseVersion;

            if (matchCollection[0].Groups["discography"].Success)
            {
                int.TryParse(matchCollection[0].Groups["startyear"].Value, out var discStart);
                int.TryParse(matchCollection[0].Groups["endyear"].Value, out var discEnd);
                result.Discography = true;

                if (discStart > 0 && discEnd > 0)
                {
                    result.DiscographyStart = discStart;
                    result.DiscographyEnd = discEnd;
                }
                else if (discEnd > 0)
                {
                    result.DiscographyEnd = discEnd;
                }

                result.BookTitle = "Discography";
            }

            Logger.Trace("Book Parsed. {0}", result);

            return result;
        }

        private static bool ValidateBeforeParsing(string title)
        {
            if (title.ToLower().Contains("password") && title.ToLower().Contains("yenc"))
            {
                Logger.Trace("");
                return false;
            }

            if (!title.Any(char.IsLetterOrDigit))
            {
                return false;
            }

            var titleWithoutExtension = RemoveFileExtension(title);

            if (RejectHashedReleasesRegex.Any(v => v.IsMatch(titleWithoutExtension)))
            {
                Logger.Trace("Rejected Hashed Release Title: {0}", title);
                return false;
            }

            return true;
        }

        private static string GetSubGroup(MatchCollection matchCollection)
        {
            var subGroup = matchCollection[0].Groups["subgroup"];

            if (subGroup.Success)
            {
                return subGroup.Value;
            }

            return string.Empty;
        }

        private static string GetReleaseHash(MatchCollection matchCollection)
        {
            var hash = matchCollection[0].Groups["hash"];

            if (hash.Success)
            {
                var hashValue = hash.Value.Trim('[', ']');

                if (hashValue.Equals("1280x720"))
                {
                    return string.Empty;
                }

                return hashValue;
            }

            return string.Empty;
        }

    }
}
