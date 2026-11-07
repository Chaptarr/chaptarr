using NzbDrone.Core.Annotations;

namespace NzbDrone.Core.Download.Clients.QBittorrent
{
    public enum QBittorrentState
    {
        [FieldOption(Label = "Started")]
        Start = 0,

        [FieldOption(Label = "provider.option.offfe404b2285")]
        ForceStart = 1,

        [FieldOption(Label = "provider.option.o1a4f630ac1b6")]
        Stop = 2
    }
}
