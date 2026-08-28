using System;
using System.Collections.Generic;
using Chaptarr.Http.REST;
using Newtonsoft.Json;
using NzbDrone.Core.Books;
using NLog;
using Swashbuckle.AspNetCore.Annotations;

namespace Chaptarr.Api.V1.PendingImport
{
    public class PendingAuthorImportResource : RestResource
    {
        public string ProviderId { get; set; }
        public string ProviderPrefix { get; set; }
        public string AuthorName { get; set; }
        
        // Status
        public string AudiobookStatus { get; set; }
        public string EbookStatus { get; set; }
        public string OverallStatus { get; set; }
        
        // Audiobook configuration: the author gate is yes/no; new-item policy is separate.
        public bool? AudiobookMonitored { get; set; }
        public NewItemMonitorTypes? AudiobookMonitorNewItems { get; set; }
        public MonitorTypes? AudiobookMonitorExistingMode { get; set; }
        public int? AudiobookQualityProfileId { get; set; }
        public int? AudiobookMetadataProfileId { get; set; }
        public string AudiobookRootFolderPath { get; set; }
        public List<string> AudiobookBooksToMonitor { get; set; }
        public List<string> AudiobookBooksToSearch { get; set; }
        
        // Ebook configuration: the author gate is yes/no; new-item policy is separate.
        public bool? EbookMonitored { get; set; }
        public NewItemMonitorTypes? EbookMonitorNewItems { get; set; }
        public MonitorTypes? EbookMonitorExistingMode { get; set; }
        public int? EbookQualityProfileId { get; set; }
        public int? EbookMetadataProfileId { get; set; }
        public string EbookRootFolderPath { get; set; }
        public List<string> EbookBooksToMonitor { get; set; }
        public List<string> EbookBooksToSearch { get; set; }
        
        // Common
        public HashSet<int> Tags { get; set; }
        public bool SearchForMissingBooks { get; set; }
        
        // Tracking
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? LastAttemptAt { get; set; }
        public DateTime NextAttemptAt { get; set; }
        [SwaggerSchema("Observational retry count; it is not a terminal-failure threshold.")]
        public int AttemptCount { get; set; }
        [SwaggerSchema("Zero means the transient/not-ready retry lifecycle is unbounded. Typed 404/409 outcomes stop automatic retrying instead.")]
        public int MaxAttempts { get; set; }
        public string LastError { get; set; }
        
        // Source
        public string RequestedBy { get; set; }
        public string SourceApplication { get; set; }
        public string CorrelationId { get; set; }
    }

    public static class PendingAuthorImportResourceMapper
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
        
        private static bool TryDeserializeJson<T>(string json, out T result, string fieldName, int resourceId, int maxJsonLength = 50000, int previewLength = 200)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(json)) return false;
            
            if (json.Length > maxJsonLength)
            {
                _logger.Error("JSON field {Field} for resource {Id} too large ({Len} chars)", fieldName, resourceId, json.Length);
                return false;
            }
            
            try 
            {
                var settings = new JsonSerializerSettings
                {
                    TypeNameHandling = TypeNameHandling.None,
                    MissingMemberHandling = MissingMemberHandling.Ignore,
                    MaxDepth = 64
                };
                
                result = JsonConvert.DeserializeObject<T>(json, settings);
                return true;
            }
            catch (JsonException ex)
            {
                var preview = json.Length > previewLength ? json.Substring(0, previewLength) + "..." : json;
                _logger.Warn(ex, "Failed to deserialize {Field} for resource {Id} (len={Len}) preview='{Preview}'", fieldName, resourceId, json.Length, preview);
                return false;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unexpected error deserializing {Field} for resource {Id}", fieldName, resourceId);
                return false;
            }
        }
        
        private static TEnum ParseEnumOrDefault<TEnum>(string value, TEnum defaultValue, string fieldName, int resourceId) 
            where TEnum : struct, global::System.Enum
        {
            if (string.IsNullOrWhiteSpace(value)) return defaultValue;
            
            // Reject numeric strings - only accept named values
            if (int.TryParse(value.Trim(), out _))
            {
                _logger.Warn("Rejected numeric enum value for {Field} on resource {Id}: '{Value}' - using default '{Default}'.", 
                    fieldName, resourceId, value, defaultValue);
                return defaultValue;
            }
            
            if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var candidate) && Enum.IsDefined(typeof(TEnum), candidate))
                return candidate;
            
            _logger.Warn("Failed to parse enum {Field} for resource {Id}. Value: '{Value}'. Using default '{Default}'.", 
                fieldName, resourceId, value, defaultValue);
            return defaultValue;
        }
        
        public static PendingAuthorImportResource ToResource(this PendingAuthorImport model)
        {
            if (model == null) return null;

            var resource = new PendingAuthorImportResource
            {
                Id = model.Id,
                ProviderId = model.ProviderId,
                ProviderPrefix = model.ProviderPrefix,
                AuthorName = model.AuthorName,
                
                AudiobookStatus = model.AudiobookStatus.ToString(),
                EbookStatus = model.EbookStatus.ToString(),
                OverallStatus = model.OverallStatus.ToString(),
                
                AudiobookMonitored = model.AudiobookMonitored,
                AudiobookMonitorNewItems = model.AudiobookMonitorNewItems,
                AudiobookMonitorExistingMode = model.AudiobookMonitorExistingMode,
                AudiobookQualityProfileId = model.AudiobookQualityProfileId,
                AudiobookMetadataProfileId = model.AudiobookMetadataProfileId,
                AudiobookRootFolderPath = model.AudiobookRootFolderPath,
                
                EbookMonitored = model.EbookMonitored,
                EbookMonitorNewItems = model.EbookMonitorNewItems,
                EbookMonitorExistingMode = model.EbookMonitorExistingMode,
                EbookQualityProfileId = model.EbookQualityProfileId,
                EbookMetadataProfileId = model.EbookMetadataProfileId,
                EbookRootFolderPath = model.EbookRootFolderPath,
                
                SearchForMissingBooks = model.SearchForMissingBooks,
                
                CreatedAt = model.CreatedAt,
                UpdatedAt = model.UpdatedAt,
                LastAttemptAt = model.LastAttemptAt,
                NextAttemptAt = model.NextAttemptAt,
                AttemptCount = model.AttemptCount,
                MaxAttempts = model.MaxAttempts,
                LastError = model.LastError,
                
                RequestedBy = model.RequestedBy,
                SourceApplication = model.SourceApplication,
                CorrelationId = model.CorrelationId
            };

            // Deserialize JSON fields with safe parsing
            if (TryDeserializeJson(model.AudiobookBooksToMonitor, out List<string> audiobookBooks, nameof(model.AudiobookBooksToMonitor), model.Id))
            {
                resource.AudiobookBooksToMonitor = audiobookBooks ?? new List<string>();
            }
            else
            {
                resource.AudiobookBooksToMonitor = new List<string>();
            }

            if (TryDeserializeJson(model.AudiobookBooksToSearch, out List<string> audiobookBooksToSearch, nameof(model.AudiobookBooksToSearch), model.Id))
            {
                resource.AudiobookBooksToSearch = audiobookBooksToSearch ?? new List<string>();
            }
            else
            {
                resource.AudiobookBooksToSearch = new List<string>();
            }

            if (TryDeserializeJson(model.EbookBooksToMonitor, out List<string> ebookBooks, nameof(model.EbookBooksToMonitor), model.Id))
            {
                resource.EbookBooksToMonitor = ebookBooks ?? new List<string>();
            }
            else
            {
                resource.EbookBooksToMonitor = new List<string>();
            }

            if (TryDeserializeJson(model.EbookBooksToSearch, out List<string> ebookBooksToSearch, nameof(model.EbookBooksToSearch), model.Id))
            {
                resource.EbookBooksToSearch = ebookBooksToSearch ?? new List<string>();
            }
            else
            {
                resource.EbookBooksToSearch = new List<string>();
            }

            if (TryDeserializeJson(model.Tags, out HashSet<int> tags, nameof(model.Tags), model.Id))
            {
                resource.Tags = tags ?? new HashSet<int>();
            }
            else
            {
                resource.Tags = new HashSet<int>();
            }

            return resource;
        }

        public static PendingAuthorImport ToModel(this PendingAuthorImportResource resource)
        {
            if (resource == null) return null;

            var model = new PendingAuthorImport
            {
                Id = resource.Id,
                ProviderId = resource.ProviderId,
                ProviderPrefix = resource.ProviderPrefix,
                AuthorName = resource.AuthorName,
                
                AudiobookStatus = ParseEnumOrDefault(resource.AudiobookStatus, PendingImportStatus.NotRequested, nameof(resource.AudiobookStatus), resource.Id),
                EbookStatus = ParseEnumOrDefault(resource.EbookStatus, PendingImportStatus.NotRequested, nameof(resource.EbookStatus), resource.Id),
                OverallStatus = ParseEnumOrDefault(resource.OverallStatus, PendingImportStatus.NotRequested, nameof(resource.OverallStatus), resource.Id),
                
                AudiobookMonitored = resource.AudiobookMonitored,
                AudiobookMonitorNewItems = resource.AudiobookMonitorNewItems,
                AudiobookMonitorExistingMode = resource.AudiobookMonitorExistingMode,
                AudiobookQualityProfileId = resource.AudiobookQualityProfileId,
                AudiobookMetadataProfileId = resource.AudiobookMetadataProfileId,
                AudiobookRootFolderPath = resource.AudiobookRootFolderPath,
                
                EbookMonitored = resource.EbookMonitored,
                EbookMonitorNewItems = resource.EbookMonitorNewItems,
                EbookMonitorExistingMode = resource.EbookMonitorExistingMode,
                EbookQualityProfileId = resource.EbookQualityProfileId,
                EbookMetadataProfileId = resource.EbookMetadataProfileId,
                EbookRootFolderPath = resource.EbookRootFolderPath,
                
                SearchForMissingBooks = resource.SearchForMissingBooks,
                
                CreatedAt = resource.CreatedAt,
                UpdatedAt = resource.UpdatedAt,
                LastAttemptAt = resource.LastAttemptAt,
                NextAttemptAt = resource.NextAttemptAt,
                AttemptCount = resource.AttemptCount,
                MaxAttempts = resource.MaxAttempts,
                LastError = resource.LastError,
                
                RequestedBy = resource.RequestedBy,
                SourceApplication = resource.SourceApplication,
                CorrelationId = resource.CorrelationId
            };

            // Serialize JSON fields
            if (resource.AudiobookBooksToMonitor?.Count > 0)
            {
                model.AudiobookBooksToMonitor = JsonConvert.SerializeObject(resource.AudiobookBooksToMonitor);
            }

            if (resource.EbookBooksToMonitor?.Count > 0)
            {
                model.EbookBooksToMonitor = JsonConvert.SerializeObject(resource.EbookBooksToMonitor);
            }

            if (resource.AudiobookBooksToSearch?.Count > 0)
            {
                model.AudiobookBooksToSearch = JsonConvert.SerializeObject(resource.AudiobookBooksToSearch);
            }

            if (resource.EbookBooksToSearch?.Count > 0)
            {
                model.EbookBooksToSearch = JsonConvert.SerializeObject(resource.EbookBooksToSearch);
            }

            if (resource.Tags?.Count > 0)
            {
                model.Tags = JsonConvert.SerializeObject(resource.Tags);
            }

            return model;
        }
    }
}
