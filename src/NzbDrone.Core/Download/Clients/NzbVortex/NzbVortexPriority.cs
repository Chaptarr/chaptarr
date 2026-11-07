using NzbDrone.Core.Annotations;

namespace NzbDrone.Core.Download.Clients.NzbVortex
{
    public enum NzbVortexPriority
    {
        [FieldOption(Label = "provider.option.of793de205ead")]
        Low = -1,
        [FieldOption(Label = "provider.option.oa7248eeb45eb")]
        Normal = 0,
        [FieldOption(Label = "provider.option.oc4ebc6d4a583")]
        High = 1,
    }
}
