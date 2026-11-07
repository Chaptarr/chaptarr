using System;
using NLog;
using NzbDrone.Core.Authors;
using NzbDrone.Core.Books;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    internal static class BookshelfMatchCompatibilityProfileResolver
    {
        public static string GetPreferredEditionTerms(
            Author author,
            string filePath,
            BookMediaType mediaType,
            IMetadataProfileService metadataProfileService,
            IRootFolderService rootFolderService,
            Logger logger)
        {
            var profile = author?.GetMetadataProfileFor(mediaType);
            int? authorProfileId = mediaType == BookMediaType.Ebook
                ? author?.EbookMetadataProfileId
                : author?.AudiobookMetadataProfileId;
            if (authorProfileId.GetValueOrDefault() <= 0)
            {
                authorProfileId = author?.MetadataProfileId;
            }

            // BookshelfNG uses an explicitly assigned author profile even when its
            // preferred-edition terms are empty; only an unprofiled author falls
            // back to the root folder's default profile.
            if (authorProfileId is > 0)
            {
                try
                {
                    return metadataProfileService?.Get(authorProfileId.Value)?.PreferredEditionTerms ??
                        profile?.PreferredEditionTerms;
                }
                catch (Exception ex)
                {
                    logger?.Debug(ex, "Could not load BookshelfNG-compatible metadata profile {0}", authorProfileId.Value);
                    return profile?.PreferredEditionTerms;
                }
            }

            if (profile != null)
            {
                return profile.PreferredEditionTerms;
            }

            try
            {
                var rootFolder = rootFolderService?.GetBestRootFolder(filePath);
                var rootSettings = mediaType == BookMediaType.Ebook
                    ? rootFolder?.GetEbookSettings()
                    : rootFolder?.GetAudiobookSettings();
                if (rootSettings?.MetadataProfileId is > 0)
                {
                    return metadataProfileService?.Get(rootSettings.MetadataProfileId.Value)?.PreferredEditionTerms;
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "Could not resolve the BookshelfNG-compatible root metadata profile for {0}", filePath);
            }

            return null;
        }
    }
}
