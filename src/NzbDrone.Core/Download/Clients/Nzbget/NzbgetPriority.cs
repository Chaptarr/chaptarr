using NzbDrone.Core.Annotations;

namespace NzbDrone.Core.Download.Clients.Nzbget
{
    public enum NzbgetPriority
    {
        [FieldOption(Label = "provider.option.ob07913e85f96")]
        VeryLow = -100,
        [FieldOption(Label = "provider.option.of793de205ead")]
        Low = -50,
        [FieldOption(Label = "provider.option.oa7248eeb45eb")]
        Normal = 0,
        [FieldOption(Label = "provider.option.oc4ebc6d4a583")]
        High = 50,
        [FieldOption(Label = "provider.option.o98950fca2b5e")]
        VeryHigh = 100,
        [FieldOption(Label = "provider.option.ocfda1755ffb1")]
        Force = 900
    }
}
