using System;
using System.Linq.Expressions;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Books
{
    public static class AuthorExtensions
    {
        public static AuthorStatusType GetLifeStatus(DateTime? deathDate, DateTime? utcNow = null)
        {
            return deathDate.HasValue && deathDate.Value.Date <= (utcNow ?? DateTime.UtcNow).Date
                ? AuthorStatusType.Ended
                : AuthorStatusType.Continuing;
        }

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

            return author.GetMonitoredForMediaType(isAudiobook) == true;
        }

        public static bool IsMonitoredWithAuthor(this Book book)
        {
            return book.IsMonitoredWithAuthor(book?.Author);
        }

        public static bool IsMonitoredWithAuthor(this Book book, Author author)
        {
            if (book == null || author == null)
            {
                return false;
            }

            var isAudiobook = book.MediaType == BookMediaType.Audiobook;
            return book.IsMonitored() && author.IsMonitoredForMediaType(isAudiobook);
        }

        public static Expression<Func<Book, bool>> GetBookMonitoringFilter(BookMediaType? mediaType, bool monitored)
        {
            if (mediaType == BookMediaType.Audiobook)
            {
                return monitored
                    ? book => book.MediaType == BookMediaType.Audiobook &&
                              book.AudiobookMonitored == true &&
                              book.Author.AudiobookMonitored == true
                    : book => book.MediaType == BookMediaType.Audiobook &&
                              (book.AudiobookMonitored == false ||
                               book.Author.AudiobookMonitored == false ||
                               book.Author.AudiobookMonitored == null);
            }

            if (mediaType == BookMediaType.Ebook)
            {
                return monitored
                    ? book => book.MediaType == BookMediaType.Ebook &&
                              book.EbookMonitored == true &&
                              book.Author.EbookMonitored == true
                    : book => book.MediaType == BookMediaType.Ebook &&
                              (book.EbookMonitored == false ||
                               book.Author.EbookMonitored == false ||
                               book.Author.EbookMonitored == null);
            }

            return monitored
                ? book => (book.MediaType == BookMediaType.Audiobook &&
                           book.AudiobookMonitored == true &&
                           book.Author.AudiobookMonitored == true) ||
                          (book.MediaType == BookMediaType.Ebook &&
                           book.EbookMonitored == true &&
                           book.Author.EbookMonitored == true)
                : book => (book.MediaType == BookMediaType.Audiobook &&
                           (book.AudiobookMonitored == false ||
                            book.Author.AudiobookMonitored == false ||
                            book.Author.AudiobookMonitored == null)) ||
                          (book.MediaType == BookMediaType.Ebook &&
                           (book.EbookMonitored == false ||
                            book.Author.EbookMonitored == false ||
                            book.Author.EbookMonitored == null));
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

        public static bool? GetMonitoredForMediaType(this Author author, bool isAudiobook)
        {
            return isAudiobook ? author?.AudiobookMonitored : author?.EbookMonitored;
        }

        public static NewItemMonitorTypes? GetMonitorNewItemsForMediaType(this Author author, bool isAudiobook)
        {
            return isAudiobook ? author?.AudiobookMonitorNewItems : author?.EbookMonitorNewItems;
        }

        public static bool ApplyMediaTypeMonitoringSettings(
            this Author author,
            BookMediaType mediaType,
            bool? monitored,
            NewItemMonitorTypes? monitorNewItems)
        {
            if (author == null)
            {
                return false;
            }

            var changed = false;
            if (mediaType == BookMediaType.Audiobook)
            {
                if (monitored.HasValue && author.AudiobookMonitored != monitored)
                {
                    author.AudiobookMonitored = monitored;
                    changed = true;
                }

                if (monitorNewItems.HasValue && author.AudiobookMonitorNewItems != monitorNewItems)
                {
                    author.AudiobookMonitorNewItems = monitorNewItems;
                    changed = true;
                }
            }
            else if (mediaType == BookMediaType.Ebook)
            {
                if (monitored.HasValue && author.EbookMonitored != monitored)
                {
                    author.EbookMonitored = monitored;
                    changed = true;
                }

                if (monitorNewItems.HasValue && author.EbookMonitorNewItems != monitorNewItems)
                {
                    author.EbookMonitorNewItems = monitorNewItems;
                    changed = true;
                }
            }

            if (changed)
            {
                author.Monitored = author.IsMonitoredFromMediaSettings();
            }

            return changed;
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
