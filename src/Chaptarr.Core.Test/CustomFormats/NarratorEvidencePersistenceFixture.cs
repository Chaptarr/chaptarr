using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.History;
using Chaptarr.Api.V1.Indexers;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.History;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.CustomFormats
{
    [TestFixture]
    [NonParallelizable]
    public class NarratorEvidencePersistenceFixture
    {
        [TestCase(false, "Narrated by Scott Brick; 64 kbps", true)]
        [TestCase(true, "Narrated by Scott Brick; 64 kbps", true)]
        [TestCase(false, "Gelesen von Scott Brick; weitere Angaben", true)]
        [TestCase(true, "Gelesen von Scott Brick; weitere Angaben", true)]
        [TestCase(false, "Lu par Scott Brick; autres renseignements", true)]
        [TestCase(true, "Lu par Scott Brick; autres renseignements", true)]
        [TestCase(false, "Private tracker text: unknown reader", false)]
        [TestCase(true, "Private tracker text: unknown reader", false)]
        public void known_narrator_evidence_should_survive_existing_pending_and_history_json_without_public_raw_text(bool postgres, string field, bool expected)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                var author = new Author { Id = 101, Name = "Frank Herbert" };
                var book = new Book
                {
                    Id = 201, AuthorId = author.Id, Author = author, Title = "Dune", MediaType = BookMediaType.Audiobook, AnyEditionOk = false,
                    Editions = new List<Edition>
                    {
                        new Edition { Id = 301, BookId = 201, Title = "Dune", ReadingFormatId = 2, ManualAdd = true, Monitored = true, NarratorNames = new List<string> { "Scott Brick" } }
                    }
                };
                var parsed = new ParsedBookInfo { BookTitle = "Dune", Quality = new QualityModel(Quality.MP3) };
                var release = new ReleaseInfo { Title = "Dune", PublishDate = DateTime.UtcNow, NarratorEvidenceFields = new List<string> { field } };
                var pending = new PendingReleaseRepository(database, events);
                var saved = pending.Insert(new PendingRelease
                {
                    AuthorId = author.Id, Title = release.Title, Added = DateTime.UtcNow, ParsedBookInfo = parsed,
                    Release = release, Reason = PendingReleaseReason.Delay, AdditionalInfo = new PendingReleaseAdditionalInfo { BookIds = new List<int> { book.Id } }
                });
                var restored = pending.Get(saved.Id);
                var remote = new RemoteBook { Author = author, Books = new List<Book> { book }, ParsedBookInfo = restored.ParsedBookInfo, Release = restored.Release };
                var format = new CustomFormat
                {
                    Id = 401, Name = "Known wanted narrator",
                    Specifications = new List<ICustomFormatSpecification> { new PreferredNarratorSpecification() }
                };
                var calculator = new CustomFormatCalculationService(InterfaceProxy.Proxy<ICustomFormatService>((method, _) =>
                    method.Name == nameof(ICustomFormatService.All) ? new List<CustomFormat> { format } : throw new InvalidOperationException(method.Name)), LogManager.GetCurrentClassLogger());
                var historyRepository = new HistoryRepository(database, events);
                var written = historyRepository.Insert(new EntityHistory
                {
                    AuthorId = author.Id, BookId = book.Id, EditionId = 301, SourceTitle = release.Title, Quality = parsed.Quality,
                    Date = DateTime.UtcNow, EventType = EntityHistoryEventType.Grabbed,
                    Data = new Dictionary<string, string> { [nameof(ReleaseInfo.NarratorEvidenceFields)] = release.NarratorEvidenceFields.ToJson(), ["existingMetadata"] = "retained" }
                });
                var history = historyRepository.Get(written.Id);
                history.Book = book;
                history.Author = author;
                var releaseResource = new DownloadDecision(remote).ToResource();
                var historyResource = history.ToResource(calculator);
                Assert.Multiple(() =>
                {
                    Assert.That(restored.Release.NarratorEvidenceFields, Is.EqualTo(new[] { field }));
                    Assert.That(restored.Release.Narrator, Is.Null);
                    Assert.That(calculator.ParseCustomFormat(remote, 0).Any(match => match.Id == format.Id), Is.EqualTo(expected));
                    Assert.That(calculator.ParseCustomFormat(history, author).Any(match => match.Id == format.Id), Is.EqualTo(expected));
                    Assert.That(releaseResource.Narrator, Is.EqualTo(expected ? "Scott Brick" : null));
                    Assert.That(history.GetDataValue(nameof(ReleaseInfo.NarratorEvidenceFields)), Is.EqualTo(release.NarratorEvidenceFields.ToJson()));
                    Assert.That(historyResource.Data.Keys.Any(key => string.Equals(key, nameof(ReleaseInfo.NarratorEvidenceFields), StringComparison.OrdinalIgnoreCase)), Is.False);
                    Assert.That(historyResource.Data["existingMetadata"], Is.EqualTo("retained"));
                    Assert.That(System.Text.Json.JsonSerializer.Serialize(releaseResource), Does.Not.Contain(field));
                });
            });
        }
    }
}
