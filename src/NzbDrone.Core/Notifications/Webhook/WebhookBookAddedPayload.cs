namespace NzbDrone.Core.Notifications.Webhook
{
    public class WebhookBookAddedPayload : WebhookPayload
    {
        public WebhookAuthor Author { get; set; }
        public WebhookBook Book { get; set; }
    }
}

