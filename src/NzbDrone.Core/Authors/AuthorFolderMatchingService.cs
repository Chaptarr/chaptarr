using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;

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
        /// Validates a complete author name with the shared ordered name rule.
        /// A longer surname or conflicting supplied name part must not assign another author's folder.
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

            var knownNames = ReleaseIdentityEvidence.GetKnownAuthorNames(author).ToList();

            // Include the root folder itself as a candidate (closest to root)
            try
            {
                var rootName = new DirectoryInfo(rootFolderPath).Name;
                var rootMatch = CalculateBestMatch(rootName, knownNames, author);
                if (rootMatch != null)
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
                var match = CalculateBestMatch(folderName, knownNames, author);

                if (match != null)
                {
                    match.Path = folder;
                    match.FolderName = folderName;
                    matches.Add(match);
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
                var knownNames = ReleaseIdentityEvidence.GetKnownAuthorNames(author).ToList();
                var currentPath = rootFolderPath;

                foreach (var segment in segments)
                {
                    currentPath = Path.Combine(currentPath, segment);

                    var match = CalculateBestMatch(segment, knownNames, author);
                    if (match != null)
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

        private AuthorFolderMatch CalculateBestMatch(string folderName, List<(List<string> Tokens, int FirstCount, int LastCount, int CharacterCount, bool HasGivenInitialGroup, bool HasContributorBoundary, string LiteralKey, IReadOnlyList<int> JoinerIndexes)> knownNames, Author author)
        {
            // Series decoration has an explicit separator; a surname prefix is never identity evidence.
            var suffix = folderName.IndexOf(" -", StringComparison.Ordinal);
            var candidate = AuthorNameMatcher.PrepareName(suffix < 0 ? folderName : folderName.Substring(0, suffix));
            if (!knownNames.Any(name => AuthorNameMatcher.AuthorNamesMatch(name, candidate)))
            {
                return null;
            }

            var exact = string.Equals(folderName, author.Name, StringComparison.OrdinalIgnoreCase);
            return new AuthorFolderMatch
            {
                ConfidenceScore = exact ? 1.0 : suffix >= 0 ? 0.99 : 0.98,
                MatchReason = exact ? "Exact name match" : suffix >= 0 ? "Name match with series suffix" : "Shared author name match",
                IsExactMatch = exact
            };
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
        /// Validates a complete author name with the shared ordered name rule.
        /// A longer surname or conflicting supplied name part must not assign another author's folder.
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
                var knownNames = ReleaseIdentityEvidence.GetKnownAuthorNames(tempAuthor).ToList();

                // Calculate match using existing logic
                var match = CalculateBestMatch(folderName, knownNames, tempAuthor);

                if (match == null)
                {
                    _logger.Debug("Folder-author validation FAILED: folder='{0}', author='{1}'", folderName, authorName);
                    return (false, 0.0, $"Folder '{folderName}' does not match author '{authorName}'");
                }

                _logger.Debug("Folder-author validation PASSED: folder='{0}' matches author='{1}' (score: {2:F3}, reason: {3})",
                    folderName, authorName, match.ConfidenceScore, match.MatchReason);
                return (true, match.ConfidenceScore, match.MatchReason);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Error validating folder '{0}' matches author '{1}'", folderPath, authorName);
                return (false, 0.0, $"Error during validation: {ex.Message}");
            }
        }
    }
}
