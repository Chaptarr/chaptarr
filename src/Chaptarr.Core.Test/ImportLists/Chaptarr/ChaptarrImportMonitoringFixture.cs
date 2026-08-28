using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Chaptarr;
using NzbDrone.Core.ThingiProvider.Status;

namespace Chaptarr.Core.Test.ImportLists.Chaptarr
{
    [TestFixture]
    public class ChaptarrImportMonitoringFixture
    {
        private sealed class StubProxy : IChaptarrV1Proxy
        {
            public List<ChaptarrAuthor> Authors { get; } = new();
            public List<ChaptarrBook> Books { get; } = new();

            public List<ChaptarrAuthor> GetAuthors(ChaptarrSettings settings) => Authors;
            public List<ChaptarrBook> GetBooks(ChaptarrSettings settings) => Books;
            public List<ChaptarrProfile> GetProfiles(ChaptarrSettings settings) => new();
            public List<ChaptarrRootFolder> GetRootFolders(ChaptarrSettings settings) => new();
            public List<ChaptarrTag> GetTags(ChaptarrSettings settings) => new();
            public ValidationFailure Test(ChaptarrSettings settings) => null;
        }

        private sealed class StubStatusService : IImportListStatusService
        {
            public List<ImportListStatus> GetBlockedProviders() => new();
            public void RecordSuccess(int providerId) { }
            public void RecordFailure(int providerId, TimeSpan minimumBackOff = default) { }
            public void RecordConnectionFailure(int providerId) { }
            public DateTime? GetLastSyncListInfo(int importListId) => null;
            public void UpdateListSyncStatus(int importListId) { }
        }

        [Test]
        public void should_apply_the_author_gate_for_each_books_media_type()
        {
            var proxy = new StubProxy();
            proxy.Authors.Add(new ChaptarrAuthor
            {
                Id = 1,
                AuthorName = "Split Author",
                ForeignAuthorId = "hc:author",
                Monitored = true,
                AudiobookMonitored = false,
                EbookMonitored = true
            });
            proxy.Books.AddRange(new[]
            {
                BuildBook("Audio", "hc:audio", "audiobook"),
                BuildBook("Ebook", "hc:ebook", "ebook")
            });

            var import = BuildImport(proxy);
            var items = import.Fetch();

            Assert.That(items.Select(item => item.BookProviderId), Is.EqualTo(new[] { "hc:ebook" }));
        }

        [Test]
        public void should_fall_back_to_the_aggregate_gate_for_older_chaptarr_instances()
        {
            var proxy = new StubProxy();
            proxy.Authors.Add(new ChaptarrAuthor
            {
                Id = 1,
                AuthorName = "Legacy Author",
                ForeignAuthorId = "hc:author",
                Monitored = true
            });
            proxy.Books.Add(BuildBook("Legacy Book", "hc:book", null));

            var items = BuildImport(proxy).Fetch();

            Assert.That(items.Select(item => item.BookProviderId), Is.EqualTo(new[] { "hc:book" }));
        }

        private static ChaptarrImport BuildImport(IChaptarrV1Proxy proxy)
        {
            return new ChaptarrImport(
                proxy,
                new StubStatusService(),
                configService: null,
                parsingService: null,
                logger: LogManager.GetCurrentClassLogger())
            {
                Definition = new ImportListDefinition
                {
                    Id = 1,
                    Name = "Chaptarr",
                    Settings = new ChaptarrSettings()
                }
            };
        }

        private static ChaptarrBook BuildBook(string title, string providerId, string mediaType)
        {
            return new ChaptarrBook
            {
                Title = title,
                ForeignBookId = providerId,
                ForeignEditionId = providerId + ":edition",
                AuthorId = 1,
                MediaType = mediaType,
                Monitored = true,
                AudiobookMonitored = mediaType == "audiobook" ? true : null,
                EbookMonitored = mediaType == "ebook" ? true : null
            };
        }
    }
}
