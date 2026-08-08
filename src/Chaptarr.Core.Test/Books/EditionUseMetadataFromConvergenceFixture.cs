using System;
using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class EditionUseMetadataFromConvergenceFixture
    {
        [Test]
        public void use_metadata_from_should_converge_on_api_blob()
        {
            var local = new Edition
            {
                Id = 10,
                BookId = 100,
                ForeignEditionId = "ed:1",
                TitleSlug = "local-slug",
                Monitored = true,
                ManualAdd = true,
                IsFallbackEdition = true,
                IsGraphicAudio = true,
                AudioProductionType = "local",
                Narrator = "Local Narrator",
                MatchingTitle = "local",

                Isbn13 = "9780000000001",
                Isbn10 = "0000000001",
                Asin = "A000000000",
                Asins = new List<string> { "A000000000" },
                Title = "Local Title",
                Subtitle = "Local Subtitle",
                Language = "en",
                Overview = "Local Overview",
                Format = "Audio CD",
                IsEbook = false,
                Disambiguation = "Local",
                Publisher = "Local Publisher",
                PageCount = 123,
                ReleaseDate = new DateTime(2020, 01, 01, 0, 0, 0, DateTimeKind.Utc),

                GoodreadsEditionId = 1,
                HardcoverEditionId = "hc:ed-local",
                OpenLibraryEditionId = "ol:ed-local",
                ReadingFormatId = 2,
                EditionFormat = "Audiobook",
                EditionInfo = "Local edition",
                DurationSeconds = 3600,
                ChapterCount = 20,
                HasChapters = true,
                Chapters = new List<EditionChapter>
                {
                    new EditionChapter { Title = "Local Chapter", StartOffsetMs = 0, StartOffsetSec = 0, LengthMs = 60000 }
                },

                AudibleASIN = "B000000000",
                GoogleBooksEditionId = "gb:ed-local",
                ReviewCount = 7,
                NarratorNames = new List<string> { "Local Narrator" },
                ProviderUrls = new ProviderUrlMap { ["goodreads"] = "https://example.com/local" }
            };

            var remote = new Edition
            {
                ForeignEditionId = "ed:1",
                TitleSlug = null, // API may not provide a slug for editions

                Isbn13 = "9781111111111",
                Isbn10 = "1111111111",
                Asin = "A111111111",
                Asins = new List<string> { "A111111111", "A222222222" },
                Title = "Remote Title",
                Subtitle = "Remote Subtitle",
                Language = "en",
                Overview = null,
                Format = "MP3",
                IsEbook = false,
                Disambiguation = "Remote",
                Publisher = "Remote Publisher",
                PageCount = 456,
                ReleaseDate = new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc),

                GoodreadsEditionId = 2,
                HardcoverEditionId = "hc:ed-remote",
                OpenLibraryEditionId = "ol:ed-remote",
                ReadingFormatId = 2,
                EditionFormat = "Audiobook",
                EditionInfo = "Remote edition",
                DurationSeconds = 7200,
                ChapterCount = 40,
                HasChapters = false,
                Chapters = new List<EditionChapter>
                {
                    new EditionChapter { Title = "Remote Chapter 1", StartOffsetMs = 0, StartOffsetSec = 0, LengthMs = 120000 },
                    new EditionChapter { Title = "Remote Chapter 2", StartOffsetMs = 120000, StartOffsetSec = 120, LengthMs = 180000 }
                },
                AudioProductionType = "remote",

                AudibleASIN = null,
                GoogleBooksEditionId = "gb:ed-remote",
                ReviewCount = 22,
                NarratorNames = new List<string>(),
                ProviderUrls = new ProviderUrlMap { ["goodreads"] = "https://example.com/remote" }
            };

            var remoteForUpdate = RefreshEntityCopy.CloneEdition(remote);
            remoteForUpdate.UseDbFieldsFrom(local);

            local.UseMetadataFrom(remoteForUpdate);

            Assert.That(local.Equals(remoteForUpdate), Is.True);
        }
    }
}
