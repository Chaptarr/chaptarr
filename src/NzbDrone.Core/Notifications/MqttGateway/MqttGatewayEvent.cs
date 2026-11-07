using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Notifications.MqttGateway
{
    public sealed class MqttGatewayEvent
    {
        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = 1;

        [JsonPropertyName("event")]
        public string Event { get; init; }

        [JsonPropertyName("eventId")]
        public string EventId { get; init; } = Guid.NewGuid().ToString("N");

        [JsonPropertyName("correlationId")]
        public string CorrelationId { get; init; }

        [JsonPropertyName("occurredAt")]
        public DateTime OccurredAt { get; init; } = DateTime.UtcNow;

        [JsonPropertyName("instance")]
        public MqttGatewayEventInstance Instance { get; init; }

        [JsonPropertyName("author")]
        public MqttGatewayEventAuthor Author { get; init; }

        [JsonPropertyName("book")]
        public MqttGatewayEventBook Book { get; init; }

        [JsonPropertyName("download")]
        public MqttGatewayEventDownload Download { get; init; }

        [JsonPropertyName("series")]
        public MqttGatewayEventSeries Series { get; init; }

        [JsonPropertyName("isUpgrade")]
        public bool? IsUpgrade { get; init; }

        [JsonPropertyName("stage")]
        public string Stage { get; init; }

        public string ToJson()
        {
            return JsonSerializer.Serialize(this, SerializerOptions);
        }

        public static MqttGatewayEvent Create(string eventName,
                                              string instanceId,
                                              string instanceName,
                                              Author author = null,
                                              Book book = null,
                                              Series series = null,
                                              string downloadId = null,
                                              string downloadClient = null,
                                              bool? isUpgrade = null,
                                              string correlationId = null,
                                              string stage = null,
                                              string eventId = null,
                                              DateTime? occurredAt = null)
        {
            return new MqttGatewayEvent
            {
                Event = eventName,
                EventId = eventId ?? Guid.NewGuid().ToString("N"),
                CorrelationId = correlationId ?? Guid.NewGuid().ToString("N"),
                OccurredAt = occurredAt ?? DateTime.UtcNow,
                Instance = new MqttGatewayEventInstance
                {
                    Id = instanceId,
                    Name = instanceName,
                    Version = BuildInfo.Version.ToString()
                },
                Author = author == null ? null : new MqttGatewayEventAuthor { Id = author.Id, Name = author.Name },
                Book = book == null ? null : new MqttGatewayEventBook
                {
                    Id = book.Id,
                    Title = book.Title,
                    MediaType = book.MediaType == BookMediaType.Audiobook ? "audiobook" : "ebook"
                },
                Series = series == null ? null : new MqttGatewayEventSeries
                {
                    Id = series.Id,
                    Title = series.Title,
                    MediaType = series.MediaType == BookMediaType.Audiobook ? "audiobook" : "ebook"
                },
                Download = string.IsNullOrWhiteSpace(downloadId) && string.IsNullOrWhiteSpace(downloadClient)
                    ? null
                    : new MqttGatewayEventDownload { DownloadId = downloadId, Client = downloadClient },
                IsUpgrade = isUpgrade,
                Stage = stage
            };
        }
    }

    public sealed class MqttGatewayEventInstance
    {
        public string Id { get; init; }
        public string Name { get; init; }
        public string Version { get; init; }
    }

    public sealed class MqttGatewayEventAuthor
    {
        public int Id { get; init; }
        public string Name { get; init; }
    }

    public sealed class MqttGatewayEventBook
    {
        public int Id { get; init; }
        public string Title { get; init; }
        public string MediaType { get; init; }
    }

    public sealed class MqttGatewayEventDownload
    {
        public string DownloadId { get; init; }
        public string Client { get; init; }
    }

    public sealed class MqttGatewayEventSeries
    {
        public int Id { get; init; }
        public string Title { get; init; }
        public string MediaType { get; init; }
    }
}
