using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Goodreads;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.Goodreads;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tags;
using NzbDrone.Core.ThingiProvider.Status;

namespace Chaptarr.Core.Test.ImportLists.Goodreads
{
    [TestFixture]
    public class GoodreadsImportListLimitFixture
    {
        private sealed class StubImportListStatusService : IImportListStatusService
        {
            public List<ImportListStatus> GetBlockedProviders() => new();
            public void RecordSuccess(int providerId) { }
            public void RecordFailure(int providerId, TimeSpan minimumBackOff = default) { }
            public void RecordConnectionFailure(int providerId) { }
            public DateTime? GetLastSyncListInfo(int importListId) => null;
            public void UpdateListSyncStatus(int importListId) { }
        }

        private sealed class StubListInfo : IProvideListInfo
        {
            private readonly Dictionary<int, ListResource> _pages;

            public StubListInfo(Dictionary<int, ListResource> pages)
            {
                _pages = pages;
            }

            public List<int> RequestedPages { get; } = new();

            public ListResource GetListInfo(int id, int page, bool useCache = true)
            {
                RequestedPages.Add(page);
                return _pages.TryGetValue(page, out var resource)
                    ? resource
                    : new ListResource { Books = new List<BookResource>() };
            }
        }

        private sealed class StubSeriesInfo : IProvideSeriesInfo
        {
            private readonly SeriesResource _series;

            public StubSeriesInfo(SeriesResource series)
            {
                _series = series;
            }

            public SeriesResource GetSeriesInfo(int id, bool useCache = true)
            {
                return _series;
            }
        }

        [Test]
        public void should_limit_goodreads_list_by_unique_work_and_stop_fetching_pages()
        {
            var listInfo = new StubListInfo(new Dictionary<int, ListResource>
            {
                {
                    1,
                    new ListResource
                    {
                        Books = new List<BookResource>
                        {
                            BuildBook(101, 1001, "First Book", 501, "First Author"),
                            BuildBook(102, 1001, "First Book Duplicate Edition", 501, "First Author"),
                            BuildBook(201, 2001, "Second Book", 502, "Second Author")
                        }
                    }
                },
                {
                    2,
                    new ListResource
                    {
                        Books = new List<BookResource>
                        {
                            BuildBook(301, 3001, "Third Book", 503, "Third Author")
                        }
                    }
                }
            });

            var importList = new GoodreadsListImportList(
                listInfo,
                new StubImportListStatusService(),
                configService: null,
                parsingService: null,
                qualityProfileService: new Lazy<IQualityProfileService>(() => null),
                metadataProfileService: new Lazy<IMetadataProfileService>(() => null),
                tagService: new Lazy<ITagService>(() => null),
                rootFolderService: null,
                rootFolderSettingsResolver: null,
                logger: LogManager.GetCurrentClassLogger())
            {
                Definition = new ImportListDefinition
                {
                    Id = 1,
                    Name = "Goodreads List",
                    Settings = new GoodreadsListImportListSettings
                    {
                        ListId = 10,
                        ImportLimit = 2
                    }
                }
            };

            var items = importList.Fetch();

            Assert.That(items, Has.Count.EqualTo(2));
            Assert.That(items.Select(i => i.BookGoodreadsId), Is.EqualTo(new[] { "gr:1001", "gr:2001" }));
            Assert.That(items.Select(i => i.EditionGoodreadsId), Is.EqualTo(new[] { "gr:101", "gr:201" }));
            Assert.That(listInfo.RequestedPages, Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void should_preserve_existing_edition_level_dedupe_when_import_limit_is_disabled()
        {
            var listInfo = new StubListInfo(new Dictionary<int, ListResource>
            {
                {
                    1,
                    new ListResource
                    {
                        Books = new List<BookResource>
                        {
                            BuildBook(101, 1001, "First Book", 501, "First Author"),
                            BuildBook(102, 1001, "First Book Alternate Edition", 501, "First Author")
                        }
                    }
                },
                {
                    2,
                    new ListResource
                    {
                        Books = new List<BookResource>()
                    }
                }
            });

            var importList = new GoodreadsListImportList(
                listInfo,
                new StubImportListStatusService(),
                configService: null,
                parsingService: null,
                qualityProfileService: new Lazy<IQualityProfileService>(() => null),
                metadataProfileService: new Lazy<IMetadataProfileService>(() => null),
                tagService: new Lazy<ITagService>(() => null),
                rootFolderService: null,
                rootFolderSettingsResolver: null,
                logger: LogManager.GetCurrentClassLogger())
            {
                Definition = new ImportListDefinition
                {
                    Id = 1,
                    Name = "Goodreads List",
                    Settings = new GoodreadsListImportListSettings
                    {
                        ListId = 10,
                        ImportLimit = 0
                    }
                }
            };

            var items = importList.Fetch();

            Assert.That(items, Has.Count.EqualTo(2));
            Assert.That(items.Select(i => i.BookGoodreadsId), Is.EqualTo(new[] { "gr:1001", "gr:1001" }));
            Assert.That(items.Select(i => i.EditionGoodreadsId), Is.EqualTo(new[] { "gr:101", "gr:102" }));
            Assert.That(listInfo.RequestedPages, Is.EqualTo(new[] { 1, 2 }));
        }

        [Test]
        public void should_limit_goodreads_series_by_returned_work_order()
        {
            var seriesInfo = new StubSeriesInfo(new SeriesResource
            {
                Works = new List<WorkResource>
                {
                    BuildWork(1001, 101, "First Book", 501, "First Author"),
                    BuildWork(2001, 201, "Second Book", 502, "Second Author"),
                    BuildWork(3001, 301, "Third Book", 503, "Third Author")
                }
            });

            var importList = new GoodreadsSeriesImportList(
                seriesInfo,
                new StubImportListStatusService(),
                configService: null,
                parsingService: null,
                qualityProfileService: new Lazy<IQualityProfileService>(() => null),
                metadataProfileService: new Lazy<IMetadataProfileService>(() => null),
                tagService: new Lazy<ITagService>(() => null),
                rootFolderService: null,
                rootFolderSettingsResolver: null,
                logger: LogManager.GetCurrentClassLogger())
            {
                Definition = new ImportListDefinition
                {
                    Id = 2,
                    Name = "Goodreads Series",
                    Settings = new GoodreadsSeriesImportListSettings
                    {
                        SeriesId = 20,
                        ImportLimit = 2
                    }
                }
            };

            var items = importList.Fetch();

            Assert.That(items, Has.Count.EqualTo(2));
            Assert.That(items.Select(i => i.BookGoodreadsId), Is.EqualTo(new[] { "gr:1001", "gr:2001" }));
            Assert.That(items.Select(i => i.EditionGoodreadsId), Is.EqualTo(new[] { "gr:101", "gr:201" }));
        }

        private static BookResource BuildBook(long editionId, long workId, string title, long authorId, string authorName)
        {
            var book = new BookResource();
            book.Parse(XElement.Parse($@"
<book>
  <id>{editionId}</id>
  <title>{title}</title>
  <work>
    <id>{workId}</id>
    <original_title>{title}</original_title>
  </work>
  <authors>
    <author>
      <id>{authorId}</id>
      <name>{authorName}</name>
    </author>
  </authors>
</book>"));
            return book;
        }

        private static WorkResource BuildWork(long workId, long editionId, string title, long authorId, string authorName)
        {
            var work = new WorkResource();
            work.Parse(XElement.Parse($@"
<work>
  <id>{workId}</id>
  <original_title>{title}</original_title>
  <best_book>
    <id>{editionId}</id>
    <title>{title}</title>
    <author>
      <id>{authorId}</id>
      <name>{authorName}</name>
    </author>
  </best_book>
</work>"));
            return work;
        }
    }
}
