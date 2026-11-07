using NzbDrone.Core.Annotations;

namespace NzbDrone.Core.Notifications.Webhook
{
    public enum WebhookMethod
    {
        [FieldOption(Label = "provider.option.o9aee6b1bcdf6")]
        POST = 1,
        [FieldOption(Label = "provider.option.o907bf9a40f9c")]
        PUT = 2
    }
}
