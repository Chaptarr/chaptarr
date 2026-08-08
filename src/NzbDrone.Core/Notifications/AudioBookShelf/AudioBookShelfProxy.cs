using System;
using System.Collections.Generic;
using System.Net.Http;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;

namespace NzbDrone.Core.Notifications.AudioBookShelf
{
    public interface IAudioBookShelfProxy
    {
        void ScanLibrary(AudioBookShelfSettings settings);
        void ScanLibrary(AudioBookShelfSettings settings, string libraryId);
        void UpdateWatchedPath(AudioBookShelfSettings settings, string libraryId, string path, string type, string oldPath = null);
        ValidationFailure Test(AudioBookShelfSettings settings);
        List<AudioBookShelfLibrary> GetLibraries(AudioBookShelfSettings settings);
    }

    public class AudioBookShelfProxy : IAudioBookShelfProxy
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public AudioBookShelfProxy(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public void ScanLibrary(AudioBookShelfSettings settings)
        {
            // Scan all libraries (backward compatibility)
            // AudioBookShelf does not expose a "/api/libraries/scan" endpoint; scans are triggered per-library:
            // POST /api/libraries/:id/scan
            var libraries = GetLibraries(settings);

            if (libraries == null || libraries.Count == 0)
            {
                _logger.Warn("AudioBookShelf library scan requested but no libraries were returned");
                return;
            }

            foreach (var library in libraries)
            {
                if (library == null || string.IsNullOrWhiteSpace(library.Id))
                {
                    continue;
                }

                // Skip podcast libraries; Chaptarr only integrates with book libraries.
                if (string.Equals(library.MediaType, "podcast", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(library.MediaType, "podcasts", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ScanLibrary(settings, library.Id);
            }
        }

        public void ScanLibrary(AudioBookShelfSettings settings, string libraryId)
        {
            // Scan specific library
            if (string.IsNullOrEmpty(libraryId))
            {
                _logger.Warn("Cannot scan library: libraryId is null or empty");
                return;
            }

            var request = BuildRequest(settings, $"/api/libraries/{libraryId}/scan");
            request.Method = HttpMethod.Post;

            var response = _httpClient.Execute(request);

            if (response.HasHttpError)
            {
                throw new HttpException(response);
            }
        }

        public void UpdateWatchedPath(AudioBookShelfSettings settings, string libraryId, string path, string type, string oldPath = null)
        {
            if (libraryId.IsNullOrWhiteSpace())
            {
                throw new ArgumentException("AudioBookShelf libraryId must be provided", nameof(libraryId));
            }

            if (path.IsNullOrWhiteSpace())
            {
                throw new ArgumentException("AudioBookShelf watcher path must be provided", nameof(path));
            }

            if (type.IsNullOrWhiteSpace())
            {
                throw new ArgumentException("AudioBookShelf watcher update type must be provided", nameof(type));
            }

            var request = BuildRequest(settings, "/api/watcher/update");
            request.Method = HttpMethod.Post;
            request.Headers.ContentType = "application/json";
            request.SetContent(new AudioBookShelfWatcherUpdateRequest
            {
                LibraryId = libraryId,
                Path = path,
                Type = type,
                OldPath = oldPath
            }.ToJson());

            var response = _httpClient.Execute(request);

            if (response.HasHttpError)
            {
                throw new HttpException(response);
            }
        }

        public ValidationFailure Test(AudioBookShelfSettings settings)
        {
            try
            {
                var request = BuildRequest(settings, "/api/libraries");
                var response = _httpClient.Execute(request);

                if (response.HasHttpError)
                {
                    _logger.Warn("AudioBookShelf test failed: {0}", response.StatusCode);
                    return new ValidationFailure(nameof(AudioBookShelfSettings.Host), $"Unable to connect to AudioBookShelf. Status: {response.StatusCode}");
                }

                var libraries = Json.Deserialize<AudioBookShelfLibrariesResponse>(response.Content);

                if (libraries?.Libraries == null || libraries.Libraries.Count == 0)
                {
                    return new ValidationFailure("", "No libraries found on AudioBookShelf server");
                }

                _logger.Debug("AudioBookShelf connection test successful. Found {0} libraries", libraries.Libraries.Count);
                return null;
            }
            catch (HttpException ex)
            {
                _logger.Error(ex, "AudioBookShelf connection test failed");

                if (ex.Response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    return new ValidationFailure("ApiKey", "Authentication failed. Check API key");
                }

                return new ValidationFailure(nameof(AudioBookShelfSettings.Host), $"Unable to connect to AudioBookShelf: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "AudioBookShelf connection test failed");
                return new ValidationFailure(nameof(AudioBookShelfSettings.Host), $"Unable to connect to AudioBookShelf: {ex.Message}");
            }
        }

        public List<AudioBookShelfLibrary> GetLibraries(AudioBookShelfSettings settings)
        {
            var request = BuildRequest(settings, "/api/libraries");
            var response = _httpClient.Execute(request);

            if (response.HasHttpError)
            {
                throw new HttpException(response);
            }

            var librariesResponse = Json.Deserialize<AudioBookShelfLibrariesResponse>(response.Content);
            return librariesResponse?.Libraries ?? new List<AudioBookShelfLibrary>();
        }

        private HttpRequest BuildRequest(AudioBookShelfSettings settings, string resource)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings), "AudioBookShelf settings cannot be null");
            }

            if (settings.Host.IsNullOrWhiteSpace())
            {
                throw new ArgumentException("AudioBookShelf Host must be configured", nameof(settings));
            }

            var baseUrl = HttpRequestBuilder.BuildBaseUrl(settings.UseSsl, settings.Host.ToUrlHost(), settings.Port, settings.UrlBase);
            var request = new HttpRequestBuilder(baseUrl)
                .Resource(resource)
                .Build();

            request.RequestTimeout = RequestTimeout;
            request.Headers.Add("User-Agent", "Chaptarr");

            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                request.Headers.Add("Authorization", $"Bearer {settings.ApiKey}");
            }
            else
            {
                throw new ArgumentException("AudioBookShelf API Key must be configured", nameof(settings));
            }

            return request;
        }

        private class AudioBookShelfLibrariesResponse
        {
            public List<AudioBookShelfLibrary> Libraries { get; set; }
        }

        private class AudioBookShelfWatcherUpdateRequest
        {
            public string LibraryId { get; set; }
            public string Path { get; set; }
            public string Type { get; set; }
            public string OldPath { get; set; }
        }
    }

    public class AudioBookShelfLibrary
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string MediaType { get; set; }
        public AudioBookShelfLibrarySettings Settings { get; set; }
        public List<AudioBookShelfLibraryFolder> Folders { get; set; } = new List<AudioBookShelfLibraryFolder>();
    }

    public class AudioBookShelfLibrarySettings
    {
        public bool DisableWatcher { get; set; }
        public bool AudiobooksOnly { get; set; }
    }

    public class AudioBookShelfLibraryFolder
    {
        public string Id { get; set; }
        public string FullPath { get; set; }
        public string LibraryId { get; set; }
    }
}
