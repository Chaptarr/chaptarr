using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Queue;
using NUnit.Framework;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Common
{
    internal static class RecordingBookSearch
    {
        public static void AssertSearchOnAddDelay(SearchCriteriaBase criteria, bool paused)
        {
            var quality = criteria.Books[0].MediaType == BookMediaType.Audiobook
                ? NzbDrone.Core.Qualities.Quality.M4B : NzbDrone.Core.Qualities.Quality.EPUB;
            var profile = new NzbDrone.Core.Profiles.Qualities.QualityProfile
            {
                Id = 1, Items = new() { new() { Quality = quality, Allowed = true } }
            };
            criteria.Author.AudiobookQualityProfile = criteria.Author.EbookQualityProfile = profile;
            var remote = new RemoteBook
            {
                Author = criteria.Author, Books = criteria.Books,
                ParsedBookInfo = new ParsedBookInfo { Quality = new NzbDrone.Core.Qualities.QualityModel(quality) },
                Release = new ReleaseInfo { PublishDate = DateTime.UtcNow, DownloadProtocol = DownloadProtocol.Usenet }
            };
            var delay = new NzbDrone.Core.DecisionEngine.Specifications.RssSync.DelaySpecification(
                Proxy<NzbDrone.Core.Download.Pending.IPendingReleaseService>((_, _) => null), null,
                Proxy<NzbDrone.Core.Profiles.Delay.IDelayProfileService>((_, _) => new NzbDrone.Core.Profiles.Delay.DelayProfile
                    { PreferredProtocol = DownloadProtocol.Usenet, UsenetDelay = 60 }),
                Proxy<NzbDrone.Core.MediaFiles.IMediaFileService>((_, _) => new List<BookFile>()), NLog.LogManager.GetCurrentClassLogger());
            var decision = delay.IsSatisfiedBy(remote, criteria);
            Assert.That(criteria.UserInvokedSearch, Is.EqualTo(paused));
            Assert.That(decision.Accepted, Is.EqualTo(paused), decision.Reason);
        }

        public static BookSearchService Create(IBookService books, IAuthorService authors, NLog.Logger logger,
            Action<SearchCriteriaBase> onFetch)
        {
            var indexer = Proxy<IIndexer>((method, args) =>
            {
                if (method.Name == "get_Definition") return new IndexerDefinition { Name = "Fixture", Tags = new HashSet<int>() };
                Assert.That(method.Name, Is.EqualTo(nameof(IIndexer.Fetch)));
                onFetch((SearchCriteriaBase)args[0]);
                return Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo>());
            });
            var search = new ReleaseSearchService(Proxy<IIndexerFactory>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IIndexerFactory.AutomaticSearchEnabled)));
                return new List<IIndexer> { indexer };
            }), books, authors, null, Proxy<IMakeDownloadDecision>((method, args) => new List<DownloadDecision>()), logger);
            return new BookSearchService(search, books, null,
                Proxy<IQueueService>((method, args) => new List<NzbDrone.Core.Queue.Queue>()),
                Proxy<IProcessDownloadDecisions>((method, args) => Task.FromResult(new ProcessedDecisions(new(), new(), new()))), logger);
        }
    }
}
