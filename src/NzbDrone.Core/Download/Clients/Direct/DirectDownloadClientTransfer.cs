using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download.Clients;

namespace NzbDrone.Core.Download.Clients.Direct
{
    public enum HtmlResponseClassification
    {
        None,
        DdosChallenge,
        LoginOrKeyError,
        RateLimit,
        WrongUrl,
        OrdinaryDetailPage,
        UnknownHtml
    }

    public partial class DirectDownloadClient
    {
        private async Task DownloadInternalAsync(string downloadId, CancellationToken cancellationToken)
        {
            try
            {
                var state = _stateStore.Find(Settings.StagingFolder, Definition.Id, downloadId);
                if (state == null || PromoteCompletedFileIfPresent(state))
                {
                    return;
                }

                for (var attempt = state.AttemptCount + 1; attempt <= MaxAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    state.AttemptCount = attempt;
                    state.Status = DownloadItemStatus.Downloading;
                    state.Message = null;
                    state.DownloadedBytes = 0;
                    _stateStore.Save(Settings.StagingFolder, Definition.Id, state);

                    try
                    {
                        await ExecuteAttemptAsync(state, cancellationToken);
                        PromoteCompletedFileIfPresent(state, true);
                        return;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (attempt == MaxAttempts || IsPermanentFailure(ex))
                        {
                            state.Status = DownloadItemStatus.Failed;
                            state.Message = FormatFailureMessage(ex, attempt, MaxAttempts);
                            _stateStore.Save(Settings.StagingFolder, Definition.Id, state);
                            return;
                        }

                        DeleteIfPresent(state.PartFilePath);
                        var delay = ComputeBackoffDelay(attempt);
                        state.Message = $"Retrying after error (attempt {attempt}/{MaxAttempts}): {ex.Message}";
                        _stateStore.Save(Settings.StagingFolder, Definition.Id, state);
                        await Task.Delay(delay, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Debug("Direct download '{0}' was cancelled.", downloadId);
            }
            finally
            {
                if (_activeDownloads.TryRemove(downloadId, out var cancellationTokenSource))
                {
                    cancellationTokenSource.Dispose();
                }
            }
        }

        private async Task ExecuteAttemptAsync(DirectDownloadClientState state, CancellationToken cancellationToken)
        {
            _diskProvider.EnsureFolder(state.OutputDirectory);
            DeleteIfPresent(state.PartFilePath);

            long persistedBytes = 0;
            long lastPersistedBytes = 0;

            await using var fileStream = _diskProvider.OpenWriteStream(state.PartFilePath);
            await using var progressStream = new DirectDownloadProgressStream(fileStream, bytes =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                persistedBytes = bytes;
                if (bytes == 0 || bytes - lastPersistedBytes < 4096)
                {
                    return;
                }

                lastPersistedBytes = bytes;
                state.DownloadedBytes = bytes;
                _stateStore.Save(Settings.StagingFolder, Definition.Id, state);
            });

            var request = new HttpRequest(state.DownloadUrl)
            {
                AllowAutoRedirect = true,
                RequestTimeout = TimeSpan.FromMinutes(2),
                ResponseStream = progressStream,
                CancellationToken = cancellationToken
            };

            var response = await _httpClient.GetAsync(request);
            if (response.Headers.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true)
            {
                var classification = ClassifyHtmlResponse(response);
                throw new DownloadClientException($"Direct source returned HTML ({classification}) instead of a downloadable file.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            state.DownloadedBytes = Math.Max(persistedBytes, _diskProvider.GetFileSize(state.PartFilePath));
            if (state.DownloadedBytes <= 0)
            {
                throw new DownloadClientException("Direct source returned an empty file.");
            }

            _diskProvider.MoveFile(state.PartFilePath, state.OutputFilePath, true);
        }

        private bool PromoteCompletedFileIfPresent(DirectDownloadClientState state, bool allowOverwriteMessage = false)
        {
            if (!_diskProvider.FileExists(state.OutputFilePath))
            {
                return false;
            }

            state.Status = DownloadItemStatus.Completed;
            state.DownloadedBytes = _diskProvider.GetFileSize(state.OutputFilePath);
            state.TotalSize = Math.Max(state.TotalSize, state.DownloadedBytes);
            if (allowOverwriteMessage)
            {
                state.Message = null;
            }

            _stateStore.Save(Settings.StagingFolder, Definition.Id, state);
            return true;
        }

        private void ReconcileState(DirectDownloadClientState state)
        {
            if (state.Status == DownloadItemStatus.Completed && !_diskProvider.FileExists(state.OutputFilePath))
            {
                state.Status = DownloadItemStatus.Failed;
                state.Message = "Completed file is missing from the staging folder.";
                _stateStore.Save(Settings.StagingFolder, Definition.Id, state);
                return;
            }

            if (state.Status is DownloadItemStatus.Queued or DownloadItemStatus.Downloading)
            {
                PromoteCompletedFileIfPresent(state, true);
            }
        }

        private static bool IsPermanentFailure(Exception exception)
        {
            return exception switch
            {
                DownloadClientException { Message: var msg } when msg.Contains("empty file", StringComparison.OrdinalIgnoreCase) => true,
                _ => false
            };
        }

        private static bool IsTransientFailure(Exception exception)
        {
            return exception switch
            {
                WebException { Status: WebExceptionStatus.Timeout or WebExceptionStatus.ConnectFailure or WebExceptionStatus.ReceiveFailure or WebExceptionStatus.NameResolutionFailure } => true,
                HttpException { Response.HasHttpServerError: true } => true,
                HttpException { Response.StatusCode: HttpStatusCode.RequestTimeout } => true,
                DownloadClientException { Message: var msg } when msg.Contains("HTML", StringComparison.OrdinalIgnoreCase) => true,
                HttpException { Response.StatusCode: HttpStatusCode.Forbidden, Response.Headers.ContentType: var ct } when ct?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true => true,
                _ => false
            };
        }

        internal static HtmlResponseClassification ClassifyHtmlResponse(HttpResponse response)
        {
            if (response == null)
            {
                return HtmlResponseClassification.None;
            }

            var isHtml = response.Headers.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true;
            if (!isHtml)
            {
                return HtmlResponseClassification.None;
            }

            var content = response.Content ?? string.Empty;
            var statusCode = response.StatusCode;

            // DDoS / anti-bot challenge pages
            if (content.Contains("DDoS-Guard", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("DDoS Protection", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("checking your browser", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("Just a moment", StringComparison.OrdinalIgnoreCase))
            {
                return HtmlResponseClassification.DdosChallenge;
            }

            // Rate-limit / too-many-requests
            if (statusCode == HttpStatusCode.TooManyRequests ||
                statusCode == (HttpStatusCode)429 ||
                content.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("too many requests", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("No downloads left", StringComparison.OrdinalIgnoreCase))
            {
                return HtmlResponseClassification.RateLimit;
            }

            // Login or API key errors
            if (statusCode == HttpStatusCode.Unauthorized ||
                statusCode == HttpStatusCode.Forbidden ||
                content.Contains("login", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("sign in", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("invalid key", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("bad key", StringComparison.OrdinalIgnoreCase))
            {
                return HtmlResponseClassification.LoginOrKeyError;
            }

            // Wrong URL / not found
            if (statusCode == HttpStatusCode.NotFound ||
                content.Contains("404", StringComparison.Ordinal) ||
                content.Contains("page not found", StringComparison.OrdinalIgnoreCase))
            {
                return HtmlResponseClassification.WrongUrl;
            }

            // Ordinary detail page (looks like an info/detail page with links)
            if (content.Contains("GET</a>", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("Download</a>", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("js-download-link", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("slow_download", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("fast_download", StringComparison.OrdinalIgnoreCase))
            {
                return HtmlResponseClassification.OrdinaryDetailPage;
            }

            return HtmlResponseClassification.UnknownHtml;
        }

        private static TimeSpan ComputeBackoffDelay(int completedAttempt)
        {
            var multiplier = 1 << (completedAttempt - 1);
            var delay = TimeSpan.FromTicks(BaseRetryDelay.Ticks * multiplier);
            return delay > MaxRetryDelay ? MaxRetryDelay : delay;
        }

        private static string FormatFailureMessage(Exception exception, int finalAttempt, int maxAttempts)
        {
            if (IsPermanentFailure(exception))
            {
                return $"Download failed permanently after {finalAttempt} attempt(s): {exception.Message}";
            }

            return $"Download failed after {finalAttempt}/{maxAttempts} attempts: {exception.Message}";
        }
    }
}
