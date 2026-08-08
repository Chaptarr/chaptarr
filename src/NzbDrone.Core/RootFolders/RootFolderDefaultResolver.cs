using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.RootFolders
{
    public static class RootFolderDefaultResolver
    {
        public static bool TryGetEffectiveDefaultRootFolder(IEnumerable<RootFolder> rootFolders,
                                                            FolderType mediaType,
                                                            string configuredDefaultRootFolderPath,
                                                            out RootFolder rootFolder,
                                                            out string error,
                                                            bool allowAmbiguousFallback = false)
        {
            rootFolder = null;
            error = null;

            if (mediaType != FolderType.Audiobook && mediaType != FolderType.Ebook)
            {
                throw new ArgumentException("Default root folders are only resolved for audiobook or ebook media types", nameof(mediaType));
            }

            var mediaLabel = mediaType == FolderType.Audiobook ? "audiobook" : "ebook";
            var folders = (rootFolders ?? Enumerable.Empty<RootFolder>())
                .Where(r => r != null && !r.Path.IsNullOrWhiteSpace())
                .ToList();

            if (!configuredDefaultRootFolderPath.IsNullOrWhiteSpace())
            {
                var configuredRoot = folders.FirstOrDefault(r => r.Path.PathEquals(configuredDefaultRootFolderPath));
                if (configuredRoot == null)
                {
                    error = $"Default {mediaLabel} root folder '{configuredDefaultRootFolderPath}' is not configured";
                    return false;
                }

                if (!IsCompatibleRootFolder(configuredRoot, mediaType))
                {
                    error = $"Default {mediaLabel} root folder '{configuredDefaultRootFolderPath}' is not compatible with {mediaLabel} imports";
                    return false;
                }

                rootFolder = configuredRoot;
                return true;
            }

            var compatibleRootFolders = folders
                .Where(r => IsCompatibleRootFolder(r, mediaType))
                .ToList();

            if (compatibleRootFolders.Count == 1)
            {
                rootFolder = compatibleRootFolders[0];
                return true;
            }

            if (allowAmbiguousFallback && compatibleRootFolders.Count > 1)
            {
                rootFolder = compatibleRootFolders.FirstOrDefault(r => r.FolderType == mediaType) ??
                             compatibleRootFolders.FirstOrDefault(r => r.FolderType == FolderType.Mixed);
                return rootFolder != null;
            }

            error = compatibleRootFolders.Count == 0
                ? $"No {mediaLabel} or mixed root folder configured"
                : $"Multiple {mediaLabel} or mixed root folders are configured; select a default {mediaLabel} root folder";

            return false;
        }

        public static bool IsCompatibleRootFolder(RootFolder rootFolder, FolderType mediaType)
        {
            if (rootFolder == null)
            {
                return false;
            }

            return rootFolder.FolderType == mediaType || rootFolder.FolderType == FolderType.Mixed;
        }
    }
}
