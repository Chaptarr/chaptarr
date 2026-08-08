using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Download;

namespace Chaptarr.Api.V1.DownloadClient
{
    public class DownloadClientBulkResource : ProviderBulkResource<DownloadClientBulkResource>
    {
        public bool? Enable { get; set; }
        public int? Priority { get; set; }
        public bool? RemoveCompletedDownloads { get; set; }
        public bool? RemoveFailedDownloads { get; set; }
    }

    public class DownloadClientBulkResourceMapper : ProviderBulkResourceMapper<DownloadClientBulkResource, DownloadClientDefinition>
    {
        public override List<DownloadClientDefinition> UpdateModel(DownloadClientBulkResource resource, List<DownloadClientDefinition> existingDefinitions)
        {
            if (resource == null)
            {
                return new List<DownloadClientDefinition>();
            }

            existingDefinitions.ForEach(existing =>
            {
                existing.Enable = resource.Enable ?? existing.Enable;
                existing.Priority = resource.Priority ?? existing.Priority;
                existing.RemoveCompletedDownloads = resource.RemoveCompletedDownloads ?? existing.RemoveCompletedDownloads;
                existing.RemoveFailedDownloads = resource.RemoveFailedDownloads ?? existing.RemoveFailedDownloads;

                if (resource.Tags != null)
                {
                    existing.AudiobookTags ??= new HashSet<int>();
                    existing.EbookTags ??= new HashSet<int>();

                    switch (resource.ApplyTags)
                    {
                        case ApplyTags.Add:
                            resource.Tags.ForEach(t =>
                            {
                                existing.AudiobookTags.Add(t);
                                existing.EbookTags.Add(t);
                            });
                            break;
                        case ApplyTags.Remove:
                            resource.Tags.ForEach(t =>
                            {
                                existing.AudiobookTags.Remove(t);
                                existing.EbookTags.Remove(t);
                            });
                            break;
                        case ApplyTags.Replace:
                            existing.AudiobookTags = new HashSet<int>(resource.Tags);
                            existing.EbookTags = new HashSet<int>(resource.Tags);
                            break;
                    }

                    existing.Tags = existing.AudiobookTags.Concat(existing.EbookTags).ToHashSet();
                }
            });

            return existingDefinitions;
        }
    }
}
