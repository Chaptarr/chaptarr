using System;
using System.Reflection;
using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.DecisionEngine.Specifications.RssSync;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class DelaySpecificationFixture
    {
        private class ThrowingProxy<T> : DispatchProxy
            where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"User-invoked search should not call {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class ResultProxy<T> : DispatchProxy
            where T : class
        {
            public Func<MethodInfo, object[], object> Handler { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args) => Handler(targetMethod, args);
        }

        private static T Proxy<T>(Func<MethodInfo, object[], object> handler)
            where T : class
        {
            var proxy = DispatchProxy.Create<T, ResultProxy<T>>();
            ((ResultProxy<T>)(object)proxy).Handler = handler;
            return proxy;
        }

        [TestCase(BookMediaType.Audiobook, true)]
        [TestCase(BookMediaType.Ebook, false)]
        public void unknown_quality_should_apply_the_target_profile_highest_quality_bypass(BookMediaType mediaType, bool accepted)
        {
            var audio = new QualityProfile
            {
                Id = 2,
                ProfileType = ProfileType.Audiobook,
                Items = new List<QualityProfileQualityItem> { new() { Quality = Quality.Unknown, Allowed = true } }
            };
            var ebook = new QualityProfile
            {
                Id = 3,
                ProfileType = ProfileType.Ebook,
                Items = new List<QualityProfileQualityItem>
                {
                    new() { Quality = Quality.Unknown, Allowed = false },
                    new() { Quality = Quality.EPUB, Allowed = true }
                }
            };
            var remoteBook = new RemoteBook
            {
                Author = new Author
                {
                    AudiobookQualityProfileId = audio.Id,
                    AudiobookQualityProfile = audio,
                    EbookQualityProfileId = ebook.Id,
                    EbookQualityProfile = ebook
                },
                Books = new List<Book> { new() { Id = 100, MediaType = mediaType } },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.Unknown) },
                Release = new ReleaseInfo { PublishDate = DateTime.UtcNow, DownloadProtocol = DownloadProtocol.Usenet }
            };
            var delay = new DelayProfile
            {
                PreferredProtocol = DownloadProtocol.Usenet,
                UsenetDelay = 60,
                BypassIfHighestQuality = true
            };
            var subject = new DelaySpecification(
                Proxy<IPendingReleaseService>((method, args) => null),
                DispatchProxy.Create<IUpgradableSpecification, ThrowingProxy<IUpgradableSpecification>>(),
                Proxy<IDelayProfileService>((method, args) => delay),
                Proxy<IMediaFileService>((method, args) => new List<BookFile>()),
                LogManager.GetCurrentClassLogger());

            Assert.That(subject.IsSatisfiedBy(remoteBook, null).Accepted, Is.EqualTo(accepted));
        }

        [TestCase("rss")] [TestCase("automatic_search")] [TestCase("user_search")]
        public void empty_decision_batch_should_not_load_pending_evidence(string mode)
        {
            var pendingReads = 0;
            var delay = new DelaySpecification(Proxy<IPendingReleaseService>((method, args) =>
                {
                    pendingReads++;
                    if (args.Length == 1) args[0] = new Dictionary<(int AuthorId, int BookId), DateTime>();
                    return new List<ReleaseInfo>();
                }), null, null, null, LogManager.GetCurrentClassLogger());
            var maker = new DownloadDecisionMaker(new[] { delay }, null, null, null, null, null, null,
                ConfigServiceTestProxy.Create(), LogManager.GetCurrentClassLogger());
            var decisions = mode == "rss" ? maker.GetRssDecision(new List<ReleaseInfo>())
                : maker.GetSearchDecision(new List<ReleaseInfo>(), new BookSearchCriteria { UserInvokedSearch = mode == "user_search" });
            Assert.That(decisions, Is.Empty);
            Assert.That(pendingReads, Is.Zero);
        }

        [Test]
        public void should_ignore_delay_for_user_invoked_search()
        {
            var subject = new DelaySpecification(
                DispatchProxy.Create<IPendingReleaseService, ThrowingProxy<IPendingReleaseService>>(),
                DispatchProxy.Create<IUpgradableSpecification, ThrowingProxy<IUpgradableSpecification>>(),
                DispatchProxy.Create<IDelayProfileService, ThrowingProxy<IDelayProfileService>>(),
                DispatchProxy.Create<IMediaFileService, ThrowingProxy<IMediaFileService>>(),
                LogManager.GetCurrentClassLogger());

            var decision = subject.IsSatisfiedBy(
                new RemoteBook(),
                new BookSearchCriteria { UserInvokedSearch = true });

            Assert.That(decision.Accepted, Is.True);
        }

        [TestCase(null, "epub", 22)] [TestCase(null, "m4b", 11)]
        [TestCase(BookMediaType.Ebook, "m4b", 22)] [TestCase(BookMediaType.Audiobook, "epub", 11)]
        [TestCase(BookMediaType.Ebook, "unknown", 22)] [TestCase(BookMediaType.Audiobook, "unknown", 11)]
        public void delay_tags_should_follow_the_resolved_profile_media(BookMediaType? media, string format, int expectedTag)
        {
            var author = DualProfileAuthor();
            var remote = new RemoteBook
            {
                Author = author,
                Books = media.HasValue ? new List<Book> { new() { MediaType = media.Value } } : new List<Book>(),
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(format == "epub" ? Quality.EPUB : format == "m4b" ? Quality.M4B : Quality.Unknown) },
                Release = new ReleaseInfo { DownloadProtocol = DownloadProtocol.Torrent }
            };
            HashSet<int> selectedTags = null;
            var subject = new DelaySpecification(null, null,
                Proxy<IDelayProfileService>((method, args) =>
                {
                    selectedTags = (HashSet<int>)args[0];
                    return new DelayProfile { PreferredProtocol = DownloadProtocol.Torrent, TorrentDelay = 0 };
                }), null, LogManager.GetCurrentClassLogger());

            Assert.That(subject.IsSatisfiedBy(remote, null).Accepted, Is.True);
            Assert.That(selectedTags, Is.EquivalentTo(new[] { expectedTag }));
        }

        [TestCase(false)] [TestCase(true)]
        public void unresolved_media_should_not_be_reported_as_an_absent_configured_profile(bool mixed)
        {
            var remote = new RemoteBook
            {
                Author = DualProfileAuthor(),
                Books = mixed ? new List<Book> { new() { MediaType = BookMediaType.Audiobook }, new() { MediaType = BookMediaType.Ebook } } : new List<Book>(),
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.Unknown) },
                Release = new ReleaseInfo { Title = "The Ebook Handbook" }
            };
            var decision = new DelaySpecification(null, null, null, null, LogManager.GetCurrentClassLogger()).IsSatisfiedBy(remote, null);
            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Reason, Is.EqualTo("Unable to determine release media type"));
        }

        [TestCase(BookMediaType.Audiobook)] [TestCase(BookMediaType.Ebook)]
        public void resolved_media_with_no_configured_profile_should_keep_the_missing_profile_reason(BookMediaType media)
        {
            var remote = new RemoteBook
            {
                Author = new Author(), Books = new List<Book> { new() { MediaType = media } },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.Unknown) }
            };
            var decision = new DelaySpecification(null, null, null, null, LogManager.GetCurrentClassLogger()).IsSatisfiedBy(remote, null);
            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Reason, Is.EqualTo($"No quality profile configured for {Quality.Unknown.Name} files"));
        }

        private static Author DualProfileAuthor() => new()
        {
            AudiobookQualityProfileId = 2, AudiobookQualityProfile = new QualityProfile { Id = 2, ProfileType = ProfileType.Audiobook },
            EbookQualityProfileId = 3, EbookQualityProfile = new QualityProfile { Id = 3, ProfileType = ProfileType.Ebook },
            AudiobookTags = new HashSet<int> { 11 }, EbookTags = new HashSet<int> { 22 }
        };
    }
}
