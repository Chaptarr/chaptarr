using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Http;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.MediaFiles.Commands
{
    public class SendMatchingLogsCommandHandler : IExecute<SendMatchingLogsCommand>
    {
        private readonly IMatchingLogsCollector _matchingLogsCollector;
        private readonly IConfigService _configService;
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        private const int CHUNK_SIZE = 2 * 1024 * 1024; // 2MB chunks
        private const int MAX_UPLOAD_SIZE = 100 * 1024 * 1024; // 100MB max total
        private const int MAX_TAG_VALUE_CHARS = 200;
        private const int MAX_TAG_VALUES_PER_KEY = 3;

        private static readonly HashSet<string> AllowedTagKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Audio canonical keys (TagCanonicalizer)
            "TITLE",
            "ALBUM",
            "ARTIST",
            "ALBUMARTIST",
            "NARRATOR",
            "SERIES",
            "ISBN",
            "ASIN",
            "AUDIBLE_ASIN",
            "PUBLISHER",
            "DATE",
            "ORIGINALDATE",
            "LANGUAGE",
            "TRACKNUMBER",
            "DISCNUMBER",
            "TOTALTRACKS",
            "TOTALDISCS",

            // Ebook field keys (EBookTagService)
            "title",
            "author",
            "authors",
            "all_authors",
            "series",
            "series_index",
            "isbn",
            "asin",
            "publisher",
            "language",
            "date"
        };

        public SendMatchingLogsCommandHandler(
            IMatchingLogsCollector matchingLogsCollector,
            IConfigService configService,
            IHttpClient httpClient,
            Logger logger)
        {
            _matchingLogsCollector = matchingLogsCollector;
            _configService = configService;
            _httpClient = httpClient;
            _logger = logger;
        }

        public void Execute(SendMatchingLogsCommand message)
        {
            try
            {
                ExecuteAsync(message).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to upload matching logs");
                throw;
            }
        }

        private async Task ExecuteAsync(SendMatchingLogsCommand message)
        {
            _logger.Info("Starting matching logs upload process");

            // Step 1: Collect and filter logs
            message.ProgressMessage = "Collecting matching logs...";
            var logs = _matchingLogsCollector.Collect(message);

            if (!logs.Any())
            {
                _logger.Warn("No matching logs found to upload");
                message.ProgressMessage = "No logs found to upload";
                return;
            }

            _logger.Info("Collected {0} log entries for upload", logs.Count);

            // Step 2: Prepare and compress logs
            message.ProgressMessage = "Preparing logs for upload...";
            var compressedData = await PrepareLogsForUpload(logs);

            if (compressedData.Length > MAX_UPLOAD_SIZE)
            {
                throw new InvalidOperationException($"Compressed logs too large: {compressedData.Length / (1024 * 1024)}MB (max: {MAX_UPLOAD_SIZE / (1024 * 1024)}MB)");
            }

            _logger.Info("Compressed {0} log entries to {1} bytes", logs.Count, compressedData.Length);

            // Step 3: Upload via chunked upload
            message.ProgressMessage = "Uploading logs to metadata server...";
            var uploadId = await UploadLogsChunked(compressedData, message);

            _logger.Info("Successfully uploaded matching logs. Upload ID: {0}", uploadId);
            message.ProgressMessage = $"Upload completed! Reference ID: {uploadId}";
        }

        private async Task<byte[]> PrepareLogsForUpload(List<MatchingLogEntry> logs)
        {
            // Create compact log format
            var uploadLogs = BuildUploadPayload(logs);

            // Serialize to JSON
            var json = JsonConvert.SerializeObject(uploadLogs, Formatting.None);
            var jsonBytes = Encoding.UTF8.GetBytes(json);

            // Compress with gzip
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
                {
                    await gzip.WriteAsync(jsonBytes, 0, jsonBytes.Length);
                }
                return output.ToArray();
            }
        }

        public static object BuildUploadPayload(IReadOnlyCollection<MatchingLogEntry> logs)
        {
            return new
            {
                version = 1,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                chaptarr_version = BuildInfo.Version.ToString(),
                total_logs = logs.Count,
                logs = logs.Select(BuildUploadEntry)
            };
        }

        public static object BuildUploadEntry(MatchingLogEntry log)
        {
            return new
            {
                t = log.Timestamp,
                p = log.FileName, // MatchingLogger stores a sanitized path, usually parent folder + filename.
                f = Path.GetFileName(log.FileName), // Keep basename for compatibility with existing upload consumers.
                m = log.MediaType,
                tags = FilterTagsForUpload(log.ExtractedTags),
                result = log.MatchResult
            };
        }

        public static Dictionary<string, List<string>> FilterTagsForUpload(Dictionary<string, List<string>> tags)
        {
            if (tags == null || tags.Count == 0)
            {
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }

            var filtered = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in tags)
            {
                if (string.IsNullOrWhiteSpace(kv.Key))
                {
                    continue;
                }

                if (!AllowedTagKeys.Contains(kv.Key))
                {
                    continue;
                }

                var values = kv.Value ?? new List<string>();
                var cleaned = new List<string>();

                foreach (var v in values)
                {
                    var sanitized = SanitizeTagValue(v, MAX_TAG_VALUE_CHARS);
                    if (!string.IsNullOrWhiteSpace(sanitized))
                    {
                        cleaned.Add(sanitized);
                        if (cleaned.Count >= MAX_TAG_VALUES_PER_KEY)
                        {
                            break;
                        }
                    }
                }

                if (cleaned.Count > 0)
                {
                    filtered[kv.Key] = cleaned;
                }
            }

            return filtered;
        }

        private static string SanitizeTagValue(string value, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            maxChars = Math.Max(1, maxChars);

            var sb = new StringBuilder(Math.Min(value.Length, maxChars));
            var lastWasSpace = false;

            foreach (var ch in value)
            {
                if (char.IsControl(ch) || char.IsWhiteSpace(ch))
                {
                    if (!lastWasSpace)
                    {
                        sb.Append(' ');
                        lastWasSpace = true;
                    }
                }
                else
                {
                    sb.Append(ch);
                    lastWasSpace = false;
                }

                if (sb.Length >= maxChars)
                {
                    break;
                }
            }

            var sanitized = sb.ToString().Trim();
            return sanitized.Length == 0 ? null : sanitized;
        }

        private async Task<string> UploadLogsChunked(byte[] compressedData, SendMatchingLogsCommand message)
        {
            var metadataServerUrl = _configService.MetadataServerUrl;
            if (string.IsNullOrWhiteSpace(metadataServerUrl))
            {
                throw new InvalidOperationException("Metadata server URL is not configured");
            }

            var uploadToken = _configService.MatchingLogsUploadToken?.Trim();
            if (string.IsNullOrWhiteSpace(uploadToken))
            {
                message.ProgressMessage = "Upload failed: matching logs upload token is not configured";
                throw new InvalidOperationException("Matching logs upload token is not configured (MatchingLogsUploadToken)");
            }

            metadataServerUrl = metadataServerUrl.Trim().TrimEnd('/');

            if (!Uri.TryCreate(metadataServerUrl, UriKind.Absolute, out var metadataServerUri))
            {
                message.ProgressMessage = "Upload failed: metadata server URL is invalid";
                throw new InvalidOperationException($"Metadata server URL is invalid: '{metadataServerUrl}'");
            }

            if (!string.Equals(metadataServerUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                !metadataServerUri.IsLoopback)
            {
                message.ProgressMessage = "Upload failed: metadata server URL must be HTTPS";
                throw new InvalidOperationException("Matching logs upload requires an HTTPS metadata server URL (or loopback for dev)");
            }

            // Calculate MD5 hash for integrity verification
            string md5Hash;
            using (var md5 = MD5.Create())
            {
                var hashBytes = md5.ComputeHash(compressedData);
                md5Hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            // Step 1: Initialize upload session
            var initUrl = $"{metadataServerUrl}/api/v5/matching/logs/init";
            var totalChunks = (int)Math.Ceiling((double)compressedData.Length / CHUNK_SIZE);

            _logger.Debug("Initializing upload session for {0} bytes in {1} chunks, MD5: {2}", compressedData.Length, totalChunks, md5Hash);

            var initRequest = new HttpRequestBuilder(initUrl)
                .Accept(HttpAccept.Json)
                .SetHeader("Content-Type", "application/json")
                .SetHeader("X-Chaptarr-Upload-Token", uploadToken)
                .Build();

            initRequest.Method = HttpMethod.Post;
            initRequest.SetContent(JsonConvert.SerializeObject(new
            {
                total_size = compressedData.Length,
                chunk_count = totalChunks,
                content_type = "application/gzip",
                md5_hash = md5Hash
            }));

            var initResponse = await _httpClient.ExecuteAsync(initRequest);
            if (initResponse.StatusCode != System.Net.HttpStatusCode.Created)
            {
                throw new HttpException(initRequest, initResponse);
            }

            var sessionInfo = JsonConvert.DeserializeObject<dynamic>(initResponse.Content);
            string sessionId = sessionInfo.session_id;
            int recommendedChunkSize = sessionInfo.chunk_size ?? CHUNK_SIZE;

            _logger.Info("Upload session initialized: {0}", sessionId);

            // Step 2: Upload chunks
            var actualChunkSize = Math.Min(recommendedChunkSize, CHUNK_SIZE);
            var actualTotalChunks = (int)Math.Ceiling((double)compressedData.Length / actualChunkSize);

            for (int chunkIndex = 0; chunkIndex < actualTotalChunks; chunkIndex++)
            {
                var start = chunkIndex * actualChunkSize;
                var length = Math.Min(actualChunkSize, compressedData.Length - start);
                var chunkData = new byte[length];
                Array.Copy(compressedData, start, chunkData, 0, length);

                var chunkUrl = $"{metadataServerUrl}/api/v5/matching/logs/{sessionId}/chunks/{chunkIndex}";
                var chunkRequest = new HttpRequestBuilder(chunkUrl)
                    .SetHeader("Content-Type", "application/octet-stream")
                    .SetHeader("Content-Length", length.ToString())
                    .SetHeader("X-Chaptarr-Upload-Token", uploadToken)
                    .Build();

                chunkRequest.Method = HttpMethod.Put;
                chunkRequest.SetContent(chunkData);

                var chunkResponse = await _httpClient.ExecuteAsync(chunkRequest);
                if (chunkResponse.StatusCode != System.Net.HttpStatusCode.NoContent)
                {
                    throw new HttpException(chunkRequest, chunkResponse);
                }

                // Update progress
                var progressPercent = (chunkIndex + 1) * 100 / actualTotalChunks;
                message.ProgressMessage = $"Uploading... {progressPercent}% (chunk {chunkIndex + 1}/{actualTotalChunks})";

                _logger.Debug("Uploaded chunk {0}/{1} ({2} bytes)", chunkIndex + 1, actualTotalChunks, length);
            }

            // Step 3: Finalize upload
            var finalizeUrl = $"{metadataServerUrl}/api/v5/matching/logs/{sessionId}/complete";
            var finalizeRequest = new HttpRequestBuilder(finalizeUrl)
                .Accept(HttpAccept.Json)
                .SetHeader("X-Chaptarr-Upload-Token", uploadToken)
                .Build();

            finalizeRequest.Method = HttpMethod.Post;
            finalizeRequest.SetContent("{}");

            var finalizeResponse = await _httpClient.ExecuteAsync(finalizeRequest);
            if (finalizeResponse.StatusCode != System.Net.HttpStatusCode.OK)
            {
                throw new HttpException(finalizeRequest, finalizeResponse);
            }

            var finalResult = JsonConvert.DeserializeObject<dynamic>(finalizeResponse.Content);
            string uploadId = finalResult.upload_id ?? sessionId;

            return uploadId;
        }
    }
}
