using System;
using System.Collections.Generic;
using Chaptarr.Core.Test;
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
        public void should_accept_discography_when_all_books_are_released()
        {
            var spec = new DiscographySpecification(ConfigServiceTestProxy.Create(), LogManager.GetCurrentClassLogger());
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

            Assert.That(decision.Accepted, Is.True);
        }

        [Test]
        public void should_reject_discography_when_a_book_is_unreleased_or_has_no_release_date()
        {
            var spec = new DiscographySpecification(ConfigServiceTestProxy.Create(), LogManager.GetCurrentClassLogger());
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo { Title = "Pierce Brown Discography 2020-2030" },
                ParsedBookInfo = new ParsedBookInfo { Discography = true },
                Books = new List<Book>
                {
                    new Book { ReleaseDate = DateTime.UtcNow.AddYears(-1) },
                    new Book { ReleaseDate = DateTime.UtcNow.AddYears(1) },
                    new Book { ReleaseDate = null }
                }
            };

            var decision = spec.IsSatisfiedBy(remoteBook, new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Reason, Is.EqualTo("Discography release rejected. All books haven't released yet."));
        }

        [Test]
        public void should_accept_non_discography_releases()
        {
            var spec = new DiscographySpecification(ConfigServiceTestProxy.Create(), LogManager.GetCurrentClassLogger());
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo { Title = "Pierce Brown - Red Rising" },
                ParsedBookInfo = new ParsedBookInfo { Discography = false }
            };

            var decision = spec.IsSatisfiedBy(remoteBook, new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.True);
        }

        [Test]
        public void should_preserve_bookshelf_acceptance_for_empty_discographies_on_migrated_installs()
        {
            var spec = new DiscographySpecification(
                ConfigServiceTestProxy.Create(bookshelfMatchCompatibility: true),
                LogManager.GetCurrentClassLogger());
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo { Title = "Unresolved Discography" },
                ParsedBookInfo = new ParsedBookInfo { Discography = true },
                Books = new List<Book>()
            };

            var decision = spec.IsSatisfiedBy(remoteBook, new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.True);
        }

        [Test]
        public void should_continue_rejecting_empty_discographies_on_fresh_installs()
        {
            var spec = new DiscographySpecification(ConfigServiceTestProxy.Create(), LogManager.GetCurrentClassLogger());
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo { Title = "Unresolved Discography" },
                ParsedBookInfo = new ParsedBookInfo { Discography = true },
                Books = new List<Book>()
            };

            var decision = spec.IsSatisfiedBy(remoteBook, new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.False);
        }
    }
}
