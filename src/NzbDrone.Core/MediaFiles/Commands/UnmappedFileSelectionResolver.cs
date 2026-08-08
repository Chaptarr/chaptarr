using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common;

namespace NzbDrone.Core.MediaFiles.Commands
{
    internal static class UnmappedFileSelectionResolver
    {
        internal static List<BookFile> ResolveRows(
            IMediaFileService mediaFileService,
            UnmappedFilesSelection selection,
            string mediaType,
            Logger logger,
            string logPrefix,
            bool allowEmptySelected = false)
        {
            if (selection == null)
            {
                return new List<BookFile>();
            }

            var scope = selection.Scope?.Trim();
            if (string.IsNullOrWhiteSpace(scope))
            {
                throw new ArgumentException("Unmapped files scope must be provided");
            }

            var normalizedMediaType = NormalizeMediaType(mediaType);
            var excludedIds = selection.ExceptBookFileIds?
                .Where(id => id > 0)
                .ToHashSet() ?? new HashSet<int>();

            List<BookFile> files;
            if (string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase))
            {
                files = mediaFileService.GetUnmappedFiles(normalizedMediaType);
            }
            else if (string.Equals(scope, "selected", StringComparison.OrdinalIgnoreCase))
            {
                var requestedIds = selection.BookFileIds?
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList() ?? new List<int>();

                if (!requestedIds.Any())
                {
                    if (!allowEmptySelected)
                    {
                        throw new ArgumentException("Selected unmapped files scope requires at least one bookFileId");
                    }

                    files = new List<BookFile>();
                }
                else
                {
                    files = mediaFileService.GetUnmappedFiles(requestedIds, normalizedMediaType);
                }

                if (files.Count != requestedIds.Count)
                {
                    logger?.Debug("{0} Selected unmapped resolver kept {1}/{2} requested BookFile IDs after current-unmapped and mediaType filtering",
                        logPrefix,
                        files.Count,
                        requestedIds.Count);
                }
            }
            else
            {
                throw new ArgumentException($"Unknown unmapped files scope '{selection.Scope}'");
            }

            return files
                .Where(file => file != null && !excludedIds.Contains(file.Id) && !string.IsNullOrWhiteSpace(file.Path))
                .GroupBy(file => file.Path, PathEqualityComparer.Instance)
                .Select(group => group.First())
                .ToList();
        }

        internal static List<string> ResolvePaths(
            IMediaFileService mediaFileService,
            UnmappedFilesSelection selection,
            string mediaType,
            Logger logger,
            string logPrefix,
            bool allowEmptySelected = false)
        {
            return ResolveRows(mediaFileService, selection, mediaType, logger, logPrefix, allowEmptySelected)
                .Select(file => file.Path)
                .Distinct(PathEqualityComparer.Instance)
                .ToList();
        }

        internal static string NormalizeMediaType(string mediaType)
        {
            if (string.IsNullOrWhiteSpace(mediaType) ||
                string.Equals(mediaType, "all", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (string.Equals(mediaType, "audiobook", StringComparison.OrdinalIgnoreCase))
            {
                return "audiobook";
            }

            if (string.Equals(mediaType, "ebook", StringComparison.OrdinalIgnoreCase))
            {
                return "ebook";
            }

            throw new ArgumentException($"Invalid unmapped files mediaType '{mediaType}'. Expected 'all', 'audiobook', or 'ebook'.");
        }
    }
}
