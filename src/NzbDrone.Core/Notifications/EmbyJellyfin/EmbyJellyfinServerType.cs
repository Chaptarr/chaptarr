using NzbDrone.Core.Annotations;

namespace NzbDrone.Core.Notifications.EmbyJellyfin
{
    public enum EmbyJellyfinServerType
    {
        [FieldOption(Label = "provider.option.o8e03b3c29d2b")]
        Jellyfin = 0,

        [FieldOption(Label = "provider.option.o745833c910da")]
        Emby = 1
    }
}
