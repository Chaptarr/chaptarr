using NzbDrone.Core.Annotations;

namespace NzbDrone.Core.Notifications.Gotify
{
    public enum GotifyPriority
    {
        [FieldOption(Label = "Min")]
        Min = 0,
        [FieldOption(Label = "provider.option.of793de205ead")]
        Low = 2,
        [FieldOption(Label = "provider.option.oa7248eeb45eb")]
        Normal = 5,
        [FieldOption(Label = "provider.option.oc4ebc6d4a583")]
        High = 8
    }
}
