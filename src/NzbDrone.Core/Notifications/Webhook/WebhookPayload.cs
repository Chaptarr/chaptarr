using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NzbDrone.Core.Notifications.Webhook
{
    public class WebhookPayload
    {
        public WebhookEventType EventType { get; set; }
        public string InstanceName { get; set; }

        [JsonProperty("canonical", NullValueHandling = NullValueHandling.Ignore)]
        public JObject CanonicalEvent { get; set; }
    }
}
