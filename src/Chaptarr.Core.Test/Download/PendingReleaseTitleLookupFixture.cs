using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NLog;
using NUnit.Framework;
using Chaptarr.Core.Test.Parser;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class PendingReleaseTitleLookupFixture
    {
        private sealed class Observation { public int BatchWrites; public int Events; }

        private static PendingRelease Pending(int id, Author author, ParsedBookInfo parsed) => new()
        {
            Id = id, AuthorId = author.Id, Title = $"release-{id}", ParsedBookInfo = parsed,
            Release = new ReleaseInfo { Title = $"release-{id}", Indexer = "Test Indexer" }
        };

        private static PendingReleaseService Service(ParsingServiceTitleLookupFixture.Harness harness,
            List<PendingRelease> pending, Action authorRead = null, Observation seen = null)
        {
            var repository = ParsingServiceTitleLookupFixture.Proxy<IPendingReleaseRepository>((method, args) =>
            {
                if (method.Name == nameof(IPendingReleaseRepository.AllByAuthorId))
                {
                    return pending.Where(release => release.AuthorId == (int)args[0]).ToList();
                }

                if (method.Name == nameof(IPendingReleaseRepository.UpdateMany))
                {
                    seen.BatchWrites++;
                    foreach (var release in (IList<PendingRelease>)args[0])
                    {
                        var index = pending.FindIndex(value => value.Id == release.Id);
                        Assert.That(index, Is.GreaterThanOrEqualTo(0));
                        pending[index] = release;
                    }
                    return null;
                }

                if (method.Name == "Update")
                {
                    return args[0];
                }

                throw new NotImplementedException(method.Name);
            });
            var authors = ParsingServiceTitleLookupFixture.Proxy<IAuthorService>((method, args) =>
            {
                if (method.Name is nameof(IAuthorService.GetAuthors) or nameof(IAuthorService.GetExistingAuthors))
                {
                    authorRead?.Invoke();
                    return new List<Author> { harness.Author };
                }

                throw new NotImplementedException(method.Name);
            });
            var calculator = ParsingServiceTitleLookupFixture.Proxy<ICustomFormatCalculationService>((method, args) => new List<CustomFormat>());
            var aggregation = ParsingServiceTitleLookupFixture.Proxy<IRemoteBookAggregationService>((method, args) => null);
            var events = ParsingServiceTitleLookupFixture.Proxy<IEventAggregator>((method, args) => { seen.Events++; return null; });
            return new PendingReleaseService(null, repository, authors, harness.Parser, null, null, null,
                calculator, aggregation, null, null, events, LogManager.GetCurrentClassLogger(), harness.Books);
        }

        [TestCase(1)]
        [TestCase(25)]
        public void unresolved_pending_batch_should_load_the_catalog_once(int requestCount)
        {
            var catalog = Enumerable.Range(1, 1000).Select(id => ParsingServiceTitleLookupFixture.Make(id, $"Distinct title number {id}")).ToList();
            var harness = new ParsingServiceTitleLookupFixture.Harness(catalog);
            var pending = catalog.Take(requestCount).Select(book => Pending(book.Id, harness.Author,
                ParsingServiceTitleLookupFixture.Parsed(book.Title))).ToList();
            var authorReads = 0;
            var seen = new Observation();
            var service = Service(harness, pending, () => authorReads++, seen);
            var timer = Stopwatch.StartNew();

            var remotes = service.GetPendingRemoteBooks(harness.Author.Id);

            timer.Stop();
            Assert.That(remotes.Select(remote => remote.Books.Single().Id), Is.EqualTo(catalog.Take(requestCount).Select(book => book.Id)));
            Assert.That(authorReads, Is.EqualTo(1));
            Assert.That(harness.BookReads, Is.EqualTo(1));
            Assert.That(harness.EditionReads, Is.EqualTo(1));
            Assert.That(harness.SeriesReads, Is.EqualTo(1));
            Assert.That(seen.BatchWrites, Is.EqualTo(1));
            Assert.That(seen.Events, Is.EqualTo(1));
            Assert.That(pending.SelectMany(row => row.AdditionalInfo.BookIds), Is.EqualTo(catalog.Take(requestCount).Select(book => book.Id)));
            TestContext.Progress.WriteLine($"PENDING_BATCH requests={requestCount} books=1000 authorBatchReads={authorReads} bookReads={harness.BookReads} editionReads={harness.EditionReads} seriesReads={harness.SeriesReads} elapsedMs={timer.Elapsed.TotalMilliseconds:F3}");
        }

        [Test]
        public void pending_batch_should_preserve_media_and_item_failure_boundaries()
        {
            var audio = ParsingServiceTitleLookupFixture.Make(501, "The Paper Lanterns");
            var ebook = ParsingServiceTitleLookupFixture.Make(502, "Paper Lanterns", BookMediaType.Ebook);
            var internalWords = ParsingServiceTitleLookupFixture.Make(503, "I Married a Naga");
            var harness = new ParsingServiceTitleLookupFixture.Harness(new List<Book> { audio, ebook, internalWords });
            var invalid = ParsingServiceTitleLookupFixture.Parsed("Bad date range");
            invalid.Discography = true;
            invalid.DiscographyStart = 10000;
            invalid.DiscographyEnd = 10000;
            var pending = new List<PendingRelease>
            {
                Pending(1, harness.Author, invalid),
                Pending(2, harness.Author, ParsingServiceTitleLookupFixture.Parsed("Paper Lanterns")),
                Pending(3, harness.Author, ParsingServiceTitleLookupFixture.Parsed("Paper Lanterns", BookMediaType.Ebook)),
                Pending(4, harness.Author, ParsingServiceTitleLookupFixture.Parsed("I Married Naga"))
            };

            var seen = new Observation();
            var remotes = Service(harness, pending, seen: seen).GetPendingRemoteBooks(harness.Author.Id);

            Assert.That(remotes.Select(remote => remote.Books.Select(book => book.Id).ToArray()),
                Is.EqualTo(new[] { Array.Empty<int>(), new[] { audio.Id }, new[] { ebook.Id }, Array.Empty<int>() }));
            Assert.That(harness.BookReads, Is.EqualTo(1));
            Assert.That(seen.BatchWrites, Is.EqualTo(1));
            Assert.That(seen.Events, Is.EqualTo(1));
            Assert.That(pending.SelectMany(row => row.AdditionalInfo?.BookIds ?? new List<int>()), Is.EqualTo(new[] { audio.Id, ebook.Id }));
        }

        [Test]
        public void already_identified_pending_releases_should_require_no_author_or_catalog_reload()
        {
            var book = ParsingServiceTitleLookupFixture.Make(501, "Paper Lanterns");
            var harness = new ParsingServiceTitleLookupFixture.Harness(new List<Book> { book });
            var pending = Pending(1, harness.Author, ParsingServiceTitleLookupFixture.Parsed(book.Title));
            var authorReads = 0;
            var seen = new Observation();
            var service = Service(harness, new List<PendingRelease> { pending }, () => authorReads++, seen);
            var remote = new RemoteBook
            {
                Author = harness.Author, Books = new List<Book> { book },
                ParsedBookInfo = pending.ParsedBookInfo, Release = pending.Release
            };

            service.AddMany(new List<Tuple<DownloadDecision, PendingReleaseReason>>
            {
                Tuple.Create(new DownloadDecision(remote), pending.Reason)
            });

            Assert.That(pending.RemoteBook.Books, Is.SameAs(remote.Books));
            Assert.That(authorReads + harness.BookReads + harness.AuthorReads + harness.EditionReads + harness.SeriesReads, Is.Zero);
            Assert.That(seen.BatchWrites, Is.EqualTo(1));
            Assert.That(seen.Events, Is.EqualTo(1));
            Assert.That(pending.AdditionalInfo.BookIds, Is.EqualTo(new[] { book.Id }));
        }
    }
}
