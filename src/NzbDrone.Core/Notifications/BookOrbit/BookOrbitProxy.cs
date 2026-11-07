using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using FluentValidation.Results;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Notifications.BookOrbit
{
    public interface IBookOrbitProxy
    {
        ValidationFailure Test(BookOrbitSettings settings);
        void UploadFiles(BookOrbitSettings settings, IEnumerable<string> filePaths);
    }

    public class BookOrbitProxy : IBookOrbitProxy
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public BookOrbitProxy(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public ValidationFailure Test(BookOrbitSettings settings)
        {
            try
            {
                var accessToken = GetAccessToken(settings);
                VerifyBookDockAccess(settings, accessToken);
                return null;
            }
            catch (HttpException ex) when (IsBookDockPermissionRejection(ex))
            {
                _logger.Warn("BookOrbit denied Book Dock access with HTTP {0} at {1}", (int)ex.Response.StatusCode, settings.BaseUrl);
                return new ValidationFailure(string.Empty, "The BookOrbit account must have Book Dock Access permission.");
            }
            catch (HttpException ex) when (IsCredentialRejection(ex))
            {
                _logger.Warn("BookOrbit rejected the configured login with HTTP {0} at {1}", (int)ex.Response.StatusCode, settings.BaseUrl);
                return new ValidationFailure(nameof(settings.Password), "BookOrbit rejected the login. Check the credentials and password-login policy.");
            }
            catch (HttpException ex)
            {
                _logger.Warn("BookOrbit returned HTTP {0} at {1}", (int)ex.Response.StatusCode, settings.BaseUrl);
                return new ValidationFailure(string.Empty, "Unable to connect to BookOrbit. Check the URL, credentials, and logs.");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to connect to BookOrbit at {0}", settings.BaseUrl);
                return new ValidationFailure(string.Empty, "Unable to connect to BookOrbit. Check the URL, credentials, and logs.");
            }
        }

        public void UploadFiles(BookOrbitSettings settings, IEnumerable<string> filePaths)
        {
            var paths = filePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (paths.Count == 0)
            {
                return;
            }

            foreach (var path in paths)
            {
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("The imported book file no longer exists.", path);
                }
            }

            var accessToken = GetAccessToken(settings);

            foreach (var path in paths)
            {
                UploadFile(settings, accessToken, path);
            }
        }

        private string GetAccessToken(BookOrbitSettings settings)
        {
            var request = new HttpRequestBuilder($"{settings.BaseUrl.TrimEnd('/')}/api/v1/auth/login")
                .Post()
                .SetHeader("Content-Type", "application/json")
                .Accept(HttpAccept.Json)
                .Build();

            request.SetContent(JsonConvert.SerializeObject(new
            {
                username = settings.Username,
                password = settings.Password
            }));
            request.AllowAutoRedirect = false;
            request.LogHttpError = false;
            request.RequestTimeout = TimeSpan.FromSeconds(30);

            var response = _httpClient.Execute(request);
            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
            {
                throw new InvalidDataException("BookOrbit login did not return a successful response.");
            }

            var accessToken = JObject.Parse(response.Content)["accessToken"]?.Value<string>();

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new InvalidDataException("BookOrbit login response did not include an access token.");
            }

            return accessToken;
        }

        private void VerifyBookDockAccess(BookOrbitSettings settings, string accessToken)
        {
            var request = new HttpRequestBuilder($"{settings.BaseUrl.TrimEnd('/')}/api/v1/book-dock/summary")
                .SetHeader("Authorization", $"Bearer {accessToken}")
                .Accept(HttpAccept.Json)
                .Build();

            request.AllowAutoRedirect = false;
            request.LogHttpError = false;
            request.RequestTimeout = TimeSpan.FromSeconds(30);
            var response = _httpClient.Execute(request);
            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
            {
                throw new InvalidDataException("BookOrbit Book Dock access check did not return a successful response.");
            }
        }

        private static bool IsCredentialRejection(HttpException exception)
        {
            return exception.Response.StatusCode == HttpStatusCode.Unauthorized ||
                   exception.Response.StatusCode == HttpStatusCode.Forbidden;
        }

        private static bool IsBookDockPermissionRejection(HttpException exception)
        {
            return exception.Response.StatusCode == HttpStatusCode.Forbidden &&
                   exception.Request.Url.ToString().EndsWith("/api/v1/book-dock/summary", StringComparison.Ordinal);
        }

        private void UploadFile(BookOrbitSettings settings, string accessToken, string path)
        {
            using var fileStream = File.OpenRead(path);
            using var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var formContent = new MultipartFormDataContent();
            formContent.Add(fileContent, "file", Path.GetFileName(path));

            var request = new HttpRequestBuilder($"{settings.BaseUrl.TrimEnd('/')}/api/v1/book-dock/upload")
                .Post()
                .SetHeader("Authorization", $"Bearer {accessToken}")
                .KeepAlive()
                .Build();

            request.AllowAutoRedirect = false;
            request.LogHttpError = false;
            request.RequestTimeout = TimeSpan.FromHours(1);
            request.ContentSummary = $"file={Path.GetFileName(path)} ({fileStream.Length} bytes)";
            request.SetContent(formContent);

            HttpResponse response;
            try
            {
                response = _httpClient.Execute(request);
            }
            catch (HttpException ex)
            {
                throw UploadFailure(path, (int)ex.Response.StatusCode);
            }

            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
            {
                throw UploadFailure(path, (int)response.StatusCode);
            }

            _logger.Info("Uploaded {0} to BookOrbit Book Dock", Path.GetFileName(path));
        }

        private static WebException UploadFailure(string path, int statusCode)
        {
            return new WebException(
                $"BookOrbit rejected '{Path.GetFileName(path)}' with HTTP {statusCode}. Check its Book Dock permissions, accepted formats, and upload-size limit.",
                null,
                WebExceptionStatus.ProtocolError,
                null);
        }
    }
}
