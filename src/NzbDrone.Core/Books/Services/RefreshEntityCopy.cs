using System;
using System.Collections.Generic;
using System.Linq;
using MediaCoverModel = NzbDrone.Core.MediaCover.MediaCover;

namespace NzbDrone.Core.Books
{
    internal static class RefreshEntityCopy
    {
        public static Edition CloneEdition(Edition source)
        {
            if (source == null)
            {
                return null;
            }

            return new Edition
            {
                // Identity / provider identifiers (DB Id intentionally not copied)
                BookId = source.BookId,
                ForeignEditionId = source.ForeignEditionId,
                TitleSlug = source.TitleSlug,

                // ISBNs and identifiers
                Isbn13 = source.Isbn13,
                Isbn10 = source.Isbn10,
                Asin = source.Asin,
                Asins = source.Asins?.ToList() ?? new List<string>(),

                // Metadata
                Title = source.Title,
                Subtitle = source.Subtitle,
                MatchingTitle = source.MatchingTitle,
                Language = source.Language,
                Overview = source.Overview,
                Format = source.Format,
                IsEbook = source.IsEbook,
                Disambiguation = source.Disambiguation,
                Publisher = source.Publisher,
                PageCount = source.PageCount,
                ReleaseDate = source.ReleaseDate,
                Images = source.Images?.Select(i => new MediaCoverModel
                {
                    Url = i.Url,
                    CoverType = i.CoverType,
                    Hash = i.Hash
                }).ToList() ?? new List<MediaCoverModel>(),
                Links = source.Links?.Select(l => new Links
                {
                    Url = l.Url,
                    Name = l.Name
                }).ToList() ?? new List<Links>(),
                Ratings = source.Ratings == null ? new Ratings() : new Ratings
                {
                    Votes = source.Ratings.Votes,
                    Value = source.Ratings.Value
                },

                // Provider IDs
                GoodreadsEditionId = source.GoodreadsEditionId,
                HardcoverEditionId = source.HardcoverEditionId,
                OpenLibraryEditionId = source.OpenLibraryEditionId,

                // Format identification
                ReadingFormatId = source.ReadingFormatId,
                EditionFormat = source.EditionFormat,
                EditionInfo = source.EditionInfo,

                // Audiobook specific
                DurationSeconds = source.DurationSeconds,
                ChapterCount = source.ChapterCount,
                HasChapters = source.HasChapters,
                Chapters = source.Chapters?.Select(c => new EditionChapter
                {
                    Title = c?.Title,
                    StartOffsetMs = c?.StartOffsetMs ?? 0,
                    StartOffsetSec = c?.StartOffsetSec ?? 0,
                    LengthMs = c?.LengthMs ?? 0
                }).ToList() ?? new List<EditionChapter>(),

                // Classification
                IsGraphicAudio = source.IsGraphicAudio,
                AudioProductionType = source.AudioProductionType,

                // Narrator info
                Narrator = source.Narrator,
                NarratorNames = source.NarratorNames?.ToList() ?? new List<string>(),
                NarratorCredits = source.NarratorCredits?.Select(c => new NarratorCredit
                {
                    Name = c?.Name,
                    GoodreadsNarratorId = c?.GoodreadsNarratorId,
                    HardcoverNarratorId = c?.HardcoverNarratorId,
                    Order = c?.Order ?? 0,
                    IsPrimary = c?.IsPrimary ?? false,
                    Role = c?.Role ?? "Narrator"
                }).ToList() ?? new List<NarratorCredit>(),

                // New provider and metadata fields
                AudibleASIN = source.AudibleASIN,
                GoogleBooksEditionId = source.GoogleBooksEditionId,
                ReviewCount = source.ReviewCount,
                ProviderUrls = source.ProviderUrls == null ? new ProviderUrlMap() : new ProviderUrlMap(source.ProviderUrls),
                LastUpdated = source.LastUpdated,

                // Local state/config
                Monitored = source.Monitored,
                ManualAdd = source.ManualAdd,
                IsFallbackEdition = source.IsFallbackEdition
            };
        }

        public static Book CloneBook(Book source, bool includeEditions)
        {
            if (source == null)
            {
                return null;
            }

            var clone = new Book
            {
                // Identity / provider identifiers (DB Id intentionally not copied)
                ForeignEditionId = source.ForeignEditionId,
                TitleSlug = source.TitleSlug,

                // Core metadata
                Title = source.Title,
                Subtitle = source.Subtitle,
                OriginalTitle = source.OriginalTitle,
                Overview = source.Overview,
                ReleaseDate = source.ReleaseDate,
                Links = source.Links?.Select(l => new Links { Url = l.Url, Name = l.Name }).ToList() ?? new List<Links>(),
                Genres = source.Genres?.ToList() ?? new List<string>(),
                RelatedBooks = source.RelatedBooks?.ToList() ?? new List<int>(),
                Ratings = source.Ratings == null ? new Ratings() : new Ratings { Votes = source.Ratings.Votes, Value = source.Ratings.Value },
                Images = source.Images?.Select(i => new MediaCoverModel { Url = i.Url, CoverType = i.CoverType, Hash = i.Hash }).ToList() ?? new List<MediaCoverModel>(),

                // Provider IDs
                GoodreadsBookId = source.GoodreadsBookId,
                GoodreadsWorkId = source.GoodreadsWorkId,
                HardcoverBookId = source.HardcoverBookId,
                ISBN10 = source.ISBN10,
                ISBN13 = source.ISBN13,
                OpenLibraryEditionId = source.OpenLibraryEditionId,
                OpenLibraryWorkId = source.OpenLibraryWorkId,
                GoogleBooksId = source.GoogleBooksId,
                ASIN = source.ASIN,
                AudibleASIN = source.AudibleASIN,
                RemoteProviderIds = CloneProviderIds(source.RemoteProviderIds),

                // Grouping
                BaseBookId = source.BaseBookId,
                UnitKeyHash = source.UnitKeyHash,

                // Additional metadata
                LanguageCode = source.LanguageCode,
                LanguageName = source.LanguageName,
                PublicationYear = source.PublicationYear,
                Publisher = source.Publisher,
                PageCount = source.PageCount,

                // Series denormalization
                SeriesId = source.SeriesId,
                SeriesName = source.SeriesName,
                SeriesPosition = source.SeriesPosition,

                // Classification / narrator
                IsGraphicAudio = source.IsGraphicAudio,
                AudioProductionType = source.AudioProductionType,
                IsOmnibus = source.IsOmnibus,
                Narrator = source.Narrator,

                // Instance management
                DurationMinutes = source.DurationMinutes,
                MediaType = source.MediaType,

                // Local/system fields
                CleanTitle = source.CleanTitle,
                Monitored = source.Monitored,
                AudiobookMonitored = source.AudiobookMonitored,
                EbookMonitored = source.EbookMonitored,
                AnyEditionOk = source.AnyEditionOk,
                LastInfoSync = source.LastInfoSync,
                Added = source.Added,
                LastSearchTime = source.LastSearchTime,
                AddOptions = source.AddOptions == null ? new AddBookOptions() : new AddBookOptions
                {
                    AddType = source.AddOptions.AddType,
                    SearchForNewBook = source.AddOptions.SearchForNewBook
                },

                // New metadata fields
                ProviderUrls = source.ProviderUrls == null ? new ProviderUrlMap() : new ProviderUrlMap(source.ProviderUrls),
                LastUpdated = source.LastUpdated,

                // Relationships (kept shallow; caller may replace)
                Author = source.Author == null ? null : new Author
                {
                    Name = source.Author.Name,
                    TitleSlug = source.Author.TitleSlug,
                    GoodreadsAuthorId = source.Author.GoodreadsAuthorId,
                    HardcoverAuthorId = source.Author.HardcoverAuthorId,
                    OpenLibraryAuthorId = source.Author.OpenLibraryAuthorId,
                    GoogleBooksAuthorId = source.Author.GoogleBooksAuthorId,
                    AudnexusAuthorId = source.Author.AudnexusAuthorId,
                    RemoteProviderIds = CloneProviderIds(source.Author.RemoteProviderIds)
                },
                SeriesLinks = source.SeriesLinks
            };

            if (includeEditions)
            {
                clone.Editions = source.Editions?.Select(CloneEdition).ToList() ?? new List<Edition>();
            }

            BookEditionIdentity.ClearBookLevelEditionIdentity(clone);

            return clone;
        }

        private static HashSet<string> CloneProviderIds(IEnumerable<string> source)
        {
            var values = source?
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return values?.Count > 0 ? values : null;
        }

        public static SeriesBookLink CloneSeriesBookLink(SeriesBookLink source)
        {
            if (source == null)
            {
                return null;
            }

            return new SeriesBookLink
            {
                Position = source.Position,
                SeriesPosition = source.SeriesPosition,
                SeriesId = source.SeriesId,
                BookId = source.BookId,
                IsPrimary = source.IsPrimary,
                SeriesInstanceType = source.SeriesInstanceType,
                IsInheritedLink = source.IsInheritedLink,
                Series = source.Series,
                Book = source.Book
            };
        }

        public static Series CloneSeries(Series source)
        {
            if (source == null)
            {
                return null;
            }

            return new Series
            {
                Title = source.Title,
                TitleSlug = source.TitleSlug,
                Description = source.Description,
                Numbered = source.Numbered,
                WorkCount = source.WorkCount,
                PrimaryWorkCount = source.PrimaryWorkCount,

                // Provider IDs
                GoodreadsSeriesId = source.GoodreadsSeriesId,
                HardcoverSeriesId = source.HardcoverSeriesId,
                OpenLibrarySeriesId = source.OpenLibrarySeriesId,
                AmazonSeriesAsin = source.AmazonSeriesAsin,

                // Series metadata
                SeriesType = source.SeriesType,
                ParentSeriesId = source.ParentSeriesId,
                TotalBooks = source.TotalBooks,
                PrimaryBooks = source.PrimaryBooks,

                // Narrator variant fields
                Narrator = source.Narrator,
                BaseSeriesId = source.BaseSeriesId,
                InstanceNumber = source.InstanceNumber,
                PreferredNarratorId = source.PreferredNarratorId,

                ProviderUrls = source.ProviderUrls == null ? new ProviderUrlMap() : new ProviderUrlMap(source.ProviderUrls),
                LastUpdated = source.LastUpdated,
                Links = source.Links == null ? new Dictionary<string, string>() : new Dictionary<string, string>(source.Links),

                // Children (shallow)
                SeriesBooks = source.SeriesBooks,
                LinkItems = source.LinkItems,
                Books = source.Books
            };
        }
    }
}
