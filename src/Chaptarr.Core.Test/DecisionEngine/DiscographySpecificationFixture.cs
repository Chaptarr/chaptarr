using System;
using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.DecisionEngine
{
    [TestFixture]
    public class DiscographySpecificationFixture
    {
        [Test]
        public void should_hard_reject_discography_even_when_all_books_are_released()
        {
            var spec = new DiscographySpecification(LogManager.GetCurrentClassLogger());
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo { Title = "Pierce Brown Discography 2014-2024" },
                ParsedBookInfo = new ParsedBookInfo { Discography = true },
                Books = new List<Book>
                {
                    new Book { ReleaseDate = DateTime.UtcNow.AddYears(-10) },
                    new Book { ReleaseDate = DateTime.UtcNow.AddYears(-1) }
                }
            };

            var decision = spec.IsSatisfiedBy(remoteBook, new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.CanBypass, Is.False);
            Assert.That(decision.Category, Is.EqualTo("Pack"));
            Assert.That(decision.Reason, Is.EqualTo("Release appears to contain multiple books (discography)"));
        }

        [Test]
        public void should_accept_non_discography_releases()
        {
            var spec = new DiscographySpecification(LogManager.GetCurrentClassLogger());
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo { Title = "Pierce Brown - Red Rising" },
                ParsedBookInfo = new ParsedBookInfo { Discography = false }
            };

            var decision = spec.IsSatisfiedBy(remoteBook, new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.True);
        }
    }
}
