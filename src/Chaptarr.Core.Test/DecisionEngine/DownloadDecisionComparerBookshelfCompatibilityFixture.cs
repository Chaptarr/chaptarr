using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class DownloadDecisionComparerBookshelfCompatibilityFixture
    {
        [Test]
        public void should_use_bookshelf_quality_priority_before_title_match_for_migrated_audiobooks()
        {
            var author = CreateAuthorWithProfiles();
            var higherQualitySibling = CreateDecision(author, Quality.M4B, "The Hobbit: An Unexpected Journey");
            var lowerQualityExact = CreateDecision(author, Quality.MP3, "The Hobbit");
            var comparer = CreateComparer(bookshelfMatchCompatibility: true);

            var sorted = new[] { lowerQualityExact, higherQualitySibling }
                .OrderByDescending(decision => decision, comparer)
                .ToList();
            var comparison = comparer.CompareWithReasons(higherQualitySibling, lowerQualityExact);

            Assert.That(sorted[0], Is.SameAs(higherQualitySibling));
            Assert.That(comparison.Result, Is.GreaterThan(0));
            Assert.That(comparison.Reasons.Single().Reason, Does.Contain("Quality profile preference"));
        }

        [Test]
        public void should_keep_chaptarr_title_priority_for_fresh_audiobook_installs()
        {
            var author = CreateAuthorWithProfiles();
            var higherQualitySibling = CreateDecision(author, Quality.M4B, "The Hobbit: An Unexpected Journey");
            var lowerQualityExact = CreateDecision(author, Quality.MP3, "The Hobbit");
            var comparer = CreateComparer(bookshelfMatchCompatibility: false);

            var sorted = new[] { higherQualitySibling, lowerQualityExact }
                .OrderByDescending(decision => decision, comparer)
                .ToList();

            Assert.That(sorted[0], Is.SameAs(lowerQualityExact));
        }

        [Test]
        public void should_keep_chaptarr_format_priority_for_mixed_migrated_releases()
        {
            var author = CreateAuthorWithProfiles();
            var audiobook = CreateDecision(author, Quality.M4B, "The Hobbit");
            var ebook = CreateDecision(author, Quality.EPUB, "The Hobbit");
            var comparer = CreateComparer(bookshelfMatchCompatibility: true);

            var sorted = new[] { ebook, audiobook }
                .OrderByDescending(decision => decision, comparer)
                .ToList();

            Assert.That(sorted[0], Is.SameAs(audiobook));
        }

        [Test]
        public void should_use_bookshelf_custom_format_score_before_title_match_for_migrated_audiobooks()
        {
            var author = CreateAuthorWithProfiles();
            var highScoreSibling = CreateDecision(author, Quality.MP3, "The Hobbit: An Unexpected Journey", 100);
            var lowScoreExact = CreateDecision(author, Quality.MP3, "The Hobbit");
            var comparer = CreateComparer(bookshelfMatchCompatibility: true);

            var sorted = new[] { lowScoreExact, highScoreSibling }
                .OrderByDescending(decision => decision, comparer)
                .ToList();
            var comparison = comparer.CompareWithReasons(highScoreSibling, lowScoreExact);

            Assert.That(sorted[0], Is.SameAs(highScoreSibling));
            Assert.That(comparison.Result, Is.GreaterThan(0));
            Assert.That(comparison.Reasons.Single().Reason, Does.Contain("Custom format score"));
        }

        [Test]
        public void should_use_bookshelf_author_tags_for_migrated_protocol_priority()
        {
            var author = CreateAuthorWithProfiles();
            author.Tags = new HashSet<int> { 1 };
            author.AudiobookTags = new HashSet<int> { 2 };
            author.EbookTags = new HashSet<int>();

            var delayProfileService = new DelayProfileServiceStub(tags => tags.Contains(1)
                ? DownloadProtocol.Usenet
                : DownloadProtocol.Torrent);

            var usenet = CreateDecision(author, Quality.MP3, "The Hobbit", downloadProtocol: DownloadProtocol.Usenet);
            var torrent = CreateDecision(author, Quality.MP3, "The Hobbit", downloadProtocol: DownloadProtocol.Torrent);
            var comparer = CreateComparer(bookshelfMatchCompatibility: true, delayProfileService: delayProfileService);
            var comparison = comparer.CompareWithReasons(usenet, torrent);

            Assert.That(comparison.Result, Is.GreaterThan(0));
            Assert.That(comparison.Reasons.Single().Reason, Does.Contain("Usenet"));
        }

        [Test]
        public void should_prefer_revision_after_quality_for_migrated_audiobooks()
        {
            var author = CreateAuthorWithProfiles();
            var newer = CreateDecision(author, Quality.MP3, "The Hobbit", revision: new Revision(version: 2));
            var older = CreateDecision(author, Quality.MP3, "The Hobbit");
            var comparer = CreateComparer(bookshelfMatchCompatibility: true);
            var comparison = comparer.CompareWithReasons(newer, older);

            Assert.That(comparison.Result, Is.GreaterThan(0));
            Assert.That(comparison.Reasons.Single().Reason, Does.Contain("revision"));
        }

        [Test]
        public void should_preserve_bookshelf_size_rounding_for_migrated_audiobooks()
        {
            var author = CreateAuthorWithProfiles();
            var smaller = CreateDecision(author, Quality.MP3, "The Hobbit", releaseSize: 201 * 1024 * 1024);
            var larger = CreateDecision(author, Quality.MP3, "The Hobbit", releaseSize: 299 * 1024 * 1024);
            var delayProfileService = new DelayProfileServiceStub(_ => DownloadProtocol.Unknown);
            var comparer = CreateComparer(bookshelfMatchCompatibility: true, delayProfileService: delayProfileService);

            Assert.That(comparer.Compare(smaller, larger), Is.Zero);
        }

        private static DownloadDecisionComparer CreateComparer(bool bookshelfMatchCompatibility, IDelayProfileService delayProfileService = null)
        {
            return new DownloadDecisionComparer(
                ConfigServiceTestProxy.Create(bookshelfMatchCompatibility: bookshelfMatchCompatibility),
                delayProfileService,
                null,
                LogManager.GetCurrentClassLogger());
        }

        private static Author CreateAuthorWithProfiles()
        {
            var audiobookProfile = new QualityProfile
            {
                Id = 2,
                Name = "Audiobook",
                ProfileType = ProfileType.Audiobook,
                Items = new List<QualityProfileQualityItem>
                {
                    new QualityProfileQualityItem { Quality = Quality.MP3, Allowed = true },
                    new QualityProfileQualityItem { Quality = Quality.M4B, Allowed = true }
                }
            };
            var ebookProfile = new QualityProfile
            {
                Id = 1,
                Name = "Ebook",
                ProfileType = ProfileType.Ebook,
                Items = new List<QualityProfileQualityItem>
                {
                    new QualityProfileQualityItem { Quality = Quality.EPUB, Allowed = true }
                }
            };

            return new Author
            {
                Id = 123,
                Name = "J.R.R. Tolkien",
                AudiobookQualityProfileId = audiobookProfile.Id,
                AudiobookQualityProfile = audiobookProfile,
                EbookQualityProfileId = ebookProfile.Id,
                EbookQualityProfile = ebookProfile
            };
        }

        private static DownloadDecision CreateDecision(
            Author author,
            Quality quality,
            string matchedVariant,
            int customFormatScore = 0,
            long releaseSize = 0,
            DownloadProtocol downloadProtocol = DownloadProtocol.Unknown,
            Revision revision = null)
        {
            var mediaType = quality == Quality.EPUB ? BookMediaType.Ebook : BookMediaType.Audiobook;

            return new DownloadDecision(new RemoteBook
            {
                Author = author,
                Books = new List<Book> { new Book { Author = author, MediaType = mediaType } },
                Release = new ReleaseInfo { Title = matchedVariant, Size = releaseSize, DownloadProtocol = downloadProtocol },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel { Quality = quality, Revision = revision ?? new Revision() } },
                CustomFormatScore = customFormatScore,
                SearchCriteriaMatch = new TitleMatchResult
                {
                    IsMatch = true,
                    PrimaryTitle = "The Hobbit",
                    MatchedVariant = matchedVariant
                }
            });
        }

        private sealed class DelayProfileServiceStub : IDelayProfileService
        {
            private readonly Func<HashSet<int>, DownloadProtocol> _preferredProtocol;

            public DelayProfileServiceStub(Func<HashSet<int>, DownloadProtocol> preferredProtocol)
            {
                _preferredProtocol = preferredProtocol;
            }

            public DelayProfile BestForTags(HashSet<int> tagIds)
            {
                return new DelayProfile { PreferredProtocol = _preferredProtocol(tagIds) };
            }

            public DelayProfile Add(DelayProfile profile) => throw new NotImplementedException();
            public DelayProfile Update(DelayProfile profile) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public List<DelayProfile> All() => throw new NotImplementedException();
            public DelayProfile Get(int id) => throw new NotImplementedException();
            public List<DelayProfile> AllForTag(int tagId) => throw new NotImplementedException();
            public List<DelayProfile> AllForTags(HashSet<int> tagIds) => throw new NotImplementedException();
            public List<DelayProfile> Reorder(int id, int? afterId) => throw new NotImplementedException();
        }
    }
}
