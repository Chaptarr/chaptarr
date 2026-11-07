using NzbDrone.Core.Annotations;

namespace NzbDrone.Core.Notifications.Ntfy
{
    public enum NtfyPriority
    {
        [FieldOption(Label = "Min")]
        Min = 1,
        [FieldOption(Label = "provider.option.of793de205ead")]
        Low = 2,
        [FieldOption(Label = "Default")]
        Default = 3,
        [FieldOption(Label = "provider.option.oc4ebc6d4a583")]
        High = 4,
        [FieldOption(Label = "Max")]
        Max = 5
    }
}
