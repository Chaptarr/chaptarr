using System;
using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Localization;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Notifications.Simplepush
{
    public interface ISimplepushProxy
    {
        void SendNotification(string title, string message, SimplepushSettings settings);
        ValidationFailure Test(SimplepushSettings settings);
    }

    public class SimplepushProxy : ISimplepushProxy
    {
        private const string URL = "https://api.simplepush.io/send";
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;
        private readonly NotificationText _notificationText;

        public SimplepushProxy(IHttpClient httpClient, ILocalizationService localizationService, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _notificationText = new NotificationText(localizationService);
        }

        public void SendNotification(string title, string message, SimplepushSettings settings)
        {
            var requestBuilder = new HttpRequestBuilder(URL).Post();

            requestBuilder.AddFormParameter("key", settings.Key)
                          .AddFormParameter("event", settings.Event)
                          .AddFormParameter("title", title)
                          .AddFormParameter("msg", message);

            var request = requestBuilder.Build();

            _httpClient.Post(request);
        }

        public ValidationFailure Test(SimplepushSettings settings)
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
