using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Utilities;

namespace NzbDrone.Core.Authors
{
    public interface IAuthorFolderMatchingService
    {
        List<AuthorFolderMatch> FindAuthorFolders(string rootFolderPath, Author author);
        string NormalizeAuthorNameForFolder(string authorName);
        /// <summary>
        /// Walk UP from a file path toward the root, testing each directory name against the author name.
        /// Returns the HIGHEST (closest to root) folder that matches the author name. Null if none match.
        /// </summary>
        string FindAuthorFolderByWalkingUp(string filePath, string rootFolderPath, Author author);

        /// <summary>
        /// Validates that a folder name matches an author name using fuzzy matching.
        /// Returns true if the folder name is close enough to the author name (>= 0.90 confidence).
        /// This prevents assigning folders like "Frank Herbert" to author "Brian Herbert".
        /// </summary>
        /// <param name="folderPath">The folder path to validate</param>
        /// <param name="authorName">The author name to match against</param>
        /// <returns>True if folder matches author name, false otherwise</returns>
        bool ValidateFolderMatchesAuthor(string folderPath, string authorName);

        /// <summary>
        /// Same as ValidateFolderMatchesAuthor but also returns the confidence score.
        /// </summary>
        (bool isValid, double confidence, string reason) ValidateFolderMatchesAuthorWithDetails(string folderPath, string authorName);
    }

    public class AuthorFolderMatch
    {
        public string Path { get; set; }
        public string FolderName { get; set; }
        public double ConfidenceScore { get; set; }
        public string MatchReason { get; set; }
        public bool IsExactMatch { get; set; }
    }

    public class AuthorFolderMatchingService : IAuthorFolderMatchingService
    {
        // Automatic match threshold (loosened per staging flow)
        private const double AUTO_MATCH_THRESHOLD = 0.90;
        private const double DISPLAY_MATCH_THRESHOLD = 0.90;

        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public AuthorFolderMatchingService(IDiskProvider diskProvider, Logger logger)
        {
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public List<AuthorFolderMatch> FindAuthorFolders(string rootFolderPath, Author author)
        {
            var matches = new List<AuthorFolderMatch>();

            if (!_diskProvider.FolderExists(rootFolderPath))
            {
                _logger.Warn($"Root folder path does not exist: {rootFolderPath}");
                return matches;
            }

            var folders = _diskProvider.GetDirectories(rootFolderPath).ToList();
            _logger.Debug($"Scanning {folders.Count} folders in {rootFolderPath} for author '{author.Name}'");

            // Generate all possible name variations for the author
            var nameVariations = GenerateNameVariations(author);
            _logger.Trace($"Generated {nameVariations.Count} name variations for '{author.Name}': {string.Join(", ", nameVariations)}");

            // Include the root folder itself as a candidate (closest to root)
            try
            {
                var rootName = new DirectoryInfo(rootFolderPath).Name;
                var rootMatch = CalculateBestMatch(rootName, nameVariations, author);
                if (rootMatch != null && rootMatch.ConfidenceScore >= DISPLAY_MATCH_THRESHOLD)
                {
                    rootMatch.Path = rootFolderPath;
                    rootMatch.FolderName = rootName;
                    matches.Add(rootMatch);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Error evaluating root folder as author folder candidate");
            }

            foreach (var folder in folders)
            {
                var folderName = new DirectoryInfo(folder).Name;
                var match = CalculateBestMatch(folderName, nameVariations, author);

                if (match != null)
                {
                    if (match.ConfidenceScore >= AUTO_MATCH_THRESHOLD)
                    {
                        _logger.Debug($"High confidence match for '{author.Name}': '{folderName}' (score: {match.ConfidenceScore:F3})");
                    }
                    else if (match.ConfidenceScore >= DISPLAY_MATCH_THRESHOLD)
                    {
                        _logger.Trace($"Potential match for '{author.Name}': '{folderName}' (score: {match.ConfidenceScore:F3})");
                    }

                    if (match.ConfidenceScore >= DISPLAY_MATCH_THRESHOLD)
                    {
                        match.Path = folder;
                        match.FolderName = folderName;
                        matches.Add(match);
                    }
                }
            }

            _logger.Debug($"Found {matches.Count} potential matches for author '{author.Name}'");
            return matches
                .OrderByDescending(m => m.ConfidenceScore)
                .ThenBy(m => string.Equals(m.Path, rootFolderPath, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();
        }

        public string FindAuthorFolderByWalkingUp(string filePath, string rootFolderPath, Author author)
        {
            if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(rootFolderPath) || author == null)
            {
                return null;
            }

            try
            {
                var fileDir = Path.GetDirectoryName(filePath);
                if (string.IsNullOrWhiteSpace(fileDir))
                {
                    return null;
                }

                // Get relative path from root to file's directory
                var relativePath = Path.GetRelativePath(rootFolderPath, fileDir);
                if (relativePath.StartsWith("..", StringComparison.Ordinal))
                {
                    return null; // File is not under root
                }

                // Split into segments (root → file order)
                var segments = relativePath
                    .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(s => s != "." && s != "..")
                    .ToList();

                if (segments.Count == 0)
                {
                    return null;
                }

                // Check each segment from root toward file, return first match
                var nameVariations = GenerateNameVariations(author);
                var currentPath = rootFolderPath;

                foreach (var segment in segments)
                {
                    currentPath = Path.Combine(currentPath, segment);

                    var match = CalculateBestMatch(segment, nameVariations, author);
                    if (match != null && match.ConfidenceScore >= AUTO_MATCH_THRESHOLD)
                    {
                        _logger.Debug("Found author folder: '{0}' matches '{1}' (score: {2:F3})",
                            segment, author.Name, match.ConfidenceScore);
                        return NormalizeDirectory(currentPath);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Error finding author folder for path '{0}' and author '{1}'", filePath, author?.Name);
            }

            return null;
        }

        private List<string> GenerateNameVariations(Author author)
        {
            var variations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Primary name (e.g., "Stephen King")
            if (!string.IsNullOrWhiteSpace(author.Name))
            {
                variations.Add(author.Name);

                // Generate "Last, First" from "First Last" (e.g., "Stephen King" -> "King, Stephen")
                var nameParts = author.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (nameParts.Length >= 2)
                {
                    var lastName = nameParts[^1]; // Last word
                    var firstName = string.Join(" ", nameParts[..^1]); // Everything before last word
                    variations.Add($"{lastName}, {firstName}");
                }
            }

            // Sort name (LastName, FirstName)
            if (!string.IsNullOrWhiteSpace(author.SortName))
            {
                variations.Add(author.SortName);

                // Also try reversing "LastName, FirstName" to "FirstName LastName"
                var sortParts = author.SortName.Split(',');
                if (sortParts.Length == 2)
                {
                    var reversed = $"{sortParts[1].Trim()} {sortParts[0].Trim()}";
                    variations.Add(reversed);
                }
            }

            // Clean name (from database)
            if (!string.IsNullOrWhiteSpace(author.CleanName))
            {
                variations.Add(author.CleanName);
            }

            // Handle initials with and without periods
            var additionalVariations = new List<string>();
            foreach (var name in variations.ToList())
            {
                // "J.R.R. Tolkien" -> "JRR Tolkien" (remove ALL periods from initials)
                var noPeriods = Regex.Replace(name, @"\.", "").Replace("  ", " ").Trim();
                additionalVariations.Add(noPeriods);

                // "J.R.R. Tolkien" -> "J. R. R. Tolkien" (ensure space after each period)
                additionalVariations.Add(Regex.Replace(name, @"\.(?=\S)", ". "));

                // "JRR Tolkien" -> "J.R.R. Tolkien" (add periods to consecutive capitals)
                additionalVariations.Add(AddPeriodsToInitials(name));
            }

            foreach (var variation in additionalVariations)
            {
                variations.Add(variation);
            }

            return variations.ToList();
        }

        private string AddPeriodsToInitials(string name)
        {
            // Add periods to uppercase letters that look like initials
            return Regex.Replace(name, @"\b([A-Z])(?=[A-Z]|\s)", "$1.");
        }

        private AuthorFolderMatch CalculateBestMatch(string folderName, List<string> nameVariations, Author author)
        {
            AuthorFolderMatch bestMatch = null;

            foreach (var variation in nameVariations)
            {
                // Exact match (case insensitive)
                if (string.Equals(folderName, variation, StringComparison.OrdinalIgnoreCase))
                {
                    return new AuthorFolderMatch
                    {
                        ConfidenceScore = 1.0,
                        MatchReason = "Exact name match",
                        IsExactMatch = true
                    };
                }

                // Exact match with series suffix
                // "Terry Pratchett - Discworld" matches "Terry Pratchett"
                if (folderName.StartsWith(variation + " -", StringComparison.OrdinalIgnoreCase))
                {
                    return new AuthorFolderMatch
                    {
                        ConfidenceScore = 0.99,
                        MatchReason = "Name match with series suffix",
                        IsExactMatch = false
                    };
                }

                // Supernormalization: remove ALL spaces, punctuation, case
                var normalizedFolder = Supernormalize(folderName);
                var normalizedVariation = Supernormalize(variation);

                if (string.IsNullOrEmpty(normalizedFolder) || string.IsNullOrEmpty(normalizedVariation))
                {
                    continue;
                }

                // Check for exact supernormalized match
                if (string.Equals(normalizedFolder, normalizedVariation, StringComparison.Ordinal))
                {
                    var match = new AuthorFolderMatch
                    {
                        ConfidenceScore = 0.98,
                        MatchReason = $"Supernormalized match to '{variation}'",
                        IsExactMatch = false
                    };

                    if (match.ConfidenceScore > (bestMatch?.ConfidenceScore ?? 0))
                    {
                        bestMatch = match;
                    }
                }

                // Check if folder contains the author name (but not vice versa to prevent substring matches)
                else if (normalizedFolder.StartsWith(normalizedVariation, StringComparison.Ordinal))
                {
                    var match = new AuthorFolderMatch
                    {
                        ConfidenceScore = 0.95,
                        MatchReason = $"Folder starts with '{variation}'",
                        IsExactMatch = false
                    };

                    if (match.ConfidenceScore > (bestMatch?.ConfidenceScore ?? 0))
                    {
                        bestMatch = match;
                    }
                }
            }

            return bestMatch;
        }

        private string Supernormalize(string text)
        {
            return UnicodeComparisonNormalizer.NormalizeKey(text);
        }

        public string NormalizeAuthorNameForFolder(string authorName)
        {
            if (string.IsNullOrWhiteSpace(authorName))
            {
                return string.Empty;
            }

            // Basic normalization for folder names
            // Keep it readable but remove invalid characters
            var normalized = authorName;

            // Remove invalid path characters
            var invalidChars = Path.GetInvalidFileNameChars();
            foreach (var c in invalidChars)
            {
                normalized = normalized.Replace(c.ToString(), "");
            }

            // Replace multiple spaces with single space
            normalized = Regex.Replace(normalized, @"\s+", " ");

            // Trim
            normalized = normalized.Trim();

            return normalized;
        }

        private int LevenshteinDistance(string s1, string s2)
        {
            if (string.IsNullOrEmpty(s1))
            {
                return s2?.Length ?? 0;
            }

            if (string.IsNullOrEmpty(s2))
            {
                return s1.Length;
            }

            var bounds = new { Height = s1.Length + 1, Width = s2.Length + 1 };

            var matrix = new int[bounds.Height, bounds.Width];

            for (var height = 0; height < bounds.Height; height++)
            {
                matrix[height, 0] = height;
            }

            for (var width = 0; width < bounds.Width; width++)
            {
                matrix[0, width] = width;
            }

            for (var height = 1; height < bounds.Height; height++)
            {
                for (var width = 1; width < bounds.Width; width++)
                {
                    var cost = (s1[height - 1] == s2[width - 1]) ? 0 : 1;

                    matrix[height, width] = Math.Min(
                        Math.Min(
                            matrix[height - 1, width] + 1,      // deletion
                            matrix[height, width - 1] + 1),     // insertion
                        matrix[height - 1, width - 1] + cost);  // substitution
                }
            }

            return matrix[bounds.Height - 1, bounds.Width - 1];
        }

        private string NormalizeDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            try
            {
                var full = Path.GetFullPath(path);
                return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        }

        /// <summary>
        /// Validates that a folder name matches an author name using fuzzy matching.
        /// Returns true if the folder name is close enough to the author name (>= 0.90 confidence).
        /// This is a critical smoke test that prevents assigning "Frank Herbert" folder to "Brian Herbert" author.
        /// </summary>
        public bool ValidateFolderMatchesAuthor(string folderPath, string authorName)
        {
            var (isValid, _, _) = ValidateFolderMatchesAuthorWithDetails(folderPath, authorName);
            return isValid;
        }

        /// <summary>
        /// Same as ValidateFolderMatchesAuthor but also returns the confidence score and reason.
        /// </summary>
        public (bool isValid, double confidence, string reason) ValidateFolderMatchesAuthorWithDetails(string folderPath, string authorName)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(authorName))
            {
                return (false, 0.0, "Folder path or author name is empty");
            }

            try
            {
                // Extract folder name from path
                string folderName;
                try
                {
                    folderName = new DirectoryInfo(folderPath).Name;
                }
                catch
                {
                    // Fallback: get the last path segment
                    folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }

                if (string.IsNullOrWhiteSpace(folderName))
                {
                    return (false, 0.0, "Could not extract folder name from path");
                }

                // Create a temporary Author object to use existing matching logic
                var tempAuthor = new Author { Name = authorName };
                var nameVariations = GenerateNameVariations(tempAuthor);

                // Calculate match using existing logic
                var match = CalculateBestMatch(folderName, nameVariations, tempAuthor);

                if (match == null)
                {
                    // No match at all - check supernormalized as a sanity check
                    var normFolder = Supernormalize(folderName);
                    var normAuthor = Supernormalize(authorName);

                    if (string.IsNullOrEmpty(normFolder) || string.IsNullOrEmpty(normAuthor))
                    {
                        return (false, 0.0, $"Folder '{folderName}' does not match author '{authorName}'");
                    }

                    // Calculate basic similarity for logging
                    var distance = LevenshteinDistance(normFolder, normAuthor);
                    var maxLen = Math.Max(normFolder.Length, normAuthor.Length);
                    var similarity = maxLen > 0 ? 1.0 - ((double)distance / maxLen) : 0.0;

                    _logger.Debug("Folder-author smoke test FAILED: folder='{0}' (normalized='{1}'), author='{2}' (normalized='{3}'), similarity={4:F3}",
                        folderName, normFolder, authorName, normAuthor, similarity);

                    return (false, similarity, $"Folder '{folderName}' does not match author '{authorName}' (similarity: {similarity:P0})");
                }

                var isValid = match.ConfidenceScore >= AUTO_MATCH_THRESHOLD;

                if (isValid)
                {
                    _logger.Debug("Folder-author smoke test PASSED: folder='{0}' matches author='{1}' (score: {2:F3}, reason: {3})",
                        folderName, authorName, match.ConfidenceScore, match.MatchReason);
                }
                else
                {
                    _logger.Debug("Folder-author smoke test FAILED: folder='{0}' vs author='{1}' (score: {2:F3} < threshold {3:F3})",
                        folderName, authorName, match.ConfidenceScore, AUTO_MATCH_THRESHOLD);
                }

                return (isValid, match.ConfidenceScore, match.MatchReason ?? $"Score: {match.ConfidenceScore:F3}");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Error validating folder '{0}' matches author '{1}'", folderPath, authorName);
                return (false, 0.0, $"Error during validation: {ex.Message}");
            }
        }
    }
}
