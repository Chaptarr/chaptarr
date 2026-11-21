using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Download.Clients.Sabnzbd.Responses;

namespace NzbDrone.Core.Download.Clients.Sabnzbd
{
    public interface ISabnzbdProxy
    {
        string GetBaseUrl(SabnzbdSettings settings, string relativePath = null);
        SabnzbdAddResponse DownloadNzb(byte[] nzbData, string filename, string category, int priority, SabnzbdSettings settings);
        void RemoveFromQueue(string id, bool deleteData, SabnzbdSettings settings);
        void RemoveFromHistory(string id, bool deleteData, bool deletePermanently, SabnzbdSettings settings);
        string GetVersion(SabnzbdSettings settings);
        SabnzbdConfig GetConfig(SabnzbdSettings settings);
        SabnzbdFullStatus GetFullStatus(SabnzbdSettings settings);
        SabnzbdQueue GetQueue(int start, int limit, SabnzbdSettings settings);
        SabnzbdHistory GetHistory(int start, int limit, SabnzbdSettings settings);
        List<OsPath> GetFiles(OsPath path, string title, SabnzbdSettings settings);
        string RetryDownload(string id, SabnzbdSettings settings);
    }

    public class SabnzbdProxy : ISabnzbdProxy
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public SabnzbdProxy(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public string GetBaseUrl(SabnzbdSettings settings, string relativePath = null)
        {
            var baseUrl = HttpRequestBuilder.BuildBaseUrl(settings.UseSsl, settings.Host, settings.Port, settings.UrlBase);
            baseUrl = HttpUri.CombinePath(baseUrl, relativePath);

            return baseUrl;
        }

        public SabnzbdAddResponse DownloadNzb(byte[] nzbData, string filename, string category, int priority, SabnzbdSettings settings)
        {
            var request = BuildRequest("addfile", settings).Post();

            var sabCategory = category;
            if (sabCategory.IsNullOrWhiteSpace())
            {
                sabCategory = settings.MusicCategory;
            }

            if (sabCategory.IsNotNullOrWhiteSpace())
            {
                request.AddQueryParam("cat", sabCategory);
            }
            request.AddQueryParam("priority", priority);

            request.AddFormUpload("name", filename, nzbData, "application/x-nzb");

            if (!Json.TryDeserialize<SabnzbdAddResponse>(ProcessRequest(request, settings), out var response))
            {
                response = new SabnzbdAddResponse();
                response.Status = true;
            }

            return response;
        }

        public void RemoveFromQueue(string id, bool deleteData, SabnzbdSettings settings)
        {
            var request = BuildRequest("queue", settings);
            request.AddQueryParam("name", "delete");
            request.AddQueryParam("del_files", deleteData ? 1 : 0);
            request.AddQueryParam("value", id);

            ProcessRequest(request, settings);
        }

        public void RemoveFromHistory(string id, bool deleteData, bool deletePermanently, SabnzbdSettings settings)
        {
            var request = BuildRequest("history", settings);
            request.AddQueryParam("name", "delete");
            request.AddQueryParam("del_files", deleteData ? 1 : 0);
            request.AddQueryParam("value", id);
            request.AddQueryParam("archive", deletePermanently ? 0 : 1);

            ProcessRequest(request, settings);
        }

        public string GetVersion(SabnzbdSettings settings)
        {
            var request = BuildRequest("version", settings);

            if (!Json.TryDeserialize<SabnzbdVersionResponse>(ProcessRequest(request, settings), out var response))
            {
                response = new SabnzbdVersionResponse();
            }

            return response.Version;
        }

        public SabnzbdConfig GetConfig(SabnzbdSettings settings)
        {
            var request = BuildRequest("get_config", settings);

            var response = Json.Deserialize<SabnzbdConfigResponse>(ProcessRequest(request, settings));

            return response.Config;
        }

        public SabnzbdFullStatus GetFullStatus(SabnzbdSettings settings)
        {
            var request = BuildRequest("fullstatus", settings);
            request.AddQueryParam("skip_dashboard", "1");

            var response = Json.Deserialize<SabnzbdFullStatusResponse>(ProcessRequest(request, settings));

            return response.Status;
        }

        public SabnzbdQueue GetQueue(int start, int limit, SabnzbdSettings settings)
        {
            var request = BuildRequest("queue", settings);
            request.AddQueryParam("start", start);
            request.AddQueryParam("limit", limit);

            var audiobookCategory = settings.AudiobookCategory?.Trim();
            var ebookCategory = settings.EbookCategory?.Trim();

            // SAB only supports filtering by a single category; only apply the filter when both media types are
            // configured to use the same non-empty category (or legacy MusicCategory is in effect via fallback).
            if (audiobookCategory.IsNotNullOrWhiteSpace() &&
                ebookCategory.IsNotNullOrWhiteSpace() &&
                audiobookCategory.Equals(ebookCategory, StringComparison.InvariantCultureIgnoreCase))
            {
                request.AddQueryParam("category", audiobookCategory);
            }

            var response = ProcessRequest(request, settings);

            return Json.Deserialize<SabnzbdQueue>(JObject.Parse(response).SelectToken("queue").ToString());
        }

        public SabnzbdHistory GetHistory(int start, int limit, SabnzbdSettings settings)
        {
            var request = BuildRequest("history", settings);
            request.AddQueryParam("start", start);
            request.AddQueryParam("limit", limit);

            var audiobookCategory = settings.AudiobookCategory?.Trim();
            var ebookCategory = settings.EbookCategory?.Trim();

            // SAB only supports filtering by a single category; only apply the filter when both media types are
            // configured to use the same non-empty category (or legacy MusicCategory is in effect via fallback).
            if (audiobookCategory.IsNotNullOrWhiteSpace() &&
                ebookCategory.IsNotNullOrWhiteSpace() &&
                audiobookCategory.Equals(ebookCategory, StringComparison.InvariantCultureIgnoreCase))
            {
                request.AddQueryParam("category", audiobookCategory);
            }

            var response = ProcessRequest(request, settings);

            return Json.Deserialize<SabnzbdHistory>(JObject.Parse(response).SelectToken("history").ToString());
        }

        public List<OsPath> GetFiles(OsPath path, string title, SabnzbdSettings settings)
        {
            var timer = Stopwatch.StartNew();
            var pending = new Queue<OsPath>();
            var visited = new HashSet<OsPath>();
            var files = new HashSet<OsPath>();
            pending.Enqueue(path);

            while (pending.TryDequeue(out var directory))
            {
                if (!visited.Add(directory.AsDirectory()))
                {
                    continue;
                }

                var request = BuildRequest("browse", settings)
                    .AddQueryParam("name", directory.FullPath)
                    .AddQueryParam("show_files", 1)
                    .AddQueryParam("show_hidden_folders", 1);
                var entries = JObject.Parse(ProcessRequest(request, settings))["paths"] as JArray
                    ?? throw new DownloadClientException("SABnzbd did not return a directory listing for {0}", directory);
                var currentPath = new OsPath(entries.FirstOrDefault(entry => entry["current_path"] != null)?.Value<string>("current_path"));

                // SAB browses the parent for both a single file and a missing path.
                // Only an exact file entry proves that this is the single-file case.
                if (currentPath.AsDirectory() != directory.AsDirectory())
                {
                    if (directory == path && currentPath.AsDirectory() == path.Directory &&
                        entries.Any(entry => entry.Value<bool?>("dir") == false && new OsPath(entry.Value<string>("path")) == path))
                    {
                        return new List<OsPath> { path };
                    }

                    throw new DownloadClientException("SABnzbd returned a different directory for {0}: {1}", directory, currentPath);
                }

                // Sorting can place several jobs in a shared directory. Its listing
                // cannot establish this job's inventory; keep the existing fallback.
                if (directory == path && !string.Equals(path.FileName, title, StringComparison.OrdinalIgnoreCase))
                {
                    throw new DownloadClientException("SABnzbd output is not a per-job directory: {0}", path);
                }

                foreach (var entry in entries.Where(entry => entry["path"] != null && entry.Value<string>("name") != ".."))
                {
                    var child = new OsPath(entry.Value<string>("path"));
                    if (!child.IsRooted || child.Directory != directory.AsDirectory() || entry.Value<bool?>("dir") == null)
                    {
                        throw new DownloadClientException("SABnzbd returned an invalid child of {0}", directory);
                    }

                    if (entry.Value<bool>("dir"))
                    {
                        pending.Enqueue(child);
                    }
                    else
                    {
                        files.Add(child);
                    }
                }
            }

            _logger.Debug("SABnzbd final output listing: {0} files in {1} directories, {2} ms", files.Count, visited.Count, timer.ElapsedMilliseconds);
            return files.ToList();
        }

        public string RetryDownload(string id, SabnzbdSettings settings)
        {
            var request = BuildRequest("retry", settings);
            request.AddQueryParam("value", id);

            if (!Json.TryDeserialize<SabnzbdRetryResponse>(ProcessRequest(request, settings), out var response))
            {
                response = new SabnzbdRetryResponse();
                response.Status = true;
            }

            return response.Id;
        }

        private HttpRequestBuilder BuildRequest(string mode, SabnzbdSettings settings)
        {
            var baseUrl = GetBaseUrl(settings, "api");

            var requestBuilder = new HttpRequestBuilder(baseUrl)
                .Accept(HttpAccept.Json)
                .AddQueryParam("mode", mode);

            requestBuilder.LogResponseContent = true;

            if (settings.ApiKey.IsNotNullOrWhiteSpace())
            {
                requestBuilder.AddSuffixQueryParam("apikey", settings.ApiKey);
            }
            else
            {
                requestBuilder.AddSuffixQueryParam("ma_username", settings.Username);
                requestBuilder.AddSuffixQueryParam("ma_password", settings.Password);
            }

            requestBuilder.AddSuffixQueryParam("output", "json");

            return requestBuilder;
        }

        private string ProcessRequest(HttpRequestBuilder requestBuilder, SabnzbdSettings settings)
        {
            var httpRequest = requestBuilder.Build();

            HttpResponse response;

            _logger.Debug("Url: {0}", CleanseLogMessage.Cleanse(httpRequest.Url.ToString()));

            try
            {
                response = _httpClient.Execute(httpRequest);
            }
            catch (HttpException ex)
            {
                throw new DownloadClientException("Unable to connect to SABnzbd, {0}", ex, ex.Message);
            }
            catch (HttpRequestException ex)
            {
                throw new DownloadClientUnavailableException("Unable to connect to SABnzbd, {0}", ex, ex.Message);
            }
            catch (WebException ex)
            {
                if (ex.Status == WebExceptionStatus.TrustFailure)
                {
                    throw new DownloadClientUnavailableException("Unable to connect to SABnzbd, certificate validation failed.", ex);
                }

                throw new DownloadClientUnavailableException("Unable to connect to SABnzbd, {0}", ex, ex.Message);
            }

            CheckForError(response);

            return response.Content;
        }

        private void CheckForError(HttpResponse response)
        {
            if (!Json.TryDeserialize<SabnzbdJsonError>(response.Content, out var result))
            {
                //Handle plain text responses from SAB
                result = new SabnzbdJsonError();

                if (response.Content.StartsWith("error", StringComparison.InvariantCultureIgnoreCase))
                {
                    result.Status = "false";
                    result.Error = response.Content.Replace("error: ", "");
                }
                else
                {
                    result.Status = "true";
                }

                result.Error = response.Content.Replace("error: ", "");
            }

            if (result.Failed)
            {
                throw new DownloadClientException("Error response received from SABnzbd: {0}", result.Error);
            }
        }
    }
}
