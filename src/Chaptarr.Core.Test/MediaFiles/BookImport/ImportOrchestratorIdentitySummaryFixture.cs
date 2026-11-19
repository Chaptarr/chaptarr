using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class ImportOrchestratorIdentitySummaryFixture
    {
        [Test]
        public void receipt_summary_rebinds_to_committed_edition_ownership_once_per_batch_and_can_retry()
        {
#pragma warning disable SYSLIB0050
            var sut = (ImportOrchestratorV2)FormatterServices.GetUninitializedObject(typeof(ImportOrchestratorV2));
#pragma warning restore SYSLIB0050
            var calls = new Dictionary<string, int>();
            var fail = true;
            void Set(string field, object value) => typeof(ImportOrchestratorV2).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(sut, value);
            void Count(string name) => calls[name] = calls.GetValueOrDefault(name) + 1;
            Set("_logger", LogManager.GetCurrentClassLogger());
            Set("_mediaFileRepository", Proxy<IMediaFileRepository>((method, args) =>
            {
                Count("files");
                if (fail) throw new InvalidOperationException("temporary library read outage");
                return ((List<string>)args[0]).Select(path => new BookFile { Path = path, EditionId = 73 }).ToList();
            }));
            Set("_editionService", Proxy<IEditionService>((method, args) =>
            {
                Count("editions");
                Assert.That((IEnumerable<int>)args[0], Is.EqualTo(new[] { 73 }));
                return new List<Edition> { new() { Id = 73, BookId = 99 } };
            }));
            Set("_bookService", Proxy<IBookService>((method, args) =>
            {
                Count("books");
                Assert.That(((IEnumerable<int>)args[0]).Distinct(), Is.EqualTo(new[] { 99 }));
                return new List<Book> { new() { Id = 99, AuthorId = 626, Title = "The Horde" } };
            }));
            Set("_authorService", Proxy<IAuthorService>((method, args) =>
            {
                Count("authors");
                return new List<Author> { new() { Id = 626, Name = "Linda Kay Silva" } };
            }));
            var result = new OrchestratorImportResult { StagingResults = Enumerable.Range(1, 20).Select(index =>
                new NzbDrone.Core.Datastore.ImportResult { Path = $"/library/The Horde/{index}.mp3", BookId = 41, AuthorId = 518, Outcome = ImportOutcome.Imported }).ToList() };
            sut.PopulateImportSummary(result);
            Assert.That(result.ImportedFiles, Is.Empty);
            Assert.That(result.StagingResults.All(item => item.Outcome == ImportOutcome.Imported), Is.True);
            fail = false;
            calls.Clear();
            sut.PopulateImportSummary(result);
            Assert.Multiple(() =>
            {
                Assert.That(result.ImportedFiles, Has.Count.EqualTo(20));
                Assert.That(result.ImportedFiles.All(item => item.BookId == 99 && item.AuthorName == "Linda Kay Silva"), Is.True);
                Assert.That(result.StagingResults.All(item => item.AuthorId == 626 && item.BookId == 99), Is.True);
                Assert.That(result.AddedAuthors, Is.EqualTo(new[] { "Linda Kay Silva" }));
                Assert.That(calls.Values, Is.All.EqualTo(1));
            });
        }
    }
}
