using System;
using Chaptarr.Http.REST;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Indexers;

namespace Chaptarr.Api.V1.Ignored
{
    public class IgnoredDownloadResource : RestResource
    {
        public int AuthorId { get; set; }
        public int BookId { get; set; }
        public string DownloadId { get; set; }
        public string SourceTitle { get; set; }
        public DateTime Date { get; set; }
        public DownloadProtocol Protocol { get; set; }
        public int DownloadClientId { get; set; }
        public string DownloadClient { get; set; }
        public string DownloadClientName { get; set; }
        public bool IsInClient { get; set; }
    }

    public static class IgnoredDownloadResourceMapper
    {
        public static IgnoredDownloadResource ToResource(this DownloadHistory model, bool isInClient)
        {
            if (model == null)
            {
                return null;
            }

            model.Data ??= new global::System.Collections.Generic.Dictionary<string, string>();

            model.Data.TryGetValue("DownloadClient", out var downloadClient);
            model.Data.TryGetValue("DownloadClientName", out var downloadClientName);

            return new IgnoredDownloadResource
            {
                Id = model.Id,
                AuthorId = model.AuthorId,
                BookId = model.BookId,
                DownloadId = model.DownloadId,
                SourceTitle = model.SourceTitle,
                Date = model.Date,
                Protocol = model.Protocol,
                DownloadClientId = model.DownloadClientId,
                DownloadClient = downloadClient,
                DownloadClientName = downloadClientName,
                IsInClient = isInClient
            };
        }
    }
}
