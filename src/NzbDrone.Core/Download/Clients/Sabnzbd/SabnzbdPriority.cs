using NzbDrone.Core.Annotations;

namespace NzbDrone.Core.Download.Clients.Sabnzbd
{
    public enum SabnzbdPriority
    {
        [FieldOption(Label = "Default")]
        Default = -100,
        [FieldOption(Label = "QueueStatusPaused")]
        Paused = -2,
        [FieldOption(Label = "provider.option.of793de205ead")]
        Low = -1,
        [FieldOption(Label = "provider.option.oa7248eeb45eb")]
        Normal = 0,
        [FieldOption(Label = "provider.option.oc4ebc6d4a583")]
        High = 1,
        [FieldOption(Label = "provider.option.ocfda1755ffb1")]
        Force = 2
    }
}
