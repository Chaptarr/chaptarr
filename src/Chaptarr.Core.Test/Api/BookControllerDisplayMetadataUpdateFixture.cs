using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Books;
using Chaptarr.Core.Test.Common;
using Chaptarr.Http.Middleware;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Validation;
using NzbDrone.SignalR;

using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public partial class BookControllerDisplayMetadataUpdateFixture
    {
        private sealed class DisconnectedBroadcaster : IBroadcastSignalRMessage
        {
            public bool IsConnected => false;
            public Task BroadcastMessage(SignalRMessage message) => Task.CompletedTask;
        }

        [TestCase(false, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, false, false)]
        [TestCase(true, true, false)]
        [TestCase(false, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, false, true)]
        [TestCase(true, true, true)]
        public void round_tripped_book_put_preserves_metadata_and_automatic_edition_selection(bool postgres, bool facade, bool hiddenNarrator)
        {
            WithEndpoint(postgres, facade, hiddenNarrator, (controller, books, editions, book, context) =>
            {
                var request = controller.GetBookById(book.Id).Value;
                Assert.That(request.Ratings.Value, Is.EqualTo(4.8m));
                Assert.That(request.Narrator, Is.EqualTo(hiddenNarrator ? null : "Edition Narrator"));
                Assert.That(request.DurationMinutes, Is.EqualTo(hiddenNarrator ? 12 : 90.5));
                request.Monitored = true;
                request.AudiobookMonitored = true;
                if (facade)
                {
                    request.Monitored = false;
                    request.AudiobookMonitored = false;
                }

                var json = JsonSerializer.Serialize(request);
                Assert.That(json.IndexOf("NarratorSpecified", StringComparison.OrdinalIgnoreCase), Is.EqualTo(-1));
                request = JsonSerializer.Deserialize<BookResource>(json);
                var result = facade ? controller.UpdateBookByBody(request) : controller.UpdateBook(request);
                Assert.That(result.Result, Is.InstanceOf<AcceptedAtActionResult>());
                var persisted = books.Get(book.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(persisted.Ratings.Value, Is.EqualTo(3.2m));
                    Assert.That(persisted.Ratings.Votes, Is.EqualTo(100));
                    Assert.That(persisted.Narrator, Is.EqualTo("Book Narrator"));
                    Assert.That(persisted.DurationMinutes, Is.EqualTo(12));
                    Assert.That(persisted.AudiobookMonitored, Is.EqualTo(!facade));
                    Assert.That(persisted.AnyEditionOk, Is.True);
                    Assert.That(editions.Get(book.Editions.Single(edition => edition.Monitored).Id).ManualAdd, Is.False);
                });
                TestContext.WriteLine($"Book PUT: {book.Title}; book rating={persisted.Ratings.Value}; narrator={persisted.Narrator}; duration={persisted.DurationMinutes}; generic={persisted.AnyEditionOk}");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ebook_put_preserves_work_rating_and_duration_from_get_projection(bool postgres)
        {
            WithEndpoint(postgres, false, true, (controller, books, _, book, _) =>
            {
                var request = controller.GetBookById(book.Id).Value;
                Assert.That(request.Ratings.Value, Is.EqualTo(4.8m));
                Assert.That(request.DurationMinutes, Is.EqualTo(12));
                controller.UpdateBook(JsonSerializer.Deserialize<BookResource>(JsonSerializer.Serialize(request)));
                var persisted = books.Get(book.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(persisted.MediaType, Is.EqualTo(BookMediaType.Ebook));
                    Assert.That(persisted.Ratings.Value, Is.EqualTo(3.2m));
                    Assert.That(persisted.DurationMinutes, Is.EqualTo(12));
                    Assert.That(persisted.Narrator, Is.Null);
                });
            }, BookMediaType.Ebook);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void pinning_generic_book_does_not_turn_hidden_null_into_an_explicit_narrator_clear(bool postgres)
        {
            WithEndpoint(postgres, false, true, (controller, books, _, book, _) =>
            {
                var request = controller.GetBookById(book.Id).Value;
                request.AnyEditionOk = false;
                controller.UpdateBook(request);
                Assert.That(books.Get(book.Id).Narrator, Is.EqualTo("Book Narrator"));
                Assert.That(books.Get(book.Id).AnyEditionOk, Is.False);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void explicit_narrator_clear_is_preserved_but_omitted_narrator_is_not_a_clear(bool postgres)
        {
            WithEndpoint(postgres, false, false, (controller, books, _, book, _) =>
            {
                var partial = JsonSerializer.Deserialize<BookResource>("{\"Id\":" + book.Id + ",\"AnyEditionOk\":true}");
                controller.UpdateBook(partial);
                Assert.That(books.Get(book.Id).Narrator, Is.EqualTo("Book Narrator"));

                var clear = JsonSerializer.Deserialize<BookResource>("{\"Id\":" + book.Id + ",\"AnyEditionOk\":true,\"Narrator\":null}");
                controller.UpdateBook(clear);
                Assert.That(books.Get(book.Id).Narrator, Is.Null);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void round_trip_preserves_an_existing_manual_edition_pin(bool postgres)
        {
            WithEndpoint(postgres, false, true, (controller, books, editions, book, _) =>
            {
                book.AnyEditionOk = false;
                books.SetFields(book, item => item.AnyEditionOk);
                var selected = book.Editions.Single(edition => edition.Monitored);
                selected.ManualAdd = true;
                editions.SetFields(selected, edition => edition.ManualAdd);
                var request = controller.GetBookById(book.Id).Value;
                Assert.That(request.Narrator, Is.EqualTo("Edition Narrator"));

                controller.UpdateBook(request);

                Assert.Multiple(() =>
                {
                    Assert.That(books.Get(book.Id).Narrator, Is.EqualTo("Book Narrator"));
                    Assert.That(books.Get(book.Id).AnyEditionOk, Is.False);
                    Assert.That(editions.Get(selected.Id).ManualAdd, Is.True);
                    Assert.That(editions.FindByBook(new[] { book.Id }).Single(edition => edition.Monitored).Id, Is.EqualTo(selected.Id));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void native_put_still_accepts_explicit_narrator_edit_and_edition_selection(bool postgres)
        {
            WithEndpoint(postgres, false, true, (controller, books, editions, book, _) =>
            {
                var request = controller.GetBookById(book.Id).Value;
                request.Narrator = "Chosen Narrator";
                request.Editions = book.Editions.Select(edition => edition.ToResource()).ToList();
                var selectedId = book.Editions.Single(edition => !edition.Monitored).Id;
                foreach (var edition in request.Editions)
                {
                    edition.Monitored = edition.Id == selectedId;
                }

                request = JsonSerializer.Deserialize<BookResource>(JsonSerializer.Serialize(request));
                controller.UpdateBook(request);
                var persisted = books.Get(book.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(persisted.Narrator, Is.EqualTo("Chosen Narrator"));
                    Assert.That(persisted.AnyEditionOk, Is.True);
                    Assert.That(editions.FindByBook(new[] { book.Id }).Single(edition => edition.Monitored).Id, Is.EqualTo(selectedId));
                });
            });
        }

        [TestCase(false, true)]
        [TestCase(true, true)]
        [TestCase(false, false)]
        [TestCase(true, false)]
        public void duration_update_preserves_the_recordings_file_identity(bool postgres, bool pinned)
        {
            WithEndpoint(postgres, false, true, null, fileTest: (_, writer, files, book) =>
            {
                var recordedEdition = book.Editions.Single(edition => !edition.Monitored);
                var parts = new List<BookFile>
                {
                    files.Insert(new BookFile { DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow, Quality = new QualityModel(Quality.M4B), MediaInfo = new MediaInfoModel(), EditionId = recordedEdition.Id, Path = "/fixture/part01.mp3", MediaType = "audiobook", DurationSeconds = 1800 }),
                    files.Insert(new BookFile { DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow, Quality = new QualityModel(Quality.M4B), MediaInfo = new MediaInfoModel(), EditionId = recordedEdition.Id, Path = "/fixture/part02.mp3", MediaType = "audiobook", DurationSeconds = 3600 })
                };
                book.AnyEditionOk = !pinned;
                var media = Proxy<IMediaFileService>((method, args) => method.Name == nameof(IMediaFileService.GetFilesByBook)
                    ? files.GetFilesByBook((int)args[0]) : throw new NotImplementedException(method.Name));
                var extractor = Proxy<IMediaInfoExtractor>((method, _) => throw new AssertionException("Stored durations should not probe media: " + method.Name));
                new BookDurationService(writer, media, extractor, LogManager.GetCurrentClassLogger()).UpdateBookDuration(book);

                Assert.Multiple(() =>
                {
                    Assert.That(writer.GetBook(book.Id).DurationMinutes, Is.EqualTo(90));
                    Assert.That(writer.GetBook(book.Id).AnyEditionOk, Is.EqualTo(!pinned));
                    Assert.That(files.Get(parts.Select(part => part.Id)).Select(part => part.EditionId), Is.All.EqualTo(recordedEdition.Id));
                });
            });
        }

        [Test]
        public void explicit_book_edit_corrects_file_edition_when_selecting_or_pinning(
            [Values(false, true)] bool postgres, [Values(false, true)] bool pinCurrentSelection,
            [Values(false, true)] bool initiallyStrict)
        {
            WithEndpoint(postgres, false, true, null, fileTest: (controller, writer, files, book) =>
            {
                if (initiallyStrict)
                {
                    book.AnyEditionOk = false;
                    writer.UpdateBook(book);
                }

                var originalId = book.Editions.Single(edition => !edition.Monitored).Id;
                var file = files.Insert(new BookFile { DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow, Quality = new QualityModel(Quality.M4B), MediaInfo = new MediaInfoModel(), EditionId = originalId, Path = "/fixture/recording.m4b", MediaType = "audiobook", DurationSeconds = 1800 });
                var request = controller.GetBookById(book.Id).Value;
                var selectedId = book.Editions.Single(edition => edition.Monitored).Id;
                if (!pinCurrentSelection)
                {
                    file.EditionId = selectedId;
                    files.Update(file);
                    selectedId = originalId;
                    request.Editions = book.Editions.Select(edition => edition.ToResource()).ToList();
                    foreach (var edition in request.Editions)
                    {
                        edition.Monitored = edition.Id == selectedId;
                    }
                }

                request.AnyEditionOk = false;
                controller.UpdateBook(request);
                Assert.That(files.Get(file.Id).EditionId, Is.EqualTo(initiallyStrict && pinCurrentSelection ? originalId : selectedId));
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void unrelated_book_put_preserves_existing_file_identity(bool postgres, bool facade)
        {
            WithEndpoint(postgres, facade, true, (controller, books, _, book, _) =>
            {
                book.AnyEditionOk = false;
                books.SetFields(book, item => item.AnyEditionOk);
            }, fileTest: (controller, _, files, book) =>
            {
                var originalId = book.Editions.Single(edition => !edition.Monitored).Id;
                var file = files.Insert(new BookFile { DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow, Quality = new QualityModel(Quality.M4B), MediaInfo = new MediaInfoModel(), EditionId = originalId, Path = "/fixture/recording.m4b", MediaType = "audiobook", DurationSeconds = 1800 });
                var request = controller.GetBookById(book.Id).Value;
                request.Monitored = false;
                request.AudiobookMonitored = false;
                if (facade)
                {
                    controller.UpdateBookByBody(request);
                }
                else
                {
                    controller.UpdateBook(request);
                }

                Assert.That(files.Get(file.Id).EditionId, Is.EqualTo(originalId));
            });
        }

        [Test]
        public void facade_put_saves_supplied_edition_policy_with_monitor_request(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType,
            [Values(false, true)] bool byId,
            [Values(false, true)] bool wasMonitored,
            [Values(false, true)] bool anyEditionOk)
        {
            WithEndpoint(postgres, true, true, (controller, books, editions, book, _) =>
            {
                book.AnyEditionOk = !anyEditionOk;
                book.SetMonitored(wasMonitored);
                books.Update(book);
                var sibling = books.Insert(new Book
                {
                    AuthorId = book.AuthorId, Title = book.Title, CleanTitle = book.CleanTitle,
                    TitleSlug = "other-format", HardcoverBookId = book.HardcoverBookId,
                    MediaType = mediaType == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook,
                    AnyEditionOk = true, Added = DateTime.UtcNow
                });
                var authorGates = (book.Author.AudiobookMonitored, book.Author.EbookMonitored);
                var request = controller.GetBookById(book.Id).Value;
                request.Monitored = true;
                request.AnyEditionOk = anyEditionOk;
                request.Editions = book.Editions.Select(edition => edition.ToResource()).ToList();
                var selectedId = book.Editions.Single(edition => edition.Monitored == anyEditionOk).Id;
                foreach (var edition in request.Editions)
                {
                    edition.Monitored = edition.Id == selectedId;
                }

                request = JsonSerializer.Deserialize<BookResource>(JsonSerializer.Serialize(request));
                if (byId) controller.UpdateBook(request);
                else controller.UpdateBookByBody(request);

                var persisted = books.Get(book.Id);
                var persistedEditions = editions.FindByBook(new[] { book.Id });
                Assert.Multiple(() =>
                {
                    Assert.That(persisted.AnyEditionOk, Is.EqualTo(anyEditionOk));
                    Assert.That(persisted.IsMonitored(), Is.True);
                    Assert.That(persistedEditions.Single(edition => edition.Monitored).Id, Is.EqualTo(selectedId));
                    Assert.That(EditionPinPolicy.GetProtectedEditions(persisted, persistedEditions).Select(edition => edition.Id),
                        Is.EquivalentTo(anyEditionOk ? Array.Empty<int>() : new[] { selectedId }));
                    Assert.That(books.Get(sibling.Id).IsMonitored(), Is.False);
                    Assert.That(books.Get(sibling.Id).AnyEditionOk, Is.True);
                    Assert.That((persisted.Author.AudiobookMonitored, persisted.Author.EbookMonitored), Is.EqualTo(authorGates));
                });
            }, mediaType);
        }

        [Test]
        public void monitoring_put_preserves_saved_edition_policy_and_slug(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType,
            [Values(false, true)] bool facade,
            [Values(false, true)] bool anyEditionOk,
            [Values(null, "", "client-supplied-slug")] string submittedSlug)
        {
            WithEndpoint(postgres, facade, true, (controller, books, editions, book, _) =>
            {
                book.AnyEditionOk = anyEditionOk;
                books.Update(book);
                var savedSlug = book.TitleSlug;
                var selectedId = book.Editions.Single(edition => edition.Monitored).Id;
                var request = JsonSerializer.Deserialize<BookResource>(
                    "{\"Id\":" + book.Id + ",\"Monitored\":true}");
                if (submittedSlug != null) request.TitleSlug = submittedSlug;
                if (facade) controller.UpdateBookByBody(request);
                else controller.UpdateBook(request);

                Assert.Multiple(() =>
                {
                    Assert.That(books.Get(book.Id).AnyEditionOk, Is.EqualTo(anyEditionOk));
                    Assert.That(books.Get(book.Id).IsMonitored(), Is.True);
                    Assert.That(books.Get(book.Id).TitleSlug, Is.EqualTo(savedSlug));
                    Assert.That(editions.FindByBook(new[] { book.Id }).Single(edition => edition.Monitored).Id, Is.EqualTo(selectedId));
                });
            }, mediaType);
        }

        [Test]
        public void facade_pin_save_keeps_monitoring_root_validation(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType,
            [Values(false, true)] bool byId)
        {
            WithEndpoint(postgres, true, true, (controller, books, _, book, _) =>
            {
                var request = controller.GetBookById(book.Id).Value;
                request.Monitored = true;
                request.AnyEditionOk = false;
                if (byId) controller.UpdateBook(request);
                else controller.UpdateBookByBody(request);

                Assert.Multiple(() =>
                {
                    Assert.That(books.Get(book.Id).AnyEditionOk, Is.False);
                    Assert.That(books.Get(book.Id).IsMonitored(), Is.False);
                });
            }, mediaType, configuredRoot: false);
        }

        [Test]
        public void book_put_rejects_foreign_or_unknown_editions_before_changing_any_rows(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType,
            [Values(false, true)] bool facade,
            [Values(false, true)] bool byId)
        {
            List<(int Id, int EditionId, string Path)> originalFiles = null;
            WithEndpoint(postgres, facade, false, (controller, books, editions, book, context) =>
            {
                originalFiles = book.BookFiles.Select(file => (file.Id, file.EditionId, file.Path)).ToList();
                var selected = book.Editions.Single(edition => edition.Monitored);
                var own = selected.ToResource();
                var invalid = new List<EditionResource> { null, new() { Id = int.MaxValue }, new() { Id = 0 } };
                foreach (var side in new[] { mediaType, mediaType == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook })
                {
                    var other = books.Insert(new Book
                    {
                        AuthorId = book.AuthorId, Title = book.Title, CleanTitle = book.CleanTitle,
                        TitleSlug = "other-" + side, HardcoverBookId = book.HardcoverBookId,
                        MediaType = side, AnyEditionOk = true, Added = DateTime.UtcNow
                    });
                    invalid.Add(editions.Insert(new Edition
                    {
                        BookId = other.Id, Title = book.Title, ForeignEditionId = selected.ForeignEditionId,
                        HardcoverEditionId = selected.HardcoverEditionId, Monitored = true
                    }).ToResource());
                    invalid.Add(new EditionResource { Id = selected.Id, BookId = other.Id });
                }

                var beforeBooks = books.All().Select(row => (row.Id, row.AnyEditionOk, row.AudiobookMonitored, row.EbookMonitored)).ToList();
                var beforeEditions = editions.All().Select(row => (row.Id, row.BookId, row.Monitored, row.ManualAdd, row.ForeignEditionId)).ToList();
                foreach (var badEdition in invalid)
                {
                    var request = new BookResource
                    {
                        Id = book.Id, Monitored = true, AnyEditionOk = false,
                        Editions = new List<EditionResource> { own, badEdition }
                    };
                    var error = Assert.Throws<ValidationException>(() =>
                    {
                        if (byId) controller.UpdateBook(request);
                        else controller.UpdateBookByBody(request);
                    });
                    Assert.That(error.Errors.Single().PropertyName, Is.EqualTo(nameof(BookResource.Editions)));
                    Assert.Multiple(() =>
                    {
                        Assert.That(books.All().Select(row => (row.Id, row.AnyEditionOk, row.AudiobookMonitored, row.EbookMonitored)), Is.EqualTo(beforeBooks));
                        Assert.That(editions.All().Select(row => (row.Id, row.BookId, row.Monitored, row.ManualAdd, row.ForeignEditionId)), Is.EqualTo(beforeEditions));
                    });
                }
            }, mediaType, fileTest: (_, _, files, _) =>
                Assert.That(files.All().Select(file => (file.Id, file.EditionId, file.Path)), Is.EqualTo(originalFiles)));
        }

        [Test]
        public void partial_book_put_keeps_parent_identity_and_selects_only_the_requested_edition(
            [Values(false, true)] bool postgres,
            [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType,
            [Values(false, true)] bool facade,
            [Values(false, true)] bool byId,
            [Values(false, true)] bool changeSelection)
        {
            WithEndpoint(postgres, facade, true, (controller, books, editions, book, context) =>
            {
                var selected = book.Editions.Single(edition => edition.Monitored != changeSelection);
                var submitted = selected.ToResource();
                submitted.BookId = 0;
                submitted.ForeignEditionId = null;
                submitted.Monitored = true;
                var request = new BookResource
                {
                    Id = book.Id, Monitored = true, AnyEditionOk = false,
                    Editions = new List<EditionResource> { submitted }
                };
                if (byId) controller.UpdateBook(request);
                else controller.UpdateBookByBody(request);

                Assert.Multiple(() =>
                {
                    Assert.That(books.Get(book.Id).AnyEditionOk, Is.False);
                    Assert.That(editions.Get(selected.Id).BookId, Is.EqualTo(book.Id));
                    Assert.That(editions.Get(selected.Id).ForeignEditionId, Is.EqualTo(selected.ForeignEditionId));
                    Assert.That(editions.FindByBook(new[] { book.Id }).Single(edition => edition.Monitored).Id, Is.EqualTo(selected.Id));
                    Assert.That(editions.FindByBook(new[] { book.Id }), Has.Count.EqualTo(2));
                });
            }, mediaType);
        }

        [Test]
        public void partial_edition_metadata_put_preserves_the_omitted_selection(
            [Values(false, true)] bool postgres, [Values(false, true)] bool facade,
            [Values(false, true)] bool byId, [Values(BookMediaType.Audiobook, BookMediaType.Ebook)] BookMediaType mediaType)
        {
            WithEndpoint(postgres, facade, true, (controller, books, editions, book, _) =>
            {
                var selected = book.Editions.Single(edition => edition.Monitored);
                var request = controller.GetBookById(book.Id).Value;
                request.Editions = new List<EditionResource> { book.Editions.Single(edition => !edition.Monitored).ToResource() };
                if (byId) controller.UpdateBook(request);
                else controller.UpdateBookByBody(request);
                Assert.That(editions.FindByBook(new[] { book.Id }).Single(edition => edition.Monitored).Id, Is.EqualTo(selected.Id));
                Assert.That(books.Get(book.Id).AnyEditionOk, Is.True);
            }, mediaType);
        }

        [Test]
        public void ambiguous_edition_payload_is_rejected_before_mutating_the_book(
            [Values(false, true)] bool postgres, [Values(false, true)] bool duplicateId)
        {
            WithEndpoint(postgres, false, true, (controller, books, editions, book, _) =>
            {
                var before = editions.FindByBook(new[] { book.Id }).Select(edition => (edition.Id, edition.Monitored, edition.ManualAdd)).ToArray();
                var request = controller.GetBookById(book.Id).Value;
                request.AnyEditionOk = false;
                request.Editions = book.Editions.Select(edition => edition.ToResource()).ToList();
                if (duplicateId) request.Editions.Add(request.Editions[0]);
                else request.Editions.ForEach(edition => edition.Monitored = true);
                Assert.Throws<ValidationException>(() => controller.UpdateBook(request));
                Assert.That(books.Get(book.Id).AnyEditionOk, Is.True);
                Assert.That(editions.FindByBook(new[] { book.Id }).Select(edition => (edition.Id, edition.Monitored, edition.ManualAdd)), Is.EqualTo(before));
            });
        }

        [Test]
        public void edition_ownership_validation_reuses_loaded_editions_and_does_not_mutate_on_failure()
        {
            var book = new Book
            {
                Id = 1, MediaType = BookMediaType.Audiobook, AnyEditionOk = true,
                Editions = Enumerable.Range(1, 1132).Select(id => new Edition { Id = id, BookId = 1, Title = "Edition " + id }).ToList()
            };
            var request = new BookResource
            {
                Id = 1, Monitored = true, AnyEditionOk = false,
                Editions = book.Editions.Select(edition => new EditionResource { Id = edition.Id, Monitored = edition.Id == 1 }).ToList()
            };
            var originalEditions = book.Editions;
            request.Editions.Add(new EditionResource { Id = 1133, BookId = 1 });
            Assert.Throws<ValidationException>(() => request.ToModel(book));
            Assert.Multiple(() =>
            {
                Assert.That(book.AnyEditionOk, Is.True);
                Assert.That(book.AudiobookMonitored, Is.False);
                Assert.That(book.Editions, Is.SameAs(originalEditions));
            });
            request.Editions.RemoveAt(request.Editions.Count - 1);
            for (var i = 0; i < 10; i++) request.ToModel(book);
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            for (var i = 0; i < 100; i++) request.ToModel(book);
            timer.Stop();
            TestContext.Out.WriteLine("Loaded-only mapping of 1132 editions: {0:F3} ms, {1} allocated bytes per update",
                timer.Elapsed.TotalMilliseconds / 100, (GC.GetAllocatedBytesForCurrentThread() - allocated) / 100);
            Assert.That(book.Editions.All(edition => edition.BookId == book.Id), Is.True);
            Assert.That(book.AnyEditionOk, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void partial_edition_update_does_not_rewrite_the_loaded_catalog(bool postgres)
        {
            var statements = new List<string>();
            WithEndpoint(postgres, false, true, (controller, books, editions, book, _) =>
            {
                editions.InsertMany(Enumerable.Range(0, 1130).Select(i => new Edition
                {
                    BookId = book.Id, Title = "Other edition " + i, ReadingFormatId = 2
                }).ToList());
                var next = editions.FindRowsByBook(new[] { book.Id }).First(edition => !edition.Monitored).ToResource();
                next.Monitored = true;
                statements.Clear();
                var watch = Stopwatch.StartNew();
                controller.UpdateBookByBody(new BookResource { Id = book.Id, Editions = new List<EditionResource> { next } });
                watch.Stop();
                var editionWrites = statements.Count(sql => sql.StartsWith("UPDATE \"Editions\" SET", StringComparison.Ordinal) && sql.Contains("\"Title\"", StringComparison.Ordinal));
                Assert.That(editionWrites, Is.EqualTo(2), "Only the submitted edition and the previous selection need full-row writes");
                Assert.That(editions.FindRowsByBook(new[] { book.Id }).Single(edition => edition.Monitored).Id, Is.EqualTo(next.Id));
                TestContext.WriteLine($"{(postgres ? "PostgreSQL" : "SQLite")}: partial PUT across 1132 editions, full edition writes {editionWrites}, elapsedMs={watch.Elapsed.TotalMilliseconds:F2}");
            }, observeCommand: sql => statements.Add(sql));
        }

        [Test]
        public void explicit_policy_wins_for_changed_and_unchanged_selections(
            [Values(false, true)] bool postgres, [Values(false, true)] bool facade,
            [Values(false, true)] bool byId, [Values(false, true)] bool changed,
            [Values(null, false, true)] bool? policy)
        {
            WithEndpoint(postgres, facade, true, (controller, books, editions, book, _) =>
            {
                var selectedId = book.Editions.Single(edition => edition.Monitored != changed).Id;
                var request = new BookResource
                {
                    Id = book.Id,
                    Editions = book.Editions.Select(edition => edition.ToResource()).ToList()
                };
                request.Editions.ForEach(edition => edition.Monitored = edition.Id == selectedId);
                if (policy.HasValue) request.AnyEditionOk = policy.Value;

                if (byId) controller.UpdateBook(request);
                else controller.UpdateBookByBody(request);

                var stored = books.Get(book.Id);
                var storedEditions = editions.FindByBook(new[] { book.Id });
                Assert.Multiple(() =>
                {
                    Assert.That(stored.AnyEditionOk, Is.EqualTo(policy ?? !(byId && changed)));
                    Assert.That(storedEditions.Single(edition => edition.Monitored).Id, Is.EqualTo(selectedId));
                    Assert.That(EditionPinPolicy.CanAutomationSelectEdition(stored, storedEditions), Is.EqualTo(stored.AnyEditionOk));
                });
            });
        }

        [Test]
        public void metadata_save_preserves_an_independent_unselected_retention_pin(
            [Values(false, true)] bool postgres, [Values(false, true)] bool byId)
        {
            WithEndpoint(postgres, false, true, (controller, books, editions, book, _) =>
            {
                var retained = book.Editions.Single(edition => !edition.Monitored);
                retained.ManualAdd = true;
                editions.SetFields(retained, edition => edition.ManualAdd);
                var request = controller.GetBookById(book.Id).Value;
                request.Editions = editions.FindByBook(new[] { book.Id }).ToResource();
                if (byId) controller.UpdateBook(request);
                else controller.UpdateBookByBody(request);
                Assert.That(editions.Get(retained.Id).ManualAdd, Is.True);
                Assert.That(editions.Get(retained.Id).Monitored, Is.False);
            });
        }

        [Test]
        public void full_edition_round_trip_writes_only_actual_changes(
            [Values(false, true)] bool postgres, [Values(false, true)] bool changeSelection)
        {
            var statements = new List<string>();
            WithEndpoint(postgres, false, true, (controller, books, editions, book, _) =>
            {
                editions.InsertMany(Enumerable.Range(0, 1130).Select(i => new Edition
                {
                    BookId = book.Id, Title = "Other edition " + i, ReadingFormatId = 2
                }).ToList());
                var request = controller.GetBookById(book.Id).Value;
                request.Editions = editions.FindByBook(new[] { book.Id }).ToResource();
                if (changeSelection)
                {
                    var nextId = request.Editions.First(edition => !edition.Monitored).Id;
                    request.Editions.ForEach(edition => edition.Monitored = edition.Id == nextId);
                }

                statements.Clear();
                controller.UpdateBookByBody(request);
                var fullWrites = statements.Count(sql => sql.StartsWith("UPDATE \"Editions\" SET", StringComparison.Ordinal) && sql.Contains("\"Title\"", StringComparison.Ordinal));
                Assert.That(fullWrites, Is.EqualTo(changeSelection ? 2 : 0));
                TestContext.WriteLine($"{(postgres ? "PostgreSQL" : "SQLite")}: full 1132-edition PUT, full edition writes={fullWrites}");
            }, observeCommand: sql => statements.Add(sql));
        }

        [Test]
        public void removed_edition_conflict_rolls_back_book_selection_and_aliases(
            [Values(false, true)] bool postgres, [Values(false, true)] bool changeSelection)
        {
            WithEndpoint(postgres, false, true, (controller, books, editions, book, _) =>
            {
                var previousId = book.Editions.Single(edition => edition.Monitored).Id;
                var targetId = book.Editions.Single(edition => !edition.Monitored).Id;
                var request = new BookResource
                {
                    Id = book.Id, AnyEditionOk = false, Monitored = true,
                    Editions = book.Editions.Select(edition => edition.ToResource()).ToList()
                };
                request.Editions.ForEach(edition => edition.Monitored = edition.Id == (changeSelection ? targetId : previousId));
                Assert.Throws<ModelConflictException>(() => controller.UpdateBook(request));
                Assert.Multiple(() =>
                {
                    Assert.That(books.Get(book.Id).AnyEditionOk, Is.True);
                    Assert.That(books.Get(book.Id).IsMonitored(), Is.False);
                    if (changeSelection) Assert.That(editions.Get(previousId).Monitored, Is.True);
                    else Assert.That(editions.FindByBook(new[] { book.Id }).Any(edition => edition.Monitored), Is.False);
                });
            }, beforeBookSave: (model, editions) => editions.Delete(model.Editions.Single(edition => edition.Monitored).Id));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void zero_selection_repair_uses_real_file_evidence(bool postgres)
        {
            WithEndpoint(postgres, false, true, null, fileTest: (controller, _, files, book) =>
            {
                var fileEdition = book.Editions.Single(edition => !edition.Monitored);
                files.Insert(new BookFile
                {
                    DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow, Quality = new QualityModel(Quality.M4B),
                    MediaInfo = new MediaInfoModel(), EditionId = fileEdition.Id, Path = "/fixture/recording.m4b", MediaType = "audiobook"
                });
                var request = controller.GetBookById(book.Id).Value;
                request.Editions = book.Editions.ToResource();
                request.Editions.ForEach(edition => edition.Monitored = false);
                controller.UpdateBookByBody(request);
                Assert.That(controller.GetBookById(book.Id).Value.ForeignEditionId, Is.EqualTo(fileEdition.ForeignEditionId));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void edition_write_failure_rolls_back_book_and_facade_sync_without_events(bool postgres)
        {
            var injectFailure = false;
            var emitted = new List<object>();
            WithEndpoint(postgres, true, true, (controller, books, editions, book, _) =>
            {
                var sibling = books.Insert(new Book
                {
                    AuthorId = book.AuthorId, Title = book.Title, CleanTitle = book.CleanTitle,
                    TitleSlug = "other-format", HardcoverBookId = book.HardcoverBookId,
                    MediaType = BookMediaType.Ebook, AnyEditionOk = true, Added = DateTime.UtcNow
                });
                var selectedId = book.Editions.Single(edition => edition.Monitored).Id;
                var request = controller.GetBookById(book.Id).Value;
                request.AnyEditionOk = false;
                request.Monitored = true;
                request.Editions = book.Editions.ToResource();
                request.Editions.ForEach(edition => edition.Monitored = edition.Id != selectedId);
                emitted.Clear();
                injectFailure = true;
                Assert.That(() => controller.UpdateBookByBody(request), Throws.Exception);
                injectFailure = false;
                Assert.Multiple(() =>
                {
                    Assert.That(books.Get(book.Id).AnyEditionOk, Is.True);
                    Assert.That(books.Get(book.Id).IsMonitored(), Is.False);
                    Assert.That(books.Get(sibling.Id).IsMonitored(), Is.False);
                    Assert.That(editions.FindByBook(new[] { book.Id }).Single(edition => edition.Monitored).Id, Is.EqualTo(selectedId));
                    Assert.That(emitted.OfType<NzbDrone.Core.Books.Events.BookEditedEvent>(), Is.Empty);
                });
                controller.UpdateBookByBody(request);
                Assert.That(books.Get(book.Id).IsMonitored(), Is.True);
                Assert.That(books.Get(sibling.Id).IsMonitored(), Is.True);
            }, observeCommand: sql =>
            {
                if (injectFailure && sql.StartsWith("UPDATE \"Editions\"", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Injected edition write failure");
                }
            }, observeEvent: emitted.Add, syncMonitoring: true);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void omitted_retention_preserves_the_selected_marker_and_explicit_false_can_release_it(bool postgres)
        {
            WithEndpoint(postgres, false, true, (controller, books, editions, book, _) =>
            {
                var selected = book.Editions.Single(edition => edition.Monitored);
                selected.ManualAdd = true;
                editions.SetFields(selected, edition => edition.ManualAdd);
                var request = new BookResource { Id = book.Id, AnyEditionOk = true, Editions = new() { selected.ToResource() } };
                var payload = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(request));
                payload[nameof(BookResource.Editions)][0].AsObject().Remove(nameof(EditionResource.ManualAdd));
                var omitted = JsonSerializer.Deserialize<BookResource>(payload.ToJsonString());
                controller.UpdateBookByBody(omitted);
                Assert.That(editions.Get(selected.Id).ManualAdd, Is.True);
                omitted.Editions[0].ManualAdd = false;
                controller.UpdateBookByBody(omitted);
                Assert.That(editions.Get(selected.Id).ManualAdd, Is.False);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void committed_save_survives_postcommit_notification_failure(bool postgres)
        {
            WithEndpoint(postgres, false, true, (controller, books, editions, book, _) =>
            {
                var request = controller.GetBookById(book.Id).Value;
                request.AnyEditionOk = false;
                Assert.That(() => controller.UpdateBookByBody(request), Throws.Nothing);
                Assert.That(books.Get(book.Id).AnyEditionOk, Is.False);
            }, observeEvent: message =>
            {
                if (message is NzbDrone.Core.Books.Events.BookEditedEvent)
                {
                    throw new InvalidOperationException("Injected postcommit observer failure");
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void stale_unchanged_selection_conflicts_after_a_concurrent_switch(bool postgres)
        {
            WithEndpoint(postgres, false, true, (controller, books, editions, book, _) =>
            {
                var selectedId = book.Editions.Single(edition => edition.Monitored).Id;
                var request = controller.GetBookById(book.Id).Value;
                request.Editions = book.Editions.ToResource();
                request.AnyEditionOk = false;
                Assert.Throws<ModelConflictException>(() => controller.UpdateBookByBody(request));
                Assert.That(books.Get(book.Id).AnyEditionOk, Is.True);
                Assert.That(editions.FindByBook(new[] { book.Id }).Single(edition => edition.Monitored).Id, Is.Not.EqualTo(selectedId));
            }, beforeBookSave: (model, editions) =>
                editions.SetMonitored(editions.FindByBook(new[] { model.Id }).Single(edition => !edition.Monitored)));
        }

        [Test]
        public void edition_selection_keeps_provider_images_when_resources_have_local_urls_or_placeholders(
            [Values(false, true)] bool postgres, [Values(false, true)] bool changeSelection,
            [Values(false, true)] bool placeholders)
        {
            var writes = new List<string>();
            WithEndpoint(postgres, false, true, (controller, books, editions, book, _) =>
            {
                foreach (var edition in book.Editions)
                {
                    edition.Images = new List<NzbDrone.Core.MediaCover.MediaCover> { new(MediaCoverTypes.Cover, $"https://covers.example/{edition.Id}.jpg") };
                    editions.Update(edition);
                }
                var request = controller.GetBookById(book.Id).Value;
                request.Editions = editions.FindByBook(new[] { book.Id }).ToResource();
                foreach (var edition in request.Editions)
                {
                    edition.Images = placeholders ? new List<NzbDrone.Core.MediaCover.MediaCover> { null } :
                        new List<NzbDrone.Core.MediaCover.MediaCover> { new(MediaCoverTypes.Cover, $"/MediaCover/Books/{edition.Id}/cover.jpg") };
                    if (changeSelection) edition.Monitored = !edition.Monitored;
                }
                writes.Clear();
                controller.UpdateBook(request);
                foreach (var edition in editions.FindByBook(new[] { book.Id }))
                {
                    Assert.That(edition.Images.Single().Url, Is.EqualTo($"https://covers.example/{edition.Id}.jpg"));
                }
                Assert.That(writes.Count(sql => sql.StartsWith("UPDATE \"Editions\" SET", StringComparison.Ordinal) &&
                    sql.Contains("\"Title\"", StringComparison.Ordinal)), Is.EqualTo(changeSelection ? 2 : 0));
            }, observeCommand: writes.Add);
        }

        [Test]
        public void file_relink_and_classification_commit_or_roll_back_together(
            [Values(false, true)] bool postgres, [Values(false, true)] bool rejectRelink)
        {
            var originalId = 0;
            var targetId = 0;
            var writes = new List<string>();
            WithEndpoint(postgres, false, false, (controller, books, editions, book, _) =>
            {
                originalId = book.Editions.Single(edition => edition.Monitored).Id;
                targetId = book.Editions.Single(edition => !edition.Monitored).Id;
                var target = editions.Get(targetId);
                target.Title = "How to Sell a Haunted House [Dramatized Adaptation]";
                target.Publisher = "Blackstone Publishing, Inc.";
                editions.Update(target);
                var request = controller.GetBookById(book.Id).Value;
                request.AnyEditionOk = false;
                request.Editions = editions.FindByBook(new[] { book.Id }).ToResource();
                request.Editions.ForEach(edition => edition.Monitored = edition.Id == targetId);
                writes.Clear();
                if (rejectRelink)
                {
                    var failure = Assert.Catch<Exception>(() => controller.UpdateBook(request));
                    Assert.That(failure.ToString(), Does.Contain(postgres ? "test_relink_rejected" : "relink rejected"));
                }
                else controller.UpdateBook(request);
                Assert.That(books.Get(book.Id).AnyEditionOk, Is.EqualTo(rejectRelink));
                Assert.That(editions.FindByBook(new[] { book.Id }).Single(edition => edition.Monitored).Id,
                    Is.EqualTo(rejectRelink ? originalId : targetId));
                Assert.That(writes.Any(sql => sql.StartsWith("UPDATE \"BookFiles\" SET \"AudioProductionOverride\"", StringComparison.Ordinal)), Is.True,
                    "The classification write executes before the rejected relink");
            }, fileTest: (_, _, files, book) =>
            {
                var saved = files.GetFilesByBook(book.Id).Single();
                Assert.That(saved.EditionId, Is.EqualTo(rejectRelink ? originalId : targetId));
                Assert.That(saved.AudioProductionOverride, Is.EqualTo(rejectRelink ? (bool?)null : true));
            }, beforeTest: (database, book) =>
            {
                if (!rejectRelink) return;
                using var connection = database.OpenConnection();
                if (postgres)
                    Dapper.SqlMapper.Execute(connection, $"ALTER TABLE \"BookFiles\" ADD CONSTRAINT test_relink_rejected CHECK (\"EditionId\" = {book.Editions.Single(edition => edition.Monitored).Id})");
                else
                    Dapper.SqlMapper.Execute(connection, "CREATE TRIGGER test_relink_rejected BEFORE UPDATE OF EditionId ON BookFiles BEGIN SELECT RAISE(ABORT, 'relink rejected'); END");
            }, observeCommand: writes.Add);
        }

        private static void WithEndpoint(bool postgres, bool facade, bool hiddenNarrator,
            Action<BookController, BookRepository, EditionRepository, Book, ReadarrFacadeContext> test,
            BookMediaType mediaType = BookMediaType.Audiobook,
            Action<BookController, BookService, MediaFileRepository, Book> fileTest = null,
            bool configuredRoot = true, Action<string> observeCommand = null,
            Action<Book, EditionRepository> beforeBookSave = null, Action<object> observeEvent = null, bool syncMonitoring = false, Action<IMainDatabase, Book> beforeTest = null)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                if (observeCommand != null)
                {
                    var inner = database;
                    database = Proxy<IMainDatabase>((method, args) => method.Name == nameof(IMainDatabase.OpenConnection)
                        ? CommandObserver.Wrap(inner.OpenConnection(), command => observeCommand(command.CommandText), includeWrites: true)
                        : method.Invoke(inner, args));
                }
                var logger = LogManager.GetCurrentClassLogger();
                var events = Proxy<IEventAggregator>((method, args) =>
                {
                    if (method.Name == nameof(IEventAggregator.PublishEvent)) observeEvent?.Invoke(args[0]);
                    return null;
                });
                var authors = new AuthorRepository(database, events, logger);
                var author = authors.Insert(new Author
                {
                    Name = "Fixture Author", CleanName = "fixtureauthor", TitleSlug = "fixture-author",
                    Path = @"C:\Audio\Fixture Author".AsOsAgnostic(), Added = DateTime.UtcNow,
                    SyncMonitoredAcrossFormats = syncMonitoring,
                    AudiobookRootFolderPath = syncMonitoring || mediaType == BookMediaType.Audiobook ? @"C:\Audio".AsOsAgnostic() : null,
                    EbookRootFolderPath = syncMonitoring || mediaType == BookMediaType.Ebook ? @"C:\Audio".AsOsAgnostic() : null
                });
                var authorService = Proxy<IAuthorService>((method, args) => method.Name switch
                {
                    nameof(IAuthorService.GetAuthor) => authors.Get((int)args[0]),
                    nameof(IAuthorService.GetAuthors) => authors.Get((IEnumerable<int>)args[0]).ToList(),
                    _ => throw new NotImplementedException(method.Name)
                });
                var roots = Proxy<IRootFolderService>((method, _) => method.Name == nameof(IRootFolderService.All)
                    ? configuredRoot
                        ? new List<RootFolder> { new() { Path = @"C:\Audio".AsOsAgnostic(), FolderType = syncMonitoring ? FolderType.Mixed : mediaType == BookMediaType.Audiobook ? FolderType.Audiobook : FolderType.Ebook } }
                        : new List<RootFolder>()
                    : throw new NotImplementedException(method.Name));
                var books = new BookRepository(database, events);
                var editions = new EditionRepository(database, events);
                var book = books.Insert(new Book
                {
                    AuthorId = author.Id, Title = "Projected Metadata", CleanTitle = "projectedmetadata",
                    TitleSlug = "projected-metadata", HardcoverBookId = "hc:123", MediaType = mediaType,
                    AnyEditionOk = true, Added = DateTime.UtcNow, Narrator = mediaType == BookMediaType.Audiobook ? "Book Narrator" : null, DurationMinutes = 12,
                    Ratings = new Ratings { Votes = 100, Value = 3.2m }
                });
                var selected = editions.Insert(new Edition
                {
                    BookId = book.Id, Title = book.Title, ForeignEditionId = "hc:edition:456", HardcoverEditionId = "456",
                    ReadingFormatId = mediaType == BookMediaType.Audiobook ? 2 : 3,
                    Format = mediaType == BookMediaType.Audiobook ? "audiobook" : "ebook", Monitored = true,
                    Narrator = mediaType == BookMediaType.Audiobook ? "Edition Narrator" : null,
                    Ratings = new Ratings { Votes = 20, Value = 4.8m }
                });
                editions.Insert(new Edition
                {
                    BookId = book.Id, Title = book.Title, ForeignEditionId = "hc:edition:789", HardcoverEditionId = "789",
                    ReadingFormatId = mediaType == BookMediaType.Audiobook ? 2 : 3,
                    Format = mediaType == BookMediaType.Audiobook ? "audiobook" : "ebook", Monitored = false,
                    Narrator = mediaType == BookMediaType.Audiobook ? "Other Narrator" : null
                });
                var files = hiddenNarrator ? new List<BookFile>() : new List<BookFile>
                {
                    new() { Id = 1, EditionId = selected.Id, MediaType = "audiobook", DurationSeconds = 5430,
                        Path = @"C:\Audio\Fixture Author\Projected Metadata.m4b".AsOsAgnostic() }
                };
                var fileRepository = new MediaFileRepository(database, events);
                foreach (var file in files)
                {
                    file.Id = 0;
                    file.DateAdded = DateTime.UtcNow;
                    file.Modified = DateTime.UtcNow;
                    file.Quality = new QualityModel(Quality.M4B);
                    file.MediaInfo = new MediaInfoModel();
                    fileRepository.Insert(file);
                }

                var media = Proxy<IMediaFileService>((method, args) =>
                {
                    if (method.Name == nameof(IMediaFileService.GetFilesByBooks))
                    {
                        return fileRepository.GetFilesByBooks((List<int>)args[0]);
                    }

                    if (method.Name == nameof(IMediaFileService.GetFilesByBook))
                    {
                        return fileRepository.GetFilesByBook((int)args[0]);
                    }

                    if (method.Name == nameof(IMediaFileService.RelinkAndClassifyFilesByBook))
                    {
                        return fileRepository.RelinkAndClassifyFilesByBook((int)args[0], (int?)args[1], (BookMediaType)args[2], (AudioProductionCorrection)args[3],
                            (System.Data.IDbConnection)args[4], (System.Data.IDbTransaction)args[5]);
                    }

                    if (method.Name == nameof(IMediaFileService.Update) && args[0] is List<BookFile> updated)
                    {
                        fileRepository.UpdateMany(updated);
                        return null;
                    }

                    throw new NotImplementedException(method.Name);
                });
                var editionService = new EditionService(editions, events, logger);
                var writer = new BookService(books, editionService, events, authorService, media, roots,
                    new SeriesBookLinkRepository(database, events), Proxy<IMultiCopySeriesService>((method, _) => throw new NotImplementedException(method.Name)),
                    logger, providerAliasService: new ProviderAliasService(new ProviderAliasRepository(database, events)));
                var stats = Proxy<IAuthorStatisticsService>((method, _) => method.Name == nameof(IAuthorStatisticsService.AuthorStatistics)
                    ? new AuthorStatistics { BookStatistics = new() }
                    : throw new NotImplementedException(method.Name));
                var covers = Proxy<IMapCoversToLocal>((method, _) => method.Name == nameof(IMapCoversToLocal.ConvertBookCoversToLocalUrls)
                    ? null
                    : throw new NotImplementedException(method.Name));
                var apiWriter = beforeBookSave == null ? (IBookService)writer : Proxy<IBookService>((method, args) =>
                {
                    if (method.Name == nameof(IBookService.UpdateBook)) beforeBookSave((Book)args[0], editions);
                    try { return method.Invoke(writer, args); }
                    catch (System.Reflection.TargetInvocationException error) when (error.InnerException != null)
                    {
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                        throw;
                    }
                });
                var controller = new BookController(null, apiWriter, null, editionService, new EditionSelector(logger), null,
                    stats, media, covers, null, new DisconnectedBroadcaster(), null, null, null,
                    new QualityProfileExistsValidator(new TestQualityProfileService()),
                    new MetadataProfileExistsValidator(new TestMetadataProfileService()), logger);
                var context = facade ? new ReadarrFacadeContext("hc", mediaType == BookMediaType.Audiobook ? "audiobook" : "ebook",
                    mediaType == BookMediaType.Audiobook ? "/readarr/hc/audiobook" : "/readarr/hc/ebook") : null;
                var http = new DefaultHttpContext();
                if (context != null)
                {
                    http.Items[ReadarrFacadeContext.ItemKey] = context;
                }

                controller.ControllerContext = new ControllerContext { HttpContext = http };
                beforeTest?.Invoke(database, writer.GetBook(book.Id));
                test?.Invoke(controller, books, editions, writer.GetBook(book.Id), context);
                fileTest?.Invoke(controller, writer, fileRepository, writer.GetBook(book.Id));
            });
        }
    }
}
