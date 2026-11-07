using System;
using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Localization;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Notifications.Pushover
{
    public interface IPushoverProxy
    {
        void SendNotification(string title, string message, PushoverSettings settings);
        ValidationFailure Test(PushoverSettings settings);
    }

    public class PushoverProxy : IPushoverProxy
    {
        private const string URL = "https://api.pushover.net/1/messages.json";
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;
        private readonly NotificationText _notificationText;

        public PushoverProxy(IHttpClient httpClient, ILocalizationService localizationService, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _notificationText = new NotificationText(localizationService);
        }

        public void SendNotification(string title, string message, PushoverSettings settings)
        {
            var requestBuilder = new HttpRequestBuilder(URL).Post();

            requestBuilder.AddFormParameter("token", settings.ApiKey)
                          .AddFormParameter("user", settings.UserKey)
                          .AddFormParameter("device", string.Join(",", settings.Devices))
                          .AddFormParameter("title", title)
                          .AddFormParameter("message", message)
                          .AddFormParameter("priority", settings.Priority);

            if ((PushoverPriority)settings.Priority == PushoverPriority.Emergency)
            {
                requestBuilder.AddFormParameter("retry", settings.Retry);
                requestBuilder.AddFormParameter("expire", settings.Expire);
            }

            if (!settings.Sound.IsNullOrWhiteSpace())
            {
                requestBuilder.AddFormParameter("sound", settings.Sound);
            }

            var request = requestBuilder.Build();

            _httpClient.Post(request);
        }

        public ValidationFailure Test(PushoverSettings settings)
        {
            try
            {
                var title = _notificationText.Get("notification.test.title", "Test Notification");
                var body = _notificationText.Get("notification.test.body", "This is a test message from Chaptarr.");

                SendNotification(title, body, settings);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to send test message");
                return new ValidationFailure("ApiKey", "Unable to send test message");
            }

            return null;
        }
    }
}
