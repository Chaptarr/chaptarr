using NzbDrone.Core.Annotations;

namespace NzbDrone.Core.Notifications.Join
{
    public enum JoinPriority
    {
        [FieldOption(Label = "provider.option.oddbcf0672648")]
        Silent = -2,
        [FieldOption(Label = "provider.option.oc0ebb7d6b693")]
        Quiet = -1,
        [FieldOption(Label = "provider.option.oa7248eeb45eb")]
        Normal = 0,
        [FieldOption(Label = "provider.option.oc4ebc6d4a583")]
        High = 1,
        [FieldOption(Label = "provider.option.o4538f2dc67b5")]
        Emergency = 2
    }
}
