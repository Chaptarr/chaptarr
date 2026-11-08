using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Newtonsoft.Json;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.IndexerSearch;

namespace Chaptarr.Core.Test.Books
{
    public partial class PersistedAliasPendingLifecycleFixture
    {
        [Test]
        public void add_only_work_survives_unavailable_catalog_then_materializes_unmonitored(
            [Values(false, true)] bool postgres, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var audio = media == BookMediaType.Audiobook;
                var id = h.Pending.EnqueueAsync(h.Author.HardcoverAuthorId, new MonitoringConfig
                {
                    CreateAudiobook = audio, CreateEbook = !audio,
                    AudiobookMonitorExistingMode = audio ? MonitorTypes.None : null,
                    EbookMonitorExistingMode = !audio ? MonitorTypes.None : null,
                    AudiobookBooksToAdd = audio ? new() { "hc:7000" } : null,
                    EbookBooksToAdd = !audio ? new() { "hc:7000" } : null,
                    AudiobookRootFolderPath = audio ? @"C:\Audio" : null,
                    EbookRootFolderPath = !audio ? @"C:\Ebooks" : null
                }, "AddBook").GetAwaiter().GetResult();
                h.Run();
                var pending = h.Requests.Get(id);
                Assert.That(JsonConvert.DeserializeObject<List<string>>(audio ? pending.AudiobookBooksToAdd : pending.EbookBooksToAdd),
                    Is.EqualTo(new[] { "hc:7000" }));
                Assert.That(h.Books.All(), Is.Empty);
                Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                var book = h.AddBook(media, "hc:7000", "hc:edition:7000");
                h.WorkReady = true;
                pending.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
                h.Requests.Update(pending);
                h.Run();
                Assert.That(h.BookService.GetBook(book.Id).IsMonitored(), Is.False);
                Assert.That(h.BookService.GetBook(book.Id).AddOptions.AddType, Is.EqualTo(BookAddType.Manual));
                Assert.That(h.Requests.Find(id), Is.Null);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void queued_explicit_narration_respects_do_not_monitor(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { WorkReady = true };
                var book = h.AddBook(BookMediaType.Audiobook, "hc:7000", "hc:edition:7000");
                var id = h.Pending.EnqueueAsync(h.Author.HardcoverAuthorId, new MonitoringConfig
                {
                    CreateAudiobook = true, CreateEbook = false,
                    AudiobookMonitorExistingMode = MonitorTypes.None,
                    AudiobookBooksToAdd = new() { "hc:7000" },
                    AudiobookNarrations = new() { new() { WorkProviderId = "hc:7000", EditionProviderId = "hc:edition:7000",
                        NarratorNames = new() { "Fixture Narrator" }, RequiresEdition = true } },
                    AudiobookRootFolderPath = @"C:\Audio"
                }, "AddBook").GetAwaiter().GetResult();
                h.Run();
                Assert.That(h.BookService.GetBook(book.Id).IsMonitored(), Is.False);
                Assert.That(h.Editions.FindRowsByBook(new[] { book.Id }).Single().ManualAdd, Is.True);
                Assert.That(h.Requests.Find(id), Is.Null);
            });
        }

        [Test]
        public void repeated_book_request_keeps_the_latest_explicit_monitoring_choice(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(false, true)] bool monitor)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { WorkReady = true };
                var book = h.AddBook(media, "hc:7000", "hc:edition:7000");
                var audio = media == BookMediaType.Audiobook;
                MonitoringConfig Request(bool value) => new()
                {
                    CreateAudiobook = audio, CreateEbook = !audio,
                    AudiobookMonitorExistingMode = audio ? MonitorTypes.None : null,
                    EbookMonitorExistingMode = !audio ? MonitorTypes.None : null,
                    AudiobookBooksToAdd = audio && !value ? new() { "hc:7000" } : null,
                    EbookBooksToAdd = !audio && !value ? new() { "hc:7000" } : null,
                    AudiobookBooksToMonitor = audio && value ? new() { "hc:7000" } : null,
                    EbookBooksToMonitor = !audio && value ? new() { "hc:7000" } : null,
                    AudiobookRootFolderPath = audio ? @"C:\Audio" : null,
                    EbookRootFolderPath = !audio ? @"C:\Ebooks" : null
                };
                var id = h.Pending.EnqueueAsync(h.Author.HardcoverAuthorId, Request(!monitor), "AddBook").GetAwaiter().GetResult();
                Assert.That(h.Pending.EnqueueAsync(h.Author.HardcoverAuthorId, Request(monitor), "AddBook").GetAwaiter().GetResult(), Is.EqualTo(id));
                var saved = h.Requests.Get(id);
                saved.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
                h.Requests.Update(saved);
                h.Run();
                Assert.That(h.BookService.GetBook(book.Id).IsMonitored(), Is.EqualTo(monitor));
                Assert.That(h.Requests.Find(id), Is.Null);
            });
        }

        [Test]
        public void add_only_requests_survive_queue_merge_and_restart_without_becoming_monitored(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType media,
            [Values(false, true)] bool search,
            [Values(false, true)] bool retry)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { WorkReady = true };
                var audio = media == BookMediaType.Audiobook;
                var addOnly = h.AddBook(media, "hc:7000", "hc:edition:7000");
                var monitored = h.AddBook(media, "hc:7001", "hc:edition:7001");
                h.BookService.SetBookMonitored(addOnly.Id, true);
                MonitoringConfig Request(bool monitor) => new()
                {
                    CreateAudiobook = audio, CreateEbook = !audio,
                    AudiobookMonitorExistingMode = audio ? (monitor ? MonitorTypes.SpecificBook : MonitorTypes.None) : null,
                    EbookMonitorExistingMode = !audio ? (monitor ? MonitorTypes.SpecificBook : MonitorTypes.None) : null,
                    AudiobookBooksToAdd = audio && !monitor ? new() { "hc:7000" } : null,
                    EbookBooksToAdd = !audio && !monitor ? new() { "hc:7000" } : null,
                    AudiobookBooksToMonitor = audio && monitor ? new() { "hc:7001" } : null,
                    EbookBooksToMonitor = !audio && monitor ? new() { "hc:7001" } : null,
                    AudiobookBooksToSearch = audio && !monitor && search ? new() { "hc:7000" } : null,
                    EbookBooksToSearch = !audio && !monitor && search ? new() { "hc:7000" } : null,
                    AudiobookRootFolderPath = audio ? @"C:\Audio" : null,
                    EbookRootFolderPath = !audio ? @"C:\Ebooks" : null
                };
                var id = h.Pending.EnqueueAsync(h.Author.HardcoverAuthorId, Request(false), "AddBook").GetAwaiter().GetResult();
                Assert.That(h.Pending.EnqueueAsync(h.Author.HardcoverAuthorId, Request(true), "AddBook").GetAwaiter().GetResult(), Is.EqualTo(id));
                var saved = h.Requests.Get(id);
                Assert.That(JsonConvert.DeserializeObject<List<string>>(audio ? saved.AudiobookBooksToAdd : saved.EbookBooksToAdd), Is.EqualTo(new[] { "hc:7000" }));
                saved.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
                h.Requests.Update(saved);

                if (retry)
                {
                    h.RejectedBookId = addOnly.Id;
                    h.Run();
                    if (search)
                    {
                        Assert.That(h.BookService.GetBook(addOnly.Id).IsMonitored(), Is.False);
                        var remaining = h.Requests.Get(id);
                        Assert.That(JsonConvert.DeserializeObject<List<string>>(audio ? remaining.AudiobookBooksToAdd : remaining.EbookBooksToAdd),
                            Is.EqualTo(new[] { "hc:7000" }));
                        remaining.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
                        h.Requests.Update(remaining);
                    }
                    h.RejectedBookId = null;
                }
                h.Run();

                Assert.Multiple(() =>
                {
                    Assert.That(h.BookService.GetBook(addOnly.Id).IsMonitored(), Is.False);
                    Assert.That(h.BookService.GetBook(monitored.Id).IsMonitored(), Is.True);
                    Assert.That(h.BookService.GetBook(addOnly.Id).AddOptions.AddType, Is.EqualTo(BookAddType.Manual));
                    Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds),
                        Is.EquivalentTo(search ? new[] { addOnly.Id } : Array.Empty<int>()));
                    Assert.That(h.Requests.Find(id), Is.Null);
                    Assert.That(h.Authors.Get(h.Author.Id).AudiobookMonitored, Is.False);
                    Assert.That(h.Authors.Get(h.Author.Id).EbookMonitored, Is.False);
                });
            });
        }
    }
}
