using System;
using System.Collections.Generic;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.DirectDownload
{
    public enum DirectDownloadSourceFamily
    {
        CatalogPage,
        MirrorIndex
    }

    public sealed class DirectDownloadProbeRequest
    {
        public IReadOnlyList<string> SourceUrls { get; set; } = Array.Empty<string>();

        public string ApiKey { get; set; }

        public string Author { get; set; }

        public string Title { get; set; }

        public string Isbn { get; set; }

        public TimeSpan RequestTimeout { get; set; }

        public int MaxResponseBytes { get; set; } = 256 * 1024;
    }

    public sealed class DirectDownloadProbeResult
    {
        public string SelectedSourceUrl { get; set; }

        public DirectDownloadSourceFamily SelectedFamily { get; set; }

        public IReadOnlyList<ReleaseInfo> Releases { get; set; } = Array.Empty<ReleaseInfo>();
    }

    public enum ApiKeyValidationOutcome
    {
        EmptyKey,
        Valid,
        InvalidOrExpired,
        NoDownloadsRemaining,
        TransientFailure
    }

    public sealed class ApiKeyValidationResult
    {
        public ApiKeyValidationResult(ApiKeyValidationOutcome outcome, string message)
        {
            Outcome = outcome;
            Message = message;
        }

        public ApiKeyValidationOutcome Outcome { get; }

        public string Message { get; }

        public static ApiKeyValidationResult Empty() =>
            new(ApiKeyValidationOutcome.EmptyKey, "No API key configured. Using public download links only.");

        public static ApiKeyValidationResult Valid() =>
            new(ApiKeyValidationOutcome.Valid, "API key is valid.");

        public static ApiKeyValidationResult InvalidOrExpired(string detail = null) =>
            new(ApiKeyValidationOutcome.InvalidOrExpired, detail ?? "API key is invalid or expired.");

        public static ApiKeyValidationResult NoDownloadsRemaining(string detail = null) =>
            new(ApiKeyValidationOutcome.NoDownloadsRemaining, detail ?? "API key has no downloads remaining. Please wait for the quota to reset or configure an additional source URL.");

        public static ApiKeyValidationResult TransientFailure(string detail = null) =>
            new(ApiKeyValidationOutcome.TransientFailure, detail ?? "Could not reach the provider API. Try again later.");
    }

    public sealed class DirectDownloadProbeException : Exception
    {
        public DirectDownloadProbeException(string message)
            : base(message)
        {
        }

        public DirectDownloadProbeException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
