using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    public partial class PersistedAliasPendingLifecycleFixture
    {
        private static RequestedNarration TypedPendingEdition(BookMediaType side, string work, string edition, bool search = true)
        {
            var payload = JObject.FromObject(new RequestedNarration
            {
                WorkProviderId = work, EditionProviderId = edition, RequiresEdition = true, Search = search
            });
            payload["MediaType"] = (int)side;
            return payload.ToObject<RequestedNarration>();
        }

        private static MonitoringConfig TypedPendingConfig(BookMediaType side, string work, string edition)
        {
            return new MonitoringConfig
            {
                CreateAudiobook = side == BookMediaType.Audiobook, CreateEbook = side == BookMediaType.Ebook,
                AudiobookBooksToMonitor = side == BookMediaType.Audiobook ? new List<string> { work } : null,
                AudiobookBooksToSearch = side == BookMediaType.Audiobook ? new List<string> { work } : null,
                EbookBooksToMonitor = side == BookMediaType.Ebook ? new List<string> { work } : null,
                EbookBooksToSearch = side == BookMediaType.Ebook ? new List<string> { work } : null,
                AudiobookNarrations = new List<RequestedNarration> { TypedPendingEdition(side, work, edition) },
                AudiobookRootFolderPath = @"C:\Audio".AsOsAgnostic(), EbookRootFolderPath = @"C:\Ebooks".AsOsAgnostic(),
                AudiobookQualityProfileId = 1, AudiobookMetadataProfileId = 1, EbookQualityProfileId = 1, EbookMetadataProfileId = 1,
                RequestedBy = "ImportList:Explicit source"
            };
        }

        private static Edition AddPendingEdition(Harness h, Book book, string edition)
        {
            return h.Editions.Insert(new Edition
            {
                BookId = book.Id, Title = "Requested edition", ForeignEditionId = edition, HardcoverEditionId = edition,
                ReadingFormatId = book.MediaType == BookMediaType.Ebook ? 3 : 2, Language = "eng", Narrator = "Requested Narrator"
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void legacy_narration_json_defaults_to_audio_and_does_not_pin_ebook_sibling(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var audio = h.AddBook(work: "hc:1001", edition: "hc:edition:201");
                var ebook = h.AddBook(BookMediaType.Ebook, "hc:1001", "hc:edition:201");
                var requested = AddPendingEdition(h, audio, "hc:edition:202");
                AddPendingEdition(h, ebook, "hc:edition:202");
                var item = h.Request(new List<string> { "hc:1001" });
                item.AudiobookNarrations = "[{\"WorkProviderId\":\"hc:1001\",\"EditionProviderId\":\"hc:edition:202\",\"RequiresEdition\":true,\"Search\":true}]";
                h.Requests.Update(item);
                var decoded = JsonConvert.DeserializeObject<List<RequestedNarration>>(h.Requests.Get(item.Id).AudiobookNarrations).Single();
                Assert.That(JObject.FromObject(decoded)["MediaType"]?.Value<int>() ?? 0, Is.EqualTo((int)BookMediaType.Audiobook));
                h.Run();
                Assert.Multiple(() =>
                {
                    Assert.That(h.Requests.All(), Is.Empty);
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(h.BookService.GetBook(audio.Id)).Id, Is.EqualTo(requested.Id));
                    Assert.That(h.BookService.GetBook(ebook.Id).AnyEditionOk, Is.True);
                    Assert.That(h.BookService.GetBook(ebook.Id).Editions.Any(edition => edition.ManualAdd), Is.False);
                    Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(command => command.BookIds), Is.EqualTo(new[] { audio.Id }));
                });
            });
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void typed_requests_merge_reload_repeat_and_cancel_only_removed_side(bool postgres, BookMediaType removedSide)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                h.Pending.EnqueueAsync("hc:9001", TypedPendingConfig(BookMediaType.Audiobook, "hc:1001", "hc:edition:202"), "ImportList:Explicit source").GetAwaiter().GetResult();
                h.Pending.EnqueueAsync("hc:9001", TypedPendingConfig(BookMediaType.Ebook, "hc:1001", "hc:edition:202"), "ImportList:Explicit source").GetAwaiter().GetResult();
                var pending = h.Requests.GetByProviderId("hc:9001");
                var raw = JArray.Parse(pending.AudiobookNarrations);
                Assert.Multiple(() =>
                {
                    Assert.That(raw, Has.Count.EqualTo(2));
                    Assert.That(raw.Select(request => request["MediaType"]?.Value<int>()), Is.EquivalentTo(new int?[] { 0, 1 }));
                    Assert.That(raw.Select(request => request["EditionProviderId"]?.Value<string>()), Is.All.EqualTo("hc:edition:202"));
                    Assert.That(raw.Select(request => request["Search"]?.Value<bool>()), Is.All.True);
                });
                var version = pending.Version;
                h.Pending.EnqueueAsync("hc:9001", TypedPendingConfig(BookMediaType.Ebook, "HC:1001", "HC:EDITION:202"), "ImportList:Explicit source").GetAwaiter().GetResult();
                var repeated = h.Requests.Get(pending.Id);
                Assert.That(JArray.Parse(repeated.AudiobookNarrations), Has.Count.EqualTo(2));
                Assert.That(repeated.Version, Is.EqualTo(version));
                var changed = AuthorsCopy(h.Author);
                if (removedSide == BookMediaType.Audiobook) { changed.AudiobookRootFolderPath = null; changed.AudiobookQualityProfileId = null; }
                else { changed.EbookRootFolderPath = null; changed.EbookQualityProfileId = null; }
                h.Pending.Handle(new AuthorEditedEvent(changed, h.Author));
                var cancelled = h.Requests.Get(pending.Id);
                var retainedSide = removedSide == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook;
                Assert.Multiple(() =>
                {
                    Assert.That(cancelled.IsActive(), Is.True);
                    Assert.That(cancelled.HasAudiobook(), Is.EqualTo(retainedSide == BookMediaType.Audiobook));
                    Assert.That(cancelled.HasEbook(), Is.EqualTo(retainedSide == BookMediaType.Ebook));
                    Assert.That(JArray.Parse(cancelled.AudiobookNarrations).Single()["MediaType"]?.Value<int>(), Is.EqualTo((int)retainedSide));
                    Assert.That(JArray.Parse(cancelled.AudiobookNarrations).Single()["Search"]?.Value<bool>(), Is.True);
                    Assert.That(h.Pending.IsActiveRequest(pending.Id, version), Is.False);
                });
            });
        }

        [TestCase(false, BookMediaType.Audiobook)]
        [TestCase(true, BookMediaType.Audiobook)]
        [TestCase(false, BookMediaType.Ebook)]
        [TestCase(true, BookMediaType.Ebook)]
        public void mixed_media_same_work_list_batch_keeps_both_intents_until_accepted(bool postgres, BookMediaType rejectedSide)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var audio = h.AddBook(work: "hc:1001", edition: "hc:edition:201");
                var ebook = h.AddBook(BookMediaType.Ebook, "hc:1001", "hc:edition:201");
                var audioEdition = AddPendingEdition(h, audio, "hc:edition:202");
                var ebookEdition = AddPendingEdition(h, ebook, "hc:edition:202");
                var item = h.Request(new List<string> { "hc:1001" }, new List<RequestedNarration>
                {
                    TypedPendingEdition(BookMediaType.Audiobook, "hc:1001", "hc:edition:202"),
                    TypedPendingEdition(BookMediaType.Ebook, "hc:1001", "hc:edition:202")
                }, ebook: true);
                item.EbookBooksToSearch = JsonConvert.SerializeObject(new[] { "hc:1001" });
                h.Requests.Update(item);
                var rejected = rejectedSide == BookMediaType.Audiobook ? audio : ebook;
                var other = rejectedSide == BookMediaType.Audiobook ? ebook : audio;
                h.RejectedBookId = rejected.Id;
                h.Run();
                var retry = h.Requests.Get(item.Id);
                var remaining = JArray.Parse(retry.AudiobookNarrations);
                Assert.Multiple(() =>
                {
                    Assert.That(retry.OverallStatus, Is.EqualTo(PendingImportStatus.Retrying));
                    Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                    Assert.That(remaining.Single(request => request["MediaType"]?.Value<int>() == (int)rejectedSide)["Search"]?.Value<bool>(), Is.True);
                    Assert.That(remaining.Single(request => request["MediaType"]?.Value<int>() == (int)other.MediaType)["Search"]?.Value<bool>(), Is.True);
                    Assert.That(h.Books.Get(rejected.Id).AddOptions.SearchForNewBook, Is.True);
                    Assert.That(h.Books.Get(other.Id).AddOptions.SearchForNewBook, Is.True);
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(h.BookService.GetBook(audio.Id)).Id, Is.EqualTo(audioEdition.Id));
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(h.BookService.GetBook(ebook.Id)).Id, Is.EqualTo(ebookEdition.Id));
                });
                h.RejectedBookId = null;
                h.Pending.RetryNow(item.Id); h.Run(); h.Run();
                Assert.Multiple(() =>
                {
                    Assert.That(h.Requests.All(), Is.Empty);
                    Assert.That(h.Commands.OfType<BookSearchCommand>().Single().BookIds, Is.EquivalentTo(new[] { audio.Id, ebook.Id }));
                    Assert.That(h.Commands.OfType<BookSearchCommand>().Single().Trigger, Is.EqualTo(CommandTrigger.Unspecified));
                    Assert.That(h.Books.All(), Has.Count.EqualTo(2));
                });
            });
        }

        [TestCase(false, "work")]
        [TestCase(true, "work")]
        [TestCase(false, "media")]
        [TestCase(true, "media")]
        [TestCase(false, "author")]
        [TestCase(true, "author")]
        public void unavailable_ebook_request_cannot_use_foreign_work_media_or_author_edition(bool postgres, string foreignBoundary)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var target = h.AddBook(BookMediaType.Ebook, "hc:1001", "hc:edition:201");
                var foreign = h.AddBook(foreignBoundary == "media" ? BookMediaType.Audiobook : BookMediaType.Ebook,
                    foreignBoundary == "work" ? "hc:9009" : "hc:1001", "hc:edition:202");
                if (foreignBoundary == "author")
                {
                    var other = h.Authors.Insert(new Author { Name = "Other Owner", CleanName = "otherowner", SortName = "otherowner",
                        TitleSlug = "other-owner", HardcoverAuthorId = "hc:9002", Path = @"C:\Ebooks\Other Owner".AsOsAgnostic(), Added = DateTime.UtcNow });
                    foreign.AuthorId = other.Id; h.Books.SetAuthorId(foreign);
                    Assert.That(h.Books.Get(foreign.Id).AuthorId, Is.EqualTo(other.Id), "The ownership negative must be persisted before resolving a request.");
                }
                var item = h.Request(new List<string>(), new List<RequestedNarration>
                { TypedPendingEdition(BookMediaType.Ebook, "hc:1001", "hc:edition:202") }, ebook: true);
                item.AudiobookStatus = PendingImportStatus.NotRequested;
                item.AudiobookBooksToMonitor = null; item.AudiobookBooksToSearch = null;
                item.EbookBooksToMonitor = JsonConvert.SerializeObject(new[] { "hc:1001" });
                item.EbookBooksToSearch = JsonConvert.SerializeObject(new[] { "hc:1001" });
                h.Requests.Update(item);
                h.Run();
                var failed = h.Requests.Get(item.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(failed.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
                    Assert.That(failed.EbookStatus, Is.EqualTo(PendingImportStatus.Failed));
                    Assert.That(failed.LastError, Does.Contain("202"));
                    Assert.That(h.Commands.OfType<BookSearchCommand>(), Is.Empty);
                    Assert.That(h.BookService.GetBook(target.Id).AnyEditionOk, Is.True);
                    Assert.That(h.BookService.GetBook(foreign.Id).AnyEditionOk, Is.True);
                    Assert.That(h.Books.All(), Has.Count.EqualTo(2));
                });
            });
        }
    }
}
