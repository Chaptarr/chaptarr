using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Crypto;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Instrumentation;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport.Identification;
using NzbDrone.Core.MediaFiles.BookImport;

namespace NzbDrone.Core.MediaFiles.BookImport.Manual
{
    public class ManualImportService : IExecute<ManualImportCommand>, IManualImportService
    {
        private sealed class SuggestedExecutionResolution
        {
            public Author Author { get; set; }
            public FileMatch Match { get; set; }
            public RawFileTags RawTags { get; set; }
            public int? DurationSeconds { get; set; }
            public HashSet<int> AllowedBookIds { get; set; }
            public string RejectionReason { get; set; }
        }

        private sealed class PreparedSuggestedFile
        {
            public ManualImportFile Request { get; set; }
            public IFileInfo FileInfo { get; set; }
            public string ActualPath { get; set; }
            public BookMediaType MediaType { get; set; }
            public RootFolder RootFolder { get; set; }
            public DiscoveredFileWithMetadata Discovered { get; set; }
            public RawFileTags RawTags { get; set; }
        }

        private readonly IDiskProvider _diskProvider;
        private readonly IParsingService _parsingService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IDiskScanService _diskScanService;
        private readonly IMakeImportDecision _importDecisionMaker;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IAuthorLibraryService _authorLibraryService;
        private readonly IRootFolderSettingsResolver _rootFolderSettingsResolver;
        private readonly IConfigService _configService;
        private readonly IFileMatchingService _fileMatchingService;
        private readonly IProvideBookInfo _bookInfo;
        private readonly IMetadataTagService _metadataTagService;
        private readonly IImportApprovedBooks _importApprovedBooks;
	        private readonly ICustomFormatCalculationService _formatCalculator;
	        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly IDownloadClientFileSnapshotService _downloadClientFileSnapshotService;
        private readonly IHistoryService _historyService;
	        private readonly IProvideImportItemService _provideImportItemService;
	        private readonly IDownloadImportModeResolver _downloadImportModeResolver;
	        private readonly IEventAggregator _eventAggregator;
	        private readonly Logger _logger;

        public ManualImportService(IDiskProvider diskProvider,
                                   IParsingService parsingService,
                                   IRootFolderService rootFolderService,
                                   IDiskScanService diskScanService,
                                   IMakeImportDecision importDecisionMaker,
                                   IAuthorService authorService,
                                   IBookService bookService,
                                   IEditionService editionService,
                                   IAuthorLibraryService authorLibraryService,
                                   IRootFolderSettingsResolver rootFolderSettingsResolver,
                                   IConfigService configService,
                                   IFileMatchingService fileMatchingService,
                                   IProvideBookInfo bookInfo,
                                   IMetadataTagService metadataTagService,
                                   IImportApprovedBooks importApprovedBooks,
	                                   ICustomFormatCalculationService formatCalculator,
	                                   ITrackedDownloadService trackedDownloadService,
                                   IDownloadClientFileSnapshotService downloadClientFileSnapshotService,
                                   IHistoryService historyService,
	                                   IProvideImportItemService provideImportItemService,
	                                   IDownloadImportModeResolver downloadImportModeResolver,
	                                   IEventAggregator eventAggregator,
	                                   Logger logger)
	        {
	            _diskProvider = diskProvider;
	            _parsingService = parsingService;
            _rootFolderService = rootFolderService;
            _diskScanService = diskScanService;
            _importDecisionMaker = importDecisionMaker;
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _authorLibraryService = authorLibraryService;
            _rootFolderSettingsResolver = rootFolderSettingsResolver;
            _configService = configService;
            _fileMatchingService = fileMatchingService;
            _bookInfo = bookInfo;
            _metadataTagService = metadataTagService;
            _importApprovedBooks = importApprovedBooks;
            _formatCalculator = formatCalculator;
	            _trackedDownloadService = trackedDownloadService;
            _downloadClientFileSnapshotService = downloadClientFileSnapshotService;
            _historyService = historyService;
	            _provideImportItemService = provideImportItemService;
	            _downloadImportModeResolver = downloadImportModeResolver;
	            _eventAggregator = eventAggregator;
	            _logger = logger;
	        }

	        public List<ManualImportItem> GetMediaFiles(string path, string downloadId, Author author, FilterFilesType filter, bool replaceExistingFiles, CancellationToken cancellationToken = default, IReadOnlyCollection<string> exactPaths = null)
	        {
	            cancellationToken.ThrowIfCancellationRequested();

	            IdentificationOverrides downloadOverrides = null;
	            ImportDecisionMakerInfo downloadItemInfo = null;

	            if (downloadId.IsNotNullOrWhiteSpace())
	            {
	                var trackedDownload = _trackedDownloadService.Find(downloadId);

	                if (trackedDownload == null)
                {
                    return new List<ManualImportItem>();
                }

                if (trackedDownload.ImportItem == null)
                {
                    trackedDownload.ImportItem = _provideImportItemService.ProvideImportItem(trackedDownload.DownloadItem, trackedDownload.ImportItem);
                }

	                path = trackedDownload.ImportItem.OutputPath.FullPath;

	                // If this manual import is coming from a tracked download, prefer the already-known author/book context.
	                // IMPORTANT: Only use real library entities (Id > 0). RemoteBook can contain parsed placeholders that
	                // look valid in the UI but serialize to AuthorId/BookId = 0 and cause the import to no-op.
	                try
	                {
	                    var remote = trackedDownload.RemoteBook;

	                    // Always pass download context to the decision maker (even when we can't resolve overrides).
	                    downloadItemInfo = new ImportDecisionMakerInfo
	                    {
	                        DownloadClientItem = trackedDownload.DownloadItem,
	                        ParsedBookInfo = remote?.ParsedBookInfo
	                    };

	                    var overrideAuthor = author;
	                    Book overrideBook = null;

	                    // Resolve author from remote context if the request didn't specify one.
	                    if (overrideAuthor == null)
	                    {
	                        if (remote?.Author?.Id > 0)
	                        {
	                            overrideAuthor = remote.Author;
	                        }
	                        else if (!string.IsNullOrWhiteSpace(remote?.Author?.Name))
	                        {
	                            overrideAuthor = _authorService.FindByName(remote.Author.Name) ??
	                                           _authorService.FindByNameInexact(remote.Author.Name);
	                        }
	                    }

	                    // Resolve a single book when possible.
	                    var remoteBookIds = remote?.Books?.Select(b => b?.Id ?? 0).Where(id => id > 0).Distinct().ToList();
	                    if (remoteBookIds?.Count == 1)
	                    {
	                        overrideBook = _bookService.GetBook(remoteBookIds[0]);
	                        overrideAuthor = overrideBook?.Author ?? overrideAuthor;
	                    }
	                    else if (overrideAuthor?.Id > 0 && !string.IsNullOrWhiteSpace(remote?.ParsedBookInfo?.BookTitle))
	                    {
	                        var title = remote.ParsedBookInfo.BookTitle;
	                        overrideBook = _bookService.FindByTitle(overrideAuthor.Id, title) ??
	                                       _bookService.FindByTitleInexact(overrideAuthor.Id, title);
	                        overrideAuthor = overrideBook?.Author ?? overrideAuthor;
	                    }

	                    var hasOverrideAuthor = overrideAuthor?.Id > 0;
	                    var hasOverrideBook = overrideBook?.Id > 0;

	                    if (hasOverrideAuthor || hasOverrideBook)
	                    {
	                        downloadOverrides = new IdentificationOverrides
	                        {
	                            Author = hasOverrideAuthor ? overrideAuthor : null,
	                            Book = hasOverrideBook ? overrideBook : null
	                        };
	                    }
	                }
	                catch (Exception ex)
	                {
	                    _logger.Debug(ex, "Failed to build tracked-download overrides for manual import (downloadId={0})", downloadId);
	                    downloadOverrides = null;
	                    downloadItemInfo = null;
	                }
	            }

		            if (!_diskProvider.FolderExists(path))
		            {
                if (!_diskProvider.FileExists(path))
                {
                    return new List<ManualImportItem>();
                }

                var files = new List<IFileInfo> { _diskProvider.GetFileInfo(path) };
                var idOverrides = downloadOverrides;
                if (author != null)
                {
                    if (idOverrides == null)
                    {
                        idOverrides = new IdentificationOverrides
                        {
                            Author = author
                        };
                    }
                    else if (idOverrides.Author == null)
                    {
                        idOverrides.Author = author;
                    }
                }

                // Ensure initial load for a single-file manual import still runs suggestion matching
                // (SimpleImportDecisionMaker uses itemInfo != null as the "initial load" indicator).
                var itemInfo = downloadItemInfo ?? new ImportDecisionMakerInfo
                {
                    ParsedBookInfo = Parser.Parser.ParseBookTitle(Path.GetFileNameWithoutExtension(path))
                };

                var config = new ImportDecisionMakerConfig
                {
                    Filter = FilterFilesType.None,
                    NewDownload = true,
                    SingleRelease = false,
                    IncludeExisting = !replaceExistingFiles,
                    AddNewAuthors = false,
	                    KeepAllEditions = true
	                };

	                var decision = _importDecisionMaker.GetImportDecisions(files, idOverrides, itemInfo, config, cancellationToken);
	                var result = MapItem(decision.First(), downloadId, replaceExistingFiles, false);

	                return new List<ManualImportItem> { result };
	            }

	            return ProcessFolder(path, downloadId, author, filter, replaceExistingFiles, cancellationToken, exactPaths);
        }

	        private List<ManualImportItem> ProcessFolder(string folder, string downloadId, Author author, FilterFilesType filter, bool replaceExistingFiles, CancellationToken cancellationToken, IReadOnlyCollection<string> exactPaths = null)
	        {
		            cancellationToken.ThrowIfCancellationRequested();
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug("[MEMORY] Manual import preview start for '{0}': {1}", folder, MemorySnapshot.CaptureDetailed());
                }

	            DownloadClientItem downloadClientItem = null;
	            var directoryInfo = new DirectoryInfo(folder);
	            author = author ?? _parsingService.GetAuthor(directoryInfo.Name);
	            Book bookOverride = null;

	            if (downloadId.IsNotNullOrWhiteSpace())
	            {
	                var trackedDownload = _trackedDownloadService.Find(downloadId);
	                downloadClientItem = trackedDownload?.DownloadItem;

	                if (author == null)
	                {
	                    author = trackedDownload?.RemoteBook?.Author;
	                }

	                // Prefer the tracked download's book when it is unambiguous (single-book release).
	                try
	                {
	                    var remoteBookId = trackedDownload?.RemoteBook?.Books?.Select(b => b?.Id ?? 0).Distinct().ToList();
	                    if (remoteBookId?.Count == 1 && remoteBookId[0] > 0)
	                    {
	                        bookOverride = _bookService.GetBook(remoteBookId[0]);
	                        author = bookOverride?.Author ?? author;
	                    }
	                    else if (author?.Id > 0 && !string.IsNullOrWhiteSpace(trackedDownload?.RemoteBook?.ParsedBookInfo?.BookTitle))
	                    {
	                        var title = trackedDownload.RemoteBook.ParsedBookInfo.BookTitle;
	                        bookOverride = _bookService.FindByTitle(author.Id, title) ??
	                                       _bookService.FindByTitleInexact(author.Id, title);
	                        author = bookOverride?.Author ?? author;
	                    }
	                }
	                catch (Exception ex)
	                {
	                    _logger.Debug(ex, "Failed to resolve tracked-download book override for manual import (downloadId={0})", downloadId);
	                }
	            }

	            var hasExactPathScope = exactPaths != null;
	            var requestedPaths = (exactPaths ?? Array.Empty<string>())
	                .Where(path => path.IsNotNullOrWhiteSpace())
	                .Where(path => Path.GetDirectoryName(path).PathEquals(folder) || folder.IsParentPath(path))
	                .Distinct(PathEqualityComparer.Instance)
	                .ToList();
	            var authorFiles = hasExactPathScope
	                ? requestedPaths
	                    .Where(_diskProvider.FileExists)
	                    .Select(_diskProvider.GetFileInfo)
	                    .Where(file => MediaFileExtensions.AllExtensions.Contains(file.Extension))
	                    .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
	                    .ToList()
	                : _diskScanService.GetBookFiles(folder).ToList();
	            cancellationToken.ThrowIfCancellationRequested();

	            var idOverrides = new IdentificationOverrides
	            {
	                Author = author,
	                Book = bookOverride
	            };
            var itemInfo = new ImportDecisionMakerInfo
            {
                DownloadClientItem = downloadClientItem,
                ParsedBookInfo = Parser.Parser.ParseBookTitle(directoryInfo.Name)
            };
            var config = new ImportDecisionMakerConfig
            {
                Filter = filter,
                NewDownload = true,
                SingleRelease = false,
                IncludeExisting = !replaceExistingFiles,
                AddNewAuthors = false,
                KeepAllEditions = true
            };

            var decisions = _importDecisionMaker.GetImportDecisions(authorFiles, idOverrides, itemInfo, config, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // Disabled deliberately: manual retry/import should re-run matching honestly.
            // Grab history or monitored-edition state must not rewrite an already matched edition.
            // if (downloadClientItem != null && bookOverride?.Id > 0)
            // {
            //     var preferredEdition = TrackedMultipartAudioRepairHelper.ResolveExpectedTrackedEdition(
            //         bookOverride,
            //         downloadClientItem.DownloadId ?? downloadId,
            //         _historyService,
            //         _editionService);
            //
            //     decisions = TrackedMultipartAudioRepairHelper.RepairTrackedSingleBookAudioDecisions(
            //         decisions,
            //         bookOverride,
            //         bookOverride.Author ?? author,
            //         preferredEdition,
            //         _editionService,
            //         _logger,
            //         downloadClientItem.Title ?? directoryInfo.Name);
            // }

            // paths will be different for new and old files which is why we need to map separately
            var newFiles = authorFiles.Join(decisions,
                                            f => f.FullName,
                                            d => d.Item.Path,
                                            (f, d) => new { File = f, Decision = d },
                                            PathEqualityComparer.Instance);

            var newItems = newFiles.Select(x => MapItem(x.Decision, downloadId, replaceExistingFiles, false));
            var existingDecisions = decisions.Except(newFiles.Select(x => x.Decision));
            var existingItems = existingDecisions.Select(x => MapItem(x, null, replaceExistingFiles, false));

            var result = newItems.Concat(existingItems).ToList();
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug("[MEMORY] Manual import preview complete for '{0}' ({1} items): {2}", folder, result.Count, MemorySnapshot.CaptureDetailed());
            }
            return result;
        }

        public List<ManualImportItem> UpdateItems(List<ManualImportItem> items)
        {
            var replaceExistingFiles = items.All(x => x.ReplaceExistingFiles);
            var groupedItems = items.Where(x => !x.AdditionalFile).GroupBy(x => x.Book?.Id);
            _logger.Debug($"UpdateItems, {groupedItems.Count()} groups, replaceExisting {replaceExistingFiles}");

            var result = new List<ManualImportItem>();

            foreach (var group in groupedItems)
            {
                _logger.Debug("UpdateItems, group key: {0}", group.Key);

                var disableReleaseSwitching = group.First().DisableReleaseSwitching;

                var files = group.Select(x => _diskProvider.GetFileInfo(x.Path)).ToList();
                var idOverride = new IdentificationOverrides
                {
                    Author = group.First().Author,
                    Book = group.First().Book,
                    Edition = group.First().Edition
                };
                var config = new ImportDecisionMakerConfig
                {
                    Filter = FilterFilesType.None,
                    NewDownload = true,
                    SingleRelease = true,
                    IncludeExisting = !replaceExistingFiles,
                    AddNewAuthors = false
                };
                var decisions = _importDecisionMaker.GetImportDecisions(files, idOverride, null, config);

                var existingItems = group.Join(decisions,
                                               i => i.Path,
                                               d => d.Item.Path,
                                               (i, d) => new { Item = i, Decision = d },
                                               PathEqualityComparer.Instance);

                foreach (var pair in existingItems)
                {
                    var item = pair.Item;
                    var decision = pair.Decision;

                    if (decision.Item.Author != null)
                    {
                        item.Author = decision.Item.Author;
                    }

                    if (decision.Item.Book != null)
                    {
                        item.Book = decision.Item.Book;
                        item.Edition = decision.Item.Edition;
                    }

                    if (item.Quality?.Quality == Quality.Unknown)
                    {
                        item.Quality = decision.Item.Quality;
                    }

                    if (item.ReleaseGroup.IsNullOrWhiteSpace())
                    {
                        item.ReleaseGroup = decision.Item.ReleaseGroup;
                    }

                    item.Rejections = decision.Rejections;
                    item.Size = decision.Item.Size;

                    result.Add(item);
                }

                var newDecisions = decisions.Except(existingItems.Select(x => x.Decision));
                result.AddRange(newDecisions.Select(x => MapItem(x, null, replaceExistingFiles, disableReleaseSwitching)));
            }

            return result;
        }

        private ManualImportItem MapItem(ImportDecision<LocalBook> decision, string downloadId, bool replaceExistingFiles, bool disableReleaseSwitching)
        {
            var item = new ManualImportItem();
            var requestedPath = decision.Item.Path;
            var fileInfo = _diskProvider.GetFileInfo(requestedPath);
            var actualPath = fileInfo.FullName ?? requestedPath;

            if (fileInfo.Exists && !actualPath.PathEquals(requestedPath))
            {
                _logger.Warn("Manual import recovered file path '{0}' as '{1}'", requestedPath, actualPath);
                item.Warnings.Add($"Recovered filename encoding mismatch. Requested path '{requestedPath}' resolved to actual file '{actualPath}'.");
                decision.Item.Path = actualPath;
            }

            item.Id = HashConverter.GetHashInt31(actualPath);
            item.Path = actualPath;
            item.Name = Path.GetFileNameWithoutExtension(actualPath);
            item.DownloadId = downloadId;

            // Suggest-only metadata (must not cause DB writes). Used only for initial UI display and optional execute-time hydration.
            item.SuggestedForeignAuthorId = decision.Item.SuggestedForeignAuthorId;
            item.SuggestedAuthorName = decision.Item.SuggestedAuthorName;
            item.SuggestedForeignBookId = decision.Item.SuggestedForeignBookId;
            item.SuggestedBookTitle = decision.Item.SuggestedBookTitle;
            item.SuggestedForeignEditionId = decision.Item.SuggestedForeignEditionId;
            item.SuggestedEditionTitle = decision.Item.SuggestedEditionTitle;

            if (decision.Item.Author != null)
            {
                item.Author = decision.Item.Author;

                item.CustomFormats = _formatCalculator.ParseCustomFormat(decision.Item);
            }

            if (decision.Item.Book != null)
            {
                item.Book = decision.Item.Book;
                item.Edition = decision.Item.Edition;
            }

            item.Quality = decision.Item.Quality;
            item.IndexerFlags = (int)decision.Item.IndexerFlags;
            item.Size = fileInfo.Length;
            item.Rejections = decision.Rejections;
            item.Tags = decision.Item.RawTags?.AllTags ?? new Dictionary<string, List<string>>();
            item.AdditionalFile = decision.Item.AdditionalFile;
            item.ReplaceExistingFiles = replaceExistingFiles;
            item.DisableReleaseSwitching = disableReleaseSwitching;

            return item;
        }

        public void Execute(ManualImportCommand message)
        {
            _logger.ProgressTrace("Manually importing {0} files using mode {1}", message.Files.Count, message.ImportMode);

            var imported = new List<ImportResult>();
            var importedTrackedDownload = new List<ManuallyImportedFile>();
            var decisions = new List<ImportDecision<LocalBook>>();
            var fileCount = 0;

            BookMediaType GetMediaTypeFromPath(string path)
            {
                var ext = Path.GetExtension(path ?? string.Empty);
                if (MediaFileExtensions.TextExtensions.Contains(ext))
                {
                    return BookMediaType.Ebook;
                }

                return BookMediaType.Audiobook;
            }

            RootFolder ResolveRootFolderForHydration(RootFolder fileRootFolder, BookMediaType mediaType, out string error)
            {
                error = null;
                var rootFolder = fileRootFolder;

                if (rootFolder == null)
                {
                    var preferredType = mediaType == BookMediaType.Audiobook ? FolderType.Audiobook : FolderType.Ebook;
                    var defaultRootFolderPath = preferredType == FolderType.Audiobook
                        ? _configService.DefaultAudiobookRootFolderPath
                        : _configService.DefaultEbookRootFolderPath;

                    if (!RootFolderDefaultResolver.TryGetEffectiveDefaultRootFolder(
                            _rootFolderService.All(),
                            preferredType,
                            defaultRootFolderPath,
                            out rootFolder,
                            out error))
                    {
                        return null;
                    }
                }

                var settings = _rootFolderSettingsResolver.ResolveSettings(rootFolder, mediaType);
                if (settings == null || !settings.IsConfigured)
                {
                    error = $"Root folder '{rootFolder.Path}' is missing complete {mediaType.ToString().ToLowerInvariant()} quality and metadata profile defaults";
                    return null;
                }

                return rootFolder;
            }

            MonitoringConfig BuildHydrationConfig(string filePath, RootFolder rootFolder, BookMediaType mediaType)
            {
                var config = new MonitoringConfig
                {
                    RequestedBy = "ManualImport",
                    IsManualAddition = true,
                    CreateAudiobook = mediaType == BookMediaType.Audiobook,
                    CreateEbook = mediaType == BookMediaType.Ebook,
                    QueueIfUnavailable = true
                };

                if (rootFolder == null)
                {
                    return config;
                }

                var settings = _rootFolderSettingsResolver.ResolveSettings(rootFolder, mediaType);
                if (mediaType == BookMediaType.Audiobook)
                {
                    config.AudiobookRootFolderPath = rootFolder.Path;
                    config.AudiobookQualityProfileId = settings?.QualityProfileId;
                    config.AudiobookMetadataProfileId = settings?.MetadataProfileId;
                    config.AudiobookMonitorExistingMode = RootFolderSettingsResolver.ResolveInitialMonitorMode(settings?.MonitorExistingMode);
                    config.AudiobookMonitored = settings?.Monitored;
                    config.AudiobookMonitorNewItems = settings?.MonitorNewItems;
                    config.AudiobookTags = settings?.Tags == null ? null : new HashSet<int>(settings.Tags);
                }
                else
                {
                    config.EbookRootFolderPath = rootFolder.Path;
                    config.EbookQualityProfileId = settings?.QualityProfileId;
                    config.EbookMetadataProfileId = settings?.MetadataProfileId;
                    config.EbookMonitorExistingMode = RootFolderSettingsResolver.ResolveInitialMonitorMode(settings?.MonitorExistingMode);
                    config.EbookMonitored = settings?.Monitored;
                    config.EbookMonitorNewItems = settings?.MonitorNewItems;
                    config.EbookTags = settings?.Tags == null ? null : new HashSet<int>(settings.Tags);
                }

                // Preserve the on-disk author folder when the file lives under a root folder.
                try
                {
                    var fileDir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrWhiteSpace(fileDir) && !string.IsNullOrWhiteSpace(rootFolder.Path))
                    {
                        var relativePath = Path.GetRelativePath(rootFolder.Path, fileDir);
                        if (!relativePath.StartsWith("..", StringComparison.Ordinal))
                        {
                            var first = relativePath
                                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
                                .FirstOrDefault();
                            if (!string.IsNullOrWhiteSpace(first))
                            {
                                config.DiscoveredAuthorFolderPath = Path.Combine(rootFolder.Path, first);
                            }
                        }
                    }
                }
                catch
                {
                    // best-effort only
                }

                return config;
            }

            List<Book> ResolveSuggestedWorkBooks(
                Author author,
                string providerWorkId,
                BookMediaType mediaType,
                out string error)
            {
                error = null;
                if (author?.Id <= 0 ||
                    !ProviderIdHelper.TryNormalize(providerWorkId, defaultPrefix: null, out var normalizedWorkId))
                {
                    error = "Suggested metadata did not provide a valid provider work ID.";
                    return new List<Book>();
                }

                var separator = normalizedWorkId.IndexOf(':');
                var books = _bookService.FindAllByWorkProviderId(
                        normalizedWorkId.Substring(0, separator),
                        ProviderIdHelper.StripPrefix(normalizedWorkId),
                        mediaType)
                    .Where(book => book != null &&
                                   book.Id > 0 &&
                                   book.AuthorId == author.Id &&
                                   book.MediaType == mediaType)
                    .GroupBy(book => book.Id)
                    .Select(group => group.First())
                    .OrderBy(book => book.Id)
                    .ToList();
                if (books.Count == 0)
                {
                    error = $"The authoritative author catalog does not currently contain suggested work '{normalizedWorkId}' for {mediaType}.";
                    return books;
                }

                var first = books[0];
                if (BookEditionIdentity.GetCanonicalWorkProviderIds(first).Count == 0 ||
                    books.Skip(1).Any(book => !WorkIdMatcher.WorkIdMatches(first, book)))
                {
                    error = $"Suggested work alias '{normalizedWorkId}' resolves ambiguously. Select a local book manually.";
                    return new List<Book>();
                }

                return books;
            }

            var suggestedResolutionByPath = new Dictionary<string, SuggestedExecutionResolution>(PathEqualityComparer.Instance);
            var preparedSuggestedFiles = new List<PreparedSuggestedFile>();
            foreach (var requested in message.Files.Where(file =>
                         file != null &&
                         !(file.AuthorId > 0 && file.BookId > 0) &&
                         !string.IsNullOrWhiteSpace(file.ForeignAuthorId)))
            {
                var fileInfo = _diskProvider.GetFileInfo(requested.Path);
                var actualPath = fileInfo.FullName ?? requested.Path;
                var mediaType = GetMediaTypeFromPath(actualPath);
                if (!fileInfo.Exists)
                {
                    suggestedResolutionByPath[requested.Path] = new SuggestedExecutionResolution
                    {
                        RejectionReason = $"Selected file no longer exists: '{requested.Path}'."
                    };
                    continue;
                }

                Dictionary<string, List<string>> allTags;
                int? durationSeconds;
                try
                {
                    (allTags, durationSeconds) = _metadataTagService.ReadAllTagsAndDuration(fileInfo);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Failed to read grouped manual-import evidence for '{0}'", actualPath);
                    allTags = new Dictionary<string, List<string>>();
                    durationSeconds = null;
                }

                allTags ??= new Dictionary<string, List<string>>();
                var copiedTags = allTags.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value?.ToList() ?? new List<string>(),
                    StringComparer.OrdinalIgnoreCase);
                preparedSuggestedFiles.Add(new PreparedSuggestedFile
                {
                    Request = requested,
                    FileInfo = fileInfo,
                    ActualPath = actualPath,
                    MediaType = mediaType,
                    RootFolder = _rootFolderService.GetBestRootFolder(actualPath),
                    RawTags = new RawFileTags { AllTags = copiedTags },
                    Discovered = new DiscoveredFileWithMetadata
                    {
                        Path = actualPath,
                        Size = fileInfo.Length,
                        Modified = fileInfo.LastWriteTimeUtc,
                        AllTags = copiedTags,
                        Quality = requested.Quality,
                        DurationSeconds = durationSeconds
                    }
                });
            }

            foreach (var suggestedGroup in preparedSuggestedFiles.GroupBy(file => new
                     {
                         Author = file.Request.ForeignAuthorId?.Trim().ToLowerInvariant(),
                         Work = file.Request.ForeignBookId?.Trim().ToLowerInvariant(),
                         Edition = file.Request.ForeignEditionId?.Trim().ToLowerInvariant(),
                         file.Request.SelectionSource,
                         file.MediaType,
                         Root = file.RootFolder?.Path?.ToLowerInvariant() ?? string.Empty
                     }))
            {
                var groupFiles = suggestedGroup.ToList();
                var first = groupFiles[0];
                string groupRejection = null;
                Author suggestedAuthor = null;
                if (string.IsNullOrWhiteSpace(first.Request.ForeignBookId))
                {
                    groupRejection = "Suggested metadata did not identify a provider work. Select a local book manually.";
                }

                string rootFolderError = null;
                var hydrationRoot = groupRejection == null
                    ? ResolveRootFolderForHydration(first.RootFolder, first.MediaType, out rootFolderError)
                    : null;
                if (groupRejection == null && hydrationRoot == null)
                {
                    groupRejection = !rootFolderError.IsNullOrWhiteSpace()
                        ? $"Cannot add suggested metadata: {rootFolderError}."
                        : "Cannot add suggested metadata: select a default root folder or select a local author/book.";
                }

                var isExplicitRemoteEditionSelection =
                    first.Request.SelectionSource == ManualImportSelectionSource.UserMetadataSuggestion &&
                    !string.IsNullOrWhiteSpace(first.Request.ForeignEditionId);
                UserSelectedEditionMaterialization explicitMaterialization = null;
                if (groupRejection == null && isExplicitRemoteEditionSelection)
                {
                    try
                    {
                        var config = BuildHydrationConfig(first.ActualPath, hydrationRoot, first.MediaType);
                        config.AuthorName = first.Request.ForeignAuthorName;
                        explicitMaterialization = _authorLibraryService.MaterializeUserSelectedEditionAsync(
                                new UserSelectedRemoteEdition
                                {
                                    AuthorProviderId = first.Request.ForeignAuthorId,
                                    WorkProviderId = first.Request.ForeignBookId,
                                    EditionProviderId = first.Request.ForeignEditionId,
                                    MediaType = first.MediaType
                                },
                                config)
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(
                            ex,
                            "Manual import could not materialize explicitly selected provider edition '{0}'",
                            first.Request.ForeignEditionId);
                        groupRejection = ex.Message;
                    }
                }

                if (groupRejection == null && explicitMaterialization == null &&
                    ProviderIdHelper.TryNormalize(first.Request.ForeignAuthorId, defaultPrefix: null, out var normalizedAuthorId))
                {
                    var separator = normalizedAuthorId.IndexOf(':');
                    suggestedAuthor = _authorService.FindByProviderId(
                        normalizedAuthorId.Substring(0, separator),
                        ProviderIdHelper.StripPrefix(normalizedAuthorId));
                }

                List<Book> allowedBooks = null;
                if (explicitMaterialization != null)
                {
                    suggestedAuthor = explicitMaterialization.Author;
                    allowedBooks = new List<Book> { explicitMaterialization.Book };
                }
                else if (groupRejection == null && suggestedAuthor?.Id > 0)
                {
                    allowedBooks = ResolveSuggestedWorkBooks(
                        suggestedAuthor,
                        first.Request.ForeignBookId,
                        first.MediaType,
                        out _);
                }

                if (groupRejection == null && explicitMaterialization == null &&
                    (suggestedAuthor?.Id <= 0 || allowedBooks?.Count == 0))
                {
                    try
                    {
                        var config = BuildHydrationConfig(first.ActualPath, hydrationRoot, first.MediaType);
                        config.AuthorName = first.Request.ForeignAuthorName;
                        config.MonitorMode = MonitorTypes.SpecificBook;
                        config.SpecificBookProviderIds = new HashSet<string>(
                            new[] { first.Request.ForeignBookId },
                            StringComparer.OrdinalIgnoreCase);
                        config.SpecificBookMediaType = first.MediaType;
                        suggestedAuthor = _authorLibraryService.AddAuthorAsync(
                                first.Request.ForeignAuthorId,
                                config)
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "Manual import failed to hydrate provider work '{0}'", first.Request.ForeignBookId);
                        suggestedAuthor = null;
                    }
                }

                if (groupRejection == null && suggestedAuthor?.Id < 0)
                {
                    groupRejection = "Suggested author is queued for metadata import. Try this manual import again after the author finishes importing.";
                }
                else if (groupRejection == null && suggestedAuthor?.Id <= 0)
                {
                    groupRejection = "Suggested author could not be added. Add the author first or select a local author/book.";
                }

                if (groupRejection == null && explicitMaterialization == null)
                {
                    allowedBooks = ResolveSuggestedWorkBooks(
                        suggestedAuthor,
                        first.Request.ForeignBookId,
                        first.MediaType,
                        out groupRejection);
                }

                FileMatchResult groupedResult = null;
                if (groupRejection == null && explicitMaterialization != null)
                {
                    groupedResult = new FileMatchResult
                    {
                        MatchedFiles = groupFiles.Select(file => new FileMatch
                        {
                            File = file.Discovered,
                            AuthorId = explicitMaterialization.Author.Id,
                            AuthorName = explicitMaterialization.Author.Name,
                            BookId = explicitMaterialization.Book.Id,
                            BookTitle = explicitMaterialization.Book.Title,
                            EditionId = explicitMaterialization.Edition.Id,
                            MatchedVia = "user_metadata_selection",
                            Provenance = MatchProvenance.UserMetadataSelection(
                                explicitMaterialization.Author,
                                explicitMaterialization.Book,
                                explicitMaterialization.Edition)
                        }).ToArray()
                    };
                }
                else if (groupRejection == null)
                {
                    var context = MatchingContextPresets.ForManualPreview();
                    context.AllowV5Identification = false;
                    context.AllowGroupedV5Suggestions = false;
                    context.HardAllowedBookIds = allowedBooks.Select(book => book.Id).ToList();
                    groupedResult = _fileMatchingService.MatchFilesToLibraryAsync(
                            groupFiles.Select(file => file.Discovered).ToArray(),
                            suggestedAuthor.Id,
                            context)
                        .GetAwaiter()
                        .GetResult();
                }

                var matchesByPath = (groupedResult?.MatchedFiles ?? Array.Empty<FileMatch>())
                    .Where(match => match?.File?.Path != null)
                    .GroupBy(match => match.File.Path, PathEqualityComparer.Instance)
                    .ToDictionary(group => group.Key, group => group.First(), PathEqualityComparer.Instance);
                foreach (var prepared in groupFiles)
                {
                    matchesByPath.TryGetValue(prepared.ActualPath, out var match);
                    suggestedResolutionByPath[prepared.Request.Path] = new SuggestedExecutionResolution
                    {
                        Author = suggestedAuthor,
                        Match = match,
                        RawTags = prepared.RawTags,
                        DurationSeconds = prepared.Discovered.DurationSeconds,
                        AllowedBookIds = allowedBooks?.Select(book => book.Id).ToHashSet(),
                        RejectionReason = groupRejection ?? (match == null
                            ? $"Local grouped matching could not match '{Path.GetFileName(prepared.ActualPath)}' inside provider work '{prepared.Request.ForeignBookId}'."
                            : null)
                    };
                }
            }

            var disableReleaseSwitchingBookIds = new HashSet<int>();

            foreach (var file in message.Files)
            {
                _logger.ProgressTrace("Processing file {0} of {1}", fileCount + 1, message.Files.Count);

                var requestedPath = file.Path;
                var fileInfo = _diskProvider.GetFileInfo(requestedPath);
                var actualPath = fileInfo.FullName ?? requestedPath;

                if (fileInfo.Exists && !actualPath.PathEquals(requestedPath))
                {
                    _logger.Warn("Manual import recovered file path '{0}' as '{1}' during execution", requestedPath, actualPath);
                }

                var fileRootFolder = _rootFolderService.GetBestRootFolder(actualPath);
                var mediaType = GetMediaTypeFromPath(actualPath);

                var localTrack = new LocalBook
                {
                    ExistingFile = fileRootFolder != null,
                    IsManualImport = true,
                    Path = actualPath,
                    Part = 1,
                    PartCount = 1,
                    Size = fileInfo.Length,
                    Modified = fileInfo.LastWriteTimeUtc,
                    Quality = file.Quality,
                    IndexerFlags = (IndexerFlags)file.IndexerFlags
                };

                var importDecision = new ImportDecision<LocalBook>(localTrack);

                Author author = null;
                Book book = null;
                Edition edition = null;
                var isExplicitLocalSelection = file.AuthorId > 0 && file.BookId > 0;

                if (isExplicitLocalSelection)
                {
                    try
                    {
                        author = _authorService.GetAuthor(file.AuthorId);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Failed to load author {0} for manual import file '{1}'", file.AuthorId, actualPath);
                    }

                    try
                    {
                        book = _bookService.GetBook(file.BookId);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Failed to load book {0} for manual import file '{1}'", file.BookId, actualPath);
                    }

                    if (file.EditionId > 0)
                    {
                        try
                        {
                            edition = _editionService.GetEdition(file.EditionId);
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug(ex, "Failed to load edition {0} for manual import file '{1}'", file.EditionId, actualPath);
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(file.ForeignAuthorId))
                {
                    suggestedResolutionByPath.TryGetValue(file.Path, out var resolution);
                    if (resolution == null || !string.IsNullOrWhiteSpace(resolution.RejectionReason))
                    {
                        importDecision.Reject(new Rejection(
                            resolution?.RejectionReason ??
                            "Suggested metadata could not be resolved as a grouped local match. Select a local author/book manually."));
                        decisions.Add(importDecision);
                        fileCount += 1;
                        continue;
                    }

                    author = resolution.Author;
                    var localMatch = resolution.Match;
                    localTrack.RawTags = resolution.RawTags;
                    localTrack.DurationSeconds = resolution.DurationSeconds;
                    localTrack.MatchProvenance = localMatch?.Provenance;
                    if (localMatch?.BookId > 0)
                    {
                        book = _bookService.GetBook(localMatch.BookId);
                        if (localMatch.EditionId > 0)
                        {
                            edition = _editionService.GetEdition(localMatch.EditionId);
                        }

                        if (!SuggestedLocalMatchMatchesSuggestion(
                                localMatch,
                                author,
                                book,
                                file.ForeignBookId,
                                out var suggestedMatchRejection,
                                resolution.AllowedBookIds))
                        {
                            importDecision.Reject(new Rejection(suggestedMatchRejection));
                            decisions.Add(importDecision);
                            fileCount += 1;
                            continue;
                        }

                        if (!SuggestedLocalMatchEditionMatchesBook(edition, book, localMatch.EditionId, out var suggestedEditionRejection))
                        {
                            importDecision.Reject(new Rejection(suggestedEditionRejection));
                            decisions.Add(importDecision);
                            fileCount += 1;
                            continue;
                        }
                    }
                }
                else
                {
                    importDecision.Reject(new Rejection("Author and book must be selected before importing"));
                    decisions.Add(importDecision);
                    fileCount += 1;
                    continue;
                }

                if (author == null || book == null)
                {
                    if (isExplicitLocalSelection)
                    {
                        throw new InvalidOperationException($"Selected author/book could not be found for manual import file '{actualPath}'.");
                    }

                    if (author != null)
                    {
                        importDecision.Reject(new Rejection($"Local matching could not match '{Path.GetFileName(actualPath)}' after adding '{author.Name}'. Select a local book manually to override."));
                    }
                    else
                    {
                        importDecision.Reject(new Rejection("Selected author or book could not be found"));
                    }

                    decisions.Add(importDecision);
                    fileCount += 1;
                    continue;
                }

                if (isExplicitLocalSelection &&
                    !ManualEditionSelectionMatchesBook(edition, book, file.EditionId, out var manualEditionRejection))
                {
                    throw new InvalidOperationException(manualEditionRejection);
                }

                if (!ManualSelectionMatchesAuthorBook(author, book, out var manualSelectionRejection))
                {
                    if (isExplicitLocalSelection)
                    {
                        throw new InvalidOperationException(manualSelectionRejection);
                    }

                    importDecision.Reject(new Rejection(manualSelectionRejection));
                    decisions.Add(importDecision);
                    fileCount += 1;
                    continue;
                }

                if (edition == null)
                {
                    var missingEditionMessage = $"Manual import resolved book '{book.Title}' without an edition for file '{actualPath}'.";
                    if (isExplicitLocalSelection)
                    {
                        throw new InvalidOperationException(missingEditionMessage);
                    }

                    importDecision.Reject(new Rejection(missingEditionMessage));
                    decisions.Add(importDecision);
                    fileCount += 1;
                    continue;
                }

                if (!EditionBelongsToBook(edition, book))
                {
                    var wrongEditionMessage = $"Manual import resolved edition '{edition.Title}' for file '{actualPath}', but it does not belong to selected book '{book.Title}'.";
                    if (isExplicitLocalSelection)
                    {
                        throw new InvalidOperationException(wrongEditionMessage);
                    }

                    importDecision.Reject(new Rejection(wrongEditionMessage));
                    decisions.Add(importDecision);
                    fileCount += 1;
                    continue;
                }

                localTrack.Author = author;
                localTrack.Book = book;
                localTrack.Edition = edition;
                PopulateTagsAndDuration(localTrack, fileInfo);

                if (file.DisableReleaseSwitching && book.Id > 0)
                {
                    disableReleaseSwitchingBookIds.Add(book.Id);
                }

                if (_rootFolderService.GetBestRootFolder(author.Path) == null)
                {
                    _logger.Warn($"Destination author folder {author.Path} not in a Root Folder, skipping import");
                    importDecision.Reject(new Rejection($"Destination author folder {author.Path} is not in a Root Folder"));
                }

                decisions.Add(importDecision);
                fileCount += 1;
            }

            // turn off anyReleaseOk if specified
            foreach (var bookId in disableReleaseSwitchingBookIds)
            {
                if (bookId <= 0)
                {
                    continue;
                }

                try
                {
                    var book = _bookService.GetBook(bookId);
                    if (book == null)
                    {
                        _logger.Warn("Manual import requested DisableReleaseSwitching but book was not found (BookId={0})", bookId);
                    }
                    else
                    {
                        book.AnyEditionOk = false;
                        _bookService.UpdateBook(book);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Manual import failed to disable release switching for BookId={0}", bookId);
                }
            }

            // Only audiobook files should be treated as multipart. Alternate ebook formats
            // (epub/mobi/kepub) remain single-part files even when they map to the same book.
            PartAssignmentHelper.NormalizeLocalBooksByEdition(
                decisions
                    .Where(d => d.Item?.Book?.Id > 0)
                    .Select(d => d.Item)
                    .ToList());

            var explicitDownloadIds = message.Files
                .Select(file => file.DownloadId)
                .Where(id => id.IsNotNullOrWhiteSpace())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            TrackedDownload associatedDownload = null;
            string associationReason;

            if (explicitDownloadIds.Count == 1)
            {
                associatedDownload = _trackedDownloadService.Find(explicitDownloadIds[0]);
                associationReason = associatedDownload == null
                    ? "MANUAL_IMPORT_EXPLICIT_DOWNLOAD_NOT_TRACKED"
                    : "MANUAL_IMPORT_EXPLICIT_DOWNLOAD_ID";
            }
            else if (explicitDownloadIds.Count > 1)
            {
                associationReason = "MANUAL_IMPORT_MULTIPLE_EXPLICIT_DOWNLOAD_IDS";
            }
            else
            {
                associatedDownload = ResolveUniqueTrackedDownload(message.Files.Select(file => file.Path), out associationReason);
            }

            if (associatedDownload == null)
            {
                _logger.Debug("[MANUAL-IMPORT][DOWNLOAD-ASSOCIATION] No tracked download associated: {0}", associationReason);
                imported.AddRange(_importApprovedBooks.Import(decisions, message.ReplaceExistingFiles, null, message.ImportMode));
            }
            else
            {
                _logger.Info("[MANUAL-IMPORT][DOWNLOAD-ASSOCIATION] Associated selected paths with download '{0}': {1}",
                    associatedDownload.DownloadItem.DownloadId,
                    associationReason);
                var importMode = _downloadImportModeResolver.Resolve(message.ImportMode, associatedDownload.DownloadItem);
                var importResults = _importApprovedBooks.Import(decisions, message.ReplaceExistingFiles, associatedDownload.DownloadItem, importMode);

                imported.AddRange(importResults);

                foreach (var importResult in importResults)
                {
                    importedTrackedDownload.Add(new ManuallyImportedFile
                    {
                        TrackedDownload = associatedDownload,
                        ImportResult = importResult
                    });
                }
            }

            var importedCount = imported.Count(i => i.Result == ImportResultType.Imported);
            var failedCount = imported.Count - importedCount;
            _logger.ProgressTrace("Manually imported {0} files{1}", importedCount, failedCount > 0 ? $" ({failedCount} failed/skipped)" : string.Empty);

            foreach (var groupedTrackedDownload in importedTrackedDownload.GroupBy(i => i.TrackedDownload.DownloadItem.DownloadId).ToList())
            {
                var trackedDownload = groupedTrackedDownload.First().TrackedDownload;

                var allItemsImported = AreAllTrackedDownloadItemsImported(
                    trackedDownload,
                    groupedTrackedDownload.Select(c => c.ImportResult).ToList(),
                    message.PreviewFiles);

                if (allItemsImported)
                {
                    trackedDownload.State = TrackedDownloadState.Imported;
                    var importedAuthorId = groupedTrackedDownload
                        .Select(item => item.ImportResult)
                        .Where(result => result.Result == ImportResultType.Imported)
                        .Select(result => result.ImportDecision?.Item?.Author?.Id ?? result.ImportDecision?.Item?.Book?.AuthorId ?? 0)
                        .FirstOrDefault(id => id > 0);
                    _eventAggregator.PublishEvent(new DownloadCompletedEvent(trackedDownload, importedAuthorId));
                }
            }
        }

        private TrackedDownload ResolveUniqueTrackedDownload(IEnumerable<string> sourcePaths, out string reason)
        {
            var trackedDownloads = _trackedDownloadService.GetTrackedDownloads() ?? new List<TrackedDownload>();
            foreach (var trackedDownload in trackedDownloads)
            {
                try
                {
                    _downloadClientFileSnapshotService.ApplySnapshot(trackedDownload?.DownloadItem);
                    _downloadClientFileSnapshotService.ApplySnapshot(trackedDownload?.ImportItem);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "[MANUAL-IMPORT][DOWNLOAD-ASSOCIATION] Failed to apply file snapshot for candidate download");
                }
            }

            return FindUniqueTrackedDownloadBySourcePaths(sourcePaths, trackedDownloads, out reason);
        }

        internal static TrackedDownload FindUniqueTrackedDownloadBySourcePaths(
            IEnumerable<string> sourcePaths,
            IEnumerable<TrackedDownload> trackedDownloads,
            out string reason)
        {
            var requestedPaths = (sourcePaths ?? Enumerable.Empty<string>())
                .Where(path => path.IsNotNullOrWhiteSpace())
                .Distinct(PathEqualityComparer.Instance)
                .ToList();
            if (requestedPaths.Count == 0)
            {
                reason = "MANUAL_IMPORT_DOWNLOAD_ASSOCIATION_NO_PATHS";
                return null;
            }

            var candidates = (trackedDownloads ?? Enumerable.Empty<TrackedDownload>())
                .Where(IsAssociationCandidate)
                .Where(download => DownloadOwnsEverySourcePath(download, requestedPaths))
                .Take(2)
                .ToList();

            if (candidates.Count == 1)
            {
                reason = "MANUAL_IMPORT_DOWNLOAD_ASSOCIATION_UNIQUE_PATH";
                return candidates[0];
            }

            reason = candidates.Count == 0
                ? "MANUAL_IMPORT_DOWNLOAD_ASSOCIATION_NOT_FOUND"
                : "MANUAL_IMPORT_DOWNLOAD_ASSOCIATION_AMBIGUOUS";
            return null;
        }

        private static bool IsAssociationCandidate(TrackedDownload trackedDownload)
        {
            return trackedDownload?.DownloadItem != null &&
                   trackedDownload.DownloadItem.DownloadId.IsNotNullOrWhiteSpace() &&
                   trackedDownload.State is TrackedDownloadState.ImportPending or
                       TrackedDownloadState.Importing or
                       TrackedDownloadState.ImportBlocked;
        }

        private static bool DownloadOwnsEverySourcePath(TrackedDownload trackedDownload, IReadOnlyCollection<string> sourcePaths)
        {
            var items = new[] { trackedDownload.DownloadItem, trackedDownload.ImportItem }
                .Where(item => item != null)
                .ToList();
            var knownFiles = items
                .SelectMany(item => item.FilePaths ?? new List<string>())
                .Where(path => path.IsNotNullOrWhiteSpace())
                .ToHashSet(PathEqualityComparer.Instance);

            // A captured client/disk file list is stronger than folder containment. If one exists,
            // every selected source must be present in that list; do not weaken it with output-path guessing.
            if (knownFiles.Count > 0)
            {
                return sourcePaths.All(knownFiles.Contains);
            }

            var outputPaths = items
                .Select(item => item.OutputPath.FullPath)
                .Where(path => path.IsNotNullOrWhiteSpace())
                .Distinct(PathEqualityComparer.Instance)
                .ToList();

            return outputPaths.Any(outputPath => sourcePaths.All(sourcePath =>
                outputPath.PathEquals(sourcePath) || outputPath.IsParentPath(sourcePath)));
        }

        internal static bool AreAllTrackedDownloadItemsImported(TrackedDownload trackedDownload, IReadOnlyCollection<ImportResult> importResults, IReadOnlyCollection<ManualImportFile> previewFiles = null)
        {
            if (importResults == null || importResults.Count == 0)
            {
                return false;
            }

            var importedResults = importResults
                .Where(result => result.Result == ImportResultType.Imported)
                .ToList();
            var expectedBooks = GetExpectedTrackedBooks(trackedDownload);
            var expectedBookIds = expectedBooks
                .Select(book => book.Id)
                .Distinct()
                .OrderBy(id => id)
                .ToList();

            if (expectedBookIds.Count == 0)
            {
                return importedResults.Count == importResults.Count;
            }

            var importedBookIds = importedResults
                .Select(result => result.ImportDecision.Item?.Book?.Id ?? 0)
                .Where(id => id > 0)
                .Distinct()
                .OrderBy(id => id)
                .ToList();

            if (importedBookIds.Count == 0 || !expectedBookIds.SequenceEqual(importedBookIds))
            {
                return false;
            }

            var expectedAudiobookBookIds = expectedBooks
                .Where(book => book.MediaType == BookMediaType.Audiobook)
                .Select(book => book.Id)
                .Distinct()
                .OrderBy(id => id)
                .ToList();

            return PreviewFilesForExpectedBooksWereImported(previewFiles, importedResults, expectedAudiobookBookIds, trackedDownload?.DownloadItem?.DownloadId);
        }

        private static bool PreviewFilesForExpectedBooksWereImported(IReadOnlyCollection<ManualImportFile> previewFiles, IReadOnlyCollection<ImportResult> importedResults, IReadOnlyCollection<int> expectedBookIds, string downloadId)
        {
            if (previewFiles == null || previewFiles.Count == 0 || expectedBookIds == null || expectedBookIds.Count == 0)
            {
                return true;
            }

            var expectedBookIdSet = expectedBookIds.ToHashSet();
            var previewPathsForExpectedBooks = previewFiles
                .Where(file => file != null &&
                               file.BookId > 0 &&
                               expectedBookIdSet.Contains(file.BookId) &&
                               file.Path.IsNotNullOrWhiteSpace() &&
                               (downloadId.IsNullOrWhiteSpace() ||
                                file.DownloadId.IsNullOrWhiteSpace() ||
                                string.Equals(file.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase)))
                .Select(file => file.Path)
                .Distinct(PathEqualityComparer.Instance)
                .ToList();

            if (!previewPathsForExpectedBooks.Any())
            {
                return true;
            }

            var importedPaths = importedResults
                .Where(result => result.Result == ImportResultType.Imported)
                .SelectMany(result => GetImportedSourcePaths(result.ImportDecision?.Item))
                .ToHashSet(PathEqualityComparer.Instance);

            return previewPathsForExpectedBooks.All(importedPaths.Contains);
        }

        private static IEnumerable<string> GetImportedSourcePaths(LocalBook item)
        {
            if (item == null)
            {
                yield break;
            }

            if (item.Path.IsNotNullOrWhiteSpace())
            {
                yield return item.Path;
            }

            if (item.GeneratedConversionSourcePaths == null)
            {
                yield break;
            }

            foreach (var path in item.GeneratedConversionSourcePaths.Where(p => p.IsNotNullOrWhiteSpace()))
            {
                yield return path;
            }
        }

        private static List<Book> GetExpectedTrackedBooks(TrackedDownload trackedDownload)
        {
            return trackedDownload?.RemoteBook?
                .GetBooksMatchingReleaseMediaType()
                .Where(book => book != null && book.Id > 0)
                .Distinct()
                .ToList()
                   ?? new List<Book>();
        }

        private void PopulateTagsAndDuration(LocalBook localBook, IFileInfo fileInfo)
        {
            if (localBook == null || fileInfo == null || !fileInfo.Exists)
            {
                return;
            }

            if (localBook.RawTags?.AllTags != null && localBook.DurationSeconds.HasValue)
            {
                return;
            }

            try
            {
                var (allTags, durationSeconds) = _metadataTagService.ReadAllTagsAndDuration(fileInfo);
                localBook.RawTags ??= new RawFileTags { AllTags = allTags ?? new Dictionary<string, List<string>>() };
                localBook.DurationSeconds ??= durationSeconds;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to read tags/duration for manual import file '{0}'", fileInfo.FullName);
            }
        }

        internal static bool SuggestedLocalMatchMatchesSuggestion(
            FileMatch localMatch,
            Author suggestedAuthor,
            Book matchedBook,
            string suggestedForeignBookId,
            out string rejectionReason,
            IReadOnlySet<int> providerResolvedBookIds = null)
        {
            rejectionReason = null;

            if (localMatch == null || localMatch.BookId <= 0)
            {
                rejectionReason = "Local matching did not find a book after adding the suggested author. Select a local book manually to override.";
                return false;
            }

            if (suggestedAuthor == null || suggestedAuthor.Id <= 0)
            {
                rejectionReason = "Suggested author could not be resolved. Select a local author/book manually to override.";
                return false;
            }

            if (matchedBook == null)
            {
                rejectionReason = $"Local matching found book id {localMatch.BookId}, but the book could not be loaded. Select a local book manually to override.";
                return false;
            }

            if (localMatch.AuthorId > 0 && localMatch.AuthorId != suggestedAuthor.Id)
            {
                rejectionReason = $"Local matching found '{localMatch.AuthorName ?? "unknown author"} - {localMatch.BookTitle ?? matchedBook.Title}', which does not belong to suggested author '{suggestedAuthor.Name}'. Select a local author/book manually to override.";
                return false;
            }

            if (matchedBook.AuthorId > 0 && matchedBook.AuthorId != suggestedAuthor.Id)
            {
                rejectionReason = $"Local matching found '{matchedBook.Title}', but that book belongs to a different local author than suggested author '{suggestedAuthor.Name}'. Select a local author/book manually to override.";
                return false;
            }

            if (providerResolvedBookIds?.Count > 0 && !providerResolvedBookIds.Contains(matchedBook.Id))
            {
                rejectionReason = $"Local matching found '{matchedBook.Title}', but it is outside the provider-resolved work boundary. Select a local book manually to override.";
                return false;
            }

            if ((providerResolvedBookIds == null || providerResolvedBookIds.Count == 0) &&
                !suggestedForeignBookId.IsNullOrWhiteSpace() &&
                !BookEditionIdentity.HasCanonicalWorkProviderId(matchedBook, suggestedForeignBookId))
            {
                rejectionReason = $"Local matching found '{matchedBook.Title}', but it does not match suggested metadata work '{suggestedForeignBookId}'. Select a local book manually to override.";
                return false;
            }

            return true;
        }

        internal static bool ManualSelectionMatchesAuthorBook(Author author, Book book, out string rejectionReason)
        {
            rejectionReason = null;

            if (author == null || book == null)
            {
                return true;
            }

            if (book.AuthorId > 0 && book.AuthorId != author.Id)
            {
                rejectionReason = $"Selected book '{book.Title}' does not belong to selected author '{author.Name}'. Select a matching local author/book.";
                return false;
            }

            return true;
        }

        internal static bool ManualEditionSelectionMatchesBook(Edition edition, Book book, int requestedEditionId, out string rejectionReason)
        {
            rejectionReason = null;

            if (book == null)
            {
                return true;
            }

            if (requestedEditionId <= 0)
            {
                rejectionReason = $"Edition must be selected for '{book.Title}'.";
                return false;
            }

            if (edition == null)
            {
                rejectionReason = $"Selected edition {requestedEditionId} could not be found for '{book.Title}'. Refresh metadata and select the edition again.";
                return false;
            }

            if (!EditionBelongsToBook(edition, book))
            {
                rejectionReason = $"Selected edition '{edition.Title}' does not belong to selected book '{book.Title}'. Select an edition from the selected book.";
                return false;
            }

            return true;
        }

        internal static bool SuggestedLocalMatchEditionMatchesBook(Edition edition, Book book, int matchedEditionId, out string rejectionReason)
        {
            rejectionReason = null;

            if (book == null)
            {
                return true;
            }

            if (matchedEditionId <= 0 || edition == null)
            {
                rejectionReason = $"Local matching found '{book.Title}', but did not resolve a local edition. Select a local book/edition manually to override.";
                return false;
            }

            if (!EditionBelongsToBook(edition, book))
            {
                rejectionReason = $"Local matching found edition '{edition.Title}', but it does not belong to matched book '{book.Title}'. Select a local book/edition manually to override.";
                return false;
            }

            return true;
        }

        internal static bool EditionBelongsToBook(Edition edition, Book book)
        {
            if (edition == null || book == null)
            {
                return true;
            }

            return edition.BookId == book.Id;
        }

    }
}
