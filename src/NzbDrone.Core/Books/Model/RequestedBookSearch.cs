using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace NzbDrone.Core.Books
{
    public sealed class RequestedBookSearch
    {
        public BookMediaType MediaType { get; set; }
        public string WorkProviderId { get; set; }
        public string EditionProviderId { get; set; }

        public bool Matches(BookMediaType mediaType, string workProviderId, string editionProviderId) =>
            MediaType == mediaType &&
            string.Equals(WorkProviderId, workProviderId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(EditionProviderId, editionProviderId, StringComparison.OrdinalIgnoreCase);

        public bool Matches(RequestedBookSearch other) => Matches(other.MediaType, other.WorkProviderId, other.EditionProviderId);

        public static List<RequestedBookSearch> Read(string json) => string.IsNullOrEmpty(json)
            ? new() : JsonConvert.DeserializeObject<List<RequestedBookSearch>>(json) ?? new();

        public static string Write(IEnumerable<RequestedBookSearch> requests) => JsonConvert.SerializeObject(requests
            .Where(request => request != null && !string.IsNullOrWhiteSpace(request.WorkProviderId))
            .DistinctBy(request => (request.MediaType, request.WorkProviderId.ToLowerInvariant(), request.EditionProviderId?.ToLowerInvariant())));
    }
}
