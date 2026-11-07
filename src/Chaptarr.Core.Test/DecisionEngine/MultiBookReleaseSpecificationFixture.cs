using System;
using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class MultiBookReleaseSpecificationFixture
    {
        [Test]
        public void should_hard_reject_multi_book_pack()
        {
            var spec = new MultiBookReleaseSpecification(LogManager.GetCurrentClassLogger());
            var decision = spec.IsSatisfiedBy(BuildRemoteBook(ReleasePackDetectionVerdict.MultipleBooks), new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.CanBypass, Is.False);
            Assert.That(decision.Category, Is.EqualTo("Pack"));
            Assert.That(decision.Reason, Is.EqualTo("Release appears to contain multiple books"));
        }

        [Test]
        public void should_allow_discography_pack_when_every_book_is_released()
        {
            var spec = new MultiBookReleaseSpecification(LogManager.GetCurrentClassLogger());
            var remoteBook = BuildRemoteBook(ReleasePackDetectionVerdict.MultipleBooks);
            remoteBook.ParsedBookInfo.Discography = true;
            remoteBook.Books = new List<Book>
            {
                new Book { ReleaseDate = DateTime.UtcNow.AddYears(-5) },
                new Book { ReleaseDate = DateTime.UtcNow.AddDays(-1) }
            };

            var decision = spec.IsSatisfiedBy(remoteBook, new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.True);
        }

        [Test]
        public void should_soft_reject_audiobook_fragment()
        {
            var spec = new MultiBookReleaseSpecification(LogManager.GetCurrentClassLogger());
            var decision = spec.IsSatisfiedBy(BuildRemoteBook(ReleasePackDetectionVerdict.AudiobookFragment), new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.CanBypass, Is.True);
            Assert.That(decision.Category, Is.EqualTo("Pack"));
            Assert.That(decision.Reason, Is.EqualTo("Release may be one disc/part of a larger audiobook"));
        }

        [TestCase(ReleasePackDetectionVerdict.None)]
        [TestCase(ReleasePackDetectionVerdict.SingleBookSplitRelease)]
        public void should_accept_non_pack_verdicts(ReleasePackDetectionVerdict verdict)
        {
            var spec = new MultiBookReleaseSpecification(LogManager.GetCurrentClassLogger());
            var decision = spec.IsSatisfiedBy(BuildRemoteBook(verdict), new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.True);
        }

        private static RemoteBook BuildRemoteBook(ReleasePackDetectionVerdict verdict)
        {
            return new RemoteBook
            {
                Release = new ReleaseInfo { Title = "Release" },
                ParsedBookInfo = new ParsedBookInfo(),
                PackDetection = new ReleasePackDetection
                {
                    Verdict = verdict,
                    PackType = "test",
                    MatchedValue = "test"
                }
            };
        }
    }
}
