using System;
using System.Linq;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Notifications.AudioBookShelf
{
    public interface IAudioBookShelfKeeperLookup
    {
        // True when an enabled AudioBookShelf connection that keeps the metadata maps this root folder
        // and media type. Chaptarr then must not write AudioBookShelf sidecars (metadata.json, cover)
        // there: an AudioBookShelf rescan would read them back over the user's own match.
        bool AudioBookShelfKeepsMetadata(int rootFolderId, string mediaType);
    }

    public class AudioBookShelfKeeperLookup : IAudioBookShelfKeeperLookup
    {
        private readonly INotificationFactory _notificationFactory;

        public AudioBookShelfKeeperLookup(INotificationFactory notificationFactory)
        {
            _notificationFactory = notificationFactory;
        }

        public bool AudioBookShelfKeepsMetadata(int rootFolderId, string mediaType)
        {
            return _notificationFactory.GetAvailableProviders()
                .OfType<AudioBookShelf>()
                .Select(s => (AudioBookShelfSettings)s.Definition.Settings)
                .Where(s => s.AudioBookShelfKeepsMetadata())
                .Any(s => s.GetLibraryMappings().Any(m =>
                    m != null &&
                    m.RootFolderId == rootFolderId &&
                    (mediaType.IsNullOrWhiteSpace() || string.Equals(m.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))));
        }
    }
}
