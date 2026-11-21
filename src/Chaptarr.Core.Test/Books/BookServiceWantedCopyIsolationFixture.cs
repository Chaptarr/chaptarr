using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Api.V1.Editions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Books
{
    public partial class BookServiceWantedNarratorSeriesLinksFixture
    {
        [Test]
        public void microphone_only_reuses_a_same_work_exact_edition_without_changing_monitoring_or_pin(
            [Values(false, true)] bool postgres, [Values(false, true)] bool pinned,
            [Values(false, true)] bool sameWork, [Values(false, true)] bool monitored)
        {
            WithWantedWriter(postgres, (service, books, editions, authors, _) =>
            {
                var authorId = authors.All().Single().Id;
                var other = SeedWantedBook(books, editions, authorId, "1001", "Stephen Fry", pinned);
                var source = SeedWantedBook(books, editions, authorId, "1002", "Jim Dale", false);
                other.HardcoverBookId = sameWork ? source.HardcoverBookId : other.HardcoverBookId;
                other.SetMonitored(monitored);
                books.Update(other);
                var requested = source.Editions[1];
                var selected = other.Editions.Single(edition => edition.Monitored);
                selected.ForeignEditionId = requested.ForeignEditionId;
                selected.HardcoverEditionId = requested.HardcoverEditionId;
                editions.Update(selected);
                var before = other.Editions.Select(edition => (edition.Id, edition.Monitored, edition.ManualAdd)).ToArray();

                var result = service.AddWantedEditionCopy(source.Id, requested.Id);

                Assert.Multiple(() =>
                {
                    Assert.That(result.ExistingCopy, Is.EqualTo(sameWork));
                    Assert.That(result.Book.Id == other.Id, Is.EqualTo(sameWork));
                    Assert.That(books.GetBooksByAuthorId(authorId), Has.Count.EqualTo(sameWork ? 2 : 3));
                    Assert.That(books.Get(other.Id).AnyEditionOk, Is.EqualTo(!pinned));
                    Assert.That(books.Get(other.Id).IsMonitored(), Is.EqualTo(monitored));
                    Assert.That(editions.FindByBook(new[] { other.Id }).OrderBy(edition => edition.Id)
                        .Select(edition => (edition.Id, edition.Monitored, edition.ManualAdd)), Is.EqualTo(before));
                    Assert.That(books.Get(source.Id).AnyEditionOk, Is.True);
                });
            });
        }

        [Test]
        public void microphone_returns_an_exact_pinned_origin_unchanged_before_other_copies(
            [Values(false, true)] bool postgres, [Values(false, true)] bool monitored)
        {
            WithWantedWriter(postgres, (service, books, editions, authors, _) =>
            {
                var authorId = authors.All().Single().Id;
                var other = SeedWantedBook(books, editions, authorId, "1001", "Jim Dale");
                var source = SeedWantedBook(books, editions, authorId, "1002", "Jim Dale");
                source.HardcoverBookId = other.HardcoverBookId;
                source.SetMonitored(monitored);
                books.Update(source);
                var requested = source.Editions.Single(edition => edition.Monitored);
                var selected = other.Editions.Single(edition => edition.Monitored);
                selected.HardcoverEditionId = requested.HardcoverEditionId;
                selected.ForeignEditionId = requested.ForeignEditionId;
                editions.Update(selected);

                var result = service.AddWantedEditionCopy(source.Id, requested.Id);
                Assert.That(result.ExistingCopy, Is.True);
                Assert.That(result.Book.Id, Is.EqualTo(source.Id));
                Assert.That(books.Get(source.Id).IsMonitored(), Is.EqualTo(monitored));
                Assert.That(books.GetBooksByAuthorId(authorId), Has.Count.EqualTo(2));
            });
        }

        [Test]
        public void different_amazon_editions_sharing_an_asin_do_not_suppress_a_copy([Values(false, true)] bool postgres)
        {
            WithWantedWriter(postgres, (service, books, editions, authors, _) =>
            {
                var source = SeedWantedBook(books, editions, authors.All().Single().Id, "1001", "Jim Dale");
                source.Editions[0].ForeignEditionId = "az:B000000001-audiobook";
                source.Editions[1].ForeignEditionId = "az:B000000002-audiobook";
                foreach (var edition in source.Editions)
                {
                    edition.HardcoverEditionId = null;
                    edition.Asin = "B000000001";
                }

                editions.UpdateMany(source.Editions);
                var result = service.AddWantedEditionCopy(source.Id, source.Editions[1].Id);
                Assert.That(result.ExistingCopy, Is.False);
                Assert.That(result.Book.Id, Is.Not.EqualTo(source.Id));
                Assert.That(result.Book.Editions.Single(edition => edition.Monitored).ForeignEditionId,
                    Is.EqualTo(source.Editions[1].ForeignEditionId));
                Assert.That(editions.FindByBook(new[] { source.Id }).Single(edition => edition.Monitored).Id,
                    Is.EqualTo(source.Editions[0].Id));
            });
        }

        [Test]
        public void microphone_reuses_the_same_primary_amazon_edition_on_repeat(
            [Values(false, true)] bool postgres, [Values(false, true)] bool normalizedCopy)
        {
            WithWantedWriter(postgres, (service, books, editions, authors, _) =>
            {
                var source = SeedWantedBook(books, editions, authors.All().Single().Id, "1001", "Jim Dale", false);
                SetPrimaryAmazonEditions(editions, source);
                var requested = source.Editions[1];
                var first = service.AddWantedEditionCopy(source.Id, requested.Id);
                Assert.That(first.ExistingCopy, Is.False);
                if (normalizedCopy)
                {
                    var selected = editions.FindByBook(new[] { first.Book.Id }).Single(edition => edition.Monitored);
                    selected.ForeignEditionId = "AZ:b000000002";
                    editions.Update(selected);
                }

                var second = service.AddWantedEditionCopy(source.Id, requested.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(second.ExistingCopy, Is.True);
                    Assert.That(second.Book.Id, Is.EqualTo(first.Book.Id));
                    Assert.That(books.GetBooksByAuthorId(source.AuthorId), Has.Count.EqualTo(2));
                    Assert.That(books.Get(source.Id).AnyEditionOk, Is.True);
                });
            });
        }

        [Test]
        public void microphone_keeps_two_different_primary_amazon_editions_as_two_copies([Values(false, true)] bool postgres)
        {
            WithWantedWriter(postgres, (service, books, editions, authors, _) =>
            {
                var source = SeedWantedBook(books, editions, authors.All().Single().Id, "1001", "Jim Dale", false);
                SetPrimaryAmazonEditions(editions, source);
                Assert.That(source.Editions[0].Monitored, Is.True);
                Assert.That(source.Editions[1].ForeignEditionId, Is.Not.EqualTo(source.Editions[0].ForeignEditionId));
                var first = service.AddWantedEditionCopy(source.Id, source.Editions[0].Id);
                var second = service.AddWantedEditionCopy(source.Id, source.Editions[1].Id);
                Assert.Multiple(() =>
                {
                    Assert.That(first.ExistingCopy, Is.True, "the original already selects the first edition");
                    Assert.That(first.Book.Id, Is.EqualTo(source.Id));
                    Assert.That(second.ExistingCopy, Is.False);
                    Assert.That(second.Book.Id, Is.Not.EqualTo(first.Book.Id));
                    Assert.That(books.GetBooksByAuthorId(source.AuthorId), Has.Count.EqualTo(2), "one row for each distinct edition");
                    Assert.That(first.Book.Editions.Single(edition => edition.Monitored).ForeignEditionId,
                        Is.EqualTo(source.Editions[0].ForeignEditionId));
                    Assert.That(second.Book.Editions.Single(edition => edition.Monitored).ForeignEditionId,
                        Is.EqualTo(source.Editions[1].ForeignEditionId));
                });
            });
        }

        [Test]
        public void microphone_primary_amazon_id_does_not_match_a_goodreads_secondary_asin(
            [Values(false, true)] bool postgres, [Values(false, true)] bool amazonFirst)
        {
            WithWantedWriter(postgres, (service, books, editions, authors, _) =>
            {
                var source = SeedWantedBook(books, editions, authors.All().Single().Id, "1001", "Jim Dale", false);
                SetPrimaryAmazonEditions(editions, source);
                var goodreads = source.Editions[0];
                goodreads.ForeignEditionId = "gr:7701-audiobook";
                goodreads.GoodreadsEditionId = 7701;
                goodreads.Asin = goodreads.AudibleASIN = "B000000002";
                goodreads.Asins = new List<string> { "B000000002" };
                editions.Update(goodreads);

                var first = service.AddWantedEditionCopy(source.Id, source.Editions[amazonFirst ? 1 : 0].Id);
                var second = service.AddWantedEditionCopy(source.Id, source.Editions[amazonFirst ? 0 : 1].Id);
                Assert.That(first.ExistingCopy, Is.EqualTo(!amazonFirst), "only the already-selected Goodreads edition reuses the original");
                Assert.That(second.ExistingCopy, Is.EqualTo(amazonFirst), "the primary Amazon edition still creates its own copy");
                Assert.That((amazonFirst ? second : first).Book.Id, Is.EqualTo(source.Id));
                Assert.That(second.Book.Id, Is.Not.EqualTo(first.Book.Id));
                Assert.That(books.GetBooksByAuthorId(source.AuthorId), Has.Count.EqualTo(2));
            });
        }

        [Test]
        public void microphone_primary_amazon_id_cannot_override_different_served_edition_ids(
            [Values(false, true)] bool postgres, [Values("hc", "gr")] string provider)
        {
            WithWantedWriter(postgres, (service, books, editions, authors, _) =>
            {
                var source = SeedWantedBook(books, editions, authors.All().Single().Id, "1001", "Jim Dale", false);
                SetPrimaryAmazonEditions(editions, source);
                for (var i = 0; i < source.Editions.Count; i++)
                {
                    var edition = source.Editions[i];
                    edition.ForeignEditionId = "az:B000000001-audiobook";
                    if (provider == "hc") edition.HardcoverEditionId = $"hc:edition:{7701 + i}";
                    else edition.GoodreadsEditionId = 7701 + i;
                }

                editions.UpdateMany(source.Editions);
                Assert.That(source.Editions[0].Monitored, Is.True);
                var first = service.AddWantedEditionCopy(source.Id, source.Editions[0].Id);
                var second = service.AddWantedEditionCopy(source.Id, source.Editions[1].Id);
                Assert.That(first.ExistingCopy, Is.True, "the original already selects the first edition");
                Assert.That(first.Book.Id, Is.EqualTo(source.Id));
                Assert.That(second.ExistingCopy, Is.False);
                Assert.That(second.Book.Id, Is.Not.EqualTo(first.Book.Id));
                Assert.That(books.GetBooksByAuthorId(source.AuthorId), Has.Count.EqualTo(2), "conflicting served editions stay separate");
            });
        }

        private static void SetPrimaryAmazonEditions(EditionRepository editions, Book book)
        {
            for (var i = 0; i < book.Editions.Count; i++)
            {
                book.Editions[i].HardcoverEditionId = null;
                book.Editions[i].ForeignEditionId = $"az:B{i + 1:D9}-audiobook";
            }

            editions.UpdateMany(book.Editions);
        }

        [Test]
        public void microphone_and_picker_reuse_an_amazon_only_work_after_reload([Values(false, true)] bool postgres)
        {
            WithWantedWriter(postgres, (service, books, editions, authors, _) =>
            {
                var source = SeedWantedBook(books, editions, authors.All().Single().Id, "1001", "Jim Dale", false);
                source.HardcoverBookId = null;
                source.RemoteProviderIds = new HashSet<string>();
                source.BaseBookId = "az:B000000001-audiobook";
                books.Update(source);
                Assert.That(BookIdentity.GetStableWorkProviderIdentityTokens(books.Get(source.Id)), Is.Empty);
                SetPrimaryAmazonEditions(editions, source);
                var requested = source.Editions[1];
                var first = service.AddWantedEditionCopy(source.Id, requested.Id);
                var stored = books.Get(first.Book.Id);
                stored.BaseBookId = "AZ:b000000001";
                stored.AudiobookMonitored = false;
                books.Update(stored);
                Assert.That(BookIdentity.GetStableWorkProviderIdentityTokens(books.Get(stored.Id)), Is.Empty);

                var controller = WantedPicker(service);
                Assert.That(controller.GetEditions(new List<int> { int.MaxValue }), Is.Empty,
                    "a deleted book must not make the edition picker throw");
                var annotation = controller.GetEditions(new List<int> { source.Id }).Single(edition => edition.Id == requested.Id);
                var second = service.AddWantedEditionCopy(source.Id, requested.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(first.ExistingCopy, Is.False);
                    Assert.That(second.ExistingCopy, Is.True);
                    Assert.That(second.Book.Id, Is.EqualTo(first.Book.Id));
                    Assert.That(annotation.ExistingCopyId, Is.EqualTo(second.Book.Id));
                    Assert.That(annotation.ExistingCopyMonitored, Is.False);
                    Assert.That(books.GetBooksByAuthorId(source.AuthorId), Has.Count.EqualTo(2));
                });
            });
        }

        [Test]
        public void picker_and_copy_use_the_same_work_and_edition_identity(
            [Values(false, true)] bool postgres, [Values("stable", "amazon", "amazon-other-work", "secondary", "different-work", "automatic", "ebook")] string identity,
            [Values(false, true)] bool monitored)
        {
            var editionReads = 0;
            WithWantedWriter(postgres, (service, books, editions, authors, files) =>
            {
                var authorId = authors.All().Single().Id;
                var other = SeedWantedBook(books, editions, authorId, "1001", "Stephen Fry", identity != "automatic");
                var source = SeedWantedBook(books, editions, authorId, "1002", "Jim Dale", false);
                var anotherRequest = SeedWantedBook(books, editions, authorId, "1003", "Jim Dale", false);
                other.HardcoverBookId = identity == "different-work" ? other.HardcoverBookId : source.HardcoverBookId;
                if (identity == "ebook") other.MediaType = BookMediaType.Ebook;
                other.SetMonitored(monitored);
                if (identity is "amazon" or "amazon-other-work")
                {
                    source.HardcoverBookId = other.HardcoverBookId = null;
                    source.RemoteProviderIds = new HashSet<string>();
                    other.RemoteProviderIds = new HashSet<string>();
                    source.BaseBookId = other.BaseBookId = "az:B000000010";
                    if (identity == "amazon-other-work") other.BaseBookId = "az:B000000011";
                    books.Update(source);
                }

                books.Update(other);
                if (identity is "amazon" or "amazon-other-work")
                {
                    Assert.That(BookIdentity.GetStableWorkProviderIdentityTokens(books.Get(source.Id)), Is.Empty);
                    Assert.That(BookIdentity.GetStableWorkProviderIdentityTokens(books.Get(other.Id)), Is.Empty);
                }
                var selected = other.Editions.Single(edition => edition.Monitored);
                var requested = source.Editions[1];
                selected.ForeignEditionId = requested.ForeignEditionId;
                selected.HardcoverEditionId = requested.HardcoverEditionId;
                if (identity is "amazon" or "amazon-other-work" or "secondary")
                {
                    requested.ForeignEditionId = "az:B000000001-audiobook";
                    requested.HardcoverEditionId = null;
                    selected.Asin = selected.AudibleASIN = "B000000001";
                    selected.Asins = new List<string> { "B000000001" };
                    if (identity is "amazon" or "amazon-other-work")
                    {
                        selected.ForeignEditionId = "az:b000000001";
                        selected.HardcoverEditionId = null;
                    }
                }

                editions.UpdateMany(new List<Edition> { selected, requested });
                var controller = WantedPicker(service);
                var resources = controller.GetEditions(new List<int> { source.Id, anotherRequest.Id });
                var annotation = resources.Single(edition => edition.Id == requested.Id);
                Assert.That(editionReads, Is.EqualTo(1), "one edition batch for the whole page");
                var result = service.AddWantedEditionCopy(source.Id, requested.Id);
                var reuse = identity == "stable" || identity == "amazon" || identity == "automatic";
                Assert.Multiple(() =>
                {
                    Assert.That(annotation.ExistingCopyId, Is.EqualTo(reuse ? (int?)other.Id : null));
                    Assert.That(annotation.ExistingCopyMonitored, Is.EqualTo(reuse ? (bool?)monitored : null));
                    Assert.That(result.ExistingCopy, Is.EqualTo(reuse));
                    Assert.That(result.Book.Id == other.Id, Is.EqualTo(reuse));
                    Assert.That(books.Get(other.Id).IsMonitored(), Is.EqualTo(monitored));
                });
            }, wrap: inner => InterfaceProxy.Proxy<IEditionService>((method, args) =>
            {
                if (method.Name == nameof(IEditionService.GetEditionsByBook) || method.Name == nameof(IEditionService.GetEditionRowsByBook)) editionReads++;
                return method.Invoke(inner, args);
            }));
        }

        [Test]
        public void picker_marks_an_automatic_origins_own_selected_edition_as_existing(
            [Values(false, true)] bool postgres, [Values(false, true)] bool monitored)
        {
            WithWantedWriter(postgres, (service, books, editions, authors, _) =>
            {
                var source = SeedWantedBook(books, editions, authors.All().Single().Id, "1001", "Jim Dale", false);
                source.SetMonitored(monitored);
                books.Update(source);
                var selected = source.Editions.Single(edition => edition.Monitored);
                var rows = WantedPicker(service).GetEditions(new List<int> { source.Id });
                var own = rows.Single(edition => edition.Id == selected.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(own.ExistingCopyId, Is.EqualTo(source.Id));
                    Assert.That(own.ExistingCopyMonitored, Is.EqualTo(monitored));
                    Assert.That(rows.Single(edition => edition.Id == source.Editions[1].Id).ExistingCopyId, Is.Null);
                    Assert.That(books.Get(source.Id).AnyEditionOk, Is.True);
                    Assert.That(books.Get(source.Id).IsMonitored(), Is.EqualTo(monitored));
                    Assert.That(books.GetBooksByAuthorId(source.AuthorId), Has.Count.EqualTo(1));
                });
            });
        }

        [Test]
        public void copy_finder_loads_editions_and_files_only_for_the_requested_work([Values(false, true)] bool postgres)
        {
            var editionQueries = new List<int[]>();
            WithWantedWriter(postgres, (service, books, editions, authors, files) =>
            {
                var authorId = authors.All().Single().Id;
                var source = SeedWantedBook(books, editions, authorId, "1001", "Jim Dale", false);
                var other = SeedWantedBook(books, editions, authorId, "1002", "Stephen Fry", false);
                other.HardcoverBookId = source.HardcoverBookId;
                books.Update(other);
                var selected = other.Editions.Single(edition => edition.Monitored);
                var requested = source.Editions[1];
                selected.ForeignEditionId = requested.ForeignEditionId;
                selected.HardcoverEditionId = requested.HardcoverEditionId;
                editions.Update(selected);
                for (var work = 2000; work < 2025; work++) SeedWantedBook(books, editions, authorId, work.ToString(), "Jim Dale", false);

                var lookup = service.FindWantedEditionCopies(new[] { source.Id });
                var result = service.AddWantedEditionCopy(source.Id, requested.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(lookup.CandidateCount, Is.EqualTo(2));
                    Assert.That(lookup.EditionCount, Is.EqualTo(4));
                    Assert.That(lookup.AuthorRowCount, Is.EqualTo(27));
                    Assert.That(lookup.FileCount, Is.Zero);
                    Assert.That(lookup.ExistingCopies[requested.Id].Id, Is.EqualTo(other.Id));
                    Assert.That(result.ExistingCopy, Is.True);
                    Assert.That(result.Book.Id, Is.EqualTo(other.Id));
                    Assert.That(editionQueries, Has.Count.EqualTo(2));
                    Assert.That(files.BatchQueries, Has.Count.EqualTo(2));
                    foreach (var ids in editionQueries.Concat(files.BatchQueries.Select(ids => ids.ToArray())))
                    {
                        Assert.That(ids, Is.EquivalentTo(new[] { source.Id, other.Id }));
                    }
                });
            }, wrap: inner => InterfaceProxy.Proxy<IEditionService>((method, args) =>
            {
                if (method.Name == nameof(IEditionService.GetEditionRowsByBook)) editionQueries.Add(((IEnumerable<int>)args[0]).ToArray());
                return method.Invoke(inner, args);
            }));
        }

        [Test]
        public void primary_amazon_work_never_overrides_a_stable_work_identity(
            [Values(false, true)] bool postgres, [Values(false, true)] bool sourceStable,
            [Values(false, true)] bool otherStable)
        {
            WithWantedWriter(postgres, (service, books, editions, authors, _) =>
            {
                var source = SeedWantedBook(books, editions, authors.All().Single().Id, "1001", "Jim Dale", false);
                var first = service.AddWantedEditionCopy(source.Id, source.Editions[1].Id);
                var other = books.Get(first.Book.Id);
                source.HardcoverBookId = sourceStable ? "hc:1001" : null;
                other.HardcoverBookId = otherStable ? "hc:1002" : null;
                source.RemoteProviderIds = new HashSet<string>();
                other.RemoteProviderIds = new HashSet<string>();
                source.BaseBookId = other.BaseBookId = "az:B000000001";
                books.UpdateMany(new List<Book> { source, other });
                Assert.That(BookIdentity.GetStableWorkProviderIdentityTokens(books.Get(source.Id)),
                    Is.EquivalentTo(sourceStable ? new[] { "hc:1001" } : System.Array.Empty<string>()));
                Assert.That(BookIdentity.GetStableWorkProviderIdentityTokens(books.Get(other.Id)),
                    Is.EquivalentTo(otherStable ? new[] { "hc:1002" } : System.Array.Empty<string>()));

                var result = service.AddWantedEditionCopy(source.Id, source.Editions[1].Id);
                Assert.That(result.ExistingCopy, Is.EqualTo(!sourceStable && !otherStable));
                Assert.That(result.Book.Id == other.Id, Is.EqualTo(!sourceStable && !otherStable));
            });
        }

        private static EditionController WantedPicker(BookService service)
        {
            return new EditionController(service, InterfaceProxy.Proxy<IMediaCoverProxy>((_, _) => null))
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
        }

        [Test]
        public void picker_and_copy_reuse_a_file_linked_edition_without_writing_existing_rows(
            [Values(false, true)] bool postgres, [Values(false, true)] bool pinned,
            [Values(false, true)] bool monitored)
        {
            WithWantedWriter(postgres, (service, books, editions, authors, files) =>
            {
                var authorId = authors.All().Single().Id;
                var other = SeedWantedBook(books, editions, authorId, "1001", "Jim Dale", pinned);
                var source = SeedWantedBook(books, editions, authorId, "1002", "Jim Dale", false);
                other.HardcoverBookId = source.HardcoverBookId;
                other.SetMonitored(monitored);
                books.Update(other);
                var filedEdition = other.Editions[1];
                var requested = source.Editions[1];
                filedEdition.ForeignEditionId = requested.ForeignEditionId;
                filedEdition.HardcoverEditionId = requested.HardcoverEditionId;
                editions.Update(filedEdition);
                Assert.That(filedEdition.Monitored, Is.False);
                var file = files.Repository.Insert(new BookFile
                {
                    EditionId = filedEdition.Id, Path = "/library/recording.m4b", MediaType = "audiobook",
                    Quality = new QualityModel(Quality.M4B),
                    MediaInfo = new MediaInfoModel(), DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow,
                    AudioProductionOverride = false
                });
                var before = editions.FindByBook(new[] { other.Id })
                    .Select(edition => (edition.Id, edition.Monitored, edition.ManualAdd)).ToArray();

                var annotation = WantedPicker(service).GetEditions(new List<int> { source.Id })
                    .Single(edition => edition.Id == requested.Id);
                var result = service.AddWantedEditionCopy(source.Id, requested.Id);
                var storedFile = files.Repository.Get(file.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(annotation.ExistingCopyId, Is.EqualTo(other.Id));
                    Assert.That(annotation.ExistingCopyMonitored, Is.EqualTo(monitored));
                    Assert.That(annotation.BookFileCount, Is.Zero, "copy ownership comes from file edition IDs, not the resource count");
                    Assert.That(result.ExistingCopy, Is.True);
                    Assert.That(result.Book.Id, Is.EqualTo(other.Id));
                    Assert.That(books.GetBooksByAuthorId(authorId), Has.Count.EqualTo(2));
                    Assert.That(books.Get(other.Id).AnyEditionOk, Is.EqualTo(!pinned));
                    Assert.That(books.Get(other.Id).IsMonitored(), Is.EqualTo(monitored));
                    Assert.That(editions.FindByBook(new[] { other.Id })
                        .Select(edition => (edition.Id, edition.Monitored, edition.ManualAdd)), Is.EqualTo(before));
                    Assert.That((storedFile.EditionId, storedFile.Path, storedFile.AudioProductionOverride),
                        Is.EqualTo((file.EditionId, file.Path, file.AudioProductionOverride)));
                    Assert.That(files.BatchQueries, Has.Count.EqualTo(2), "one file batch per picker or copy operation");
                });
            }, persistedFiles: true);
        }

        [Test]
        public void same_narrator_or_cast_label_never_hides_a_different_provider_edition(
            [Values(false, true)] bool postgres, [Values("George Guidall", "Full Cast")] string narrator)
        {
            WithWantedWriter(postgres, (service, books, editions, authors, files) =>
            {
                var source = SeedWantedBook(books, editions, authors.All().Single().Id, "1001", "Jim Dale", false);
                source.Editions.ForEach(edition => edition.Narrator = narrator);
                editions.UpdateMany(source.Editions);
                var requested = source.Editions[1];
                var annotation = WantedPicker(service).GetEditions(new List<int> { source.Id })
                    .Single(edition => edition.Id == requested.Id);
                var result = service.AddWantedEditionCopy(source.Id, requested.Id);
                Assert.Multiple(() =>
                {
                    Assert.That(annotation.ExistingCopyId, Is.Null);
                    Assert.That(result.ExistingCopy, Is.False);
                    Assert.That(result.Book.Id, Is.Not.EqualTo(source.Id));
                    Assert.That(result.Book.Editions.Single(edition => edition.Monitored).ForeignEditionId,
                        Is.EqualTo(requested.ForeignEditionId));
                    Assert.That(books.GetBooksByAuthorId(source.AuthorId), Has.Count.EqualTo(2));
                });
            });
        }

        [Test]
        public void shared_isbn_does_not_prove_an_exact_edition_copy([Values(false, true)] bool postgres)
        {
            WithWantedWriter(postgres, (service, books, editions, authors, _) =>
            {
                var source = SeedWantedBook(books, editions, authors.All().Single().Id, "1001", "Jim Dale");
                foreach (var edition in source.Editions)
                {
                    edition.HardcoverEditionId = null;
                    edition.ForeignEditionId = null;
                    edition.Isbn13 = "9780000000019";
                }

                editions.UpdateMany(source.Editions);
                var result = service.AddWantedEditionCopy(source.Id, source.Editions[1].Id);
                Assert.That(result.ExistingCopy, Is.False);
                Assert.That(result.Book.Id, Is.Not.EqualTo(source.Id));
                Assert.That(editions.FindByBook(new[] { source.Id }).Single(edition => edition.Monitored).Id,
                    Is.EqualTo(source.Editions[0].Id));
            });
        }

        [Test]
        public void non_audio_edition_is_refused_by_copy_and_audiobook_batch(
            [Values(false, true)] bool postgres, [Values(false, true)] bool copy,
            [Values(EditionReadingFormat.Print, EditionReadingFormat.Ebook)] int format)
        {
            WithWantedWriter(postgres, (service, books, editions, authors, _) =>
            {
                var source = SeedWantedBook(books, editions, authors.All().Single().Id, "1001", "Jim Dale", false);
                var requested = source.Editions[1];
                requested.ReadingFormatId = format;
                editions.Update(requested);
                Assert.Throws<ValidationException>(() =>
                {
                    if (copy) service.AddWantedEditionCopy(source.Id, requested.Id);
                    else service.AddWantedEditions(new List<Edition> { requested });
                });
                Assert.That(books.GetBooksByAuthorId(source.AuthorId), Has.Count.EqualTo(1));
                Assert.That(books.Get(source.Id).AnyEditionOk, Is.True);
                Assert.That(editions.FindByBook(new[] { source.Id }).Single(edition => edition.Monitored).Id,
                    Is.EqualTo(source.Editions[0].Id));
            });
        }
    }
}
