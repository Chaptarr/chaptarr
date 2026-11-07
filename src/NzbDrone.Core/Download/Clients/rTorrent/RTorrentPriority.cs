using NzbDrone.Core.Annotations;

namespace NzbDrone.Core.Download.Clients.RTorrent
{
    public enum RTorrentPriority
    {
        [FieldOption(Label = "provider.option.ob07913e85f96")]
        VeryLow = 0,
        [FieldOption(Label = "provider.option.of793de205ead")]
        Low = 1,
        [FieldOption(Label = "provider.option.oa7248eeb45eb")]
        Normal = 2,
        [FieldOption(Label = "provider.option.oc4ebc6d4a583")]
        High = 3
    }
}
