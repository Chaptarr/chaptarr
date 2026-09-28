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

        private IAuthorService _authorService;
        private AuthorServiceProxy _authorServiceProxy;
        private IMediaFileService _mediaFileService;
        private MediaFileServiceProxy _mediaFileServiceProxy;
        private StubImportApprovedBooks _importApprovedBooks;
        private IDiskProvider _diskProvider;
        private DiskProviderProxy _diskProviderProxy;
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

            _service = new BookFileConversionService(
                _authorService,
                DispatchProxy.Create<IBookService, BookServiceProxy>(),
                DispatchProxy.Create<IEditionService, EditionServiceProxy>(),
                _mediaFileService,
                new StubImportDecisionMaker(),
                _importApprovedBooks,
                _diskProvider,
                LogManager.GetCurrentClassLogger());
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
        public void should_import_every_file_of_a_book_as_one_conversion()
        {
            GivenFiles(
                Mp3File(1, @"C:\books\author\book\01.mp3"),
                Mp3File(2, @"C:\books\author\book\02.mp3"),
                Mp3File(3, @"C:\books\author\book\03.mp3"));

            _service.Execute(new ConvertBookFilesCommand(AuthorId, new List<int> { 1, 2, 3 }));

            Assert.That(_importApprovedBooks.Calls.Count, Is.EqualTo(1));
            Assert.That(_importApprovedBooks.Calls[0].Decisions.Count, Is.EqualTo(3));
        }

        [Test]
        public void should_flag_the_import_as_a_library_conversion_that_replaces_existing_files()
        {
            GivenFiles(Mp3File(1, @"C:\books\author\book\01.mp3"));

            _service.Execute(new ConvertBookFilesCommand(AuthorId, new List<int> { 1 }));

            var call = _importApprovedBooks.Calls.Single();

            Assert.That(call.ReplaceExisting, Is.True);
            Assert.That(call.DownloadClientItem, Is.Null);
            Assert.That(call.Decisions.All(d => d.Item.IsLibraryConversion), Is.True);
            Assert.That(call.Decisions.All(d => d.Item.Book.Id == BookId), Is.True);
            Assert.That(call.Decisions.All(d => d.Item.Edition.Id == EditionId), Is.True);
        }

        [Test]
        public void should_convert_each_edition_separately()
        {
            var first = Mp3File(1, @"C:\books\author\book\01.mp3");
            var second = Mp3File(2, @"C:\books\author\other\01.mp3");
            second.EditionId = OtherEditionId;

            GivenFiles(first, second);

            _service.Execute(new ConvertBookFilesCommand(AuthorId, new List<int> { 1, 2 }));

            Assert.That(_importApprovedBooks.Calls.Count, Is.EqualTo(2));
            Assert.That(_importApprovedBooks.Calls.SelectMany(c => c.Decisions).Count(), Is.EqualTo(2));
        }

        [Test]
        public void should_convert_the_whole_book_even_when_only_some_of_its_files_were_selected()
        {
            GivenFiles(
                Mp3File(1, @"C:\books\author\book\01.mp3"),
                Mp3File(2, @"C:\books\author\book\02.mp3"),
                Mp3File(3, @"C:\books\author\book\03.mp3"));

            _service.Execute(new ConvertBookFilesCommand(AuthorId, new List<int> { 1 }));

            var call = _importApprovedBooks.Calls.Single();

            Assert.That(call.Decisions.Count, Is.EqualTo(3));
        }

        [Test]
        public void should_not_pull_in_siblings_that_are_not_convertible()
        {
            var existingM4b = File(9, @"C:\books\author\book\book.m4b", Quality.M4B);

            GivenFiles(
                Mp3File(1, @"C:\books\author\book\01.mp3"),
                existingM4b);

            _service.Execute(new ConvertBookFilesCommand(AuthorId, new List<int> { 1 }));

            var call = _importApprovedBooks.Calls.Single();

            Assert.That(call.Decisions.Count, Is.EqualTo(1));
            Assert.That(call.Decisions[0].Item.Path, Does.EndWith("01.mp3"));
        }

        [Test]
        public void should_hand_the_task_cancellation_token_to_the_import()
        {
            GivenFiles(Mp3File(1, @"C:\books\author\book\01.mp3"));
            using var cts = new CancellationTokenSource();

            _service.Execute(new ConvertBookFilesCommand(AuthorId, new List<int> { 1 }), cts.Token);

            Assert.That(_importApprovedBooks.Calls.Single().CancellationToken, Is.EqualTo(cts.Token));
        }

        [Test]
        public void should_not_start_converting_when_the_task_is_already_cancelled()
        {
            GivenFiles(Mp3File(1, @"C:\books\author\book\01.mp3"));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                _service.Execute(new ConvertBookFilesCommand(AuthorId, new List<int> { 1 }), cts.Token));

            Assert.That(_importApprovedBooks.Calls, Is.Empty);
        }

        [Test]
        public void should_stop_the_run_when_cancelled_while_a_book_is_converting()
        {
            var first = Mp3File(1, @"C:\books\author\book\01.mp3");
            var second = Mp3File(2, @"C:\books\author\other\01.mp3");
            second.EditionId = OtherEditionId;
            GivenFiles(first, second);

            using var cts = new CancellationTokenSource();
            _importApprovedBooks.OnImport = () => cts.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                _service.Execute(new ConvertBookFilesCommand(AuthorId, new List<int> { 1, 2 }), cts.Token));

            Assert.That(_importApprovedBooks.Calls.Count, Is.EqualTo(1), "the second book must not start");
        }

        [Test]
        public void should_clear_the_tracked_conversion_status_when_a_book_finishes()
        {
            var tracking = DispatchProxy.Create<IConversionTrackingService, ConversionTrackingProxy>();
            var service = new BookFileConversionService(
                _authorService,
                DispatchProxy.Create<IBookService, BookServiceProxy>(),
                DispatchProxy.Create<IEditionService, EditionServiceProxy>(),
                _mediaFileService,
                new StubImportDecisionMaker(),
                _importApprovedBooks,
                _diskProvider,
                LogManager.GetCurrentClassLogger(),
                tracking);
            GivenFiles(Mp3File(1, @"C:\books\author\book\01.mp3"));

            service.Execute(new ConvertBookFilesCommand(AuthorId, new List<int> { 1 }));

            Assert.That(((ConversionTrackingProxy)(object)tracking).Cleared,
                Is.EqualTo(new List<string> { LocalConversionId.For(BookId, EditionId) }));
        }

        [Test]
        public void should_not_import_anything_when_no_file_is_eligible()
        {
            GivenFiles(File(2, @"C:\books\author\book\book.m4b", Quality.M4B));

            _service.Execute(new ConvertBookFilesCommand(AuthorId, new List<int> { 2 }));

            Assert.That(_importApprovedBooks.Calls, Is.Empty);
        }

        [Test]
        public void should_convert_every_eligible_file_for_the_selected_authors()
        {
            GivenFiles(Mp3File(1, @"C:\books\author\book\01.mp3"));

            _service.Execute(new ConvertAuthorCommand { AuthorIds = new List<int> { AuthorId } });

            Assert.That(_importApprovedBooks.Calls.Count, Is.EqualTo(1));
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
                    return new Book { Id = (int)args[0], AuthorId = AuthorId, Title = "Test Book" };
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
                    return new Edition { Id = (int)args[0], BookId = BookId, Title = "Test Edition" };
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
            public sealed record Call(
                List<ImportDecision<LocalBook>> Decisions,
                bool ReplaceExisting,
                NzbDrone.Core.Download.DownloadClientItem DownloadClientItem,
                CancellationToken CancellationToken);

            public List<Call> Calls { get; } = new();
            public Action OnImport { get; set; }

            public List<ImportResult> Import(
                List<ImportDecision<LocalBook>> decisions,
                bool replaceExisting,
                NzbDrone.Core.Download.DownloadClientItem downloadClientItem = null,
                ImportMode importMode = ImportMode.Auto,
                CancellationToken cancellationToken = default)
            {
                Calls.Add(new Call(decisions, replaceExisting, downloadClientItem, cancellationToken));
                OnImport?.Invoke();

                // Stand in for the conversion the real pipeline performs: one generated file that
                // supersedes everything that went into it.
                var first = decisions.First().Item;
                var converted = new LocalBook
                {
                    Path = first.Path + ".m4b",
                    Author = first.Author,
                    Book = first.Book,
                    Edition = first.Edition,
                    Quality = new QualityModel(Quality.M4B),
                    IsGeneratedConversion = true,
                    IsLibraryConversion = first.IsLibraryConversion,
                    GeneratedConversionSourcePaths = decisions.Select(d => d.Item.Path).ToList()
                };

                return new List<ImportResult>
                {
                    new ImportResult(new ImportDecision<LocalBook>(converted), ImportResultType.Imported)
                };
            }
        }

        private class ConversionTrackingProxy : DispatchProxy
        {
            public List<string> Cleared { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IConversionTrackingService.Clear):
                        Cleared.Add((string)args[0]);
                        return null;
                    case nameof(IConversionTrackingService.Get):
                        return null;
                }

                throw new NotImplementedException($"Test proxy does not implement IConversionTrackingService.{targetMethod?.Name}");
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
