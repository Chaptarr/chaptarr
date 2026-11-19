using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Repositories;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    [NonParallelizable]
    public class SeriesNarratorNameConsolidationFixture
    {
        [TestCase(false, BookMediaType.Audiobook, "Auliʻi Cravalho", "Auli'I Cravalho", 1)]
        [TestCase(true, BookMediaType.Audiobook, "Auliʻi Cravalho", "Auli'I Cravalho", 1)]
        [TestCase(false, BookMediaType.Ebook, "Auliʻi Cravalho", "Auli'I Cravalho", 1)]
        [TestCase(true, BookMediaType.Ebook, "Auliʻi Cravalho", "Auli'I Cravalho", 1)]
        [TestCase(false, BookMediaType.Audiobook, "Ana Bell", "An Abell", 2)]
        [TestCase(true, BookMediaType.Audiobook, "Ana Bell", "An Abell", 2)]
        public void persisted_series_narrators_should_share_exact_word_identity(bool postgres, BookMediaType media, string first, string second, int expected)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var logger = LogManager.GetCurrentClassLogger();
                var author = new AuthorRepository(database, events, logger).Insert(new Author
                {
                    Name = "Series voice writer", CleanName = "seriesvoicewriter", SortName = "seriesvoicewriter",
                    TitleSlug = "series-voice-writer", Path = @"C:\Books\Series voice writer".AsOsAgnostic(), Added = DateTime.UtcNow
                });
                var links = new SeriesBookLinkRepository(database, events);
                var seriesRepository = new SeriesRepository(database, events, links, logger);
                var series = seriesRepository.Insert(new Series { Title = "Voice series", TitleSlug = "voice-series", MediaType = media });
                var books = new BookRepository(database, events);
                var editions = new EditionRepository(database, events);
                foreach (var name in new[] { first, second })
                {
                    var book = books.Insert(new Book
                    {
                        Title = "Voice volume " + name, CleanTitle = "voicevolume", TitleSlug = Guid.NewGuid().ToString("N"),
                        AuthorId = author.Id, MediaType = media, Added = DateTime.UtcNow, Narrator = name
                    });
                    links.Insert(new SeriesBookLink { SeriesId = series.Id, BookId = book.Id });
                    editions.Insert(new Edition { BookId = book.Id, Title = book.Title, Monitored = true, Narrator = name });
                }

                var optionReads = 0;
                var options = InterfaceProxy.Proxy<IBookNarratorOptionRepository>((method, _) =>
                {
                    if (method.Name == nameof(IBookNarratorOptionRepository.GetPreferredByBookId))
                    {
                        optionReads++;
                        return new List<BookNarratorOption>();
                    }

                    throw new InvalidOperationException(method.Name);
                });
                var service = new SeriesNarratorService(seriesRepository, books, options,
                    InterfaceProxy.Proxy<IBookNarratorService>((method, _) => throw new InvalidOperationException(method.Name)), logger);
                Assert.That(service.GetSeriesNarrators(series.Id), Has.Count.EqualTo(expected));
                Assert.That(optionReads, Is.EqualTo(2));

                var discovery = new SeriesNarratorDiscoveryService(
                    InterfaceProxy.Proxy<ISeriesService>((method, _) => method.Name == nameof(ISeriesService.GetSeries) ? series : throw new InvalidOperationException(method.Name)),
                    InterfaceProxy.Proxy<ISeriesBookLinkService>((method, _) => method.Name == nameof(ISeriesBookLinkService.GetLinksBySeries) ? links.GetLinksBySeries(series.Id) : throw new InvalidOperationException(method.Name)),
                    InterfaceProxy.Proxy<IBookService>((method, _) => method.Name == nameof(IBookService.GetBooks) ? books.GetBooksBySeries(series.Id) : throw new InvalidOperationException(method.Name)),
                    InterfaceProxy.Proxy<IEditionService>((method, args) => method.Name == nameof(IEditionService.GetEditionsByBook) ? editions.FindRowsByBook(new[] { (int)args[0] }) : throw new InvalidOperationException(method.Name)),
                    InterfaceProxy.Proxy<IMultiCopySeriesService>((method, _) => method.Name == nameof(IMultiCopySeriesService.GetAllVariants) ? new List<Series>() : throw new InvalidOperationException(method.Name)), logger);
                var result = discovery.DiscoverNarratorsForSeries(series.Id).GetAwaiter().GetResult();
                Assert.That(result.CompleteNarrators.Count + result.PartialNarrators.Count, Is.EqualTo(expected));
            });
        }

        [TestCase("CD1", true)]
        [TestCase("Disc 2", true)]
        [TestCase("Disk_03", true)]
        [TestCase("ＣＤ１", true)]
        [TestCase("discworld2", false)]
        [TestCase("CDbook1", false)]
        [TestCase("Part 1", false)]
        public void discovery_disc_grouping_should_reuse_the_shared_predicate(string folder, bool expected)
        {
            var type = typeof(DiscoveryWorker).Assembly.GetTypes().Single(type =>
                type.FullName.StartsWith(typeof(DiscoveryWorker).FullName, StringComparison.Ordinal) &&
                type.GetMethod("IsDiscOnlyFolderName", BindingFlags.NonPublic | BindingFlags.Static) != null);
            var method = type.GetMethod("IsDiscOnlyFolderName", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.Multiple(() =>
            {
                Assert.That((bool)method.Invoke(null, new object[] { folder }), Is.EqualTo(expected));
                Assert.That(BookCoalescingHelper.IsDiscOnlyFolderName(folder), Is.EqualTo(expected));
            });
        }
    }
}
