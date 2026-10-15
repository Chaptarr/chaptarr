using System.Collections.Generic;
using Chaptarr.Api.V1.History;
using NUnit.Framework;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.History
{
    [TestFixture]
    public class HistoryResourceMapperFixture
    {
        [TestCase("NarratorEvidenceFields")]
        [TestCase("narratorEvidenceFields")]
        [TestCase("NARRATOREVIDENCEFIELDS")]
        public void internal_narrator_text_should_be_filtered_without_mutating_history(string key)
        {
            var data = new Dictionary<string, string> { [key] = "private tracker description", ["Narrator"] = "Scott Brick", ["futureMetadata"] = "preserved" };
            var history = new EntityHistory { Data = data };
            var resource = history.ToResource(new StubCustomFormatCalculationService());
            Assert.Multiple(() =>
            {
                Assert.That(resource.Data.Keys, Does.Not.Contain(key));
                Assert.That(resource.Data["Narrator"], Is.EqualTo("Scott Brick"));
                Assert.That(resource.Data["futureMetadata"], Is.EqualTo("preserved"));
                Assert.That(history.Data, Is.SameAs(data));
                Assert.That(history.Data[key], Is.EqualTo("private tracker description"));
            });
        }

        private sealed class StubCustomFormatCalculationService : ICustomFormatCalculationService
        {
            public int EntityHistoryCalls { get; private set; }

            public List<CustomFormat> ParseCustomFormat(RemoteBook remoteBook, long size) => new();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile, Author artist) => new();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile) => new();
            public List<CustomFormat> ParseCustomFormat(Blocklist blocklist, Author artist) => new();
            public List<CustomFormat> ParseCustomFormat(EntityHistory history, Author artist)
            {
                EntityHistoryCalls++;
                Assert.That(artist, Is.Not.Null);
                return new List<CustomFormat>();
            }

            public List<CustomFormat> ParseCustomFormat(LocalBook localBook) => new();
        }

        [Test]
        public void should_display_unknown_audio_for_old_audiobook_history_rows_with_unknown_text_quality()
        {
            var history = new EntityHistory
            {
                Quality = new QualityModel(Quality.Unknown),
                Book = new Book
                {
                    Id = 2017,
                    Title = "Harry Potter and the Goblet of Fire",
                    MediaType = BookMediaType.Audiobook
                }
            };

            var resource = history.ToResource(new StubCustomFormatCalculationService());

            Assert.That(resource.Quality.Quality, Is.EqualTo(Quality.UnknownAudio));
        }

        [Test]
        public void should_keep_unknown_text_for_ebook_history_rows_with_unknown_text_quality()
        {
            var history = new EntityHistory
            {
                Quality = new QualityModel(Quality.Unknown),
                Book = new Book
                {
                    Id = 2018,
                    Title = "Harry Potter and the Goblet of Fire",
                    MediaType = BookMediaType.Ebook
                }
            };

            var resource = history.ToResource(new StubCustomFormatCalculationService());

            Assert.That(resource.Quality.Quality, Is.EqualTo(Quality.Unknown));
        }

        [Test]
        public void should_omit_custom_formats_for_unassigned_history()
        {
            var formatCalculator = new StubCustomFormatCalculationService();
            var history = new EntityHistory
            {
                AuthorId = 0,
                BookId = 0,
                SourceTitle = "Pending retained import",
                Quality = new QualityModel(Quality.M4B)
            };

            var resource = history.ToResource(formatCalculator);

            Assert.That(formatCalculator.EntityHistoryCalls, Is.Zero);
            Assert.That(resource.CustomFormats, Is.Empty);
            Assert.That(resource.CustomFormatScore, Is.Zero);
        }
    }
}
