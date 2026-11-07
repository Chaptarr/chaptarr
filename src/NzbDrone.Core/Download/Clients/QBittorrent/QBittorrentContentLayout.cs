using NzbDrone.Core.Annotations;

namespace NzbDrone.Core.Download.Clients.QBittorrent
{
    public enum QBittorrentContentLayout
    {
        [FieldOption(Label = "Default")]
        Default = 0,
        [FieldOption(Label = "Original")]
        Original = 1,
        [FieldOption(Label = "provider.option.o18c2b11ffbb0")]
        Subfolder = 2
    }
}
