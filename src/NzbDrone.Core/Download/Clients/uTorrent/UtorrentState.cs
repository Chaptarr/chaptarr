using NzbDrone.Core.Annotations;

namespace NzbDrone.Core.Download.Clients.UTorrent
{
    public enum UTorrentState
    {
        [FieldOption(Label = "provider.option.oe4bb9f1ece9a")]
        Start = 0,
        [FieldOption(Label = "provider.option.o801996c00122")]
        ForceStart = 1,
        [FieldOption(Label = "Pause")]
        Pause = 2,
        [FieldOption(Label = "provider.option.ocae7d57bc067")]
        Stop = 3
    }
}
