using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Chaptarr.Http.REST;
using NzbDrone.Core.RemotePathMappings;

namespace Chaptarr.Api.V1.RemotePathMappings
{
    public class RemotePathMappingResource : RestResource
    {
        [JsonRequired]
        public int DownloadClientId { get; set; }
        public string Host { get; set; }
        public string RemotePath { get; set; }
        public string LocalPath { get; set; }
    }

    public class RemotePathMappingTestResource
    {
        public int DownloadClientId { get; set; }
        public string Host { get; set; }
        public string RemotePath { get; set; }
        public string LocalPath { get; set; }
        public string MappedPath { get; set; }
        public bool IsMapped { get; set; }
        public bool LocalPathExists { get; set; }
        public bool LocalPathWritable { get; set; }
        public bool MappedPathExists { get; set; }
        public bool MappedPathWritable { get; set; }
        public bool DownloadClientPathChecked { get; set; }
        public bool DownloadClientPathMatched { get; set; }
        public string DownloadClientMatchedPath { get; set; }
        public bool DownloadClientItemPathChecked { get; set; }
        public string DownloadClientItemMappedPath { get; set; }
        public bool DownloadClientItemPathExists { get; set; }
        public bool DownloadClientItemPathWritable { get; set; }
        public string DownloadClientTestError { get; set; }
    }

    public class RemotePathMappingSuggestionsResource
    {
        public List<string> DownloadClientPaths { get; set; } = new List<string>();
        public List<string> ChaptarrPaths { get; set; } = new List<string>();
        public string DownloadClientError { get; set; }
    }

    public static class RemotePathMappingResourceMapper
    {
        public static RemotePathMappingResource ToResource(this RemotePathMapping model)
        {
            if (model == null)
            {
                return null;
            }

            return new RemotePathMappingResource
            {
                Id = model.Id,

                DownloadClientId = model.DownloadClientId,
                Host = model.Host,
                RemotePath = model.RemotePath,
                LocalPath = model.LocalPath
            };
        }

        public static RemotePathMapping ToModel(this RemotePathMappingResource resource)
        {
            if (resource == null)
            {
                return null;
            }

            return new RemotePathMapping
            {
                Id = resource.Id,

                DownloadClientId = resource.DownloadClientId,
                Host = resource.Host,
                RemotePath = resource.RemotePath,
                LocalPath = resource.LocalPath
            };
        }

        public static List<RemotePathMappingResource> ToResource(this IEnumerable<RemotePathMapping> models)
        {
            return models.Select(ToResource).ToList();
        }

        public static RemotePathMapping ToModel(this RemotePathMappingTestResource resource)
        {
            if (resource == null)
            {
                return null;
            }

            return new RemotePathMapping
            {
                DownloadClientId = resource.DownloadClientId,
                Host = resource.Host,
                RemotePath = resource.RemotePath,
                LocalPath = resource.LocalPath
            };
        }

        public static RemotePathMappingTestResource ToResource(this RemotePathMappingTestResult model)
        {
            if (model == null)
            {
                return null;
            }

            return new RemotePathMappingTestResource
            {
                DownloadClientId = model.DownloadClientId,
                Host = model.Host,
                RemotePath = model.RemotePath,
                LocalPath = model.LocalPath,
                MappedPath = model.MappedPath,
                IsMapped = model.IsMapped,
                LocalPathExists = model.LocalPathExists,
                LocalPathWritable = model.LocalPathWritable,
                MappedPathExists = model.MappedPathExists,
                MappedPathWritable = model.MappedPathWritable
            };
        }
    }
}
