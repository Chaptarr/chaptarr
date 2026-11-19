using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class DownloadDecisionSizeTieBreakerFixture
    {
        private IDelayProfileService _delay;
        private DownloadDecisionComparer _comparer;
        private DownloadDecision _large;
        private DownloadDecision _small;

        [SetUp]
        public void SetUp()
        {
            _delay = DispatchProxy.Create<IDelayProfileService, DelayProxy>();
            _comparer = new DownloadDecisionComparer(null, _delay, null, LogManager.GetCurrentClassLogger());
            var profile = new QualityProfile
            {
                Id = 1,
                ProfileType = ProfileType.Ebook,
                Items = new List<QualityProfileQualityItem>
                {
                    new QualityProfileQualityItem { Quality = Quality.PDF, Allowed = true }
                }
            };
            var author = new Author { Id = 1, EbookQualityProfileId = 1, EbookQualityProfile = profile };
            var book = new Book { Id = 1, MediaType = BookMediaType.Ebook, Author = author };
            _large = CreateDecision(author, book, 400_000_000);
            _small = CreateDecision(author, book, 4_000_000);
        }

        [TestCase(BookMediaType.Ebook)]
        [TestCase(BookMediaType.Audiobook)]
        public void healthier_torrent_should_win_before_size(BookMediaType mediaType)
        {
            _small.RemoteBook.Books[0].MediaType = mediaType;
            if (mediaType == BookMediaType.Audiobook)
            {
                _small.RemoteBook.ParsedBookInfo.Quality = new QualityModel(Quality.MP3);
                _large.RemoteBook.ParsedBookInfo.Quality = new QualityModel(Quality.MP3);
            }

            ((TorrentInfo)_small.RemoteBook.Release).Seeders = 500;
            AssertWinner(_small, _large, "Better torrent health");
        }

        [Test]
        public void peers_should_decide_before_size_when_seeder_health_ties()
        {
            ((TorrentInfo)_small.RemoteBook.Release).Peers = 500;
            AssertWinner(_small, _large, "Better torrent health");
        }

        [Test]
        public void custom_format_score_should_beat_indexer_priority_health_and_size()
        {
            _small.RemoteBook.CustomFormatScore = 10;
            _large.RemoteBook.Release.IndexerPriority = 1;
            ((TorrentInfo)_large.RemoteBook.Release).Seeders = 500;
            AssertWinner(_small, _large, "Higher Custom Format score");
        }

        [Test]
        public void indexer_priority_should_beat_health_and_size()
        {
            _small.RemoteBook.Release.IndexerPriority = 1;
            ((TorrentInfo)_large.RemoteBook.Release).Seeders = 500;
            AssertWinner(_small, _large, "Higher indexer priority (Test Indexer)");
        }

        [Test]
        public void protocol_preference_should_beat_indexer_priority_and_size()
        {
            _large.RemoteBook.Release.DownloadProtocol = DownloadProtocol.Usenet;
            _large.RemoteBook.Release.IndexerPriority = 1;
            AssertWinner(_small, _large, "Better protocol preference (Torrent)");
        }

        [Test]
        public void release_scope_should_decide_before_size()
        {
            _small.RemoteBook.ParsedBookInfo.Discography = true;
            AssertWinner(_small, _large, "Discography preferred over single book");
        }

        [Test]
        public void usenet_age_should_decide_before_size()
        {
            _small.RemoteBook.Release.DownloadProtocol = DownloadProtocol.Usenet;
            _large.RemoteBook.Release.DownloadProtocol = DownloadProtocol.Usenet;
            _small.RemoteBook.Release.PublishDate = DateTime.UtcNow.AddHours(-2);
            _large.RemoteBook.Release.PublishDate = DateTime.UtcNow.AddDays(-10);
            AssertWinner(_small, _large, "Newer release");
        }

        [Test]
        public void size_should_break_a_complete_tie_including_unknown_health()
        {
            ((TorrentInfo)_small.RemoteBook.Release).Seeders = null;
            ((TorrentInfo)_large.RemoteBook.Release).Seeders = null;
            AssertWinner(_large, _small, "Larger file size");
        }

        [TestCase(BookMediaType.Ebook, 3.0, 3.5, 0)]
        [TestCase(BookMediaType.Ebook, 3.5, 4.0, 1)]
        [TestCase(BookMediaType.Ebook, 0.25, 0.75, 0)]
        [TestCase(BookMediaType.Audiobook, 210.0, 399.0, 0)]
        [TestCase(BookMediaType.Audiobook, 399.0, 400.0, 1)]
        public void final_size_bands_should_follow_the_book_media_type(BookMediaType mediaType, double smallerMiB, double largerMiB, int expected)
        {
            _small.RemoteBook.Books[0].MediaType = mediaType;
            _small.RemoteBook.Release.Size = smallerMiB.Megabytes();
            _large.RemoteBook.Release.Size = largerMiB.Megabytes();
            ((TorrentInfo)_small.RemoteBook.Release).Seeders = 60;
            ((TorrentInfo)_large.RemoteBook.Release).Seeders = 90;
            ((TorrentInfo)_small.RemoteBook.Release).Peers = 60;
            ((TorrentInfo)_large.RemoteBook.Release).Peers = 90;

            Assert.Multiple(() =>
            {
                Assert.That(Math.Sign(_comparer.Compare(_large, _small)), Is.EqualTo(expected));
                Assert.That(Math.Sign(_comparer.Compare(_small, _large)), Is.EqualTo(-expected));
                var result = _comparer.CompareWithReasons(_large, _small);
                Assert.That(Math.Sign(result.Result), Is.EqualTo(expected));
                Assert.That(result.Reasons.Single().Reason, Is.EqualTo(expected == 0
                    ? "Download allowed and meets all requirements"
                    : "Larger file size"));
            });
        }

        [Test]
        public void complete_tie_should_keep_the_generic_explanation()
        {
            _small.RemoteBook.Release.Size = _large.RemoteBook.Release.Size;
            Assert.That(_comparer.Compare(_large, _small), Is.Zero);
            var result = _comparer.CompareWithReasons(_large, _small);
            Assert.That(result.Result, Is.Zero);
            Assert.That(result.Reasons.Single().Reason, Is.EqualTo("Download allowed and meets all requirements"));
        }

        private void AssertWinner(DownloadDecision winner, DownloadDecision loser, string reason)
        {
            Assert.Multiple(() =>
            {
                Assert.That(_comparer.Compare(winner, loser), Is.GreaterThan(0));
                Assert.That(_comparer.Compare(loser, winner), Is.LessThan(0));
                var result = _comparer.CompareWithReasons(winner, loser);
                Assert.That(result.Result, Is.GreaterThan(0));
                Assert.That(result.Reasons.Single().Reason, Is.EqualTo(reason));
                Assert.That(_comparer.CompareWithReasons(loser, winner).Reasons.Single().Reason, Is.EqualTo(reason));
            });

            var prioritizer = new DownloadDecisionPriorizationService(null, _delay, null, LogManager.GetCurrentClassLogger());
            foreach (var input in new[] { new List<DownloadDecision> { winner, loser }, new List<DownloadDecision> { loser, winner } })
            {
                var sorted = prioritizer.PrioritizeDecisions(input);
                Assert.That(sorted.First(), Is.SameAs(winner));
                Assert.That(sorted.First().AutoPickReasons.Single().Reason, Is.EqualTo(reason));
            }
        }

        private static DownloadDecision CreateDecision(Author author, Book book, long size)
        {
            return new DownloadDecision(new RemoteBook
            {
                Author = author,
                Books = new List<Book> { book },
                DownloadAllowed = true,
                Release = new TorrentInfo
                {
                    Title = "Ranking test PDF",
                    Size = size,
                    DownloadProtocol = DownloadProtocol.Torrent,
                    Seeders = 5,
                    Peers = 5,
                    Indexer = "Test Indexer",
                    IndexerPriority = 25
                },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.PDF) }
            });
        }

        public class DelayProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo method, object[] args)
            {
                return method.Name == nameof(IDelayProfileService.BestForTags)
                    ? new DelayProfile { PreferredProtocol = DownloadProtocol.Torrent }
                    : throw new NotImplementedException(method.Name);
            }
        }
    }
}
