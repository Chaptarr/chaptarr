using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using SystemTextJsonIgnoreAttribute = System.Text.Json.Serialization.JsonIgnoreAttribute;
using SystemTextJsonIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition;

namespace Chaptarr.Api.V1.PendingImport
{
    public class PendingAuthorExistenceResource
    {
        public bool Exists { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        [SystemTextJsonIgnore(Condition = SystemTextJsonIgnoreCondition.WhenWritingNull)]
        public int? AuthorId { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        [SystemTextJsonIgnore(Condition = SystemTextJsonIgnoreCondition.WhenWritingNull)]
        public string AuthorName { get; set; }
        public bool Pending { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        [SystemTextJsonIgnore(Condition = SystemTextJsonIgnoreCondition.WhenWritingNull)]
        public int? PendingId { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        [SystemTextJsonIgnore(Condition = SystemTextJsonIgnoreCondition.WhenWritingNull)]
        public string Status { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        [SystemTextJsonIgnore(Condition = SystemTextJsonIgnoreCondition.WhenWritingNull)]
        public DateTime? NextAttempt { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        [SystemTextJsonIgnore(Condition = SystemTextJsonIgnoreCondition.WhenWritingNull)]
        public int? AttemptCount { get; set; }
    }

    public class PendingImportProfileOptionsResource
    {
        public PendingImportMediaProfileOptionsResource Audiobook { get; set; }
        public PendingImportMediaProfileOptionsResource Ebook { get; set; }
        public PendingImportMediaProfileOptionsResource All { get; set; }
    }

    public class PendingImportMediaProfileOptionsResource
    {
        public List<PendingImportProfileOptionResource> QualityProfiles { get; set; } = new List<PendingImportProfileOptionResource>();
        public List<PendingImportProfileOptionResource> MetadataProfiles { get; set; } = new List<PendingImportProfileOptionResource>();
        public List<PendingImportRootFolderOptionResource> RootFolders { get; set; } = new List<PendingImportRootFolderOptionResource>();
    }

    public class PendingImportProfileOptionResource
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    public class PendingImportRootFolderOptionResource
    {
        public string Path { get; set; }
        public string Name { get; set; }
    }

    public class QueueAuthorResponseResource
    {
        public int PendingId { get; set; }
        public string Message { get; set; }
        public string ProviderId { get; set; }
        public string Status { get; set; }
    }
}
