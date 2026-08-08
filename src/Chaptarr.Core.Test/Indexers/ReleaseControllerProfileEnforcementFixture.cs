using System.Collections.Generic;
using Chaptarr.Api.V1.Indexers;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class ReleaseControllerInteractiveDownloadFixture
    {
        [Test]
        public void should_allow_interactive_download_for_quality_profile_rejections()
        {
            var decision = BuildDecision(
                new Rejection("Author has no audiobook quality profile configured", RejectionType.Permanent, canBypass: true, category: "Format"));

            Assert.That(ReleaseController.ShouldBlockInteractiveDownload(decision), Is.False);
        }

        [Test]
        public void should_allow_interactive_download_for_hard_filters()
        {
            var decision = BuildDecision(
                new Rejection("Language English is not allowed", RejectionType.Permanent, canBypass: false, category: "Language"));

            Assert.That(ReleaseController.ShouldBlockInteractiveDownload(decision), Is.False);
        }

        [Test]
        public void should_allow_interactive_download_for_blocklisted_releases()
        {
            var decision = BuildDecision(
                new Rejection("Release is blocklisted", RejectionType.Permanent, canBypass: false, category: "General"));

            Assert.That(ReleaseController.ShouldBlockInteractiveDownload(decision), Is.False);
        }

        [Test]
        public void should_block_interactive_download_without_resolved_book_target()
        {
            var decision = new DownloadDecision(new RemoteBook
            {
                Books = new List<Book>(),
                Release = new ReleaseInfo { Title = "Test Release" }
            });

            Assert.That(ReleaseController.ShouldBlockInteractiveDownload(decision), Is.True);
        }

        [Test]
        public void should_allow_interactive_download_for_non_profile_soft_filters()
        {
            var decision = BuildDecision(
                new Rejection("Not enough seeders", RejectionType.Permanent, canBypass: true, category: "Seeders"));

            Assert.That(ReleaseController.ShouldBlockInteractiveDownload(decision), Is.False);
        }

        [Test]
        public void should_allow_interactive_download_for_bypassable_title_match_warning()
        {
            var decision = BuildDecision(
                new Rejection("Title/Author mismatch", RejectionType.Permanent, canBypass: true, category: "Matching"));

            Assert.That(ReleaseController.ShouldBlockInteractiveDownload(decision), Is.False);
        }

        [Test]
        public void should_allow_interactive_download_for_bypassable_release_profile_rejection()
        {
            var decision = BuildDecision(
                new Rejection("Contains these ignored terms: graphic audio", RejectionType.Permanent, canBypass: true, category: "Release Profile"));

            Assert.That(ReleaseController.ShouldBlockInteractiveDownload(decision), Is.False);
        }

        [Test]
        public void should_treat_quality_and_format_categories_as_profile_enforcement()
        {
            Assert.That(ReleaseController.IsProfileEnforcementRejection(new Rejection("No quality profile configured for EPUB files", RejectionType.Permanent, canBypass: true, category: "Quality")), Is.True);
            Assert.That(ReleaseController.IsProfileEnforcementRejection(new Rejection("Author has no ebook quality profile configured", RejectionType.Permanent, canBypass: true, category: "Format")), Is.True);
            Assert.That(ReleaseController.IsProfileEnforcementRejection(new Rejection("Not enough seeders", RejectionType.Permanent, canBypass: true, category: "Seeders")), Is.False);
            Assert.That(ReleaseController.IsReleaseProfileRejection(new Rejection("Contains these ignored terms: graphic audio", RejectionType.Permanent, canBypass: true, category: "Release Profile")), Is.True);
        }

        private static DownloadDecision BuildDecision(params Rejection[] rejections)
        {
            var remoteBook = new RemoteBook
            {
                Books = new List<Book> { new Book { Id = 1, Title = "Test" } },
                Release = new ReleaseInfo { Title = "Test Release" }
            };

            return new DownloadDecision(remoteBook, rejections);
        }
    }
}
