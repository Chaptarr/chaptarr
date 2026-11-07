using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Exceptions
{
    public sealed class AmbiguousLocalBookMatchException : Exception
    {
        public string WorkProviderId { get; }
        public BookMediaType MediaType { get; }
        public IReadOnlyList<Book> Candidates { get; }

        public AmbiguousLocalBookMatchException(
            string workProviderId,
            BookMediaType mediaType,
            IEnumerable<Book> candidates)
            : base($"Several local {GetMediaTypeLabel(mediaType)} records match the selected work. Open the intended record below, then request it from that record.")
        {
            WorkProviderId = workProviderId;
            MediaType = mediaType;
            Candidates = (candidates ?? Enumerable.Empty<Book>()).Where(book => book != null).ToList();
        }

        private static string GetMediaTypeLabel(BookMediaType mediaType)
        {
            return mediaType == BookMediaType.Audiobook ? "audiobook" : "ebook";
        }
    }
}
