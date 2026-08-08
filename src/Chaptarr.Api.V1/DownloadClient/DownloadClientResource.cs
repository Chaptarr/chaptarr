using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;

namespace Chaptarr.Api.V1.DownloadClient
{
    public class DownloadClientResource : ProviderResource<DownloadClientResource>
    {
        public bool Enable { get; set; }
        public DownloadProtocol Protocol { get; set; }
        public int Priority { get; set; }
        public HashSet<int> AudiobookTags { get; set; }
        public HashSet<int> EbookTags { get; set; }
        public bool RemoveCompletedDownloads { get; set; }
        public bool RemoveFailedDownloads { get; set; }
        public bool CopyUnmanagedDownloads { get; set; }
    }

    public class DownloadClientResourceMapper : ProviderResourceMapper<DownloadClientResource, DownloadClientDefinition>
    {
        public override DownloadClientResource ToResource(DownloadClientDefinition definition)
        {
            if (definition == null)
            {
                return null;
            }

            var resource = base.ToResource(definition);

            resource.Enable = definition.Enable;
            resource.Protocol = definition.Protocol;
            resource.Priority = definition.Priority;
            resource.AudiobookTags = definition.AudiobookTags ?? new HashSet<int>();
            resource.EbookTags = definition.EbookTags ?? new HashSet<int>();
            resource.Tags = resource.AudiobookTags.Concat(resource.EbookTags).ToHashSet();
            resource.RemoveCompletedDownloads = definition.RemoveCompletedDownloads;
            resource.RemoveFailedDownloads = definition.RemoveFailedDownloads;
            resource.CopyUnmanagedDownloads = definition.CopyUnmanagedDownloads;

            return resource;
        }

        public override DownloadClientDefinition ToModel(DownloadClientResource resource)
        {
            if (resource == null)
            {
                return null;
            }

            var definition = base.ToModel(resource);

            definition.Enable = resource.Enable;
            definition.Protocol = resource.Protocol;
            definition.Priority = resource.Priority;
            if (resource.AudiobookTags == null && resource.EbookTags == null && resource.Tags is { Count: > 0 })
            {
                definition.AudiobookTags = new HashSet<int>(resource.Tags);
                definition.EbookTags = new HashSet<int>(resource.Tags);
            }
            else
            {
                definition.AudiobookTags = resource.AudiobookTags ?? new HashSet<int>();
                definition.EbookTags = resource.EbookTags ?? new HashSet<int>();
            }

            definition.Tags = definition.AudiobookTags.Concat(definition.EbookTags).ToHashSet();
            definition.RemoveCompletedDownloads = resource.RemoveCompletedDownloads;
            definition.RemoveFailedDownloads = resource.RemoveFailedDownloads;
            definition.CopyUnmanagedDownloads = resource.CopyUnmanagedDownloads;

            return definition;
        }
    }
}
