using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using FluentValidation.Results;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;

namespace NzbDrone.Core.Notifications.Grimmory
{
    public interface IGrimmoryProxy
    {
        List<GrimmoryLibrary> GetLibraries(GrimmorySettings settings);
        void RefreshLibrary(GrimmorySettings settings, long libraryId);
        GrimmoryBook FindBookByPath(GrimmorySettings settings, long libraryId, string relativePath, bool bypassCache = false);
        void UpdateBookMetadata(GrimmorySettings settings, long bookId, Dictionary<string, object> metadata);
        void UploadBookCover(GrimmorySettings settings, GrimmoryBook book, byte[] image, string fileName);
        byte[] GetBookCover(GrimmorySettings settings, GrimmoryBook book);
        string BuildCoverUrl(GrimmorySettings settings, GrimmoryBook book);
        ValidationFailure Test(GrimmorySettings settings);
    }

    public class GrimmoryProxy : IGrimmoryProxy
    {
        private static readonly TimeSpan TokenCacheDuration = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan BookListCacheDuration = TimeSpan.FromMinutes(1);

        private readonly IHttpClient _httpClient;
        private readonly ICached<string> _tokenCache;
        private readonly ICached<List<GrimmoryBook>> _bookListCache;
        private readonly Logger _logger;

        public GrimmoryProxy(IHttpClient httpClient, ICacheManager cacheManager, Logger logger)
        {
            _httpClient = httpClient;
            _tokenCache = cacheManager.GetCache<string>(GetType(), "tokens");
            _bookListCache = cacheManager.GetCache<List<GrimmoryBook>>(GetType(), "books");
            _logger = logger;
        }

        public List<GrimmoryLibrary> GetLibraries(GrimmorySettings settings)
        {
            var response = ExecuteWithAuth(settings, token =>
            {
                var request = BuildRequest(settings, "api/v1/libraries", token).Build();
                return _httpClient.Get(request);
            });

            return Json.Deserialize<List<GrimmoryLibrary>>(response.Content) ?? new List<GrimmoryLibrary>();
        }

        public void RefreshLibrary(GrimmorySettings settings, long libraryId)
        {
            ExecuteWithAuth(settings, token =>
            {
                var request = BuildRequest(settings, $"api/v1/libraries/{libraryId}/refresh", token).Build();
                request.Method = HttpMethod.Put;
                return _httpClient.Execute(request);
            });

            _logger.Debug("Triggered Grimmory refresh for library {0}", libraryId);
        }

        private List<GrimmoryBook> GetLibraryBooks(GrimmorySettings settings, long libraryId, bool bypassCache)
        {
            var cacheKey = $"{settings.Url}:{settings.Username}:{libraryId}";

            if (bypassCache)
            {
                _bookListCache.Remove(cacheKey);
            }

            return _bookListCache.Get(cacheKey,
                () =>
                {
                    var response = ExecuteWithAuth(settings, token =>
                    {
                        var request = BuildRequest(settings, $"api/v1/libraries/{libraryId}/book", token).Build();
                        return _httpClient.Get(request);
                    });

                    return Json.Deserialize<List<GrimmoryBook>>(response.Content) ?? new List<GrimmoryBook>();
                },
                BookListCacheDuration);
        }

        public GrimmoryBook FindBookByPath(GrimmorySettings settings, long libraryId, string relativePath, bool bypassCache = false)
        {
            var normalized = NormalizeRelativePath(relativePath);

            if (normalized.IsNullOrWhiteSpace())
            {
                return null;
            }

            return GetLibraryBooks(settings, libraryId, bypassCache)
                .FirstOrDefault(b => b.AllFiles().Any(f => MatchesPath(f, normalized)));
        }

        private static bool MatchesPath(GrimmoryBookFile file, string normalizedPath)
        {
            var candidate = NormalizeRelativePath(file?.RelativePath());

            if (candidate.IsNullOrWhiteSpace())
            {
                return false;
            }

            if (file.FolderBased)
            {
                return normalizedPath.StartsWith(candidate + "/", StringComparison.Ordinal);
            }

            return candidate == normalizedPath;
        }

        public void UpdateBookMetadata(GrimmorySettings settings, long bookId, Dictionary<string, object> metadata)
        {
            ExecuteWithAuth(settings, token =>
            {
                var request = BuildRequest(settings, $"api/v1/books/{bookId}/metadata", token)
                    .AddQueryParam("replaceMode", "REPLACE_WHEN_PROVIDED")
                    .Build();

                request.Method = HttpMethod.Put;
                request.Headers.ContentType = "application/json";
                request.SetContent(new Dictionary<string, object> { { "metadata", metadata } }.ToJson());

                return _httpClient.Execute(request);
            });

            _logger.Debug("Updated Grimmory metadata for book {0}", bookId);
        }

        public void UploadBookCover(GrimmorySettings settings, GrimmoryBook book, byte[] image, string fileName)
        {
            ExecuteWithAuth(settings, token =>
            {
                var request = BuildRequest(settings, $"api/v1/books/{book.Id}/metadata/{book.CoverSlot}/upload", token)
                    .Post()
                    .AddFormUpload("file", fileName, image, GetImageContentType(fileName))
                    .Build();

                return _httpClient.Execute(request);
            });

            _logger.Debug("Uploaded Grimmory {0} for book {1}", book.CoverSlot, book.Id);
        }

        public byte[] GetBookCover(GrimmorySettings settings, GrimmoryBook book)
        {
            var cover = FetchCover(settings, book.Id, book.CoverSlot);

            if (cover == null && book.IsAudiobook)
            {
                cover = FetchCover(settings, book.Id, "cover");
            }

            return cover;
        }

        public string BuildCoverUrl(GrimmorySettings settings, GrimmoryBook book)
        {
            var token = GetAccessToken(settings, false);

            return $"{HttpUri.CombinePath(settings.Url, $"api/v1/media/book/{book.Id}/{book.CoverSlot}")}?token={token}";
        }

        private byte[] FetchCover(GrimmorySettings settings, long bookId, string slot)
        {
            try
            {
                var response = ExecuteWithAuth(settings, token =>
                {
                    var request = BuildRequest(settings, $"api/v1/media/book/{bookId}/{slot}", token).Build();
                    return _httpClient.Get(request);
                });

                return response.ResponseData;
            }
            catch (HttpException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public ValidationFailure Test(GrimmorySettings settings)
        {
            try
            {
                var libraries = GetLibraries(settings);

                if (settings.EbookLibraryId > 0 && !libraries.Exists(l => l.Id == settings.EbookLibraryId))
                {
                    return new ValidationFailure(nameof(GrimmorySettings.EbookLibraryId), "The selected ebook library was not found in Grimmory");
                }

                if (settings.AudiobookLibraryId > 0 && !libraries.Exists(l => l.Id == settings.AudiobookLibraryId))
                {
                    return new ValidationFailure(nameof(GrimmorySettings.AudiobookLibraryId), "The selected audiobook library was not found in Grimmory");
                }
            }
            catch (GrimmoryAuthenticationException)
            {
                return new ValidationFailure(nameof(GrimmorySettings.Username), "Authentication failed, check the username and password");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to connect to Grimmory");
                return new ValidationFailure(nameof(GrimmorySettings.Url), "Unable to connect: " + ex.Message);
            }

            return null;
        }

        private static string NormalizeRelativePath(string path)
        {
            return path?.Replace('\\', '/').Trim('/').ToLowerInvariant() ?? string.Empty;
        }

        private static string GetImageContentType(string fileName)
        {
            var extension = System.IO.Path.GetExtension(fileName)?.ToLowerInvariant();

            return extension switch
            {
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "image/jpeg"
            };
        }

        private HttpResponse ExecuteWithAuth(GrimmorySettings settings, Func<string, HttpResponse> action)
        {
            var token = GetAccessToken(settings, false);
            var response = action(token);

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                token = GetAccessToken(settings, true);
                response = action(token);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new GrimmoryAuthenticationException("Grimmory rejected the configured credentials");
            }

            if ((int)response.StatusCode >= 400)
            {
                throw new HttpException(response);
            }

            return response;
        }

        private string GetAccessToken(GrimmorySettings settings, bool forceRefresh)
        {
            var cacheKey = $"{settings.Url}:{settings.Username}";

            if (forceRefresh)
            {
                _tokenCache.Remove(cacheKey);
            }

            return _tokenCache.Get(cacheKey, () => Login(settings), TokenCacheDuration);
        }

        private string Login(GrimmorySettings settings)
        {
            var request = new HttpRequestBuilder(HttpUri.CombinePath(settings.Url, "api/v1/auth/login"))
                .Accept(HttpAccept.Json)
                .Build();

            request.Method = HttpMethod.Post;
            request.Headers.ContentType = "application/json";
            request.SuppressHttpError = true;
            request.SetContent(new { username = settings.Username, password = settings.Password }.ToJson());

            var response = _httpClient.Execute(request);

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new GrimmoryAuthenticationException("Grimmory rejected the configured credentials");
            }

            if ((int)response.StatusCode >= 400)
            {
                throw new HttpException(response);
            }

            var tokenResponse = Json.Deserialize<GrimmoryTokenResponse>(response.Content);

            if (tokenResponse?.AccessToken.IsNullOrWhiteSpace() != false)
            {
                throw new GrimmoryAuthenticationException("Grimmory did not return an access token");
            }

            return tokenResponse.AccessToken;
        }

        private static HttpRequestBuilder BuildRequest(GrimmorySettings settings, string relativePath, string token)
        {
            // SuppressHttpError so ExecuteWithAuth sees a 401/403 and can re-login.
            return new HttpRequestBuilder(HttpUri.CombinePath(settings.Url, relativePath))
            {
                SuppressHttpError = true
            }
                .Accept(HttpAccept.Json)
                .SetHeader("Authorization", $"Bearer {token}");
        }

        private class GrimmoryTokenResponse
        {
            [JsonProperty("accessToken")]
            public string AccessToken { get; set; }
        }
    }

    public class GrimmoryLibrary
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("allowedFormats")]
        public List<string> AllowedFormats { get; set; }
    }

    public class GrimmoryBook
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("libraryId")]
        public long LibraryId { get; set; }

        [JsonProperty("primaryFile")]
        public GrimmoryBookFile PrimaryFile { get; set; }

        [JsonProperty("alternativeFormats")]
        public List<GrimmoryBookFile> AlternativeFormats { get; set; }

        [JsonProperty("metadata")]
        public GrimmoryBookMetadata Metadata { get; set; }

        // Grimmory keeps a separate cover, lock and upload endpoint for books whose primary
        // file is an audiobook, and its UI shows only that one for them.
        public bool IsAudiobook => string.Equals(PrimaryFile?.BookType, "AUDIOBOOK", StringComparison.OrdinalIgnoreCase);

        public string CoverSlot => IsAudiobook ? "audiobook-cover" : "cover";

        public string CoverLockField => IsAudiobook ? "audiobookCoverLocked" : "coverLocked";

        public bool CoverIsLocked => IsAudiobook ? Metadata?.AudiobookCoverLocked == true : Metadata?.CoverLocked == true;

        public IEnumerable<GrimmoryBookFile> AllFiles()
        {
            if (PrimaryFile != null)
            {
                yield return PrimaryFile;
            }

            foreach (var file in AlternativeFormats ?? Enumerable.Empty<GrimmoryBookFile>())
            {
                yield return file;
            }
        }
    }

    public class GrimmoryBookFile
    {
        [JsonProperty("fileName")]
        public string FileName { get; set; }

        [JsonProperty("fileSubPath")]
        public string FileSubPath { get; set; }

        [JsonProperty("folderBased")]
        public bool FolderBased { get; set; }

        [JsonProperty("bookType")]
        public string BookType { get; set; }

        public string RelativePath()
        {
            return FileSubPath.IsNotNullOrWhiteSpace() ? $"{FileSubPath}/{FileName}" : FileName;
        }
    }

    public class GrimmoryBookMetadata
    {
        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("subtitle")]
        public string Subtitle { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("publisher")]
        public string Publisher { get; set; }

        [JsonProperty("publishedDate")]
        public string PublishedDate { get; set; }

        [JsonProperty("seriesName")]
        public string SeriesName { get; set; }

        [JsonProperty("seriesNumber")]
        public double? SeriesNumber { get; set; }

        [JsonProperty("language")]
        public string Language { get; set; }

        [JsonProperty("isbn13")]
        public string Isbn13 { get; set; }

        [JsonProperty("asin")]
        public string Asin { get; set; }

        [JsonProperty("goodreadsId")]
        public string GoodreadsId { get; set; }

        [JsonProperty("authors")]
        public List<string> Authors { get; set; }

        [JsonProperty("categories")]
        public List<string> Categories { get; set; }

        [JsonProperty("tags")]
        public List<string> Tags { get; set; }

        [JsonProperty("coverLocked")]
        public bool? CoverLocked { get; set; }

        [JsonProperty("audiobookCoverLocked")]
        public bool? AudiobookCoverLocked { get; set; }

        [JsonProperty("coverUpdatedOn")]
        public DateTime? CoverUpdatedOn { get; set; }

        [JsonProperty("audiobookCoverUpdatedOn")]
        public DateTime? AudiobookCoverUpdatedOn { get; set; }
    }

    public class GrimmoryAuthenticationException : Exception
    {
        public GrimmoryAuthenticationException(string message)
            : base(message)
        {
        }
    }
}
