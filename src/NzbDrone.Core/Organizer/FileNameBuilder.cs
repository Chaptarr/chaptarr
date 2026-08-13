using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.EnsureThat;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Organizer
{
    public interface IBuildFileNames
    {
        string BuildBookFileName(Author author, Edition edition, BookFile bookFile, NamingConfig namingConfig = null, List<CustomFormat> customFormats = null);
        string BuildBookFilePath(Author author, Edition edition, string fileName, string extension);
        string BuildBookPath(Author author);
        BasicNamingConfig GetBasicNamingConfig(NamingConfig nameSpec);
        string GetAuthorFolder(Author author, NamingConfig namingConfig = null, string mediaType = "audiobook");
    }

    public class FileNameBuilder : IBuildFileNames
    {
        private readonly INamingConfigService _namingConfigService;
        private readonly IQualityDefinitionService _qualityDefinitionService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly ICached<BookFormat[]> _trackFormatCache;
        private readonly Logger _logger;

        private static readonly Regex TitleRegex = new Regex(@"\{(?<prefix>[- ._,\[(\{]*)(?<token>(?:[a-z0-9]+)(?:(?<separator>[- ._]+)(?:[a-z0-9]+))?)(?::(?<customFormat>[a-z0-9]+))?(?<suffix>[- ._,)\]\}]*)\}",
                                                             RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex PartRegex = new Regex(@"\{(?<prefix>[^{]*?)(?<token1>PartNumber|PartCount)(?::(?<customFormat1>[a-z0-9]+))?(?<separator>.*(?=PartNumber|PartCount))?((?<token2>PartNumber|PartCount)(?::(?<customFormat2>[a-z0-9]+))?)?(?<suffix>[^}]*)\}",
                                                            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex SeasonEpisodePatternRegex = new Regex(@"(?<separator>(?<=})[- ._]+?)?(?<seasonEpisode>s?{season(?:\:0+)?}(?<episodeSeparator>[- ._]?[ex])(?<episode>{episode(?:\:0+)?}))(?<separator>[- ._]+?(?={))?",
                                                                            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex AuthorNameRegex = new Regex(@"(?<token>\{(?:Author)(?<separator>[- ._])(Clean)?(Sort)?Name(FirstLast)?(The)?\})",
                                                                            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static readonly Regex BookTitleRegex = new Regex(@"(?<token>\{(?:Book)(?<separator>[- ._])(Clean)?Title(The)?(NoSub)?\})",
                                                                            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex FileNameCleanupRegex = new Regex(@"([- ._])(\1)+", RegexOptions.Compiled);
        private static readonly Regex TrimSeparatorsRegex = new Regex(@"(^[- ._,]+|[- ._,]+$)", RegexOptions.Compiled);

        private static readonly Regex ScenifyRemoveChars = new Regex(@"(?<=\s)(,|<|>|\/|\\|;|:|'|""|\||`|~|!|\?|@|$|%|^|\*|-|_|=){1}(?=\s)|('|:|\?|,)(?=(?:(?:s|m)\s)|\s|$)|(\(|\)|\[|\]|\{|\})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ScenifyReplaceChars = new Regex(@"[\/]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex TitlePrefixRegex = new Regex(@"^(The|An|A) (.*?)((?: *\([^)]+\))*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public FileNameBuilder(INamingConfigService namingConfigService,
                               IQualityDefinitionService qualityDefinitionService,
                               ICacheManager cacheManager,
                               ICustomFormatCalculationService formatCalculator,
                               Logger logger)
        {
            _namingConfigService = namingConfigService;
            _qualityDefinitionService = qualityDefinitionService;
            _formatCalculator = formatCalculator;
            _trackFormatCache = cacheManager.GetCache<BookFormat[]>(GetType(), "bookFormat");
            _logger = logger;
        }

        public string BuildBookFileName(Author author, Edition edition, BookFile bookFile, NamingConfig namingConfig = null, List<CustomFormat> customFormats = null)
        {
            if (namingConfig == null)
            {
                namingConfig = _namingConfigService.GetConfig();
            }

            var mediaType = bookFile?.MediaType;
            if (mediaType.IsNullOrWhiteSpace() && bookFile?.Quality != null)
            {
                mediaType = BookFile.DetermineMediaType(bookFile.Quality);
            }

            namingConfig = namingConfig.GetForMediaType(mediaType);

            var renameBooks = namingConfig.RenameBooks;
            var originalFileName = GetOriginalFileName(bookFile);

            if (namingConfig.StandardBookFormat.IsNullOrWhiteSpace())
            {
                if (renameBooks)
                {
                    throw new NamingFormatException("File name format cannot be empty");
                }

                // No format configured and renaming is disabled: preserve legacy behavior.
                return originalFileName;
            }

            var pattern = namingConfig.StandardBookFormat;

            var tokenHandlers = new Dictionary<string, Func<TokenMatch, string>>(FileNameBuilderTokenEqualityComparer.Instance);

            AddAuthorTokens(tokenHandlers, author);
            AddBookTokens(tokenHandlers, edition);
            AddBookFileTokens(tokenHandlers, bookFile);
            AddQualityTokens(tokenHandlers, author, bookFile);
            AddMediaInfoTokens(tokenHandlers, bookFile);
            AddCustomFormats(tokenHandlers, author, bookFile, customFormats);
            
            // Add narrator tokens for audiobook organization
            AddNarratorTokens(tokenHandlers, edition);

            // Add GraphicAudio tokens - these override the regular book title tokens when GraphicAudio is detected
            AddGraphicAudioTokens(tokenHandlers, edition, bookFile);

            var splitPatterns = pattern.Split(new char[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            var components = new List<string>();

            foreach (var s in splitPatterns)
            {
                var splitPattern = s;

                var component = ReplacePartTokens(splitPattern, tokenHandlers, namingConfig).Trim();
                component = ReplaceTokens(component, tokenHandlers, namingConfig).Trim();

                component = FileNameCleanupRegex.Replace(component, match => match.Captures[0].Value[0].ToString());
                component = TrimSeparatorsRegex.Replace(component, string.Empty);

                if (component.IsNotNullOrWhiteSpace())
                {
                    components.Add(component);
                }
            }

            // When renaming is disabled, we still organize into the configured folder structure,
            // but we preserve the original filename for the final path component.
            if (!renameBooks)
            {
                if (components.Count == 0)
                {
                    return originalFileName;
                }

                components[^1] = originalFileName;
                return Path.Combine(components.ToArray());
            }

            // Multi-part audiobooks often arrive as many separate audio files (mp3 chapters/tracks).
            // If the naming pattern doesn't include any per-file disambiguator (PartNumber / Original Filename),
            // all tracks would be renamed to the same destination and only the first one would import.
            // Default to preserving the original filename for the final path component in that case.
            if (bookFile.PartCount > 1 &&
                !pattern.Contains("PartNumber", StringComparison.InvariantCultureIgnoreCase) &&
                !pattern.Contains("Original Filename", StringComparison.InvariantCultureIgnoreCase) &&
                !pattern.Contains("Original Title", StringComparison.InvariantCultureIgnoreCase) &&
                components.Count > 0)
            {
                components[^1] = CleanFileName(originalFileName, namingConfig);
            }

            return Path.Combine(components.ToArray());
        }

        public string BuildBookFilePath(Author author, Edition edition, string fileName, string extension)
        {
            Ensure.That(extension, () => extension).IsNotNullOrWhiteSpace();

            var path = BuildBookPath(author);

            return Path.Combine(path, fileName + extension);
        }

        public string BuildBookPath(Author author)
        {
            return author.Path;
        }

        public BasicNamingConfig GetBasicNamingConfig(NamingConfig nameSpec)
        {
            var trackFormat = GetTrackFormat(nameSpec.StandardBookFormat).LastOrDefault();

            if (trackFormat == null)
            {
                return new BasicNamingConfig();
            }

            var basicNamingConfig = new BasicNamingConfig
            {
                Separator = trackFormat.Separator
            };

            var titleTokens = TitleRegex.Matches(nameSpec.StandardBookFormat);

            foreach (Match match in titleTokens)
            {
                var separator = match.Groups["separator"].Value;
                var token = match.Groups["token"].Value;

                if (!separator.Equals(" "))
                {
                    basicNamingConfig.ReplaceSpaces = true;
                }

                if (token.StartsWith("{Author", StringComparison.InvariantCultureIgnoreCase))
                {
                    basicNamingConfig.IncludeAuthorName = true;
                }

                if (token.StartsWith("{Book", StringComparison.InvariantCultureIgnoreCase))
                {
                    basicNamingConfig.IncludeBookTitle = true;
                }

                if (token.StartsWith("{Quality", StringComparison.InvariantCultureIgnoreCase))
                {
                    basicNamingConfig.IncludeQuality = true;
                }
            }

            return basicNamingConfig;
        }

        public string GetAuthorFolder(Author author, NamingConfig namingConfig = null, string mediaType = "audiobook")
        {
            if (namingConfig == null)
            {
                namingConfig = _namingConfigService.GetConfig();
            }

            namingConfig = namingConfig.GetForMediaType(mediaType);

            var pattern = namingConfig.AuthorFolderFormat;
            var tokenHandlers = new Dictionary<string, Func<TokenMatch, string>>(FileNameBuilderTokenEqualityComparer.Instance);

            AddAuthorTokens(tokenHandlers, author);

            var splitPatterns = pattern.Split(new char[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            var components = new List<string>();

            foreach (var s in splitPatterns)
            {
                var splitPattern = s;

                var component = ReplaceTokens(splitPattern, tokenHandlers, namingConfig);
                component = CleanFolderName(component);

                if (component.IsNotNullOrWhiteSpace())
                {
                    components.Add(component);
                }
            }

            return Path.Combine(components.ToArray());
        }

        public static string CleanTitle(string title)
        {
            title = title.Replace("&", "and");
            title = ScenifyReplaceChars.Replace(title, " ");
            title = ScenifyRemoveChars.Replace(title, string.Empty);

            return title;
        }

        public static string TitleThe(string title)
        {
            return TitlePrefixRegex.Replace(title, "$2, $1$3");
        }

        public static string CleanFileName(string name)
        {
            return CleanFileName(name, NamingConfig.Default);
        }

        public static string CleanFolderName(string name)
        {
            name = FileNameCleanupRegex.Replace(name, match => match.Captures[0].Value[0].ToString());

            return name.Trim(' ', '.');
        }

        private void AddAuthorTokens(Dictionary<string, Func<TokenMatch, string>> tokenHandlers, Author author)
        {
            tokenHandlers["{Author Name}"] = m => author.Name;
            tokenHandlers["{Author CleanName}"] = m => CleanTitle(author.Name);
            tokenHandlers["{Author NameThe}"] = m => TitleThe(author.Name);
            tokenHandlers["{Author NameFirstLast}"] = m => GetAuthorNameFirstLast(author);
            tokenHandlers["{Author CleanNameFirstLast}"] = m => CleanTitle(GetAuthorNameFirstLast(author));
            tokenHandlers["{Author SortName}"] = m => author?.NameLastFirst ?? string.Empty;
            tokenHandlers["{Author NameFirstCharacter}"] = m => TitleThe(author.Name).Substring(0, 1).FirstCharToUpper();

            if (author.Disambiguation != null)
            {
                tokenHandlers["{Author Disambiguation}"] = m => author.Disambiguation;
            }
        }

        private static string GetAuthorNameFirstLast(Author author)
        {
            if (author == null)
            {
                return string.Empty;
            }

            var name = author.Name?.Trim();
            if (name.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            // Prefer NameLastFirst because it preserves multi-word surnames and suffix handling.
            var lastFirst = author.NameLastFirst;
            if (lastFirst.IsNullOrWhiteSpace())
            {
                lastFirst = name.ToLastFirst();
            }

            var commaIndex = lastFirst.IndexOf(',');
            if (commaIndex <= 0 || commaIndex >= lastFirst.Length - 1)
            {
                // Fallback: if we can't reliably parse last/first, keep the original name to avoid mangling.
                return name;
            }

            var lastName = lastFirst.Substring(0, commaIndex).Trim();
            var givenNames = lastFirst.Substring(commaIndex + 1).Trim();

            if (lastName.IsNullOrWhiteSpace())
            {
                return name;
            }

            if (givenNames.IsNullOrWhiteSpace())
            {
                return lastName;
            }

            var givenTokens = givenNames.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (givenTokens.Length == 0)
            {
                return lastName;
            }

            // Keep the first given-name token and preserve any subsequent initial tokens.
            // Examples:
            // - "Martin, George R.R." -> "George R.R. Martin"
            // - "Tolkien, J.R.R." -> "J.R.R. Tolkien"
            // - "Lewis, C. S." -> "C. S. Lewis"
            // - "Martin, George Raymond Richard" -> "George Martin"
            var parts = new List<string> { givenTokens[0] };
            for (var i = 1; i < givenTokens.Length; i++)
            {
                if (IsInitialToken(givenTokens[i]))
                {
                    parts.Add(givenTokens[i]);
                }
            }

            var givenToUse = string.Join(" ", parts);

            return $"{givenToUse} {lastName}".Trim();
        }

        private static bool IsInitialToken(string token)
        {
            if (token.IsNullOrWhiteSpace())
            {
                return false;
            }

            token = token.Trim();

            // Treat "R.", "R", and initial sequences like "R.R." or "J.R.R." as initials.
            // We define an initial token as one or more single-character alphanumeric segments separated
            // by non-alphanumeric characters.
            var segmentLength = 0;
            var segmentCount = 0;

            foreach (var c in token)
            {
                if (char.IsLetterOrDigit(c))
                {
                    segmentLength++;
                    if (segmentLength > 1)
                    {
                        // Segment is longer than one character (e.g. "George", "Raymond") -> not an initial token.
                        return false;
                    }
                }
                else
                {
                    if (segmentLength == 1)
                    {
                        segmentCount++;
                    }
                    segmentLength = 0;
                }
            }

            if (segmentLength == 1)
            {
                segmentCount++;
            }

            return segmentCount > 0;
        }

        private void AddBookTokens(Dictionary<string, Func<TokenMatch, string>> tokenHandlers, Edition edition)
        {
            tokenHandlers["{Book Title}"] = m => edition.Title;
            tokenHandlers["{Book CleanTitle}"] = m => CleanTitle(edition.Title);
            tokenHandlers["{Book TitleThe}"] = m => TitleThe(edition.Title);

            var authorName = edition.Book?.Author?.Name ?? string.Empty;
            var (titleNoSub, subtitle) = edition.Title.SplitBookTitle(authorName);

            tokenHandlers["{Book TitleNoSub}"] = m => titleNoSub;
            tokenHandlers["{Book CleanTitleNoSub}"] = m => CleanTitle(titleNoSub);
            tokenHandlers["{Book TitleTheNoSub}"] = m => TitleThe(titleNoSub);

            tokenHandlers["{Book Subtitle}"] = m => subtitle;
            tokenHandlers["{Book CleanSubtitle}"] = m => CleanTitle(subtitle);
            tokenHandlers["{Book SubtitleThe}"] = m => TitleThe(subtitle);

            // Series tokens - automatically handle "if applicable" logic
            var seriesName = edition.Book?.SeriesName;
            var seriesPosition = edition.Book?.SeriesPosition;

            // Fall back to SeriesLinks if present (not always loaded during renaming).
            var seriesLinks = edition.Book?.SeriesLinks;
            var primarySeries = BookSeriesLabel.SelectDisplayLink(seriesLinks);

            if (seriesName.IsNullOrWhiteSpace())
            {
                seriesName = primarySeries?.Series?.Value?.Title;
            }

            if (seriesPosition.IsNullOrWhiteSpace())
            {
                seriesPosition = primarySeries?.Position;
            }
            
            // Series name only (returns empty if no series)
            tokenHandlers["{Book Series}"] = m => 
                seriesName ?? m.DefaultValue("");
            
            // Series position only (e.g., "3" or "2.5")
            tokenHandlers["{Book SeriesPosition}"] = m => 
                seriesPosition ?? m.DefaultValue("");
            
            // Combined series with position (e.g., "Dresden Files #3")
            tokenHandlers["{Book SeriesTitle}"] = m => 
            {
                if (seriesName.IsNotNullOrWhiteSpace())
                {
                    if (seriesPosition.IsNotNullOrWhiteSpace())
                    {
                        return $"{seriesName} #{seriesPosition}";
                    }

                    return seriesName;
                }
                return m.DefaultValue("");
            };
            
            // Series folder token - includes series name with optional position
            tokenHandlers["{Series Folder}"] = m =>
            {
                if (seriesName.IsNotNullOrWhiteSpace())
                {
                    // For folder names, include position if it exists
                    if (seriesPosition.IsNotNullOrWhiteSpace())
                    {
                        return CleanTitle($"{seriesName} {seriesPosition}");
                    }

                    return CleanTitle(seriesName);
                }
                return m.DefaultValue("");
            };
            
            // Book title with series position appended if applicable
            tokenHandlers["{Book TitleWithSeries}"] = m =>
            {
                var bookTitle = edition.Title;

                if (seriesName.IsNotNullOrWhiteSpace() && seriesPosition.IsNotNullOrWhiteSpace())
                {
                    return $"{bookTitle} - {seriesName} #{seriesPosition}";
                }

                return bookTitle;
            };

            if (edition.Disambiguation != null)
            {
                tokenHandlers["{Book Disambiguation}"] = m => edition.Disambiguation;
            }

            if (edition.ReleaseDate.HasValue)
            {
                tokenHandlers["{Release Year}"] = m => edition.ReleaseDate.Value.Year.ToString();
            }
            else if (edition.Book?.ReleaseDate.HasValue == true)
            {
                tokenHandlers["{Release Year}"] = m => edition.Book.ReleaseDate.Value.Year.ToString();
            }
            else
            {
                tokenHandlers["{Release Year}"] = m => "Unknown";
            }

            if (edition.ReleaseDate.HasValue)
            {
                tokenHandlers["{Edition Year}"] = m => edition.ReleaseDate.Value.Year.ToString();
            }
            else
            {
                tokenHandlers["{Edition Year}"] = m => "Unknown";
            }

            if (edition.Book?.ReleaseDate.HasValue == true)
            {
                tokenHandlers["{Release YearFirst}"] = m => edition.Book.ReleaseDate.Value.Year.ToString();
            }
            else
            {
                tokenHandlers["{Release YearFirst}"] = m => "Unknown";
            }
        }

        private void AddNarratorTokens(Dictionary<string, Func<TokenMatch, string>> tokenHandlers, Edition edition)
        {
            // Handle multiple narrators - use the Narrator field (primary) or NarratorNames list
            var primaryNarrator = edition?.Narrator ?? string.Empty;
            var narratorList = (edition?.NarratorNames ?? new List<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .ToList();
            
            // If primary narrator is empty but we have names in the list, use the first one
            if (string.IsNullOrWhiteSpace(primaryNarrator) && narratorList.Any())
            {
                primaryNarrator = narratorList.First();
            }

            // If we have a true multi-narrator cast, represent it as "Full Cast" for display and naming.
            // (The full list remains available via the {Narrators} token.)
            var isFullCast = narratorList.Count > 2 ||
                             primaryNarrator.Equals("Full Cast", StringComparison.OrdinalIgnoreCase);

            var displayNarrator = primaryNarrator;
            if (isFullCast)
            {
                displayNarrator = "Full Cast";
            }
            else if (narratorList.Count == 2)
            {
                // Prefer an explicit primary narrator string when set, otherwise show both.
                if (string.IsNullOrWhiteSpace(displayNarrator) ||
                    displayNarrator.Equals(narratorList[0], StringComparison.OrdinalIgnoreCase) ||
                    displayNarrator.Equals(narratorList[1], StringComparison.OrdinalIgnoreCase))
                {
                    displayNarrator = $"{narratorList[0]} + {narratorList[1]}";
                }
            }

            // Full narrator name(s)
            tokenHandlers["{Narrator}"] = m => 
            {
                if (!string.IsNullOrWhiteSpace(displayNarrator))
                {
                    return displayNarrator;
                }

                return m.DefaultValue("");
            };
            
            // Clean narrator name (for file/folder naming)
            tokenHandlers["{Narrator CleanName}"] = m => 
            {
                if (!string.IsNullOrWhiteSpace(displayNarrator))
                {
                    return CleanTitle(displayNarrator);
                }

                return m.DefaultValue("");
            };
            
            // Narrator initials (e.g., "Ray Porter" -> "RP")
            tokenHandlers["{Narrator Initials}"] = m =>
            {
                if (!string.IsNullOrWhiteSpace(primaryNarrator))
                {
                    var parts = primaryNarrator.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var initials = string.Join("", parts.Select(p => p[0].ToString().ToUpper()));
                    return initials;
                }
                else if (narratorList.Any())
                {
                    var parts = narratorList.First().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var initials = string.Join("", parts.Select(p => p[0].ToString().ToUpper()));
                    return initials;
                }
                else
                    return m.DefaultValue("");
            };
            
            // First name only
            tokenHandlers["{Narrator First}"] = m =>
            {
                if (!string.IsNullOrWhiteSpace(primaryNarrator))
                {
                    var parts = primaryNarrator.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    return parts.FirstOrDefault() ?? m.DefaultValue("");
                }
                else if (narratorList.Any())
                {
                    var parts = narratorList.First().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    return parts.FirstOrDefault() ?? m.DefaultValue("");
                }
                else
                    return m.DefaultValue("");
            };
            
            // Last name only
            tokenHandlers["{Narrator Last}"] = m =>
            {
                if (!string.IsNullOrWhiteSpace(primaryNarrator))
                {
                    var parts = primaryNarrator.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    return parts.LastOrDefault() ?? m.DefaultValue("");
                }
                else if (narratorList.Any())
                {
                    var parts = narratorList.First().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    return parts.LastOrDefault() ?? m.DefaultValue("");
                }
                else
                    return m.DefaultValue("");
            };
            
            // Multiple narrators (comma separated)
            tokenHandlers["{Narrators}"] = m =>
            {
                if (narratorList.Any())
                    return string.Join(", ", narratorList);
                else if (!string.IsNullOrWhiteSpace(primaryNarrator))
                    return primaryNarrator;
                else
                    return m.DefaultValue("");
            };
        }

        private void AddBookFileTokens(Dictionary<string, Func<TokenMatch, string>> tokenHandlers, BookFile bookFile)
        {
            tokenHandlers["{Original Title}"] = m => GetOriginalTitle(bookFile);
            tokenHandlers["{Original Filename}"] = m => GetOriginalFileName(bookFile);
            tokenHandlers["{Release Group}"] = m => bookFile.ReleaseGroup ?? m.DefaultValue("Chaptarr");

            if (bookFile.PartCount > 1)
            {
                tokenHandlers["{PartNumber}"] = m => FormatPartToken(bookFile.Part, bookFile.PartCount, m.CustomFormat);
                tokenHandlers["{PartCount}"] = m => FormatPartToken(bookFile.PartCount, bookFile.PartCount, m.CustomFormat);
            }
        }

        private static string FormatPartToken(int value, int partCount, string customFormat)
        {
            if (customFormat.IsNullOrWhiteSpace())
            {
                customFormat = "0";
            }

            if (!customFormat.Equals("smart", StringComparison.OrdinalIgnoreCase))
            {
                return value.ToString(customFormat);
            }

            var width = Math.Max(1, partCount.ToString().Length);
            var paddedFormat = new string('0', width);

            return value.ToString(paddedFormat);
        }

        private void AddQualityTokens(Dictionary<string, Func<TokenMatch, string>> tokenHandlers, Author author, BookFile bookFile)
        {
            var qualityTitle = _qualityDefinitionService.Get(bookFile.Quality.Quality).Title;
            var qualityProper = GetQualityProper(bookFile.Quality);

            //var qualityReal = GetQualityReal(author, bookFile.Quality);
            tokenHandlers["{Quality Full}"] = m => string.Format("{0}", qualityTitle);
            tokenHandlers["{Quality Title}"] = m => qualityTitle;
            tokenHandlers["{Quality Proper}"] = m => qualityProper;

            //tokenHandlers["{Quality Real}"] = m => qualityReal;
        }

        private void AddMediaInfoTokens(Dictionary<string, Func<TokenMatch, string>> tokenHandlers, BookFile bookFile)
        {
            if (bookFile.MediaInfo == null)
            {
                _logger.Trace("Media info is unavailable for {0}", bookFile);

                return;
            }

            var audioCodec = MediaInfoFormatter.FormatAudioCodec(bookFile.MediaInfo);
            var audioChannels = MediaInfoFormatter.FormatAudioChannels(bookFile.MediaInfo);
            var audioChannelsFormatted = audioChannels > 0 ?
                                audioChannels.ToString("F1", CultureInfo.InvariantCulture) :
                                string.Empty;

            tokenHandlers["{MediaInfo AudioCodec}"] = m => audioCodec;
            tokenHandlers["{MediaInfo AudioChannels}"] = m => audioChannelsFormatted;
            tokenHandlers["{MediaInfo AudioBitRate}"] = m => MediaInfoFormatter.FormatAudioBitrate(bookFile.MediaInfo);
            tokenHandlers["{MediaInfo AudioBitsPerSample}"] = m => MediaInfoFormatter.FormatAudioBitsPerSample(bookFile.MediaInfo);
            tokenHandlers["{MediaInfo AudioSampleRate}"] = m => MediaInfoFormatter.FormatAudioSampleRate(bookFile.MediaInfo);
        }

        private void AddGraphicAudioTokens(Dictionary<string, Func<TokenMatch, string>> tokenHandlers, Edition edition, BookFile bookFile)
        {
            var mediaType = bookFile?.Quality != null ? BookFile.DetermineMediaType(bookFile.Quality) : bookFile?.MediaType;

            // If this is a GraphicAudio production, modify the book title tokens to include " - GraphicAudio"
            if (bookFile != null && bookFile.IsGraphicAudio && mediaType == "audiobook")
            {
                var graphicAudioSuffix = " - GraphicAudio";

                // Override the standard book title tokens to include GraphicAudio suffix
                tokenHandlers["{Book Title}"] = m => edition.Title + graphicAudioSuffix;
                tokenHandlers["{Book CleanTitle}"] = m => CleanTitle(edition.Title + graphicAudioSuffix);
                tokenHandlers["{Book TitleThe}"] = m => TitleThe(edition.Title + graphicAudioSuffix);

                var (titleNoSub, subtitle) = edition.Title.SplitBookTitle(edition.Book.Author?.Name ?? string.Empty);

                tokenHandlers["{Book TitleNoSub}"] = m => titleNoSub + graphicAudioSuffix;
                tokenHandlers["{Book CleanTitleNoSub}"] = m => CleanTitle(titleNoSub + graphicAudioSuffix);
                tokenHandlers["{Book TitleTheNoSub}"] = m => TitleThe(titleNoSub + graphicAudioSuffix);

                tokenHandlers["{GraphicAudio}"] = m => "GraphicAudio";
                tokenHandlers["{AudioProductionType}"] = m => bookFile.AudioProductionType ?? "GraphicAudio";
            }
            else
            {
                // Not GraphicAudio, ensure the tokens return empty for conditional formatting
                tokenHandlers["{GraphicAudio}"] = m => string.Empty;
                tokenHandlers["{AudioProductionType}"] = m => string.Empty;
            }
        }

        private void AddCustomFormats(Dictionary<string, Func<TokenMatch, string>> tokenHandlers, Author author, BookFile bookFile, List<CustomFormat> customFormats = null)
        {
            if (customFormats == null)
            {
                bookFile.Author = author;
                customFormats = _formatCalculator.ParseCustomFormat(bookFile, author);
            }

            tokenHandlers["{Custom Formats}"] = m => string.Join(" ", customFormats.Where(x => x.IncludeCustomFormatWhenRenaming));
        }

        private string ReplaceTokens(string pattern, Dictionary<string, Func<TokenMatch, string>> tokenHandlers, NamingConfig namingConfig)
        {
            return TitleRegex.Replace(pattern, match => ReplaceToken(match, tokenHandlers, namingConfig));
        }

        private string ReplaceToken(Match match, Dictionary<string, Func<TokenMatch, string>> tokenHandlers, NamingConfig namingConfig)
        {
            var tokenMatch = new TokenMatch
            {
                RegexMatch = match,
                Prefix = match.Groups["prefix"].Value,
                Separator = match.Groups["separator"].Value,
                Suffix = match.Groups["suffix"].Value,
                Token = match.Groups["token"].Value,
                CustomFormat = match.Groups["customFormat"].Value
            };

            if (tokenMatch.CustomFormat.IsNullOrWhiteSpace())
            {
                tokenMatch.CustomFormat = null;
            }

            var tokenHandler = tokenHandlers.GetValueOrDefault(tokenMatch.Token, m => string.Empty);

            var replacementText = tokenHandler(tokenMatch).Trim();

            if (tokenMatch.Token.All(t => !char.IsLetter(t) || char.IsLower(t)))
            {
                replacementText = replacementText.ToLower();
            }
            else if (tokenMatch.Token.All(t => !char.IsLetter(t) || char.IsUpper(t)))
            {
                replacementText = replacementText.ToUpper();
            }

            if (!tokenMatch.Separator.IsNullOrWhiteSpace())
            {
                replacementText = replacementText.Replace(" ", tokenMatch.Separator);
            }

            replacementText = CleanFileName(replacementText, namingConfig);

            if (!replacementText.IsNullOrWhiteSpace())
            {
                replacementText = tokenMatch.Prefix + replacementText + tokenMatch.Suffix;
            }

            return replacementText;
        }

        private string ReplacePartTokens(string pattern, Dictionary<string, Func<TokenMatch, string>> tokenHandlers, NamingConfig namingConfig)
        {
            return PartRegex.Replace(pattern, match => ReplacePartToken(match, tokenHandlers, namingConfig));
        }

        private string ReplacePartToken(Match match, Dictionary<string, Func<TokenMatch, string>> tokenHandlers, NamingConfig namingConfig)
        {
            var tokenHandler = tokenHandlers.GetValueOrDefault($"{{{match.Groups["token1"].Value}}}", m => string.Empty);

            var tokenText1 = tokenHandler(new TokenMatch { CustomFormat = match.Groups["customFormat1"].Success ? match.Groups["customFormat1"].Value : "0" });

            if (tokenText1 == string.Empty)
            {
                return string.Empty;
            }

            var prefix = match.Groups["prefix"].Value;

            var tokenText2 = string.Empty;

            var separator = match.Groups["separator"].Success ? match.Groups["separator"].Value : string.Empty;

            var suffix = match.Groups["suffix"].Value;

            if (match.Groups["token2"].Success)
            {
                tokenHandler = tokenHandlers.GetValueOrDefault($"{{{match.Groups["token2"].Value}}}", m => string.Empty);

                tokenText2 = tokenHandler(new TokenMatch { CustomFormat = match.Groups["customFormat2"].Success ? match.Groups["customFormat2"].Value : "0" });
            }

            return $"{prefix}{tokenText1}{separator}{tokenText2}{suffix}";
        }

        private BookFormat[] GetTrackFormat(string pattern)
        {
            return _trackFormatCache.Get(pattern, () => SeasonEpisodePatternRegex.Matches(pattern).OfType<Match>()
                .Select(match => new BookFormat
                {
                    BookSeparator = match.Groups["episodeSeparator"].Value,
                    Separator = match.Groups["separator"].Value,
                    BookPattern = match.Groups["episode"].Value,
                }).ToArray());
        }

        private string GetQualityProper(QualityModel quality)
        {
            if (quality.Revision.Version > 1)
            {
                if (quality.Revision.IsRepack)
                {
                    return "Repack";
                }

                return "Proper";
            }

            return string.Empty;
        }

        private string GetOriginalTitle(BookFile bookFile)
        {
            if (bookFile.SceneName.IsNullOrWhiteSpace())
            {
                return GetOriginalFileName(bookFile);
            }

            return bookFile.SceneName;
        }

        private string GetOriginalFileName(BookFile bookFile)
        {
            return Path.GetFileNameWithoutExtension(bookFile.Path);
        }

        private static string CleanFileName(string name, NamingConfig namingConfig)
        {
            var result = name;
            string[] badCharacters = { "\\", "/", "<", ">", "?", "*", "|", "\"" };
            string[] goodCharacters = { "+", "+", "", "", "!", "-", "", "" };

            if (namingConfig.ReplaceIllegalCharacters)
            {
                // Smart replaces a colon followed by a space with space dash space for a better appearance
                if (namingConfig.ColonReplacementFormat == ColonReplacementFormat.Smart)
                {
                    result = result.Replace(": ", " - ");
                    result = result.Replace(":", "-");
                }
                else
                {
                    var replacement = string.Empty;

                    switch (namingConfig.ColonReplacementFormat)
                    {
                        case ColonReplacementFormat.Dash:
                            replacement = "-";
                            break;
                        case ColonReplacementFormat.SpaceDash:
                            replacement = " -";
                            break;
                        case ColonReplacementFormat.SpaceDashSpace:
                            replacement = " - ";
                            break;
                    }

                    result = result.Replace(":", replacement);
                }
            }
            else
            {
                result = result.Replace(":", string.Empty);
            }

            for (var i = 0; i < badCharacters.Length; i++)
            {
                result = result.Replace(badCharacters[i], namingConfig.ReplaceIllegalCharacters ? goodCharacters[i] : string.Empty);
            }

            return result.TrimStart(' ', '.').TrimEnd(' ');
        }

        private string GetCollisionAwareTitle(Edition edition, string baseTitle)
        {
            // Simplified version without BookService dependency to avoid circular reference
            // Just return the base title for now
            return baseTitle;
        }
    }

    internal sealed class TokenMatch
    {
        public Match RegexMatch { get; set; }
        public string Prefix { get; set; }
        public string Separator { get; set; }
        public string Suffix { get; set; }
        public string Token { get; set; }
        public string CustomFormat { get; set; }

        public string DefaultValue(string defaultValue)
        {
            if (string.IsNullOrEmpty(Prefix) && string.IsNullOrEmpty(Suffix))
            {
                return defaultValue;
            }
            else
            {
                return string.Empty;
            }
        }
    }

    public enum ColonReplacementFormat
    {
        Delete = 0,
        Dash = 1,
        SpaceDash = 2,
        SpaceDashSpace = 3,
        Smart = 4
    }
}
