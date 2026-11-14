using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class CompletedDownloadServiceStatusMessageFixture
    {
        [TestCase(BookMediaType.Ebook, false)]
        [TestCase(BookMediaType.Audiobook, false)]
        [TestCase(BookMediaType.Audiobook, true)]
        public void file_failure_is_reported_once_without_a_generic_completion_warning(BookMediaType mediaType, bool converted)
        {
            var harness = CreateHarness(mediaType);
            var result = FailedFile(harness, mediaType == BookMediaType.Ebook ? @"C:\downloads\book.epub".AsOsAgnostic() : @"C:\downloads\book.m4b".AsOsAgnostic());
            if (converted)
            {
                result.ImportDecision.Item.GeneratedConversionSourcePaths = new List<string> { @"C:\downloads\book.mp3".AsOsAgnostic() };
            }

            harness.ImportResults = new List<ImportResult> { result };
            harness.Subject.Import(harness.TrackedDownload);

            Assert.That(harness.TrackedDownload.StatusMessages, Has.Length.EqualTo(1));
            var message = harness.TrackedDownload.StatusMessages.Single();
            Assert.That(message.Title, Is.EqualTo(converted ? "book.mp3" : Path.GetFileName(result.ImportDecision.Item.Path)));
            Assert.That(message.Messages, Is.EqualTo(result.Errors));
            AssertBlocked(harness);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void separate_files_keep_their_distinct_failures_even_with_the_same_filename(bool confirmation)
        {
            var harness = CreateHarness(BookMediaType.Audiobook);
            var first = FailedFile(harness, @"C:\downloads\learn-my-lesson\disc-1\01.mp3".AsOsAgnostic());
            var second = FailedFile(harness, @"C:\downloads\learn-my-lesson\disc-2\01.mp3".AsOsAgnostic(), "Import failed: No space left on device");
            if (confirmation)
            {
                first = new ImportResult(first.ImportDecision, ImportResultType.WaitingForConfirmation, first.Errors.ToArray());
                second = new ImportResult(second.ImportDecision, ImportResultType.WaitingForConfirmation, second.Errors.ToArray());
            }
            harness.ImportResults = new List<ImportResult> { first, second };

            harness.Subject.Import(harness.TrackedDownload);

            Assert.That(harness.TrackedDownload.StatusMessages.Select(message => message.Title),
                Is.EquivalentTo(new[] { @"disc-1\01.mp3".AsOsAgnostic(), @"disc-2\01.mp3".AsOsAgnostic() }));
            Assert.That(harness.TrackedDownload.StatusMessages.SelectMany(message => message.Messages),
                Is.EquivalentTo(first.Errors.Concat(second.Errors)));
            AssertBlocked(harness);
        }

        [Test]
        public void skipped_bonus_status_keeps_relative_directories_after_successful_import()
        {
            var harness = CreateHarness(BookMediaType.Audiobook);
            var imported = new ImportResult(FailedFile(harness, @"C:\downloads\learn-my-lesson\book.mp3".AsOsAgnostic()).ImportDecision);
            var first = ImportResult.SkipIncludedMedia(FailedFile(harness, @"C:\downloads\learn-my-lesson\bonus-1\book.epub".AsOsAgnostic()).ImportDecision, "Quality not allowed");
            var second = ImportResult.SkipIncludedMedia(FailedFile(harness, @"C:\downloads\learn-my-lesson\bonus-2\book.epub".AsOsAgnostic()).ImportDecision, "Quality not allowed");
            harness.ImportResults = new() { imported, first, second };
            harness.Subject.Import(harness.TrackedDownload);
            Assert.That(harness.TrackedDownload.State, Is.EqualTo(TrackedDownloadState.Imported));
            Assert.That(harness.TrackedDownload.StatusMessages.Select(message => message.Title),
                Is.EquivalentTo(new[] { @"bonus-1\book.epub".AsOsAgnostic(), @"bonus-2\book.epub".AsOsAgnostic() }));
        }

        [Test]
        public void a_file_failure_does_not_hide_another_required_file_without_an_import_result()
        {
            var harness = CreateHarness(BookMediaType.Audiobook);
            var result = FailedFile(harness, @"C:\downloads\01.mp3".AsOsAgnostic());
            harness.ImportResults = new List<ImportResult> { result };
            harness.TrackedDownload.DownloadItem.FilePaths = new List<string> { result.ImportDecision.Item.Path, @"C:\downloads\02.mp3".AsOsAgnostic() };

            harness.Subject.Import(harness.TrackedDownload);

            Assert.That(harness.TrackedDownload.StatusMessages, Has.Length.EqualTo(2));
            Assert.That(harness.TrackedDownload.StatusMessages.Single(message => message.Title == "01.mp3").Messages,
                Is.EqualTo(result.Errors));
            Assert.That(harness.TrackedDownload.StatusMessages.Single(message => message.Title == "02.mp3").Messages,
                Does.Contain("Source file has not been imported"));
            AssertBlocked(harness);
        }

        [Test]
        public void a_source_outside_the_download_folder_displays_only_its_filename()
        {
            var harness = CreateHarness(BookMediaType.Audiobook);
            harness.ImportResults = new List<ImportResult> { FailedFile(harness, @"C:\elsewhere\book.mp3".AsOsAgnostic()) };
            harness.Subject.Import(harness.TrackedDownload);
            Assert.That(harness.TrackedDownload.StatusMessages.Single().Title, Is.EqualTo("book.mp3"));
        }

        [Test]
        public void completion_without_source_evidence_keeps_its_fallback_warning()
        {
            var harness = CreateHarness(BookMediaType.Ebook);
            var completion = TrackedDownloadAlreadyImported.Evaluate(harness.TrackedDownload);

            Assert.That(completion.Complete, Is.False);
            Assert.That(completion.Missing, Has.Count.EqualTo(1));
            Assert.That(completion.Missing.Single().Messages,
                Does.Contain("Import completion could not be proven for the requested files and books"));
        }

        [TestCase(BookMediaType.Ebook)]
        [TestCase(BookMediaType.Audiobook)]
        public void successful_retry_clears_the_previous_file_failure(BookMediaType mediaType)
        {
            var harness = CreateHarness(mediaType);
            var result = FailedFile(harness, mediaType == BookMediaType.Ebook ? @"C:\downloads\book.epub".AsOsAgnostic() : @"C:\downloads\book.m4b".AsOsAgnostic());
            harness.ImportResults = new List<ImportResult> { result };
            harness.Subject.Import(harness.TrackedDownload);
            AssertBlocked(harness);

            harness.ImportResults = new List<ImportResult> { new(result.ImportDecision) };
            harness.Subject.Import(harness.TrackedDownload);

            Assert.That(harness.TrackedDownload.State, Is.EqualTo(TrackedDownloadState.Imported));
            Assert.That(harness.TrackedDownload.StatusMessages, Is.Empty);
            Assert.That(harness.EventAggregator.Events.OfType<DownloadCompletedEvent>().Count(), Is.EqualTo(1));
        }

        private static CompletedDownloadServiceQualityProfileFixture.Harness CreateHarness(BookMediaType mediaType)
        {
            var harness = new CompletedDownloadServiceQualityProfileFixture.Harness();
            harness.TrackedDownload.RemoteBook.Books.Single().MediaType = mediaType;
            harness.TrackedDownload.RemoteBook.ParsedBookInfo.Quality = new QualityModel(mediaType == BookMediaType.Ebook ? Quality.EPUB : Quality.M4B);
            return harness;
        }

        private static ImportResult FailedFile(CompletedDownloadServiceQualityProfileFixture.Harness harness, string path, string error = null)
        {
            var book = harness.TrackedDownload.RemoteBook.Books.Single();
            return new ImportResult(new ImportDecision<LocalBook>(new LocalBook
            {
                Path = path,
                Book = book,
                Edition = new Edition { Id = 20, BookId = book.Id, Book = book },
                Author = harness.TrackedDownload.RemoteBook.Author,
                Quality = harness.TrackedDownload.RemoteBook.ParsedBookInfo.Quality
            }), error ?? $"Import failed: Access to the path '{path}' is denied.");
        }

        private static void AssertBlocked(CompletedDownloadServiceQualityProfileFixture.Harness harness)
        {
            Assert.That(harness.TrackedDownload.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
            Assert.That(harness.Subject.CanRemoveDownload(harness.TrackedDownload), Is.False);
            Assert.That(harness.EventAggregator.Events.OfType<DownloadCompletedEvent>(), Is.Empty);
            Assert.That(harness.EventAggregator.Events.OfType<DownloadFailedEvent>(), Is.Empty);
            Assert.That(harness.EventAggregator.Events.OfType<BookImportIncompleteEvent>().Count(), Is.EqualTo(1));
        }
    }
}
