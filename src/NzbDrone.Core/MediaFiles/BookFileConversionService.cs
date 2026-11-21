using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Identification;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
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
    /// Each book edition becomes one library conversion job on the conversion worker, the same
    /// worker that converts downloads, so ffmpeg detection, concurrency and CPU limits, tagging,
    /// chapters, retained-artifact reuse and workspace cleanup are shared. A library job is owned by
    /// its book and edition rather than by a download. When it is ready, its converted file goes
    /// through the normal import as a replacement for exactly the files it was made from.
    ///
    /// Three commands split the work so none of them holds a command thread or disk access while a
    /// book encodes: Convert queues the jobs and returns; one progress task mirrors every library
    /// job into System > Tasks and cancels them when it is cancelled; and a short import command
    /// per edition imports each finished job.
    /// </summary>
    public class BookFileConversionService : IBookFileConversionService,
                                             IExecute<ConvertBookFilesCommand>,
                                             IExecute<ConvertAuthorCommand>,
                                             IExecute<ImportLibraryConversionCommand>,
                                             IExecute<LibraryConversionProgressCommand>,
                                             IHandle<ApplicationShutdownRequested>
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
        private readonly IConversionJobService _conversionJobService;
        private readonly IManageCommandQueue _commandQueue;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;
        private readonly TimeSpan _progressPollInterval;
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();

        public BookFileConversionService(IAuthorService authorService,
                                         IBookService bookService,
                                         IEditionService editionService,
                                         IMediaFileService mediaFileService,
                                         IMakeImportDecision importDecisionMaker,
                                         IImportApprovedBooks importApprovedBooks,
                                         IConversionJobService conversionJobService,
                                         IManageCommandQueue commandQueue,
                                         IDiskProvider diskProvider,
                                         Logger logger)
            : this(authorService, bookService, editionService, mediaFileService, importDecisionMaker, importApprovedBooks,
                conversionJobService, commandQueue, diskProvider, logger, TimeSpan.FromSeconds(2))
        {
        }

        internal BookFileConversionService(IAuthorService authorService,
                                           IBookService bookService,
                                           IEditionService editionService,
                                           IMediaFileService mediaFileService,
                                           IMakeImportDecision importDecisionMaker,
                                           IImportApprovedBooks importApprovedBooks,
                                           IConversionJobService conversionJobService,
                                           IManageCommandQueue commandQueue,
                                           IDiskProvider diskProvider,
                                           Logger logger,
                                           TimeSpan progressPollInterval)
        {
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _mediaFileService = mediaFileService;
            _importDecisionMaker = importDecisionMaker;
            _importApprovedBooks = importApprovedBooks;
            _conversionJobService = conversionJobService;
            _commandQueue = commandQueue;
            _diskProvider = diskProvider;
            _logger = logger;
            _progressPollInterval = progressPollInterval;
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

            ReportQueued(new[] { QueueFiles(author, files, message.BookId) });
        }

        public void Execute(ConvertAuthorCommand message)
        {
            var authors = _authorService.GetAuthors(message.AuthorIds ?? new List<int>());
            ReportQueued(authors.Select(author => QueueFiles(author, _mediaFileService.GetFilesByAuthor(author.Id))).ToList());
        }

        public void Execute(ImportLibraryConversionCommand message)
        {
            ImportReadyJob(message.EditionId);
        }

        public void Execute(LibraryConversionProgressCommand message)
        {
            Execute(message, CancellationToken.None);
        }

        // The command executor prefers this overload and cancels the token when the task is
        // cancelled from System > Tasks.
        public void Execute(LibraryConversionProgressCommand message, CancellationToken cancellationToken)
        {
            var seen = new Dictionary<int, (int Order, string Title)>();
            var lastMessage = string.Empty;
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);

            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    CancelActiveJobs(seen);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (_shutdown.IsCancellationRequested)
                {
                    // Shutting down is not a cancel: the conversions keep their jobs and the worker resumes
                    // them on the next start, which also brings this task back. Only the polling stops.
                    _logger.Debug("[CONVERSION] Stopping library conversion progress for shutdown");
                    return;
                }

                var active = _conversionJobService.GetActiveLibraryJobs();
                foreach (var job in active.Where(job => !seen.ContainsKey(job.EditionId)))
                {
                    seen[job.EditionId] = (seen.Count + 1, GetTitle(job));
                }

                if (active.Count == 0)
                {
                    break;
                }

                var status = DescribeProgress(active, seen);
                if (status != lastMessage)
                {
                    // Only a change is reported, so a long book does not write a log line every poll.
                    lastMessage = status;
                    _logger.ProgressInfo(status);
                }

                if (wake.Token.WaitHandle.WaitOne(_progressPollInterval))
                {
                    continue;
                }
            }

            if (seen.Count == 0)
            {
                _logger.ProgressInfo("No library conversions are running");
                return;
            }

            var finished = _conversionJobService.GetLibraryJobs(seen.Keys);
            var converted = finished.Count(job => job.Status == ConversionJobStatus.Completed);
            var failed = finished.Count(job => job.Status == ConversionJobStatus.Failed);
            var cancelled = finished.Count(job => job.Status == ConversionJobStatus.Cancelled);

            _logger.ProgressInfo("Converted {0} of {1} book(s){2}{3}",
                converted,
                seen.Count,
                failed > 0 ? $"; {failed} failed (see log)" : string.Empty,
                cancelled > 0 ? $"; {cancelled} cancelled" : string.Empty);
        }

        public void Handle(ApplicationShutdownRequested message)
        {
            _shutdown.Cancel();
        }

        private static string DescribeProgress(List<ConversionJob> active, Dictionary<int, (int Order, string Title)> seen)
        {
            var total = seen.Count;
            var converting = active.Where(job => job.Status == ConversionJobStatus.Converting).OrderBy(job => seen[job.EditionId].Order).ToList();
            if (converting.Count > 0)
            {
                var job = converting[0];
                var (order, title) = seen[job.EditionId];
                var percent = (int)Math.Floor(Math.Clamp(job.Progress ?? 0m, 0m, 100m));
                var others = converting.Count > 1 ? $", {converting.Count - 1} more converting" : string.Empty;
                return $"Converting '{title}' to {job.TargetQualityName ?? "M4B"} ({order}/{total}) - {percent}%{others}";
            }

            var importing = active.Where(job => job.Status == ConversionJobStatus.ReadyToImport).OrderBy(job => seen[job.EditionId].Order).FirstOrDefault();
            if (importing != null)
            {
                var (order, title) = seen[importing.EditionId];
                return $"Importing converted '{title}' ({order}/{total})";
            }

            if (active.Any(job => job.Status == ConversionJobStatus.Cancelling))
            {
                return "Cancelling conversion";
            }

            return $"Waiting to convert {active.Count} book(s)";
        }

        private void CancelActiveJobs(Dictionary<int, (int Order, string Title)> seen)
        {
            // Encoding stops; nothing is replaced until a conversion finishes and imports, so the
            // original files stay as they were. A finished conversion that is already importing is
            // left to complete rather than discarded mid-import.
            foreach (var job in _conversionJobService.GetActiveLibraryJobs())
            {
                if (job.Status is not (ConversionJobStatus.Queued or ConversionJobStatus.Converting))
                {
                    continue;
                }

                try
                {
                    if (_conversionJobService.CancelLibrary(job.EditionId, ConversionRemovalOrigin.UserAction))
                    {
                        _logger.Info("[CONVERSION] Cancelled library conversion of '{0}'; its original files were left as they were", seen.TryGetValue(job.EditionId, out var info) ? info.Title : GetTitle(job));
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[CONVERSION] Unable to cancel library conversion job {0}", job.Id);
                }
            }
        }

        private static string GetTitle(ConversionJob job)
        {
            try
            {
                var title = JsonSerializer.Deserialize<ConversionJobRequest>(job.RequestJson ?? string.Empty)?.BookTitle;
                if (title.IsNotNullOrWhiteSpace())
                {
                    return title;
                }
            }
            catch (JsonException)
            {
                // Fall back to the edition below.
            }

            return $"edition {job.EditionId}";
        }

        private sealed record QueueOutcome(string AuthorName, int Eligible, int Queued, int Refused);

        private QueueOutcome QueueFiles(Author author, List<BookFile> files, int? bookId = null)
        {
            var groups = BuildConversionGroups(author, files, bookId);
            var queued = 0;
            var refused = 0;

            foreach (var group in groups)
            {
                try
                {
                    var decisions = BuildDecisions(group.Author, group.Book, group.Edition, group.Files);
                    if (decisions.Count == 0)
                    {
                        _logger.Warn("[CONVERSION] No usable files remained for '{0}'; skipping", group.Book.Title);
                        refused++;
                        continue;
                    }

                    var result = _importApprovedBooks.QueueLibraryConversion(decisions, group.Author, group.Book);
                    if (result.Queued)
                    {
                        queued++;
                    }
                    else
                    {
                        refused++;
                        _logger.Warn("[CONVERSION] '{0}' was not queued for conversion: {1}", group.Book.Title, result.Error);
                    }
                }
                catch (Exception ex)
                {
                    refused++;
                    _logger.Error(ex, "[CONVERSION] Failed queueing conversion of '{0}'", group.Book?.Title);
                }
            }

            return new QueueOutcome(author.Name, groups.Count, queued, refused);
        }

        private void ReportQueued(IReadOnlyCollection<QueueOutcome> outcomes)
        {
            var eligible = outcomes.Sum(outcome => outcome.Eligible);
            var queued = outcomes.Sum(outcome => outcome.Queued);
            var refused = outcomes.Sum(outcome => outcome.Refused);
            var who = outcomes.Count == 1 ? outcomes.First().AuthorName : $"{outcomes.Count} authors";

            if (eligible == 0)
            {
                _logger.ProgressInfo("No files to convert for {0}", who);
                return;
            }

            if (queued > 0)
            {
                // One progress task follows every library conversion, so a second Convert click
                // joins the task that is already running instead of starting another.
                _commandQueue.Push(new LibraryConversionProgressCommand());
            }

            _logger.ProgressInfo("Queued {0} of {1} book(s) for conversion for {2}{3}",
                queued,
                eligible,
                who,
                refused > 0 ? $"; {refused} could not be queued (see log)" : string.Empty);
        }

        private void ImportReadyJob(int editionId)
        {
            var job = _conversionJobService.GetLibraryJob(editionId);
            if (job == null || job.Status != ConversionJobStatus.ReadyToImport || _conversionJobService.IsRunning(job.DownloadId))
            {
                _logger.Debug("[CONVERSION] No library conversion is waiting to import for edition {0}", editionId);
                return;
            }

            var title = GetTitle(job);
            try
            {
                var request = _conversionJobService.GetCompletedConversion(job.DownloadId);
                if (request == null)
                {
                    Fail(job, title, "The converted file could not be verified. The original files were kept; convert the book again.");
                    return;
                }

                var edition = _editionService.GetEdition(editionId);
                var book = edition == null ? null : _bookService.GetBook(edition.BookId);
                var author = book == null ? null : _authorService.GetAuthor(book.AuthorId);
                if (edition == null || book == null || author == null)
                {
                    Fail(job, title, "The book this conversion belongs to is no longer in the library. Nothing was imported.");
                    return;
                }

                edition.Book = book;
                var sourcePaths = request.Sources.Select(source => source.Path).ToHashSet(PathEqualityComparer.Instance);
                var files = _mediaFileService.GetFilesByEdition(editionId).Where(file => sourcePaths.Contains(file.Path)).ToList();
                if (files.Count != sourcePaths.Count || !request.MatchesSources(book.Id, sourcePaths.ToArray()))
                {
                    Fail(job, title, "The library files changed since this conversion was queued. Nothing was replaced; convert the book again.");
                    return;
                }

                var decisions = BuildDecisions(author, book, edition, files);
                var results = _importApprovedBooks.ImportLibraryConversion(request, decisions);
                if (results.Any(result => result.Result == ImportResultType.Imported && result.ImportDecision?.Item?.IsGeneratedConversion == true))
                {
                    _conversionJobService.Complete(job.DownloadId);
                    _logger.Info("[CONVERSION] Converted '{0}' to {1}", book.Title, request.TargetQualityName ?? "M4B");
                    return;
                }

                var errors = results.SelectMany(result => result.Errors ?? new List<string>())
                    .Where(error => error.IsNotNullOrWhiteSpace())
                    .Distinct()
                    .ToList();
                Fail(job, title, errors.Any() ? string.Join("; ", errors) : "The converted file was not imported.");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[CONVERSION] Failed importing the library conversion of '{0}'", title);
                Fail(job, title, "Importing the converted file failed: " + ex.Message);
            }
        }

        private void Fail(ConversionJob job, string title, string error)
        {
            // The job keeps its workspace, so a retained converted file can be reused by the next
            // Convert instead of encoding the book again.
            _logger.Warn("[CONVERSION] '{0}' was not converted: {1}", title, error);
            _conversionJobService.Fail(job.DownloadId, error);
        }

        private List<ImportDecision<LocalBook>> BuildDecisions(Author author, Book book, Edition edition, List<BookFile> files)
        {
            var fileInfos = files
                .Select(file => _diskProvider.GetFileInfo(file.Path))
                .ToList();

            var overrides = new IdentificationOverrides
            {
                Author = author,
                Book = book,
                Edition = edition
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

            var approved = decisions.Where(decision => decision.Approved && decision.Item != null).ToList();
            foreach (var decision in approved)
            {
                decision.Item.IsLibraryConversion = true;
                decision.Item.Author = author;
                decision.Item.Book = book;
                decision.Item.Edition = edition;
            }

            return approved;
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

        private List<ConversionGroup> BuildConversionGroups(Author author, List<BookFile> files, int? bookId = null)
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

                // The selection was made from the requested author's (and book's) page. A file linked
                // to another book since then is skipped rather than converting a book the user did not choose.
                if (book.AuthorId != author.Id || (bookId.HasValue && book.Id != bookId.Value))
                {
                    _logger.Warn("[CONVERSION] Skipping '{0}': its files now belong to a different {1} than the one selected", book.Title, book.AuthorId != author.Id ? "author" : "book");
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
