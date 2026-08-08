using System.Collections.Generic;
using System.Data;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class MultiCopySeriesServiceMediaTypeFixture
    {
        private sealed class StubSeriesService : ISeriesService
        {
            private int _nextId = 1000;

            public List<Series> Series { get; } = new List<Series>();

            public Series GetSeries(int seriesId) => Series.SingleOrDefault(s => s.Id == seriesId);

            public Series FindById(string foreignSeriesId) => Series.SingleOrDefault(s => Matches(s, foreignSeriesId));

            public Series FindById(string foreignSeriesId, BookMediaType mediaType) => Series.SingleOrDefault(s => s.MediaType == mediaType && Matches(s, foreignSeriesId));

            public List<Series> FindById(List<string> foreignSeriesId) => Series.Where(s => foreignSeriesId.Any(id => Matches(s, id))).ToList();

            public List<Series> FindById(List<string> foreignSeriesId, BookMediaType mediaType) => Series.Where(s => s.MediaType == mediaType && foreignSeriesId.Any(id => Matches(s, id))).ToList();

            public List<Series> GetByAuthorId(int authorId) => Series.ToList();

            public List<Series> GetAllSeries() => Series.ToList();

            public Series AddSeries(Series series)
            {
                if (series.Id == 0)
                {
                    series.Id = _nextId++;
                }

                Series.Add(series);
                return series;
            }

            public void Delete(int seriesId) => Series.RemoveAll(s => s.Id == seriesId);

            public void InsertMany(IList<Series> series)
            {
                foreach (var item in series)
                {
                    AddSeries(item);
                }
            }

            public void InsertMany(IList<Series> series, IDbConnection connection, IDbTransaction transaction) => InsertMany(series);

            public void UpdateMany(IList<Series> series)
            {
            }

            private static bool Matches(Series series, string providerId)
            {
                return series.HardcoverSeriesId == providerId ||
                       series.GoodreadsSeriesId == providerId ||
                       series.OpenLibrarySeriesId == providerId ||
                       series.AmazonSeriesAsin == providerId;
            }
        }

        [Test]
        public void should_not_reuse_narrator_variant_from_other_media_type()
        {
            var seriesService = new StubSeriesService();
            var audiobookBase = new Series
            {
                Id = 10,
                Title = "Shared Series",
                GoodreadsSeriesId = "gr:shared-series",
                MediaType = BookMediaType.Audiobook
            };

            var ebookVariant = new Series
            {
                Id = 20,
                Title = "Shared Series",
                GoodreadsSeriesId = "gr:shared-series",
                BaseSeriesId = "gr:shared-series",
                PreferredNarratorId = 42,
                Narrator = "Narrator Name",
                MediaType = BookMediaType.Ebook,
                InstanceNumber = 1
            };

            seriesService.Series.Add(audiobookBase);
            seriesService.Series.Add(ebookVariant);

            var sut = new MultiCopySeriesService(seriesService, null, LogManager.GetCurrentClassLogger());

            var result = sut.GetOrCreateNarratorVariant(audiobookBase, "Narrator Name");

            Assert.Multiple(() =>
            {
                Assert.That(result.Id, Is.Not.EqualTo(ebookVariant.Id));
                Assert.That(result.MediaType, Is.EqualTo(BookMediaType.Audiobook));
                Assert.That(result.PreferredNarratorId, Is.Null);
                Assert.That(seriesService.Series.Count(s => s.IsNarratorVariant && s.MediaType == BookMediaType.Audiobook), Is.EqualTo(1));
            });
        }
    }
}
