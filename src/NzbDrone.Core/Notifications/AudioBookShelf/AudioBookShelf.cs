using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using FluentValidation;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.Notifications.AudioBookShelf
{
    public class AudioBookShelf : NotificationBase<AudioBookShelfSettings>, IResolveProviderPendingSecrets
    {
        private readonly IAudioBookShelfProxy _proxy;
        private readonly IHttpClient _httpClient;
        private readonly IPendingProviderSecretService _pendingProviderSecretService;
        private readonly ICached<AudioBookShelfOidcPendingAuth> _oidcPendingAuthCache;
        private readonly ICached<List<AudioBookShelfLibrary>> _libraryCache;
        private readonly IRootFolderService _rootFolderService;
        private readonly Logger _logger;

        private static readonly TimeSpan LibraryCacheDuration = TimeSpan.FromMinutes(1);

        public AudioBookShelf(IAudioBookShelfProxy proxy,
                              IHttpClient httpClient,
                              IPendingProviderSecretService pendingProviderSecretService,
                              ICacheManager cacheManager,
                              IRootFolderService rootFolderService,
                              Logger logger)
        {
            _proxy = proxy;
            _httpClient = httpClient;
            _pendingProviderSecretService = pendingProviderSecretService;
            _oidcPendingAuthCache = cacheManager.GetCache<AudioBookShelfOidcPendingAuth>(GetType());
            _libraryCache = cacheManager.GetCache<List<AudioBookShelfLibrary>>(GetType(), "libraries");
            _rootFolderService = rootFolderService;
            _logger = logger;
        }

        public override string Link => "https://www.audiobookshelf.org/";

        public override string Name => "AudioBookShelf";

        public override void OnReleaseImport(BookDownloadMessage message)
        {
            var filePath = message.BookFiles?.FirstOrDefault()?.Path;
            var mediaType = GetMediaType(message.Book, message.BookFiles?.FirstOrDefault());
            _logger.Debug("AudioBookShelf: OnReleaseImport triggered for author '{0}', book '{1}', mediaType '{2}', file '{3}'",
                message.Author?.Name, message.Book?.Title, mediaType ?? "unknown", filePath ?? "no file");

            var libraryScans = NewLibraryScanSet();

            if (Settings.HasConfiguredLibraryMappings())
            {
                SendMappedAdd(message.Author, filePath, mediaType, libraryScans);
            }
            else
            {
                AddLegacyLibraryScans(message.Author, filePath, mediaType, libraryScans);
            }

            SendLibraryScans(libraryScans);
        }

        public override void OnRename(Author author, List<RenamedBookFile> renamedFiles)
        {
            var filePath = renamedFiles?.FirstOrDefault()?.BookFile?.Path;
            var mediaTypes = renamedFiles?
                .Select(x => x?.BookFile?.MediaType)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            _logger.Debug("AudioBookShelf: OnRename triggered for author '{0}', {1} files renamed, mediaTypes [{2}], first file '{3}'",
                author?.Name,
                renamedFiles?.Count ?? 0,
                string.Join(", ", mediaTypes),
                filePath ?? "no file");

            if (renamedFiles.Empty())
            {
                return;
            }

            var libraryScans = NewLibraryScanSet();

            foreach (var renamedFile in renamedFiles.Where(x => x?.BookFile != null))
            {
                var bookFile = renamedFile.BookFile;
                var mediaType = GetMediaType(null, bookFile);

                if (Settings.HasConfiguredLibraryMappings())
                {
                    SendMappedRename(author, renamedFile.PreviousPath, bookFile.Path, mediaType, libraryScans);
                }
                else
                {
                    AddLegacyLibraryScans(author, bookFile.Path, mediaType, libraryScans);
                }
            }

            SendLibraryScans(libraryScans);
        }

        public override void OnBookFileDelete(BookFileDeleteMessage deleteMessage)
        {
            var filePath = deleteMessage.BookFile?.Path;
            var mediaType = GetMediaType(deleteMessage.Book, deleteMessage.BookFile);
            var author = deleteMessage.Book?.Author ?? deleteMessage.BookFile?.Author;

            _logger.Debug("AudioBookShelf: OnBookFileDelete triggered for author '{0}', book '{1}', mediaType '{2}', file '{3}', reason '{4}'",
                author?.Name,
                deleteMessage.Book?.Title,
                mediaType ?? "unknown",
                filePath ?? "no file",
                deleteMessage.Reason);

            var libraryScans = NewLibraryScanSet();

            if (Settings.HasConfiguredLibraryMappings())
            {
                SendMappedDelete(author, filePath, mediaType, libraryScans);
            }
            else
            {
                AddLegacyLibraryScans(author, filePath, mediaType, libraryScans);
            }

            SendLibraryScans(libraryScans);
        }

        // Watcher updates and scan fallbacks are sent at event time (fire and forget). Notification
        // provider instances are transient, so cross-call queues on instance fields never survive to a
        // later drain; AudioBookShelf itself debounces incoming watcher updates before scanning.
        private void SendWatcherUpdateOrFallback(ResolvedMappedTarget target, string type, string oldRelativePath, ISet<string> libraryScans)
        {
            if (target == null || target.LibraryId.IsNullOrWhiteSpace())
            {
                return;
            }

            if (target.RelativePath.IsNullOrWhiteSpace() ||
                (string.Equals(type, "rename", StringComparison.OrdinalIgnoreCase) && oldRelativePath.IsNullOrWhiteSpace()))
            {
                libraryScans.Add(target.LibraryId);
                return;
            }

            AudioBookShelfLibrary library;

            try
            {
                library = GetCachedBookLibraries()
                    .FirstOrDefault(l => string.Equals(l.Id, target.LibraryId, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to fetch AudioBookShelf libraries before watcher update, falling back to full scan for library '{0}'", target.LibraryId);
                libraryScans.Add(target.LibraryId);
                return;
            }

            if (library == null)
            {
                _logger.Warn("AudioBookShelf: mapped library '{0}' was not returned by ABS, falling back to full scan", target.LibraryId);
                libraryScans.Add(target.LibraryId);
                return;
            }

            // A library whose watcher is disabled silently drops /api/watcher/update (ABS only routes
            // it to active library watchers), so a full scan is the only working path for it. The
            // server-wide scannerDisableWatcher setting has the same effect but is not queryable.
            if (library.Settings?.DisableWatcher ?? false)
            {
                _logger.Debug("AudioBookShelf: watcher is disabled for library '{0}', falling back to full scan", target.LibraryId);
                libraryScans.Add(target.LibraryId);
                return;
            }

            var libraryFolderPath = ResolveLibraryFolderPath(target, library);
            if (libraryFolderPath.IsNullOrWhiteSpace())
            {
                _logger.Warn("AudioBookShelf: unable to resolve folder path for library '{0}', falling back to full scan", target.LibraryId);
                libraryScans.Add(target.LibraryId);
                return;
            }

            var translatedPath = CombineAudioBookShelfPath(libraryFolderPath, target.RelativePath);
            string translatedOldPath = null;

            if (string.Equals(type, "rename", StringComparison.OrdinalIgnoreCase))
            {
                translatedOldPath = CombineAudioBookShelfPath(libraryFolderPath, oldRelativePath);
            }

            try
            {
                _logger.Debug("AudioBookShelf: sending targeted watcher update '{0}' for library '{1}' path '{2}'",
                    type,
                    target.LibraryId,
                    translatedPath);

                _proxy.UpdateWatchedPath(Settings, target.LibraryId, translatedPath, type, translatedOldPath);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to send AudioBookShelf watcher update for library '{0}', falling back to full scan", target.LibraryId);
                libraryScans.Add(target.LibraryId);
            }
        }

        private void SendLibraryScans(ISet<string> libraryIds)
        {
            if (libraryIds == null || libraryIds.Count == 0)
            {
                return;
            }

            var failedScans = new List<string>();

            foreach (var libraryId in libraryIds)
            {
                try
                {
                    _logger.Debug("Triggering AudioBookShelf scan for library: {0}", libraryId);
                    _proxy.ScanLibrary(Settings, libraryId);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Failed to trigger AudioBookShelf library scan for library: {0}", libraryId);
                    failedScans.Add(libraryId);
                }
            }

            if (failedScans.Count > 0)
            {
                throw new InvalidOperationException($"Failed to trigger AudioBookShelf library scan(s): {string.Join(", ", failedScans)}");
            }
        }

        private List<AudioBookShelfLibrary> GetCachedBookLibraries()
        {
            // Key on the full connection identity - two definitions pointing at the same host with
            // different schemes or credentials must not serve each other's library lists.
            var cacheKey = $"{Settings.UseSsl}:{Settings.Host}:{Settings.Port}:{Settings.UrlBase}:{Settings.ApiKey}";
            return _libraryCache.Get(cacheKey, GetBookLibraries, LibraryCacheDuration);
        }

        private static HashSet<string> NewLibraryScanSet()
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public override ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();

            failures.AddIfNotNull(_proxy.Test(Settings));

            return new ValidationResult(failures);
        }

        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            if (action == "startOAuth")
            {
                if (!query.TryGetValue("callbackUrl", out var callbackUrl) || callbackUrl.IsNullOrWhiteSpace())
                {
                    throw new BadRequestException("QueryParam callbackUrl invalid.");
                }

                // Allow starting OAuth before ApiKey is set (it will be generated by the flow).
                if (Settings.Host.IsNullOrWhiteSpace())
                {
                    throw new ValidationException(new[]
                    {
                        new ValidationFailure(nameof(AudioBookShelfSettings.Host), "Host is required")
                    });
                }

                var baseUrl = HttpRequestBuilder.BuildBaseUrl(Settings.UseSsl, Settings.Host.ToUrlHost(), Settings.Port, Settings.UrlBase);
                var state = Guid.NewGuid().ToString("N");
                var codeVerifier = GeneratePkceCodeVerifier();
                var codeChallenge = GeneratePkceCodeChallenge(codeVerifier);

                var requestBuilder = new HttpRequestBuilder(baseUrl)
                    .Resource("/auth/openid")
                    .AddQueryParam("response_type", "code")
                    .AddQueryParam("redirect_uri", callbackUrl)
                    .AddQueryParam("state", state)
                    .AddQueryParam("code_challenge", codeChallenge)
                    .AddQueryParam("code_challenge_method", "S256");

                requestBuilder.AllowAutoRedirect = false;
                requestBuilder.SuppressHttpError = true;

                var response = _httpClient.Execute(requestBuilder.Build());

                if (response.HasHttpError)
                {
                    var message = response.Content.IsNullOrWhiteSpace()
                        ? $"AudioBookShelf OIDC start failed: {(int)response.StatusCode} {response.StatusCode}"
                        : response.Content;

                    if (response.StatusCode == HttpStatusCode.BadRequest &&
                        message.IndexOf("Invalid redirect_uri", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        message = $"AudioBookShelf rejected the callback URL. Add '{callbackUrl}' to AudioBookShelf → Settings → Authentication → OpenID Connect → Mobile Redirect URIs (or set it to '*'), then try again.";
                    }

                    throw new BadRequestException(message);
                }

                if (!response.HasHttpRedirect)
                {
                    throw new BadRequestException($"AudioBookShelf OIDC did not redirect (status {(int)response.StatusCode} {response.StatusCode}). Ensure OpenID Connect is enabled and configured on your AudioBookShelf server.");
                }

                var oauthUrl = response.Headers.GetSingleValue("Location");
                if (oauthUrl.IsNullOrWhiteSpace())
                {
                    throw new BadRequestException("AudioBookShelf OIDC redirect missing Location header.");
                }

                var cookies = response.GetCookies();
                if (cookies == null || cookies.Count == 0)
                {
                    throw new BadRequestException("AudioBookShelf did not return any cookies for the OIDC session.");
                }

                _oidcPendingAuthCache.Set(state, new AudioBookShelfOidcPendingAuth
                {
                    BaseUrl = baseUrl,
                    CodeVerifier = codeVerifier,
                    Cookies = cookies
                }, TimeSpan.FromMinutes(10));

                return new
                {
                    OauthUrl = oauthUrl
                };
            }

            if (action == "getOAuthToken")
            {
                if (!query.TryGetValue("code", out var code) || code.IsNullOrWhiteSpace())
                {
                    throw new BadRequestException("QueryParam code invalid.");
                }

                if (!query.TryGetValue("state", out var state) || state.IsNullOrWhiteSpace())
                {
                    throw new BadRequestException("QueryParam state invalid.");
                }

                // oauth.html forwards raw querystring values which may still be URL-encoded (e.g. "%2F").
                // Decode once so we send the actual authorization code back to AudioBookShelf.
                code = Uri.UnescapeDataString(code);
                state = Uri.UnescapeDataString(state);

                var pending = _oidcPendingAuthCache.Find(state);
                _oidcPendingAuthCache.Remove(state);

                if (pending == null)
                {
                    throw new BadRequestException("OIDC session expired. Please try again.");
                }

                var callbackBuilder = new HttpRequestBuilder(pending.BaseUrl)
                    .Resource("/auth/openid/callback")
                    .AddQueryParam("code", code)
                    .AddQueryParam("state", state)
                    .AddQueryParam("code_verifier", pending.CodeVerifier);

                callbackBuilder.AllowAutoRedirect = false;
                callbackBuilder.SuppressHttpError = true;
                callbackBuilder.HttpAccept = HttpAccept.Json;
                foreach (var cookie in pending.Cookies)
                {
                    callbackBuilder.Cookies[cookie.Key] = cookie.Value;
                }

                var callbackResponse = _httpClient.Execute(callbackBuilder.Build());
                if (callbackResponse.HasHttpError)
                {
                    var message = callbackResponse.Content.IsNullOrWhiteSpace()
                        ? $"AudioBookShelf OIDC callback failed: {(int)callbackResponse.StatusCode} {callbackResponse.StatusCode}"
                        : callbackResponse.Content;

                    throw new BadRequestException(message);
                }

                var loginResponse = Json.Deserialize<AudioBookShelfOidcLoginResponse>(callbackResponse.Content);
                var accessToken = loginResponse?.User?.AccessToken;
                var userId = loginResponse?.User?.Id;

                if (accessToken.IsNullOrWhiteSpace() || userId.IsNullOrWhiteSpace())
                {
                    throw new BadRequestException("AudioBookShelf did not return a valid access token.");
                }

                var createKeyRequest = new HttpRequestBuilder(pending.BaseUrl)
                    .Resource("/api/api-keys")
                    .Accept(HttpAccept.Json);

                createKeyRequest.Method = HttpMethod.Post;
                createKeyRequest.Headers.ContentType = "application/json";
                createKeyRequest.Headers.Add("Authorization", $"Bearer {accessToken}");
                createKeyRequest.SuppressHttpError = true;

                var createPayload = new AudioBookShelfApiKeyCreateRequest
                {
                    Name = "Chaptarr",
                    UserId = userId,
                    IsActive = true
                };

                createKeyRequest.PostProcess = req =>
                {
                    req.SetContent(createPayload.ToJson());
                };

                var createResponse = _httpClient.Execute(createKeyRequest.Build());

                if (createResponse.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new ValidationException(new[]
                    {
                        new ValidationFailure(nameof(AudioBookShelfSettings.ApiKey), "AudioBookShelf requires an admin user to create API keys. Sign in as an admin, or create an API key manually in AudioBookShelf and paste it here.")
                    });
                }

                if (createResponse.HasHttpError)
                {
                    var message = createResponse.Content.IsNullOrWhiteSpace()
                        ? $"Failed to create AudioBookShelf API key: {(int)createResponse.StatusCode} {createResponse.StatusCode}"
                        : createResponse.Content;

                    throw new BadRequestException(message);
                }

                var apiKeyResponse = Json.Deserialize<AudioBookShelfApiKeyCreateResponse>(createResponse.Content);
                var apiKey = apiKeyResponse?.ApiKey?.ApiKeyValue;

                if (apiKey.IsNullOrWhiteSpace())
                {
                    throw new BadRequestException("AudioBookShelf did not return an API key.");
                }

                return new
                {
                    ApiKey = _pendingProviderSecretService.Create(apiKey)
                };
            }

            if (action == "getLibraries" || action == "getAudiobookLibraries" || action == "getEbookLibraries" || action == "getDetailedLibraries")
            {
                if (Settings.ApiKey.IsNullOrWhiteSpace() || Settings.Host.IsNullOrWhiteSpace())
                {
                    return action == "getDetailedLibraries"
                        ? new { libraries = new List<object>() }
                        : new { options = new List<object>() };
                }

                try
                {
                    var libraries = GetBookLibraries();

                    if (action == "getDetailedLibraries")
                    {
                        return new
                        {
                            libraries = libraries
                                .OrderBy(d => d.Name, StringComparer.InvariantCultureIgnoreCase)
                                .Select(d => new
                                {
                                    d.Id,
                                    d.Name,
                                    d.MediaType,
                                    AudiobooksOnly = d.Settings?.AudiobooksOnly ?? false,
                                    DisableWatcher = d.Settings?.DisableWatcher ?? false,
                                    Folders = (d.Folders ?? new List<AudioBookShelfLibraryFolder>())
                                        .OrderBy(f => f.FullPath, StringComparer.InvariantCultureIgnoreCase)
                                        .Select(f => new
                                        {
                                            f.Id,
                                            f.FullPath,
                                            f.LibraryId
                                        })
                                })
                        };
                    }

                    if (action == "getAudiobookLibraries")
                    {
                        return BuildLegacyLibraryOptions(libraries.Where(SupportsAudiobooks));
                    }

                    if (action == "getEbookLibraries")
                    {
                        return BuildLegacyLibraryOptions(libraries.Where(SupportsEbooks));
                    }

                    return BuildLegacyLibraryOptions(libraries);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to retrieve libraries from AudioBookShelf");
                    var message = ex.Message;

                    if (message != null && message.IndexOf("Name or service not known", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        message = $"{message} If you're using a Docker container name (e.g. host 'audiobookshelf'), make sure Chaptarr and AudioBookShelf are on the same Docker network, or use a hostname/IP reachable from the Chaptarr container.";
                    }

                    throw new ValidationException(new[]
                    {
                        new ValidationFailure(nameof(AudioBookShelfSettings.Host), $"Unable to retrieve libraries from AudioBookShelf: {message}")
                    });
                }
            }

            return new { };
        }

        public void ResolveProviderPendingSecrets(bool consume)
        {
            Settings.ApiKey = _pendingProviderSecretService.Resolve(Settings.ApiKey, consume);
        }

        private static string GeneratePkceCodeVerifier()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            return Base64UrlEncode(bytes);
        }

        private static string GeneratePkceCodeChallenge(string verifier)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.ASCII.GetBytes(verifier));
            return Base64UrlEncode(bytes);
        }

        private static string Base64UrlEncode(byte[] bytes)
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private class AudioBookShelfOidcPendingAuth
        {
            public string BaseUrl { get; set; }
            public string CodeVerifier { get; set; }
            public Dictionary<string, string> Cookies { get; set; }
        }

        private class AudioBookShelfOidcLoginResponse
        {
            public AudioBookShelfOidcLoginUser User { get; set; }
        }

        private class AudioBookShelfOidcLoginUser
        {
            public string Id { get; set; }
            public string Username { get; set; }
            public string Type { get; set; }
            public string AccessToken { get; set; }
            public string RefreshToken { get; set; }
        }

        private class AudioBookShelfApiKeyCreateRequest
        {
            public string Name { get; set; }
            public string UserId { get; set; }
            public int? ExpiresIn { get; set; }
            public bool IsActive { get; set; }
        }

        private class AudioBookShelfApiKeyCreateResponse
        {
            public AudioBookShelfApiKey ApiKey { get; set; }
        }

        private class AudioBookShelfApiKey
        {
            [Newtonsoft.Json.JsonProperty("apiKey")]
            public string ApiKeyValue { get; set; }
        }

        private static string GetMediaType(Book book, BookFile bookFile)
        {
            if (book != null)
            {
                return book.MediaType == BookMediaType.Ebook ? "ebook" : "audiobook";
            }

            var mediaType = bookFile?.MediaType;
            if (mediaType.IsNullOrWhiteSpace() && bookFile?.Quality != null)
            {
                mediaType = BookFile.DetermineMediaType(bookFile.Quality);
            }

            return mediaType;
        }

        private void SendMappedAdd(Author author, string filePath, string mediaType, ISet<string> libraryScans)
        {
            var target = ResolveMappedTarget(author, filePath, mediaType);
            if (target == null)
            {
                _logger.Debug("AudioBookShelf: no configured library mapping matched author '{0}', mediaType '{1}', file '{2}'",
                    author?.Name ?? "unknown",
                    mediaType ?? "unknown",
                    filePath ?? "no file");
                return;
            }

            SendWatcherUpdateOrFallback(target, "add", null, libraryScans);
        }

        private void SendMappedDelete(Author author, string filePath, string mediaType, ISet<string> libraryScans)
        {
            var target = ResolveMappedTarget(author, filePath, mediaType);
            if (target == null)
            {
                _logger.Debug("AudioBookShelf: no configured library mapping matched author '{0}', mediaType '{1}', file '{2}'",
                    author?.Name ?? "unknown",
                    mediaType ?? "unknown",
                    filePath ?? "no file");
                return;
            }

            SendWatcherUpdateOrFallback(target, "unlink", null, libraryScans);
        }

        private void SendMappedRename(Author author, string previousPath, string newPath, string mediaType, ISet<string> libraryScans)
        {
            if (!previousPath.IsNullOrWhiteSpace() && previousPath.PathEquals(newPath))
            {
                return;
            }

            var oldTarget = ResolveMappedTarget(author, previousPath, mediaType);
            var newTarget = ResolveMappedTarget(author, newPath, mediaType);

            if (oldTarget == null && newTarget == null)
            {
                _logger.Debug("AudioBookShelf: no configured library mapping matched rename for author '{0}', mediaType '{1}', old '{2}', new '{3}'",
                    author?.Name ?? "unknown",
                    mediaType ?? "unknown",
                    previousPath ?? "no previous path",
                    newPath ?? "no new path");
                return;
            }

            if (oldTarget != null && newTarget != null && TargetsSame(oldTarget, newTarget))
            {
                SendWatcherUpdateOrFallback(newTarget, "rename", oldTarget.RelativePath, libraryScans);
                return;
            }

            if (oldTarget != null)
            {
                SendWatcherUpdateOrFallback(oldTarget, "unlink", null, libraryScans);
            }

            if (newTarget != null)
            {
                SendWatcherUpdateOrFallback(newTarget, "add", null, libraryScans);
            }
        }

        private void AddLegacyLibraryScans(Author author, string filePath, string mediaType, ISet<string> libraryScans)
        {
            libraryScans.UnionWith(ResolveLegacyLibraryIds(author, filePath, mediaType));
        }

        private HashSet<string> ResolveLegacyLibraryIds(Author author, string filePath, string mediaType)
        {
            var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var audiobookLibraryId = Settings.AudiobookLibraryId;
            var ebookLibraryId = Settings.EbookLibraryId;

            if (string.Equals(mediaType, "audiobook", StringComparison.OrdinalIgnoreCase))
            {
                AddIfConfigured(libraries, audiobookLibraryId);
                return libraries;
            }

            if (string.Equals(mediaType, "ebook", StringComparison.OrdinalIgnoreCase))
            {
                AddIfConfigured(libraries, ebookLibraryId);
                return libraries;
            }

            if (!string.IsNullOrWhiteSpace(filePath))
            {
                if (!string.IsNullOrWhiteSpace(author?.AudiobookRootFolderPath) &&
                    filePath.StartsWith(author.AudiobookRootFolderPath, StringComparison.OrdinalIgnoreCase))
                {
                    AddIfConfigured(libraries, audiobookLibraryId);
                }

                if (!string.IsNullOrWhiteSpace(author?.EbookRootFolderPath) &&
                    filePath.StartsWith(author.EbookRootFolderPath, StringComparison.OrdinalIgnoreCase))
                {
                    AddIfConfigured(libraries, ebookLibraryId);
                }

                if (libraries.Count == 0)
                {
                    AddIfConfigured(libraries, audiobookLibraryId);
                    AddIfConfigured(libraries, ebookLibraryId);
                }
            }
            else
            {
                AddIfConfigured(libraries, audiobookLibraryId);
                AddIfConfigured(libraries, ebookLibraryId);
            }

            return libraries;
        }

        private ResolvedMappedTarget ResolveMappedTarget(Author author, string filePath, string mediaType)
        {
            if (filePath.IsNullOrWhiteSpace())
            {
                return null;
            }

            var mappings = Settings.GetLibraryMappings();
            if (mappings.Count == 0)
            {
                return null;
            }

            var rootFolder = ResolveRootFolder(author, filePath, mediaType);
            if (rootFolder == null)
            {
                return null;
            }

            var rootMappings = mappings
                .Where(m => m != null &&
                            m.RootFolderId == rootFolder.Id &&
                            m.LibraryId.IsNotNullOrWhiteSpace())
                .ToList();

            AudioBookShelfLibraryMapping mapping;
            if (mediaType.IsNullOrWhiteSpace())
            {
                if (rootMappings.Count != 1)
                {
                    return null;
                }

                mapping = rootMappings[0];
            }
            else
            {
                mapping = rootMappings.FirstOrDefault(m => MediaTypeMatches(m.MediaType, mediaType));
            }

            if (mapping == null)
            {
                return null;
            }

            TryGetRelativePath(rootFolder.Path, filePath, out var relativePath);

            return new ResolvedMappedTarget
            {
                LibraryId = mapping.LibraryId,
                LibraryFolderId = mapping.LibraryFolderId,
                LibraryFolderPath = mapping.LibraryFolderPath,
                RelativePath = relativePath
            };
        }

        private RootFolder ResolveRootFolder(Author author, string filePath, string mediaType)
        {
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                return _rootFolderService.GetBestRootFolder(filePath);
            }

            if (string.Equals(mediaType, "ebook", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(author?.EbookRootFolderPath))
            {
                return _rootFolderService.GetBestRootFolder(author.EbookRootFolderPath);
            }

            if (string.Equals(mediaType, "audiobook", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(author?.AudiobookRootFolderPath))
            {
                return _rootFolderService.GetBestRootFolder(author.AudiobookRootFolderPath);
            }

            return null;
        }

        private static bool TryGetRelativePath(string basePath, string fullPath, out string relativePath)
        {
            relativePath = null;

            if (basePath.IsNullOrWhiteSpace() || fullPath.IsNullOrWhiteSpace())
            {
                return false;
            }

            if (!basePath.IsParentPath(fullPath) && !basePath.PathEquals(fullPath))
            {
                return false;
            }

            relativePath = basePath.PathEquals(fullPath)
                ? string.Empty
                : basePath.GetRelativePath(fullPath);

            relativePath = relativePath
                ?.Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/')
                .Trim('/');

            return relativePath.IsNotNullOrWhiteSpace();
        }

        private static bool MediaTypeMatches(string configuredMediaType, string requestedMediaType)
        {
            return string.Equals(configuredMediaType, requestedMediaType, StringComparison.OrdinalIgnoreCase);
        }

        private static void AddIfConfigured(ISet<string> libraries, string libraryId)
        {
            if (!string.IsNullOrWhiteSpace(libraryId))
            {
                libraries.Add(libraryId);
            }
        }

        private static string ResolveLibraryFolderPath(ResolvedMappedTarget target, AudioBookShelfLibrary library)
        {
            var folders = library?.Folders ?? new List<AudioBookShelfLibraryFolder>();

            if (target.LibraryFolderId.IsNotNullOrWhiteSpace())
            {
                var folderById = folders.FirstOrDefault(f => string.Equals(f.Id, target.LibraryFolderId, StringComparison.OrdinalIgnoreCase));
                if (folderById?.FullPath.IsNotNullOrWhiteSpace() == true)
                {
                    return folderById.FullPath;
                }
            }

            if (target.LibraryFolderPath.IsNotNullOrWhiteSpace())
            {
                var folderByPath = folders.FirstOrDefault(f => f.FullPath.PathEquals(target.LibraryFolderPath));
                if (folderByPath?.FullPath.IsNotNullOrWhiteSpace() == true)
                {
                    return folderByPath.FullPath;
                }
            }

            if (folders.Count == 1 && folders[0]?.FullPath.IsNotNullOrWhiteSpace() == true)
            {
                return folders[0].FullPath;
            }

            return null;
        }

        private static string CombineAudioBookShelfPath(string libraryFolderPath, string relativePath)
        {
            if (relativePath.IsNullOrWhiteSpace())
            {
                return libraryFolderPath;
            }

            return $"{libraryFolderPath.TrimEnd('/', '\\')}/{relativePath.TrimStart('/', '\\').Replace('\\', '/')}";
        }

        private static bool TargetsSame(ResolvedMappedTarget oldTarget, ResolvedMappedTarget newTarget)
        {
            if (oldTarget == null || newTarget == null)
            {
                return false;
            }

            if (!string.Equals(oldTarget.LibraryId, newTarget.LibraryId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (oldTarget.LibraryFolderId.IsNotNullOrWhiteSpace() && newTarget.LibraryFolderId.IsNotNullOrWhiteSpace())
            {
                return string.Equals(oldTarget.LibraryFolderId, newTarget.LibraryFolderId, StringComparison.OrdinalIgnoreCase);
            }

            return oldTarget.LibraryFolderPath.PathEquals(newTarget.LibraryFolderPath);
        }

        private List<AudioBookShelfLibrary> GetBookLibraries()
        {
            return (_proxy.GetLibraries(Settings) ?? new List<AudioBookShelfLibrary>())
                .Where(l => l != null &&
                            string.Equals(l.MediaType, "book", StringComparison.OrdinalIgnoreCase))
                .OrderBy(l => l.Name, StringComparer.InvariantCultureIgnoreCase)
                .ToList();
        }

        private static object BuildLegacyLibraryOptions(IEnumerable<AudioBookShelfLibrary> libraries)
        {
            return new
            {
                options = (libraries ?? new List<AudioBookShelfLibrary>())
                    .OrderBy(d => d.Name, StringComparer.InvariantCultureIgnoreCase)
                    .Select(d => new
                    {
                        Value = d.Id,
                        Name = d.Name
                    })
            };
        }

        private static bool SupportsAudiobooks(AudioBookShelfLibrary library)
        {
            return library != null &&
                   string.Equals(library.MediaType, "book", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SupportsEbooks(AudioBookShelfLibrary library)
        {
            return SupportsAudiobooks(library) &&
                   !(library.Settings?.AudiobooksOnly ?? false);
        }

        private class ResolvedMappedTarget
        {
            public string LibraryId { get; set; }
            public string LibraryFolderId { get; set; }
            public string LibraryFolderPath { get; set; }
            public string RelativePath { get; set; }
        }

    }
}
