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
using NzbDrone.Core.Parser.Model;
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
                lock (RequestedPages)
                {
                    RequestedPages.Add(page);
                }
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
        public void should_limit_goodreads_list_by_unique_books_and_keep_editions_in_fetched_page()
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
                            BuildBook(101, 1001, "First Book Duplicate Entry", 501, "First Author"),
                            BuildBook(102, 1001, "First Book Alternate Edition", 501, "First Author"),
                            BuildBook(201, 2001, "Second Book", 502, "Second Author"),
                            BuildBook(301, 3001, "Excluded Book", 503, "Third Author"),
                            BuildBook(103, 1001, "First Book Later Edition", 501, "First Author"),
                            BuildBook(202, 2001, "Second Book Later Edition", 502, "Second Author")
                        }
                    }
                },
                {
                    2,
                    new ListResource
                    {
                        Books = new List<BookResource>
                        {
                            BuildBook(104, 1001, "First Book Unfetched Edition", 501, "First Author")
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

            Assert.That(items, Has.Count.EqualTo(5));
            Assert.That(items.Select(i => i.BookGoodreadsId), Is.EqualTo(new[] { "gr:1001", "gr:1001", "gr:2001", "gr:1001", "gr:2001" }));
            Assert.That(items.Select(i => i.EditionGoodreadsId), Is.EqualTo(new[] { "gr:101", "gr:102", "gr:201", "gr:103", "gr:202" }));
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
            Assert.That(items.All(i => i.EditionGoodreadsId == null), Is.True);
        }

        [Test]
        public void should_continue_list_pages_until_unique_book_limit_and_keep_fetched_editions()
        {
            var listInfo = new StubListInfo(new Dictionary<int, ListResource>
            {
                [1] = new ListResource { Books = new List<BookResource> { BuildBook(101, 1001, "First", 501, "Author"), BuildBook(102, 1001, "First", 501, "Author") } },
                [2] = new ListResource { Books = new List<BookResource> { BuildBook(201, 2001, "Second", 501, "Author"), BuildBook(301, 3001, "Excluded", 501, "Author"), BuildBook(103, 1001, "First", 501, "Author") } },
                [3] = new ListResource { Books = new List<BookResource> { BuildBook(104, 1001, "Unfetched", 501, "Author") } }
            });

            var items = CreateList(listInfo, 2).Fetch();

            Assert.That(items.Select(item => item.EditionProviderId), Is.EqualTo(new[] { "gr:101", "gr:102", "gr:201", "gr:103" }));
            Assert.That(listInfo.RequestedPages, Is.EqualTo(new[] { 1, 2 }));
        }

        [Test]
        public void should_keep_admitted_book_editions_from_fetched_parallel_wave_without_starting_another()
        {
            var firstPage = new ListResource();
            firstPage.Parse(XElement.Parse("<list><page>1</page><per_page>1</per_page><total_books>8</total_books><books /></list>"));
            firstPage.Books.Add(BuildBook(101, 1001, "First", 501, "Author"));
            var pages = new Dictionary<int, ListResource> { [1] = firstPage };
            for (var page = 2; page <= 8; page++)
            {
                pages[page] = new ListResource
                {
                    Books = new List<BookResource>
                    {
                        BuildBook(page == 2 ? 201 : 300 + page, page == 2 ? 2001 : 3000 + page, "Other", 501, "Author"),
                        BuildBook(100 + page, 1001, "First", 501, "Author")
                    }
                };
            }
            var listInfo = new StubListInfo(pages);

            var items = CreateList(listInfo, 2).Fetch();

            Assert.That(items.Select(item => item.EditionProviderId), Is.EqualTo(new[] { "gr:101", "gr:201", "gr:102", "gr:103", "gr:104", "gr:105", "gr:106" }));
            Assert.That(listInfo.RequestedPages.OrderBy(page => page), Is.EqualTo(new[] { 1, 2, 3, 4, 5, 6 }));
        }

        [Test]
        public void should_count_work_once_and_keep_generic_and_edition_requests()
        {
            var target = new List<ImportListItemInfo>();
            var seen = new HashSet<string>();
            foreach (var item in new[]
            {
                new ImportListItemInfo { BookProviderId = "gr:1001" },
                new ImportListItemInfo { BookProviderId = "gr:1001", EditionProviderId = "gr:101" },
                new ImportListItemInfo { BookProviderId = "gr:1001", EditionProviderId = "gr:102" },
                new ImportListItemInfo { BookProviderId = "gr:2001" },
                new ImportListItemInfo { BookProviderId = "gr:1001", EditionProviderId = "gr:103" },
                new ImportListItemInfo { BookProviderId = "gr:3001" }
            })
            {
                GoodreadsImportListLimit.TryAdd(target, item, seen, 2);
            }

            Assert.That(seen, Has.Count.EqualTo(2));
            Assert.That(target.Select(item => item.EditionProviderId), Is.EqualTo(new[] { null, "gr:101", "gr:102", null, "gr:103" }));
        }

        [Test]
        public void should_keep_work_and_edition_identifier_scopes_separate()
        {
            var target = new List<ImportListItemInfo>();
            var seen = new HashSet<string>();
            GoodreadsImportListLimit.TryAdd(target, new ImportListItemInfo { BookProviderId = "gr:123" }, seen, 2);
            GoodreadsImportListLimit.TryAdd(target, new ImportListItemInfo { EditionProviderId = "gr:123" }, seen, 2);

            Assert.That(seen, Has.Count.EqualTo(2));
            Assert.That(target, Has.Count.EqualTo(2));
        }

        [Test]
        public void should_count_edition_only_sources_without_inferring_work_from_same_names()
        {
            var target = new List<ImportListItemInfo>();
            var seen = new HashSet<string>();
            foreach (var id in new[] { "gr:101", "gr:102", "gr:103" })
            {
                GoodreadsImportListLimit.TryAdd(target, new ImportListItemInfo { EditionProviderId = id, Author = "Same Author", Book = "Same Title" }, seen, 2);
            }

            Assert.That(target.Select(item => item.EditionProviderId), Is.EqualTo(new[] { "gr:101", "gr:102" }));
            Assert.That(target.All(item => item.BookProviderId == null), Is.True);
            Assert.That(seen, Has.Count.EqualTo(2));
        }

        private static GoodreadsListImportList CreateList(IProvideListInfo listInfo, int limit)
        {
            return new GoodreadsListImportList(listInfo, new StubImportListStatusService(), null, null,
                new Lazy<IQualityProfileService>(() => null), new Lazy<IMetadataProfileService>(() => null),
                new Lazy<ITagService>(() => null), null, null, LogManager.GetCurrentClassLogger())
            {
                Definition = new ImportListDefinition
                {
                    Id = 1,
                    Name = "Goodreads List",
                    Settings = new GoodreadsListImportListSettings { ListId = 10, ImportLimit = limit }
                }
            };
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
