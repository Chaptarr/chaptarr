using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.MetadataSource
{
    public class SmartAuthorSearchService : ISmartAuthorSearchService
    {
        private readonly ISearchForNewAuthor _authorSearchService;
        private readonly IAuthorService _authorService;
        private readonly IAuthorLibraryService _authorLibraryService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly ICached<Author> _authorCache;
        private readonly Logger _logger;

        private static readonly Regex AuthorDelimiterRegex = new Regex(
            @"[;&]|\s+(?:and|AND|featuring|feat\.|ft\.|with|WITH)\s+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex TitleAuthorPattern = new Regex(
            @"^([^-:]+)\s*[-:]\s*(.+)$",
            RegexOptions.Compiled);

        private static readonly Regex ByAuthorPattern = new Regex(
            @"\s+by\s+([^,\-\(\)]+)(?:\s*[,\-\(\)]|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public SmartAuthorSearchService(
            ISearchForNewAuthor authorSearchService,
            IAuthorService authorService,
            IAuthorLibraryService authorLibraryService,
            IManageCommandQueue commandQueueManager,
            ICacheManager cacheManager,
            Logger logger)
        {
            _authorSearchService = authorSearchService;
            _authorService = authorService;
            _authorLibraryService = authorLibraryService;
            _commandQueueManager = commandQueueManager;
            _authorCache = cacheManager.GetCache<Author>(GetType(), "authorSearch");
            _logger = logger;
        }

        public List<Author> SearchAndCreateAuthors(List<string> authorNames)
        {
            var authors = new List<Author>();
            var uniqueNames = authorNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            _logger.Debug("Searching for {0} unique author names", uniqueNames.Count);

            foreach (var authorName in uniqueNames)
            {
                try
                {
                    var author = GetOrCreateAuthor(authorName);
                    if (author != null)
                    {
                        authors.Add(author);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Failed to search/create author '{0}'", authorName);
                }
            }

            return authors;
        }

        public Author GetOrCreateAuthor(string authorName)
        {
            if (string.IsNullOrWhiteSpace(authorName))
            {
                return null;
            }

            var normalizedName = NormalizeAuthorName(authorName);

            // Check cache first
            var cached = _authorCache.Find(normalizedName);
            if (cached != null)
            {
                _logger.Debug("Found author '{0}' in cache", authorName);
                return cached;
            }

            // Check local database
            var existingAuthor = FindExistingAuthor(authorName);
            if (existingAuthor != null)
            {
                _logger.Debug("Found author '{0}' in local database", authorName);
                _authorCache.Set(normalizedName, existingAuthor, TimeSpan.FromHours(24));
                return existingAuthor;
            }

            // Search Goodreads
            _logger.Debug("Searching Goodreads for author '{0}'", authorName);
            var searchResults = SearchGoodreadsForAuthor(authorName);

            if (!searchResults.Any())
            {
                _logger.Warn("No results found for author '{0}' on Goodreads", authorName);
                return null;
            }

            // Find best match
            var bestMatch = FindBestAuthorMatch(authorName, searchResults);
            if (bestMatch == null)
            {
                _logger.Warn("No suitable match found for author '{0}' among {1} results", authorName, searchResults.Count);
                return null;
            }

            // Create author in database
            var authorProviderId = bestMatch.GoodreadsAuthorId?.ToString() ?? bestMatch.HardcoverAuthorId ?? bestMatch.OpenLibraryAuthorId ?? "unknown";
            _logger.Debug("Creating author '{0}' (ProviderId: {1})", bestMatch.Name, authorProviderId);

            var createdAuthor = CreateAuthor(bestMatch);
            if (createdAuthor != null)
            {
                _authorCache.Set(normalizedName, createdAuthor, TimeSpan.FromHours(24));
            }

            return createdAuthor;
        }

        public List<string> ExtractAuthorNamesFromId3(Dictionary<string, List<string>> id3Tags)
        {
            var authorNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // FIELD-AGNOSTIC APPROACH: Check ALL non-null ID3 field values
            // We don't care what the field is called - just extract potential author names from ALL values
            foreach (var kvp in id3Tags)
            {
                // Skip null/empty values
                if (kvp.Value == null || !kvp.Value.Any())
                {
                    continue;
                }

                foreach (var value in kvp.Value.Where(v => !string.IsNullOrWhiteSpace(v)))
                {
                    // Skip very short values that are unlikely to be author names
                    if (value.Length < 3)
                    {
                        continue;
                    }

                    // Split by common delimiters to handle multiple authors in one field
                    var splitAuthors = SplitAuthorString(value);
                    foreach (var author in splitAuthors)
                    {
                        if (IsValidAuthorName(author))
                        {
                            authorNames.Add(author.Trim());
                        }
                    }

                    // Also try to extract authors from title-like patterns (e.g., "Book Title by Author Name")
                    // This handles cases where author info might be embedded in any field
                    var extractedAuthors = ExtractAuthorsFromTitle(value);
                    foreach (var author in extractedAuthors)
                    {
                        if (IsValidAuthorName(author))
                        {
                            authorNames.Add(author.Trim());
                        }
                    }
                }
            }

            _logger.Debug("Extracted {0} potential author names from {1} ID3 fields (field-agnostic)", authorNames.Count, id3Tags.Count);

            return authorNames.ToList();
        }

        private Author FindExistingAuthor(string authorName)
        {
            var allAuthors = _authorService.GetAllAuthors();

            // Exact match
            var exactMatch = allAuthors.FirstOrDefault(a =>
                a.Name.Equals(authorName, StringComparison.OrdinalIgnoreCase));
            if (exactMatch != null)
            {
                return exactMatch;
            }

            // Normalized match
            var normalized = NormalizeAuthorName(authorName);
            var normalizedMatch = allAuthors.FirstOrDefault(a =>
                NormalizeAuthorName(a.Name).Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (normalizedMatch != null)
            {
                return normalizedMatch;
            }

            // Check aliases
            return allAuthors.FirstOrDefault(a =>
                a.Aliases?.Any(alias =>
                    alias.Equals(authorName, StringComparison.OrdinalIgnoreCase)) ?? false);
        }

        private List<Author> SearchGoodreadsForAuthor(string authorName)
        {
            try
            {
                return _authorSearchService.SearchForNewAuthor(authorName);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error searching Goodreads for author '{0}'", authorName);
                return new List<Author>();
            }
        }

        private Author FindBestAuthorMatch(string searchName, List<Author> results)
        {
            // Exact match
            var exactMatch = results.FirstOrDefault(r =>
                r.Name.Equals(searchName, StringComparison.OrdinalIgnoreCase));
            if (exactMatch != null)
            {
                return exactMatch;
            }

            // Normalized match
            var normalizedSearch = NormalizeAuthorName(searchName);
            var normalizedMatch = results.FirstOrDefault(r =>
                NormalizeAuthorName(r.Name).Equals(normalizedSearch, StringComparison.OrdinalIgnoreCase));
            if (normalizedMatch != null)
            {
                return normalizedMatch;
            }

            // Word-based match (all search words must appear in result)
            var searchWords = GetSignificantWords(searchName);
            if (searchWords.Count >= 2)
            {
                var wordMatch = results.FirstOrDefault(r =>
                {
                    var resultWords = GetSignificantWords(r.Name);
                    return searchWords.All(w =>
                        resultWords.Contains(w, StringComparer.OrdinalIgnoreCase));
                });

                if (wordMatch != null)
                {
                    return wordMatch;
                }
            }

            // If only one result and it's reasonably close, use it
            if (results.Count == 1)
            {
                var similarity = CalculateSimilarity(searchName, results[0].Name);
                if (similarity > 0.7)
                {
                    return results[0];
                }
            }

            return null;
        }

        private Author CreateAuthor(Author author)
        {
            try
            {
                // Build provider ID from available metadata
                string providerId = null;
                if (!string.IsNullOrEmpty(author.GoodreadsAuthorId))
                {
                    providerId = ProviderIdHelper.Normalize(author.GoodreadsAuthorId, "gr");
                }
                else if (!string.IsNullOrEmpty(author.HardcoverAuthorId))
                {
                    providerId = ProviderIdHelper.Normalize(author.HardcoverAuthorId, "hc");
                }
                else if (!string.IsNullOrEmpty(author.OpenLibraryAuthorId))
                {
                    providerId = ProviderIdHelper.Normalize(author.OpenLibraryAuthorId, "ol");
                }

                if (string.IsNullOrEmpty(providerId))
                {
                    _logger.Warn("Cannot create author '{0}' - no valid provider ID found", author.Name);
                    return null;
                }

                // Create monitoring config
                var config = new MonitoringConfig
                {
                    AudiobookMonitored = false,
                    AudiobookMonitorNewItems = NewItemMonitorTypes.None,
                    AudiobookMonitorExistingMode = MonitorTypes.None,
                    EbookMonitored = false,
                    EbookMonitorNewItems = NewItemMonitorTypes.None,
                    EbookMonitorExistingMode = MonitorTypes.None,
                    AudiobookQualityProfileId = 1, // Default audiobook profile
                    EbookQualityProfileId = 1, // Default ebook profile
                    AudiobookMetadataProfileId = 1, // Default audiobook metadata profile
                    EbookMetadataProfileId = 1 // Default ebook metadata profile
                };

                // Use AuthorLibraryService to add the author
                var addedAuthor = Task.Run(async () =>
                    await _authorLibraryService.AddAuthorAsync(providerId, config)).GetAwaiter().GetResult();

                return addedAuthor;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to create author '{0}'", author.Name);
                return null;
            }
        }

        private List<string> SplitAuthorString(string authorString)
        {
            if (string.IsNullOrWhiteSpace(authorString))
            {
                return new List<string>();
            }

            return AuthorDelimiterRegex.Split(authorString)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }

        private List<string> ExtractAuthorsFromTitle(string title)
        {
            var authors = new List<string>();

            // Pattern: "Author - Title" or "Author: Title"
            var delimiterMatch = TitleAuthorPattern.Match(title);
            if (delimiterMatch.Success)
            {
                var potentialAuthor = delimiterMatch.Groups[1].Value.Trim();
                if (IsValidAuthorName(potentialAuthor))
                {
                    authors.Add(potentialAuthor);
                }
            }

            // Pattern: "Title by Author"
            var byMatch = ByAuthorPattern.Match(title);
            if (byMatch.Success)
            {
                authors.Add(byMatch.Groups[1].Value.Trim());
            }

            return authors;
        }

        private bool IsValidAuthorName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length < 3)
            {
                return false;
            }

            // Must contain at least one letter
            if (!name.Any(char.IsLetter))
            {
                return false;
            }

            // Reject common non-author values
            var invalidPatterns = new[]
            {
                "various", "unknown", "anonymous", "n/a", "none",
                "graphicaudio", "audible", "tantor", "blackstone",
                "unabridged", "abridged", "full cast"
            };

            var lower = name.ToLowerInvariant();
            return !invalidPatterns.Any(pattern => lower.Contains(pattern));
        }

        private string NormalizeAuthorName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            // Full normalization: remove ALL punctuation and spaces for consistent matching
            var normalized = new System.Text.StringBuilder();
            foreach (var c in name.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                {
                    normalized.Append(c);
                }
            }

            return normalized.ToString();
        }

        private List<string> GetSignificantWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new List<string>();
            }

            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // Filter out common words
            var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "the", "a", "an", "and", "or", "of", "in", "on", "at", "to", "for", "by", "with"
            };

            return words.Where(w => w.Length > 1 && !stopWords.Contains(w)).ToList();
        }

        private double CalculateSimilarity(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
            {
                return 0;
            }

            var longer = s1.Length > s2.Length ? s1 : s2;
            var shorter = s1.Length > s2.Length ? s2 : s1;

            if (longer.Length == 0)
            {
                return 1.0;
            }

            var editDistance = ComputeLevenshteinDistance(longer, shorter);
            return (longer.Length - editDistance) / (double)longer.Length;
        }

        private int ComputeLevenshteinDistance(string s1, string s2)
        {
            var distances = new int[s1.Length + 1, s2.Length + 1];

            for (var i = 0; i <= s1.Length; i++)
            {
                distances[i, 0] = i;
            }

            for (var j = 0; j <= s2.Length; j++)
            {
                distances[0, j] = j;
            }

            for (var i = 1; i <= s1.Length; i++)
            {
                for (var j = 1; j <= s2.Length; j++)
                {
                    var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                    distances[i, j] = Math.Min(
                        Math.Min(distances[i - 1, j] + 1, distances[i, j - 1] + 1),
                        distances[i - 1, j - 1] + cost);
                }
            }

            return distances[s1.Length, s2.Length];
        }
    }
}
