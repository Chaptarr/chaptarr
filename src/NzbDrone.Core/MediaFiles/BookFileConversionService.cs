using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Identification;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ProgressMessaging;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles
{
    /// <summary>
    /// One book file as the conversion preview sees it: what it is now, what it would become, and
    /// why it would be left alone.
    /// </summary>
    public class BookFileConversionPreview
    {
        public int AuthorId { get; set; }
        public int BookId { get; set; }
        public int EditionId { get; set; }
        public int BookFileId { get; set; }
        public string BookTitle { get; set; }
        public string Path { get; set; }
        public long Size { get; set; }
        public string SourceQuality { get; set; }
        public string TargetQuality { get; set; }
        public bool CanConvert { get; set; }
        public string Reason { get; set; }
    }

    public interface IBookFileConversionService
    {
        List<BookFileConversionPreview> GetConversionPreviews(int authorId, int? bookId = null);
    }

    /// <summary>
    /// Applies a quality profile's conversion target to files that are already in the library.
    ///
    /// The conversion itself belongs to the import pipeline, which owns ffmpeg detection, the
    /// workspace free-space check, the concurrency semaphore, tagging, chapters, destination naming
    /// and the recycle bin. Rather than duplicate any of that, this service hands the existing files
    /// back to that pipeline as an import of their own, flagged as a library conversion so the
    /// pipeline knows the sources are already-imported files rather than a fresh download.
    /// </summary>
    public class BookFileConversionService : IBookFileConversionService,
                                             IExecute<ConvertBookFilesCommand>,
                                             IExecute<ConvertAuthorCommand>
    {
        private sealed class ConversionGroup
        {
            public Author Author { get; init; }
            public Book Book { get; init; }
            public Edition Edition { get; init; }
            public List<BookFile> Files { get; init; }
            public Quality TargetQuality { get; init; }
        }

        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IMakeImportDecision _importDecisionMaker;
        private readonly IImportApprovedBooks _importApprovedBooks;
        private readonly IDiskProvider _diskProvider;
        private readonly IConversionTrackingService _conversionTrackingService;
        private readonly Logger _logger;

        private static readonly TimeSpan ProgressPollInterval = TimeSpan.FromSeconds(2);

        public BookFileConversionService(IAuthorService authorService,
                                         IBookService bookService,
                                         IEditionService editionService,
                                         IMediaFileService mediaFileService,
                                         IMakeImportDecision importDecisionMaker,
                                         IImportApprovedBooks importApprovedBooks,
                                         IDiskProvider diskProvider,
                                         Logger logger,
                                         IConversionTrackingService conversionTrackingService = null)
        {
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _mediaFileService = mediaFileService;
            _importDecisionMaker = importDecisionMaker;
            _importApprovedBooks = importApprovedBooks;
            _diskProvider = diskProvider;
            _conversionTrackingService = conversionTrackingService;
            _logger = logger;
        }

        public List<BookFileConversionPreview> GetConversionPreviews(int authorId, int? bookId = null)
        {
            var author = _authorService.GetAuthor(authorId);
            if (author == null)
            {
                return new List<BookFileConversionPreview>();
            }

            var files = bookId.HasValue
                ? _mediaFileService.GetFilesByBook(bookId.Value)
                : _mediaFileService.GetFilesByAuthor(authorId);

            return BuildPreviews(author, files);
        }

        public void Execute(ConvertBookFilesCommand message)
        {
            Execute(message, CancellationToken.None);
        }

        // The command executor prefers this overload when a handler has one, and cancels the token
        // when the task is cancelled from System > Tasks.
        public void Execute(ConvertBookFilesCommand message, CancellationToken cancellationToken)
        {
            var author = _authorService.GetAuthor(message.AuthorId);
            if (author == null)
            {
                _logger.Warn("[CONVERSION] Author {0} not found; nothing to convert", message.AuthorId);
                return;
            }

            List<BookFile> files;

            if (message.Files?.Count > 0)
            {
                // Every convertible file of a book becomes one output file, so a selection is read
                // as "convert the books these files belong to". Converting a subset would leave the
                // book half MP3 and half M4B.
                files = ExpandToWholeEditions(author, _mediaFileService.Get(message.Files));
            }
            else if (message.BookId.HasValue)
            {
                files = _mediaFileService.GetFilesByBook(message.BookId.Value);
            }
            else
            {
                files = _mediaFileService.GetFilesByAuthor(author.Id);
            }

            ConvertFiles(author, files, cancellationToken);
        }

        public void Execute(ConvertAuthorCommand message)
        {
            Execute(message, CancellationToken.None);
        }

        public void Execute(ConvertAuthorCommand message, CancellationToken cancellationToken)
        {
            var authors = _authorService.GetAuthors(message.AuthorIds ?? new List<int>());

            foreach (var author in authors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ConvertFiles(author, _mediaFileService.GetFilesByAuthor(author.Id), cancellationToken);
            }
        }

        private List<BookFile> ExpandToWholeEditions(Author author, List<BookFile> selected)
        {
            var editionIds = (selected ?? new List<BookFile>())
                .Where(f => f != null && f.EditionId > 0)
                .Select(f => f.EditionId)
                .ToHashSet();

            if (editionIds.Count == 0)
            {
                return selected ?? new List<BookFile>();
            }

            var expanded = new Dictionary<int, BookFile>();

            foreach (var file in selected)
            {
                expanded[file.Id] = file;
            }

            foreach (var editionId in editionIds)
            {
                foreach (var sibling in _mediaFileService.GetFilesByEdition(editionId))
                {
                    if (sibling == null || expanded.ContainsKey(sibling.Id))
                    {
                        continue;
                    }

                    // Only pull in siblings that would convert anyway; an eBook or an existing M4B
                    // filed against the same edition is not part of this conversion.
                    if (GetConversionTarget(author, sibling, out _) != null)
                    {
                        expanded[sibling.Id] = sibling;
                    }
                }
            }

            return expanded.Values.ToList();
        }

        private void ConvertFiles(Author author, List<BookFile> files, CancellationToken cancellationToken)
        {
            var groups = BuildConversionGroups(author, files);

            if (groups.Count == 0)
            {
                _logger.ProgressInfo("No files to convert for {0}", author.Name);
                return;
            }

            _logger.ProgressInfo("Converting {0} book(s) for {1}",
                groups.Count,
                author.Name);

            var converted = 0;
            var failed = 0;

            for (var i = 0; i < groups.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var group = groups[i];
                var position = $"({i + 1}/{groups.Count})";

                _logger.ProgressInfo("Converting '{0}' to {1} {2}",
                    group.Book.Title,
                    group.TargetQuality.Name,
                    position);

                var succeeded = ConvertGroup(group, position, cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    // The converter was stopped part-way. Nothing is replaced until a conversion
                    // finishes, so this book's original files are untouched.
                    _logger.Info("[CONVERSION] Cancelled while converting '{0}'; its original files were left as they were. {1} of {2} book(s) had been converted.",
                        group.Book.Title,
                        converted,
                        groups.Count);

                    throw new OperationCanceledException(cancellationToken);
                }

                if (succeeded)
                {
                    converted++;
                }
                else
                {
                    failed++;
                }
            }

            _logger.ProgressInfo("Converted {0} of {1} book(s) for {2}{3}",
                converted,
                groups.Count,
                author.Name,
                failed > 0 ? $"; {failed} failed (see log)" : string.Empty);
        }

        private bool ConvertGroup(ConversionGroup group, string position, CancellationToken cancellationToken)
        {
            var conversionId = LocalConversionId.For(group.Book.Id, group.Edition.Id);
            using var progressReporter = StartProgressReporter(group, position, conversionId);

            try
            {
                var fileInfos = group.Files
                    .Select(file => _diskProvider.GetFileInfo(file.Path))
                    .ToList();

                var overrides = new IdentificationOverrides
                {
                    Author = group.Author,
                    Book = group.Book,
                    Edition = group.Edition
                };

                // itemInfo stays null so the decision maker takes the book and edition given here
                // instead of re-matching files that are already correctly matched.
                var decisions = _importDecisionMaker.GetImportDecisions(
                    fileInfos,
                    overrides,
                    null,
                    new ImportDecisionMakerConfig
                    {
                        Filter = FilterFilesType.None,
                        NewDownload = false,
                        SingleRelease = false,
                        IncludeExisting = true,
                        AddNewAuthors = false,
                        KeepAllEditions = true
                    });

                var approved = decisions.Where(d => d.Approved && d.Item != null).ToList();

                if (approved.Count == 0)
                {
                    _logger.Warn("[CONVERSION] No usable files remained for '{0}'; skipping", group.Book.Title);
                    return false;
                }

                foreach (var decision in approved)
                {
                    decision.Item.IsLibraryConversion = true;
                    decision.Item.Author = group.Author;
                    decision.Item.Book = group.Book;
                    decision.Item.Edition = group.Edition;
                }

                var results = _importApprovedBooks.Import(approved, replaceExisting: true, cancellationToken: cancellationToken);

                var importedConversion = results.Any(r => r.Result == ImportResultType.Imported &&
                                                          r.ImportDecision?.Item?.IsGeneratedConversion == true);

                if (importedConversion)
                {
                    _logger.Info("[CONVERSION] Converted '{0}' to {1}", group.Book.Title, group.TargetQuality.Name);
                    return true;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    // Not a failure: the caller reports the cancellation and stops the run.
                    return false;
                }

                var errors = results
                    .SelectMany(r => r.Errors ?? new List<string>())
                    .Where(e => e.IsNotNullOrWhiteSpace())
                    .Distinct()
                    .ToList();

                _logger.Warn("[CONVERSION] '{0}' was not converted: {1}",
                    group.Book.Title,
                    errors.Any() ? string.Join("; ", errors) : "conversion produced no imported file");

                return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Reported by the caller, which stops the run.
                return false;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[CONVERSION] Failed converting '{0}'", group.Book?.Title);
                return false;
            }
            finally
            {
                // Library conversions have no queue item to retire their status, so drop it here.
                _conversionTrackingService?.Clear(conversionId);
            }
        }

        /// <summary>
        /// The converter reports its progress to the conversion tracker from its own threads, which
        /// the task's status message cannot see. Poll the tracker while a book converts and mirror
        /// the percentage into this task's message, so it shows in the sidebar and in
        /// System > Tasks.
        /// </summary>
        private IDisposable StartProgressReporter(ConversionGroup group, string position, string conversionId)
        {
            if (_conversionTrackingService == null)
            {
                return null;
            }

            var command = ProgressMessageContext.CommandModel;
            var lastReported = -1;

            return new PollingReporter(ProgressPollInterval, () =>
            {
                var progress = _conversionTrackingService.Get(conversionId)?.Progress;
                if (!progress.HasValue)
                {
                    return;
                }

                var percent = (int)Math.Floor(progress.Value);
                if (percent == lastReported)
                {
                    return;
                }

                lastReported = percent;

                // This runs on a pool thread. Attribute the message to the conversion's task for the
                // duration of this call only, so nothing else that later runs on the thread writes
                // into that task's status.
                var previous = ProgressMessageContext.CommandModel;
                ProgressMessageContext.CommandModel = command;

                try
                {
                    _logger.ProgressInfo("Converting '{0}' to {1} {2} - {3}%",
                        group.Book.Title,
                        group.TargetQuality.Name,
                        position,
                        percent);
                }
                finally
                {
                    ProgressMessageContext.CommandModel = previous;
                }
            });
        }

        private sealed class PollingReporter : IDisposable
        {
            private readonly CancellationTokenSource _stop = new();
            private readonly Task _loop;

            public PollingReporter(TimeSpan interval, Action report)
            {
                _loop = Task.Run(async () =>
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        try
                        {
                            await Task.Delay(interval, _stop.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }

                        try
                        {
                            report();
                        }
                        catch
                        {
                            // Progress is best-effort; it must never disturb the conversion.
                        }
                    }
                });
            }

            public void Dispose()
            {
                _stop.Cancel();

                try
                {
                    // Let an in-flight report finish, so it cannot overwrite the next status message.
                    _loop.Wait(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // Nothing to do: the loop only ever ends by cancellation.
                }

                _stop.Dispose();
            }
        }

        private List<ConversionGroup> BuildConversionGroups(Author author, List<BookFile> files)
        {
            var groups = new List<ConversionGroup>();

            foreach (var editionGroup in EligibleFiles(author, files).GroupBy(x => x.File.EditionId))
            {
                var edition = _editionService.GetEdition(editionGroup.Key);
                if (edition == null)
                {
                    continue;
                }

                var book = _bookService.GetBook(edition.BookId);
                if (book == null)
                {
                    continue;
                }

                edition.Book = book;

                groups.Add(new ConversionGroup
                {
                    Author = author,
                    Book = book,
                    Edition = edition,
                    Files = editionGroup
                        .Select(x => x.File)
                        .OrderBy(f => f.Part)
                        .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    TargetQuality = editionGroup.First().Target
                });
            }

            return groups
                .OrderBy(g => g.Book.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private IEnumerable<(BookFile File, Quality Target)> EligibleFiles(Author author, List<BookFile> files)
        {
            foreach (var file in files ?? new List<BookFile>())
            {
                var target = GetConversionTarget(author, file, out _);

                if (target != null)
                {
                    yield return (file, target);
                }
            }
        }

        private List<BookFileConversionPreview> BuildPreviews(Author author, List<BookFile> files)
        {
            var previews = new List<BookFileConversionPreview>();
            var bookTitles = new Dictionary<int, (int BookId, string Title)>();

            foreach (var file in files ?? new List<BookFile>())
            {
                var target = GetConversionTarget(author, file, out var reason);

                if (target == null && reason == null)
                {
                    // Nothing about this file is interesting to a conversion preview (an eBook, a
                    // Calibre-managed file): leave it out rather than listing it as a non-action.
                    continue;
                }

                if (!bookTitles.TryGetValue(file.EditionId, out var bookInfo))
                {
                    var edition = _editionService.GetEdition(file.EditionId);
                    var book = edition == null ? null : _bookService.GetBook(edition.BookId);
                    bookInfo = (book?.Id ?? 0, book?.Title ?? string.Empty);
                    bookTitles[file.EditionId] = bookInfo;
                }

                previews.Add(new BookFileConversionPreview
                {
                    AuthorId = author.Id,
                    BookId = bookInfo.BookId,
                    EditionId = file.EditionId,
                    BookFileId = file.Id,
                    BookTitle = bookInfo.Title,
                    Path = file.Path,
                    Size = file.Size,
                    SourceQuality = file.Quality?.Quality?.Name ?? Quality.Unknown.Name,
                    TargetQuality = target?.Name,
                    CanConvert = target != null,
                    Reason = reason
                });
            }

            return previews
                .OrderBy(p => p.BookTitle, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// The quality this file would be converted to, or null with a reason explaining why it
        /// would be left alone. A null target and a null reason together mean the file is simply
        /// not a conversion candidate and should not be shown at all.
        /// </summary>
        private Quality GetConversionTarget(Author author, BookFile file, out string reason)
        {
            reason = null;

            if (file == null || file.Path.IsNullOrWhiteSpace() || file.EditionId <= 0)
            {
                return null;
            }

            var sourceQuality = file.Quality?.Quality ?? Quality.Unknown;

            if (!QualityMediaTypeHelper.IsAudiobookQuality(sourceQuality))
            {
                return null;
            }

            if (file.CalibreId != 0)
            {
                reason = "Calibre-managed files are not converted";
                return null;
            }

            var target = QualityConversionHelper.GetPlannedConversionTarget(author, file.Quality);

            if (target == null)
            {
                var profile = author?.GetQualityProfileForQuality(sourceQuality);

                if (profile == null)
                {
                    reason = "No quality profile applies to this file";
                }
                else if (!profile.ConvertToQualityId.HasValue || profile.ConvertToQualityId.Value <= 0)
                {
                    reason = $"Quality profile '{profile.Name}' has no conversion target set";
                }
                else if (profile.ConvertToQualityId.Value == sourceQuality.Id)
                {
                    reason = $"Already {sourceQuality.Name}";
                }
                else
                {
                    reason = $"Conversion target is not allowed by quality profile '{profile.Name}'";
                }

                return null;
            }

            if (Path.GetExtension(file.Path).Equals(".m4b", StringComparison.OrdinalIgnoreCase) &&
                target == Quality.M4B)
            {
                reason = "Already M4B";
                return null;
            }

            if (!_diskProvider.FileExists(file.Path))
            {
                reason = "File is missing from disk";
                return null;
            }

            return target;
        }
    }
}
