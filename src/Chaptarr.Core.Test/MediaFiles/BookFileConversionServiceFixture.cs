using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Threading;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Identification;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class BookFileConversionServiceFixture
    {
        private const int AuthorId = 7;
        private const int BookId = 42;
        private const int EditionId = 84;
        private const int OtherEditionId = 85;

        // Editions whose files were relinked after the convert window was opened.
        private const int MovedToOtherBookEditionId = 86;
        private const int MovedToOtherAuthorEditionId = 87;

        private IAuthorService _authorService;
        private AuthorServiceProxy _authorServiceProxy;
        private IMediaFileService _mediaFileService;
        private MediaFileServiceProxy _mediaFileServiceProxy;
        private StubImportApprovedBooks _importApprovedBooks;
        private IDiskProvider _diskProvider;
        private DiskProviderProxy _diskProviderProxy;
        private ConversionJobsProxy _jobs;
        private CommandQueueProxy _commands;
        private BookFileConversionService _service;

        [SetUp]
        public void Setup()
        {
            _authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            _authorServiceProxy = (AuthorServiceProxy)(object)_authorService;
            _authorServiceProxy.Author = Author(convertToQualityId: Quality.M4B.Id);

            _mediaFileService = DispatchProxy.Create<IMediaFileService, MediaFileServiceProxy>();
            _mediaFileServiceProxy = (MediaFileServiceProxy)(object)_mediaFileService;

            _diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            _diskProviderProxy = (DiskProviderProxy)(object)_diskProvider;

            _importApprovedBooks = new StubImportApprovedBooks();

            var jobs = DispatchProxy.Create<IConversionJobService, ConversionJobsProxy>();
            _jobs = (ConversionJobsProxy)(object)jobs;

            var commands = DispatchProxy.Create<IManageCommandQueue, CommandQueueProxy>();
            _commands = (CommandQueueProxy)(object)commands;

            _service = new BookFileConversionService(
                _authorService,
                DispatchProxy.Create<IBookService, BookServiceProxy>(),
                DispatchProxy.Create<IEditionService, EditionServiceProxy>(),
                _mediaFileService,
                new StubImportDecisionMaker(),
                _importApprovedBooks,
                jobs,
                commands,
                _diskProvider,
                LogManager.GetCurrentClassLogger(),
                TimeSpan.FromMilliseconds(10));
        }

        [Test]
        public void should_list_mp3_files_as_convertible_when_profile_targets_m4b()
        {
            GivenFiles(Mp3File(1, @"C:\books\author\book\01.mp3"));

            var previews = _service.GetConversionPreviews(AuthorId);

            Assert.That(previews.Count, Is.EqualTo(1));
            Assert.That(previews[0].CanConvert, Is.True);
            Assert.That(previews[0].SourceQuality, Is.EqualTo(Quality.MP3.Name));
            Assert.That(previews[0].TargetQuality, Is.EqualTo(Quality.M4B.Name));
            Assert.That(previews[0].BookId, Is.EqualTo(BookId));
        }

        [Test]
        public void should_not_offer_to_convert_a_file_that_is_already_the_target_quality()
        {
            GivenFiles(File(2, @"C:\books\author\book\book.m4b", Quality.M4B));

            var previews = _service.GetConversionPreviews(AuthorId);

            Assert.That(previews.Count, Is.EqualTo(1));
            Assert.That(previews[0].CanConvert, Is.False);
            Assert.That(previews[0].Reason, Does.Contain("Already"));
        }

        [Test]
        public void should_explain_when_the_profile_has_no_conversion_target()
        {
            _authorServiceProxy.Author = Author(convertToQualityId: null);
            GivenFiles(Mp3File(1, @"C:\books\author\book\01.mp3"));

            var previews = _service.GetConversionPreviews(AuthorId);

            Assert.That(previews.Count, Is.EqualTo(1));
            Assert.That(previews[0].CanConvert, Is.False);
            Assert.That(previews[0].Reason, Does.Contain("no conversion target"));
        }

        [Test]
        public void should_explain_when_the_conversion_target_is_not_allowed_by_the_profile()
        {
            _authorServiceProxy.Author = Author(convertToQualityId: Quality.M4B.Id, allowM4b: false);
            GivenFiles(Mp3File(1, @"C:\books\author\book\01.mp3"));

            var previews = _service.GetConversionPreviews(AuthorId);

            Assert.That(previews.Count, Is.EqualTo(1));
            Assert.That(previews[0].CanConvert, Is.False);
            Assert.That(previews[0].Reason, Does.Contain("not allowed"));
        }

        [Test]
        public void should_not_offer_to_convert_a_file_that_is_missing_from_disk()
        {
            var file = Mp3File(1, @"C:\books\author\book\01.mp3");
            GivenFiles(file);
            _diskProviderProxy.ExistingFiles.Remove(file.Path);

            var previews = _service.GetConversionPreviews(AuthorId);

            Assert.That(previews.Count, Is.EqualTo(1));
            Assert.That(previews[0].CanConvert, Is.False);
            Assert.That(previews[0].Reason, Does.Contain("missing"));
        }

        [Test]
        public void should_leave_ebooks_out_of_the_preview_entirely()
        {
            GivenFiles(File(3, @"C:\books\author\book\book.epub", Quality.EPUB, "ebook"));

            var previews = _service.GetConversionPreviews(AuthorId);

            Assert.That(previews, Is.Empty);
        }

        [Test]
        public void should_queue_every_file_of_a_book_as_one_conversion()
        {
            GivenFiles(
                Mp3File(1, @"C:\books\author\book\01.mp3"),
                Mp3File(2, @"C:\books\author\book\02.mp3"),
                Mp3File(3, @"C:\books\author\book\03.mp3"));

            _service.Execute(new ConvertBookFilesCommand(AuthorId, new List<int> { 1, 2, 3 }));

            Assert.That(_importApprovedBooks.Queued.Count, Is.EqualTo(1));
            Assert.That(_importApprovedBooks.Queued[0].Count, Is.EqualTo(3));
            Assert.That(_importApprovedBooks.Imported, Is.Empty, "Convert only queues; the worker encodes and a separate command imports");
        }

        [Test]
        public void should_queue_the_files_as_a_library_conversion_of_their_edition()
        {
            GivenFiles(Mp3File(1, @"C:\books\author\book\01.mp3"));

            _service.Execute(new ConvertBookFilesCommand(AuthorId, new List<int> { 1 }));

            var decisions = _importApprovedBooks.Queued.Single();
            Assert.That(decisions.All(d => d.Item.IsLibraryConversion), Is.True);
            Assert.That(decisions.All(d => d.Item.Book.Id == BookId), Is.True);
            Assert.That(decisions.All(d => d.Item.Edition.Id == EditionId), Is.True);
        }

        [Test]
        public void should_queue_each_edition_separately()
        {
            var first = Mp3File(1, @"C:\books\author\book\01.mp3");
            var second = Mp3File(2, @"C:\books\author\other\01.mp3");
            second.EditionId = OtherEditionId;

            GivenFiles(first, second);

            _service.Execute(new ConvertBookFilesCommand(AuthorId, new List<int> { 1, 2 }));

            Assert.That(_importApprovedBooks.Queued.Count, Is.EqualTo(2));
            Assert.That(_importApprovedBooks.Queued.Select(decisions => decisions.Single().Item.Edition.Id), Is.EquivalentTo(new[] { EditionId, OtherEditionId }));
        }

        [Test]
        public void should_convert_the_whole_book_even_when_only_some_of_its_files_were_selected()
        {
            GivenFiles(
                Mp3File(1, @"C:\books\author\book\01.mp3"),
                Mp3File(2, @"C:\books\author\book\02.mp3"),
                Mp3File(3, @"C:\books\author\book\03.mp3"));

            _service.Execute(new ConvertBookFilesCommand(AuthorId, new List<int> { 1 }));

            Assert.That(_importApprovedBooks.Queued.Single().Count, Is.EqualTo(3));
        }

        [Test]
        public void should_not_pull_in_siblings_that_are_not_convertible()
        {
            var existingM4b = File(9, @"C:\books\author\book\book.m4b", Quality.M4B);

            GivenFiles(
                Mp3File(1, @"C:\books\author\book\01.mp3"),
                existingM4b);

            _service.Execute(new ConvertBookFilesCommand(AuthorId, new List<int> { 1 }));

            var decisions = _importApprovedBooks.Queued.Single();
            Assert.That(decisions.Count, Is.EqualTo(1));
            Assert.That(decisions[0].Item.Path, Does.EndWith("01.mp3"));
        }

        [Test]
        public void should_not_queue_a_selected_file_that_now_belongs_to_another_book()
        {
            var moved = Mp3File(1, @"C:\books\author\book\01.mp3");
            moved.EditionId = MovedToOtherBookEditionId;
            GivenFiles(moved);

            _service.Execute(new ConvertBookFilesCommand(AuthorId, new List<int> { 1 }, BookId));

            Assert.That(_importApprovedBooks.Queued, Is.Empty, "the user chose the book on this page, not the one the file moved to");
            Assert.That(_commands.Pushed, Is.Empty);
        }

        [Test]
        public void should_not_queue_a_selected_file_that_now_belongs_to_another_author()
        {
            var moved = Mp3File(1, @"C:\books\author\book\01.mp3");
            moved.EditionId = MovedToOtherAuthorEditionId;
            var stays = Mp3File(2, @"C:\books\author\book2\01.mp3");
            stays.EditionId = OtherEditionId;
            GivenFiles(moved, stays);

            _service.Execute(new ConvertBookFilesCommand(AuthorId, new List<int> { 1, 2 }));

            Assert.That(_importApprovedBooks.Queued.Select(decisions => decisions.Single().Item.Edition.Id), Is.EqualTo(new[] { OtherEditionId }));
        }

        // Cancelling the progress task while it waits in the queue removes it before it runs. The
        // conversions it covers must still be cancelled, or they would import after the user cancelled.
        [Test]
        public void cancelling_the_queued_progress_task_cancels_conversions_that_have_not_finished()
        {
            _jobs.Add(new ConversionJob { Origin = ConversionJobOrigin.Library, EditionId = 1, DownloadId = LibraryConversionJob.Key(1), Status = ConversionJobStatus.Queued });
            _jobs.Add(new ConversionJob { Origin = ConversionJobOrigin.Library, EditionId = 2, DownloadId = LibraryConversionJob.Key(2), Status = ConversionJobStatus.Converting });
            _jobs.Add(new ConversionJob { Origin = ConversionJobOrigin.Library, EditionId = 3, DownloadId = LibraryConversionJob.Key(3), Status = ConversionJobStatus.ReadyToImport });

            _service.Handle(new CommandCancelledEvent(new CommandModel { Body = new LibraryConversionProgressCommand() }, wasQueued: true));

            Assert.That(_jobs.Cancelled, Is.EquivalentTo(new[] { LibraryConversionJob.Key(1), LibraryConversionJob.Key(2) }),
                "a finished conversion that is importing is left to complete");
        }

        [Test]
        public void a_started_or_unrelated_cancellation_is_left_to_its_own_handling()
        {
            _jobs.Add(new ConversionJob { Origin = ConversionJobOrigin.Library, EditionId = 1, DownloadId = LibraryConversionJob.Key(1), Status = ConversionJobStatus.Converting });

            // A started progress task is cancelled through its token; another command's cancel is not ours.
            _service.Handle(new CommandCancelledEvent(new CommandModel { Body = new LibraryConversionProgressCommand() }, wasQueued: false));
            _service.Handle(new CommandCancelledEvent(new CommandModel { Body = new ConvertBookFilesCommand(AuthorId, new List<int> { 1 }) }, wasQueued: true));

            Assert.That(_jobs.Cancelled, Is.Empty);
        }

        [Test]
        public void progress_task_stops_on_shutdown_without_cancelling_conversions()
        {
            _jobs.Add(new ConversionJob { Origin = ConversionJobOrigin.Library, EditionId = EditionId, DownloadId = LibraryConversionJob.Key(EditionId), Status = ConversionJobStatus.Converting, Progress = 10m });
            _jobs.OnActiveRead = reads =>
            {
                if (reads == 2)
                {
                    _service.Handle(new NzbDrone.Core.Lifecycle.ApplicationShutdownRequested());
                }
            };

            Assert.DoesNotThrow(() => _service.Execute(new LibraryConversionProgressCommand(), CancellationToken.None));

            Assert.That(_jobs.ActiveReads, Is.EqualTo(2), "polling stops once shutdown begins");
            Assert.That(_jobs.Cancelled, Is.Empty, "shutting down is not a cancel; the worker resumes the job on the next start");
        }

        [Test]
        public void should_start_one_progress_task_when_books_are_queued()
        {
            GivenFiles(Mp3File(1, @"C:\books\author\book\01.mp3"));

            _service.Execute(new ConvertBookFilesCommand(AuthorId, new List<int> { 1 }));

            Assert.That(_commands.Pushed.OfType<LibraryConversionProgressCommand>().Count(), Is.EqualTo(1));
        }

        [Test]
        public void should_not_start_the_progress_task_when_nothing_was_queued()
        {
            GivenFiles(Mp3File(1, @"C:\books\author\book\01.mp3"));
            _importApprovedBooks.QueueError = "One or more files are not compatible with the M4B converter.";

            _service.Execute(new ConvertBookFilesCommand(AuthorId, new List<int> { 1 }));

            Assert.That(_importApprovedBooks.Queued.Count, Is.EqualTo(1));
            Assert.That(_commands.Pushed, Is.Empty);
        }

        [Test]
        public void should_not_queue_anything_when_no_file_is_eligible()
        {
            GivenFiles(File(2, @"C:\books\author\book\book.m4b", Quality.M4B));

            _service.Execute(new ConvertBookFilesCommand(AuthorId, new List<int> { 2 }));

            Assert.That(_importApprovedBooks.Queued, Is.Empty);
            Assert.That(_commands.Pushed, Is.Empty);
        }

        [Test]
        public void should_queue_every_eligible_file_for_the_selected_authors()
        {
            GivenFiles(Mp3File(1, @"C:\books\author\book\01.mp3"));

            _service.Execute(new ConvertAuthorCommand { AuthorIds = new List<int> { AuthorId } });

            Assert.That(_importApprovedBooks.Queued.Count, Is.EqualTo(1));
        }

        [Test]
        public void should_import_a_ready_library_job_and_complete_it()
        {
            using var library = new TemporaryLibrary();
            var files = new[] { library.Mp3(1, "01.mp3"), library.Mp3(2, "02.mp3") };
            GivenFiles(files);
            var job = _jobs.Ready(EditionId, files);

            _service.Execute(new ImportLibraryConversionCommand(EditionId));

            var imported = _importApprovedBooks.Imported.Single();
            Assert.That(imported.Request.OutputPath, Is.EqualTo(job.Request.OutputPath));
            Assert.That(imported.Decisions.Select(d => d.Item.Path), Is.EquivalentTo(files.Select(f => f.Path)));
            Assert.That(imported.Decisions.All(d => d.Item.IsLibraryConversion), Is.True);
            Assert.That(_jobs.Completed, Is.EqualTo(new[] { LibraryConversionJob.Key(EditionId) }));
            Assert.That(_jobs.Failed, Is.Empty);
        }

        [Test]
        public void should_not_replace_anything_when_the_library_files_changed_since_the_job_was_queued()
        {
            using var library = new TemporaryLibrary();
            var files = new[] { library.Mp3(1, "01.mp3"), library.Mp3(2, "02.mp3") };
            GivenFiles(files);
            _jobs.Ready(EditionId, files);
            System.IO.File.AppendAllText(files[1].Path, " edited");

            _service.Execute(new ImportLibraryConversionCommand(EditionId));

            Assert.That(_importApprovedBooks.Imported, Is.Empty);
            Assert.That(_jobs.Completed, Is.Empty);
            Assert.That(_jobs.Failed.Single().Error, Does.Contain("changed since this conversion was queued"));
        }

        [Test]
        public void should_fail_the_job_when_the_import_does_not_import_the_converted_file()
        {
            using var library = new TemporaryLibrary();
            var files = new[] { library.Mp3(1, "01.mp3") };
            GivenFiles(files);
            _jobs.Ready(EditionId, files);
            _importApprovedBooks.ImportError = "Conversion skipped because the destination is already occupied";

            _service.Execute(new ImportLibraryConversionCommand(EditionId));

            Assert.That(_jobs.Completed, Is.Empty);
            Assert.That(_jobs.Failed.Single().Error, Does.Contain("destination is already occupied"));
        }

        [Test]
        public void should_ignore_an_import_command_when_no_library_job_is_ready()
        {
            _jobs.Add(new ConversionJob { Origin = ConversionJobOrigin.Library, EditionId = EditionId, DownloadId = LibraryConversionJob.Key(EditionId), Status = ConversionJobStatus.Converting });

            _service.Execute(new ImportLibraryConversionCommand(EditionId));
            _service.Execute(new ImportLibraryConversionCommand(OtherEditionId));

            Assert.That(_importApprovedBooks.Imported, Is.Empty);
            Assert.That(_jobs.Completed, Is.Empty);
            Assert.That(_jobs.Failed, Is.Empty);
        }

        [Test]
        public void progress_task_finishes_when_no_library_job_is_left()
        {
            var job = _jobs.Add(new ConversionJob { Origin = ConversionJobOrigin.Library, EditionId = EditionId, DownloadId = LibraryConversionJob.Key(EditionId), Status = ConversionJobStatus.Converting, Progress = 42m });
            _jobs.OnActiveRead = reads =>
            {
                if (reads == 2)
                {
                    job.Status = ConversionJobStatus.Completed;
                }
            };

            _service.Execute(new LibraryConversionProgressCommand(), CancellationToken.None);

            Assert.That(_jobs.ActiveReads, Is.EqualTo(2), "the task polls while the job converts and stops once it has finished");
            Assert.That(_jobs.SummaryReads, Is.EqualTo(1), "the finished jobs are read once for the closing summary");
        }

        [Test]
        public void cancelling_the_progress_task_stops_queued_and_converting_jobs_only()
        {
            _jobs.Add(new ConversionJob { Origin = ConversionJobOrigin.Library, EditionId = 1, DownloadId = LibraryConversionJob.Key(1), Status = ConversionJobStatus.Queued });
            _jobs.Add(new ConversionJob { Origin = ConversionJobOrigin.Library, EditionId = 2, DownloadId = LibraryConversionJob.Key(2), Status = ConversionJobStatus.Converting });
            _jobs.Add(new ConversionJob { Origin = ConversionJobOrigin.Library, EditionId = 3, DownloadId = LibraryConversionJob.Key(3), Status = ConversionJobStatus.ReadyToImport });
            using var cts = new CancellationTokenSource();
            _jobs.OnActiveRead = _ => cts.Cancel();

            Assert.Throws<OperationCanceledException>(() => _service.Execute(new LibraryConversionProgressCommand(), cts.Token));

            Assert.That(_jobs.Cancelled, Is.EquivalentTo(new[] { LibraryConversionJob.Key(1), LibraryConversionJob.Key(2) }),
                "a finished conversion that is importing is left to complete");
        }

        private void GivenFiles(params BookFile[] files)
        {
            _mediaFileServiceProxy.Files = files.ToList();

            foreach (var file in files)
            {
                _diskProviderProxy.ExistingFiles.Add(file.Path);
            }
        }

        private static BookFile Mp3File(int id, string path)
        {
            return File(id, path, Quality.MP3);
        }

        private static BookFile File(int id, string path, Quality quality, string mediaType = "audiobook")
        {
            return new BookFile
            {
                Id = id,
                Path = path,
                Size = 1024,
                Quality = new QualityModel(quality),
                EditionId = EditionId,
                MediaType = mediaType,
                Part = id
            };
        }

        private static Author Author(int? convertToQualityId, bool allowM4b = true)
        {
            var profile = new QualityProfile
            {
                Id = 10,
                Name = "Audiobook",
                ProfileType = ProfileType.Audiobook,
                ConvertToQualityId = convertToQualityId,
                Items = new List<QualityProfileQualityItem>
                {
                    new() { Quality = Quality.MP3, Allowed = true },
                    new() { Quality = Quality.M4B, Allowed = allowM4b }
                }
            };

            return new Author
            {
                Id = AuthorId,
                Name = "Test Author",
                AudiobookQualityProfileId = profile.Id,
                AudiobookQualityProfile = profile
            };
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public Author Author { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IAuthorService.GetAuthor):
                        return Author;
                    case nameof(IAuthorService.GetAuthors):
                        return new List<Author> { Author };
                }

                throw new NotImplementedException($"Test proxy does not implement IAuthorService.{targetMethod?.Name}");
            }
        }

        private class BookServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetBook))
                {
                    var id = (int)args[0];
                    return new Book { Id = id, AuthorId = id == 44 ? AuthorId + 1 : AuthorId, Title = id == BookId ? "Test Book" : $"Other Book {id}" };
                }

                throw new NotImplementedException($"Test proxy does not implement IBookService.{targetMethod?.Name}");
            }
        }

        private class EditionServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IEditionService.GetEdition))
                {
                    var id = (int)args[0];
                    var bookId = id switch { MovedToOtherBookEditionId => 43, MovedToOtherAuthorEditionId => 44, _ => BookId };
                    return new Edition { Id = id, BookId = bookId, Title = "Test Edition" };
                }

                throw new NotImplementedException($"Test proxy does not implement IEditionService.{targetMethod?.Name}");
            }
        }

        private class MediaFileServiceProxy : DispatchProxy
        {
            public List<BookFile> Files { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IMediaFileService.GetFilesByAuthor):
                    case nameof(IMediaFileService.GetFilesByBook):
                        return Files;
                    case nameof(IMediaFileService.GetFilesByEdition):
                        var editionId = (int)args[0];
                        return Files.Where(f => f.EditionId == editionId).ToList();
                    case nameof(IMediaFileService.Get) when args?.Length == 1 && args[0] is IEnumerable<int> ids:
                        var requested = ids.ToHashSet();
                        return Files.Where(f => requested.Contains(f.Id)).ToList();
                }

                throw new NotImplementedException($"Test proxy does not implement IMediaFileService.{targetMethod?.Name}");
            }
        }

        private sealed class StubImportDecisionMaker : IMakeImportDecision
        {
            public List<ImportDecision<LocalBook>> GetImportDecisions(
                List<IFileInfo> bookFiles,
                IdentificationOverrides idOverrides,
                ImportDecisionMakerInfo itemInfo,
                ImportDecisionMakerConfig config,
                CancellationToken cancellationToken = default)
            {
                return bookFiles
                    .Select(file => new ImportDecision<LocalBook>(new LocalBook
                    {
                        Path = file.FullName,
                        Author = idOverrides?.Author,
                        Book = idOverrides?.Book,
                        Edition = idOverrides?.Edition,
                        Quality = new QualityModel(Quality.MP3)
                    }))
                    .ToList();
            }
        }

        private sealed class StubImportApprovedBooks : IImportApprovedBooks
        {
            public List<List<ImportDecision<LocalBook>>> Queued { get; } = new();
            public List<(ConversionJobRequest Request, List<ImportDecision<LocalBook>> Decisions)> Imported { get; } = new();
            public string QueueError { get; set; }
            public string ImportError { get; set; }

            public List<ImportResult> Import(
                List<ImportDecision<LocalBook>> decisions,
                bool replaceExisting,
                NzbDrone.Core.Download.DownloadClientItem downloadClientItem = null,
                ImportMode importMode = ImportMode.Auto,
                CancellationToken cancellationToken = default)
            {
                throw new AssertionException("A library conversion goes through QueueLibraryConversion and ImportLibraryConversion.");
            }

            public LibraryConversionQueueResult QueueLibraryConversion(List<ImportDecision<LocalBook>> decisions, Author author, Book book)
            {
                Queued.Add(decisions);
                return QueueError != null
                    ? LibraryConversionQueueResult.Refused(QueueError)
                    : new LibraryConversionQueueResult { Job = new ConversionJob { EditionId = decisions.First().Item.Edition.Id } };
            }

            public List<ImportResult> ImportLibraryConversion(ConversionJobRequest request, List<ImportDecision<LocalBook>> sourceDecisions)
            {
                Imported.Add((request, sourceDecisions));
                if (ImportError != null)
                {
                    return sourceDecisions.Select(decision => new ImportResult(decision, ImportError)).ToList();
                }

                // Stand in for the import: one generated file that supersedes the files it was made from.
                var first = sourceDecisions.First().Item;
                var converted = new LocalBook
                {
                    Path = request.OutputPath,
                    Author = first.Author,
                    Book = first.Book,
                    Edition = first.Edition,
                    Quality = new QualityModel(Quality.M4B),
                    IsGeneratedConversion = true,
                    IsLibraryConversion = true,
                    GeneratedConversionSourcePaths = sourceDecisions.Select(d => d.Item.Path).ToList()
                };

                return new List<ImportResult> { new(new ImportDecision<LocalBook>(converted), ImportResultType.Imported) };
            }
        }

        private class ConversionJobsProxy : DispatchProxy
        {
            private readonly List<(ConversionJob Job, ConversionJobRequest Request)> _jobs = new();

            public List<string> Completed { get; } = new();
            public List<(string Key, string Error)> Failed { get; } = new();
            public List<string> Cancelled { get; } = new();
            public int ActiveReads { get; private set; }
            public int SummaryReads { get; private set; }
            public Action<int> OnActiveRead { get; set; }

            public ConversionJob Add(ConversionJob job, ConversionJobRequest request = null)
            {
                job.RequestJson ??= System.Text.Json.JsonSerializer.Serialize(request ?? new ConversionJobRequest { BookTitle = "Test Book" });
                _jobs.Add((job, request));
                return job;
            }

            public (ConversionJob Job, ConversionJobRequest Request) Ready(int editionId, IEnumerable<BookFile> sources)
            {
                var request = new ConversionJobRequest
                {
                    Origin = ConversionJobOrigin.Library,
                    DownloadId = LibraryConversionJob.Key(editionId),
                    BookId = BookId,
                    EditionId = editionId,
                    BookTitle = "Test Book",
                    OutputPath = "/library/.chaptarr-conversions/library-edition/attempt/Test Book.m4b",
                    Sources = sources.Select(file => new ConversionArtifactSource
                    {
                        Path = file.Path,
                        Size = new System.IO.FileInfo(file.Path).Length,
                        ModifiedUtcTicks = System.IO.File.GetLastWriteTimeUtc(file.Path).Ticks
                    }).ToList(),
                    TargetQualityId = Quality.M4B.Id,
                    TargetQualityName = Quality.M4B.Name
                };
                var job = Add(new ConversionJob
                {
                    Origin = ConversionJobOrigin.Library,
                    DownloadId = request.DownloadId,
                    BookId = BookId,
                    EditionId = editionId,
                    Status = ConversionJobStatus.ReadyToImport
                }, request);
                return (job, request);
            }

            private (ConversionJob Job, ConversionJobRequest Request)? Find(string key) =>
                _jobs.Cast<(ConversionJob Job, ConversionJobRequest Request)?>().FirstOrDefault(entry => entry.Value.Job.DownloadId == key);

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IConversionJobService.GetLibraryJob):
                        return _jobs.Select(entry => entry.Job).FirstOrDefault(job => job.EditionId == (int)args[0]);
                    case nameof(IConversionJobService.GetLibraryJobs):
                        SummaryReads++;
                        var ids = ((IEnumerable<int>)args[0]).ToHashSet();
                        return _jobs.Select(entry => entry.Job).Where(job => ids.Contains(job.EditionId)).ToList();
                    case nameof(IConversionJobService.GetActiveLibraryJobs):
                        ActiveReads++;
                        OnActiveRead?.Invoke(ActiveReads);
                        return _jobs.Select(entry => entry.Job)
                            .Where(job => job.Status is ConversionJobStatus.Queued or ConversionJobStatus.Converting or ConversionJobStatus.Cancelling or ConversionJobStatus.ReadyToImport)
                            .ToList();
                    case nameof(IConversionJobService.IsRunning):
                        return false;
                    case nameof(IConversionJobService.GetCompletedConversion):
                        return Find((string)args[0])?.Request;
                    case nameof(IConversionJobService.Complete):
                        Completed.Add((string)args[0]);
                        return null;
                    case nameof(IConversionJobService.Fail):
                        Failed.Add(((string)args[0], (string)args[1]));
                        return null;
                    case nameof(IConversionJobService.CancelLibrary):
                        Cancelled.Add(LibraryConversionJob.Key((int)args[0]));
                        return true;
                }

                throw new NotImplementedException($"Test proxy does not implement IConversionJobService.{targetMethod?.Name}");
            }
        }

        private class CommandQueueProxy : DispatchProxy
        {
            public List<NzbDrone.Core.Messaging.Commands.Command> Pushed { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IManageCommandQueue.Push) && args?.FirstOrDefault() is NzbDrone.Core.Messaging.Commands.Command command)
                {
                    Pushed.Add(command);
                    return new NzbDrone.Core.Messaging.Commands.CommandModel { Name = command.Name, Body = command };
                }

                throw new NotImplementedException($"Test proxy does not implement IManageCommandQueue.{targetMethod?.Name}");
            }
        }

        private sealed class TemporaryLibrary : IDisposable
        {
            private readonly string _folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "library-conversion-" + Guid.NewGuid().ToString("N"));

            public TemporaryLibrary()
            {
                System.IO.Directory.CreateDirectory(_folder);
            }

            public BookFile Mp3(int id, string name)
            {
                var path = System.IO.Path.Combine(_folder, name);
                System.IO.File.WriteAllText(path, name);
                return Mp3File(id, path);
            }

            public void Dispose()
            {
                System.IO.Directory.Delete(_folder, recursive: true);
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDiskProvider.FileExists))
                {
                    return ExistingFiles.Contains((string)args[0]);
                }

                if (targetMethod?.Name == nameof(IDiskProvider.GetFileInfo))
                {
                    return new System.IO.Abstractions.FileSystem().FileInfo.FromFileName((string)args[0]);
                }

                throw new NotImplementedException($"Test proxy does not implement IDiskProvider.{targetMethod?.Name}");
            }
        }
    }
}
