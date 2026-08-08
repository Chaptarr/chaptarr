using System;
using System.Linq.Expressions;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Books
{
    public static class AuthorExtensions
    {
        public static bool IsMonitoredFromMediaSettings(this Author author)
        {
            return author.IsMonitoredForMediaType(isAudiobook: true) ||
                   author.IsMonitoredForMediaType(isAudiobook: false);
        }

        public static bool IsMonitoredForMediaType(this Author author, bool isAudiobook)
        {
            if (author == null)
            {
                return false;
            }

            // TRI-STATE + FUTURE monitoring model:
            // - MonitorExisting: 0=None, 1=All, 2=Selected. Any value > 0 means the author is tracked for that media type.
            // - MonitorFuture: true means the author is tracked for that media type (even if existing is None).
            return (author.GetMonitorExistingForMediaType(isAudiobook) ?? 0) > 0 ||
                   (author.GetMonitorFutureForMediaType(isAudiobook) ?? false);
        }

        public static bool IsMonitoredWithAuthor(this Book book)
        {
            if (book?.Author == null)
            {
                return false;
            }

            var isAudiobook = book.MediaType == BookMediaType.Audiobook;
            return book.IsMonitored() && book.Author.IsMonitoredForMediaType(isAudiobook);
        }

        public static Expression<Func<Book, bool>> GetBookMonitoringFilter(BookMediaType? mediaType, bool monitored)
        {
            if (mediaType == BookMediaType.Audiobook)
            {
                return monitored
                    ? book => book.MediaType == BookMediaType.Audiobook &&
                              book.AudiobookMonitored == true &&
                              (book.Author.AudiobookMonitorExisting > 0 ||
                               book.Author.AudiobookMonitorFuture == true)
                    : book => book.MediaType == BookMediaType.Audiobook &&
                              (book.AudiobookMonitored == false ||
                               ((book.Author.AudiobookMonitorExisting == null ||
                                 book.Author.AudiobookMonitorExisting <= 0) &&
                                (book.Author.AudiobookMonitorFuture == null ||
                                 book.Author.AudiobookMonitorFuture == false)));
            }

            if (mediaType == BookMediaType.Ebook)
            {
                return monitored
                    ? book => book.MediaType == BookMediaType.Ebook &&
                              book.EbookMonitored == true &&
                              (book.Author.EbookMonitorExisting > 0 ||
                               book.Author.EbookMonitorFuture == true)
                    : book => book.MediaType == BookMediaType.Ebook &&
                              (book.EbookMonitored == false ||
                               ((book.Author.EbookMonitorExisting == null ||
                                 book.Author.EbookMonitorExisting <= 0) &&
                                (book.Author.EbookMonitorFuture == null ||
                                 book.Author.EbookMonitorFuture == false)));
            }

            return monitored
                ? book => (book.MediaType == BookMediaType.Audiobook &&
                           book.AudiobookMonitored == true &&
                           (book.Author.AudiobookMonitorExisting > 0 ||
                            book.Author.AudiobookMonitorFuture == true)) ||
                          (book.MediaType == BookMediaType.Ebook &&
                           book.EbookMonitored == true &&
                           (book.Author.EbookMonitorExisting > 0 ||
                            book.Author.EbookMonitorFuture == true))
                : book => (book.MediaType == BookMediaType.Audiobook &&
                           (book.AudiobookMonitored == false ||
                            ((book.Author.AudiobookMonitorExisting == null ||
                              book.Author.AudiobookMonitorExisting <= 0) &&
                             (book.Author.AudiobookMonitorFuture == null ||
                              book.Author.AudiobookMonitorFuture == false)))) ||
                          (book.MediaType == BookMediaType.Ebook &&
                           (book.EbookMonitored == false ||
                            ((book.Author.EbookMonitorExisting == null ||
                              book.Author.EbookMonitorExisting <= 0) &&
                             (book.Author.EbookMonitorFuture == null ||
                              book.Author.EbookMonitorFuture == false))));
        }

        public static QualityProfile GetQualityProfileForQuality(this Author author, Quality quality)
        {
            var mediaType = GetEffectiveMediaType(quality);

            if (mediaType == BookMediaType.Audiobook)
            {
                // Return audiobook quality profile if set, otherwise null
                return author?.AudiobookQualityProfile?.Value;
            }
            else
            {
                // Return ebook quality profile if set, otherwise null
                return author?.EbookQualityProfile?.Value;
            }
        }

        public static string GetRootFolderForQuality(this Author author, Quality quality)
        {
            var mediaType = GetEffectiveMediaType(quality);

            if (mediaType == BookMediaType.Audiobook)
            {
                return author.AudiobookRootFolderPath;
            }
            else
            {
                return author.EbookRootFolderPath;
            }
        }

        public static string GetPathForMediaType(this Author author, bool isAudiobook)
        {
            if (isAudiobook)
            {
                return author.AudiobookPath;
            }
            else
            {
                return author.EbookPath;
            }
        }

        public static int? GetMonitorExistingForMediaType(this Author author, bool isAudiobook)
        {
            if (isAudiobook)
            {
                return author.AudiobookMonitorExisting;
            }
            else
            {
                return author.EbookMonitorExisting;
            }
        }

        public static bool? GetMonitorFutureForMediaType(this Author author, bool isAudiobook)
        {
            if (isAudiobook)
            {
                return author.AudiobookMonitorFuture;
            }
            else
            {
                return author.EbookMonitorFuture;
            }
        }

        private static BookMediaType GetEffectiveMediaType(Quality quality)
        {
            if (quality == null || quality == Quality.Unknown)
            {
                return BookMediaType.Ebook;
            }

            return QualityMediaTypeHelper.GetKnownMediaType(quality) ?? BookMediaType.Audiobook;
        }
    }
}
