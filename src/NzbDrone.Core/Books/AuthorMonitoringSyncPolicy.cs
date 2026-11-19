using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using FluentValidation.Results;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Books
{
    // Sync links author configuration choices. Existing book monitoring remains
    // owned by the normal monitoring and format-reconciliation operations.
    public static class AuthorMonitoringSyncPolicy
    {
        public static RootFolder GetRoot(Author author, IEnumerable<RootFolder> roots, BookMediaType mediaType)
        {
            var path = mediaType == BookMediaType.Audiobook ? author?.AudiobookRootFolderPath : author?.EbookRootFolderPath;
            if (string.IsNullOrWhiteSpace(path)) return null;
            return roots?.FirstOrDefault(root => root != null && root.Path.PathEquals(path));
        }

        public static bool IsEligible(Author author, IEnumerable<RootFolder> roots)
        {
            var audio = GetRoot(author, roots, BookMediaType.Audiobook);
            var ebook = GetRoot(author, roots, BookMediaType.Ebook);
            return audio?.FolderType is FolderType.Audiobook or FolderType.Mixed &&
                   ebook?.FolderType is FolderType.Ebook or FolderType.Mixed;
        }

        public static (bool? Value, bool Conflicting) ResolveDefault(Author author, IEnumerable<RootFolder> roots)
        {
            if (!IsEligible(author, roots)) return (null, false);
            var values = new[] { GetRoot(author, roots, BookMediaType.Audiobook)?.DefaultSyncMonitoredAcrossFormats,
                                 GetRoot(author, roots, BookMediaType.Ebook)?.DefaultSyncMonitoredAcrossFormats }
                .Where(value => value.HasValue).Distinct().ToList();
            return (values.Count == 1 ? values[0] : null, values.Count > 1);
        }

        public static (T? Audiobook, T? Ebook) ResolveLinkedChoices<T>(bool enabled, T? audiobook, T? ebook,
            string audiobookField, string ebookField) where T : struct
        {
            if (!enabled) return (audiobook, ebook);
            if (audiobook.HasValue && ebook.HasValue && !EqualityComparer<T>.Default.Equals(audiobook.Value, ebook.Value))
            {
                throw new ValidationException(new[]
                {
                    new ValidationFailure(audiobookField, $"{audiobookField} and {ebookField} must agree when syncMonitoredAcrossFormats is enabled."),
                    new ValidationFailure(ebookField, $"{audiobookField} and {ebookField} must agree when syncMonitoredAcrossFormats is enabled.")
                });
            }
            return (audiobook ?? ebook, ebook ?? audiobook);
        }

        public static void ApplyNewItemChoices(Author author, bool? requestedSync, NewItemMonitorTypes? audiobook,
            NewItemMonitorTypes? ebook)
        {
            var enabled = (requestedSync ?? author.SyncMonitoredAcrossFormats) == true;
            // Enabling the link requires a shared choice when stored values disagree.
            // An unrelated edit does not migrate legacy author settings.
            if (enabled && requestedSync == true && author.SyncMonitoredAcrossFormats != true &&
                !audiobook.HasValue && !ebook.HasValue)
            {
                audiobook = author.AudiobookMonitorNewItems;
                ebook = author.EbookMonitorNewItems;
            }
            var choices = ResolveLinkedChoices(enabled, audiobook, ebook,
                nameof(Author.AudiobookMonitorNewItems), nameof(Author.EbookMonitorNewItems));
            if (choices.Audiobook.HasValue) author.AudiobookMonitorNewItems = choices.Audiobook;
            if (choices.Ebook.HasValue) author.EbookMonitorNewItems = choices.Ebook;
            if (requestedSync.HasValue) author.SyncMonitoredAcrossFormats = requestedSync;
        }
    }
}
