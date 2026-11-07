using System;
using System.Net;
using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Localization;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Notifications.Prowl
{
    public interface IProwlProxy
    {
        void SendNotification(string title, string message, ProwlSettings settings);
        ValidationFailure Test(ProwlSettings settings);
    }

    public class ProwlProxy : IProwlProxy
    {
        private const string PUSH_URL = "https://api.prowlapp.com/publicapi/add";
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;
        private readonly NotificationText _notificationText;

        public ProwlProxy(IHttpClient httpClient, ILocalizationService localizationService, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _notificationText = new NotificationText(localizationService);
        }

        public void SendNotification(string title, string message, ProwlSettings settings)
        {
            try
            {
                var requestBuilder = new HttpRequestBuilder(PUSH_URL);

                var request = requestBuilder.Post()
                    .AddFormParameter("apikey", settings.ApiKey)
                    .AddFormParameter("application", BuildInfo.AppName)
                    .AddFormParameter("event", title)
                    .AddFormParameter("description", message)
                    .AddFormParameter("priority", settings.Priority)
                    .Build();

                _httpClient.Post(request);
            }
            catch (HttpException ex)
            {
                if (ex.Response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _logger.Error(ex, "Apikey is invalid");
                    throw new ProwlException("Apikey is invalid", ex);
                }

                throw new ProwlException("Unable to send text message: " + ex.Message, ex);
            }
            catch (WebException ex)
            {
                throw new ProwlException("Failed to connect to prowl, please check your settings.", ex);
            }
        }

        public ValidationFailure Test(ProwlSettings settings)
        {
            try
            {
                var title = _notificationText.Get("notification.test.title", "Test Notification");
                var body = _notificationText.Get("notification.test.body", "This is a test message from Chaptarr.");

                SendNotification(title, body, settings);
            }
            catch (Exception ex)
            {
                return new ValidationFailure("ApiKey", ex.Message);
            }

            return null;
        }
    }
}
