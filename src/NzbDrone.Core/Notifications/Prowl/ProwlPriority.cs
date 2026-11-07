using NzbDrone.Core.Annotations;

namespace NzbDrone.Core.Notifications.Prowl
{
    public enum ProwlPriority
    {
        [FieldOption(Label = "provider.option.ob07913e85f96")]
        VeryLow = -2,
        [FieldOption(Label = "provider.option.of793de205ead")]
        Low = -1,
        [FieldOption(Label = "provider.option.oa7248eeb45eb")]
        Normal = 0,
        [FieldOption(Label = "provider.option.oc4ebc6d4a583")]
        High = 1,
        [FieldOption(Label = "provider.option.o4538f2dc67b5")]
        Emergency = 2
    }
}
