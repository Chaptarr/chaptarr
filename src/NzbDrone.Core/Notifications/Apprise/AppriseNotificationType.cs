using NzbDrone.Core.Annotations;

using System.Runtime.Serialization;

namespace NzbDrone.Core.Notifications.Apprise
{
    public enum AppriseNotificationType
    {
        [EnumMember(Value = "info")]
        [FieldOption(Label = "provider.option.o170322a32f3c")]
        Info,

        [EnumMember(Value = "success")]
        [FieldOption(Label = "provider.option.oc88a0b907419")]
        Success,

        [EnumMember(Value = "warning")]
        [FieldOption(Label = "Warning")]
        Warning,

        [EnumMember(Value = "failure")]
        [FieldOption(Label = "provider.option.o7031edbcf9c4")]
        Failure,
    }
}
