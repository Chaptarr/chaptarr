using System.Text.Json.Serialization;

namespace NzbDrone.Core.Books
{
    /// <summary>
    /// Lightweight DTO for storing book references from V5 API series responses.
    /// This matches the structure returned by the metadata server's series.books array.
    /// NOTE: BookId contains provider IDs (e.g., "hc:123456") and is used ONLY for
    /// handshaking/matching during import. It is NEVER used for local operations.
    /// </summary>
    public class SeriesBookReference
    {
        [JsonPropertyName("bookId")]
        public string BookId { get; set; } // Provider ID - ONLY for API handshaking

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("position")]
        public string Position { get; set; }

        [JsonPropertyName("coverUrl")]
        public string CoverUrl { get; set; }

        public override string ToString()
        {
            return $"[SeriesBookRef] Title={Title}, Position={Position}";
        }
    }
}
