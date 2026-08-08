using System.Net;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.MetadataSource.BookInfo
{
    public class RefreshResult
    {
        public bool HasChanges => Reason == RefreshReason.Updated;
        public Author Author { get; set; }
        public RefreshReason Reason { get; set; }
        public string ETag { get; set; }
        public HttpStatusCode? HttpStatus { get; set; }
        public string Message { get; set; }

        // Factory methods for clarity
        public static RefreshResult NoChanges(string etag = null) => new RefreshResult
        {
            Reason = RefreshReason.NotModified,
            ETag = etag,
            HttpStatus = HttpStatusCode.NotModified
        };

        public static RefreshResult Updated(Author author, string etag = null) => new RefreshResult
        {
            Author = author,
            Reason = RefreshReason.Updated,
            ETag = etag,
            HttpStatus = HttpStatusCode.OK
        };

        public static RefreshResult Error(string message, HttpStatusCode? status = null) => new RefreshResult
        {
            Reason = RefreshReason.Error,
            Message = message,
            HttpStatus = status
        };

        public static RefreshResult NotFound() => new RefreshResult
        {
            Reason = RefreshReason.NotFound,
            HttpStatus = HttpStatusCode.NotFound
        };
    }

    public enum RefreshReason
    {
        NotModified,
        Updated,
        Error,
        NotFound
    }
}
