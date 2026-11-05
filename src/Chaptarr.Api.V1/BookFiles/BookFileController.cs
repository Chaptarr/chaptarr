using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http;
using Chaptarr.Http.Middleware;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;
using NzbDrone.Http.REST.Attributes;
using NzbDrone.SignalR;
using BadRequestException = NzbDrone.Core.Exceptions.BadRequestException;
using HttpStatusCode = System.Net.HttpStatusCode;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation;

namespace Chaptarr.Api.V1.BookFiles
{
    [V1ApiController]
    public class BookFileController : RestControllerWithSignalR<BookFileResource, BookFile>,
                                 IHandle<BookFileAddedEvent>,
                                 IHandle<BookFileUpdatedEvent>,
                                 IHandle<BookFilesAddedEvent>,
                                 IHandle<ImportSummaryEvent>,
                                 IHandle<ImportStageProgressEvent>,
                                 IHandle<CommandExecutedEvent>,
                                 IHandle<BookFileDeletedEvent>
    {
        private readonly IMediaFileService _mediaFileService;
        private readonly IDeleteMediaFiles _mediaFileDeletionService;
        private readonly IMetadataTagService _metadataTagService;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IBookshelfLegacyIdentityMapService _bookshelfLegacyIdentityMapService;
	        private readonly IUpgradableSpecification _upgradableSpecification;
	        private readonly IRootFolderService _rootFolderService;
	        private readonly Logger _logger;
	        private readonly object _authorBroadcastLock = new object();
	        private int? _lastBroadcastAuthorId;
	        private readonly object _importStateLock = new object();
	        private readonly HashSet<int> _activeImportCommands = new HashSet<int>();

        public BookFileController(IBroadcastSignalRMessage signalRBroadcaster,
                               IMediaFileService mediaFileService,
                               IDeleteMediaFiles mediaFileDeletionService,
                               IMetadataTagService metadataTagService,
                               IAuthorService authorService,
                               IBookService bookService,
                               IUpgradableSpecification upgradableSpecification,
                               IRootFolderService rootFolderService,
                               Logger logger,
                               IBookshelfLegacyIdentityMapService bookshelfLegacyIdentityMapService = null)
            : base(signalRBroadcaster)
        {
            _mediaFileService = mediaFileService;
            _mediaFileDeletionService = mediaFileDeletionService;
            _metadataTagService = metadataTagService;
            _authorService = authorService;
            _bookService = bookService;
            _upgradableSpecification = upgradableSpecification;
            _rootFolderService = rootFolderService;
            _logger = logger;
            _bookshelfLegacyIdentityMapService = bookshelfLegacyIdentityMapService;
        }

        private BookFileResource MapToResource(BookFile bookFile)
        {
            BookFileResource resource;
            if (bookFile.EditionId > 0 && bookFile.Author != null)
            {
                resource = bookFile.ToResource(bookFile.Author, _upgradableSpecification, HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility == true);
            }
            else
            {
                resource = bookFile.ToResource(HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility == true);
            }

            ProjectLegacyBookFileResource(resource);
            return resource;
        }

        private BookFileResource MapToResource(BookFile bookFile, NzbDrone.Core.Books.Author author)
        {
            var resource = bookFile.ToResource(author, _upgradableSpecification, HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility == true);
            ProjectLegacyBookFileResource(resource);
            return resource;
        }

        private void ProjectLegacyBookFileResource(BookFileResource resource)
        {
            if (resource == null || HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility != true ||
                _bookshelfLegacyIdentityMapService == null)
            {
                return;
            }

            var fileMapping = _bookshelfLegacyIdentityMapService
                .GetMappingsByTargetIds("BookFile", new[] { resource.Id })
                .FirstOrDefault();
            if (fileMapping != null)
            {
                resource.Id = fileMapping.SourceId;
            }

            var bookMapping = _bookshelfLegacyIdentityMapService
                .GetMappingsByTargetIds("Book", new[] { resource.BookId })
                .FirstOrDefault();
            if (bookMapping != null)
            {
                resource.BookId = bookMapping.SourceId;
            }
        }

        private int ResolveLegacyBookFileId(int sourceId)
        {
            if (HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility != true ||
                _bookshelfLegacyIdentityMapService == null)
            {
                return sourceId;
            }

            return _bookshelfLegacyIdentityMapService.GetMappings("BookFile", new[] { sourceId })
                .FirstOrDefault()?.TargetId ?? sourceId;
        }

        private List<int> ResolveLegacyBookFileIds(IEnumerable<int> sourceIds)
        {
            return (sourceIds ?? Enumerable.Empty<int>())
                .Where(id => id > 0)
                .Distinct()
                .Select(ResolveLegacyBookFileId)
                .ToList();
        }

        private List<BookFileResource> MapUnmappedResources(List<BookFile> files)
        {
            var resources = files.ConvertAll(MapToResource);

            try
            {
                var resourcesById = resources.ToDictionary(resource => resource.Id);
                var units = BookImportUnitGroupingService.BuildUnmappedUnits(
                    files,
                    path => _rootFolderService?.GetBestRootFolderPath(path));

                foreach (var unit in units)
                {
                    foreach (var file in unit.Files)
                    {
                        if (!resourcesById.TryGetValue(file.Id, out var resource))
                        {
                            continue;
                        }

                        resource.ImportUnitKey = unit.Key;
                        resource.ImportUnitRoot = unit.RootPath;
                    }
                }
            }
            catch (Exception ex)
            {
                // Resource mapping already assigned a unique per-file fallback. Never let a
                // grouping failure turn the entire unmapped page into a 500 response.
                _logger.Warn(ex, "Failed to build unmapped import units; returning per-file units");
            }

            return resources;
        }

        private void EnsureAuthorLoaded(BookFile bookFile)
        {
            if (bookFile == null) return;

            // Ensure Author is loaded if we have an Edition
            if (bookFile.Author == null && bookFile.EditionId > 0)
            {
                var books = _bookService.GetBooksByFileIds(new[] { bookFile.Id });
                if (books.Any())
                {
                    var book = books.First();
                    bookFile.Author = _authorService.GetAuthor(book.AuthorId);
                }
            }
        }

        private void BroadcastUpdated(BookFile bookFile)
        {
            EnsureAuthorLoaded(bookFile);
            BroadcastResourceChange(ModelAction.Updated, MapToResource(bookFile));
        }

	        private bool ShouldBroadcastForAuthor(int? authorId)
	        {
	            if (!authorId.HasValue || authorId.Value <= 0)
	            {
	                return false;
	            }

	            lock (_authorBroadcastLock)
	            {
	                if (_lastBroadcastAuthorId != authorId.Value)
	                {
	                    _lastBroadcastAuthorId = authorId.Value;
	                    return true;
	                }

	                return false;
	            }
	        }

	        private bool IsImportActive()
	        {
	            lock (_importStateLock)
	            {
	                return _activeImportCommands.Count > 0;
	            }
	        }

        private void TryBroadcastForFile(BookFile bookFile)
        {
            EnsureAuthorLoaded(bookFile);
            var authorId = bookFile?.Author?.Id;
            if (ShouldBroadcastForAuthor(authorId))
            {
                _logger.Debug("[UI-BROADCAST] Author changed to '{0}' (ID: {1}); broadcasting update once for this author", bookFile?.Author?.Name, authorId);
                BroadcastResourceChange(ModelAction.Updated, MapToResource(bookFile));
            }
            else
            {
                _logger.Debug("[UI-BROADCAST] Skipping broadcast for author '{0}' (ID: {1}); already current", bookFile?.Author?.Name, authorId);
            }
        }

        protected override BookFileResource GetResourceById(int id)
        {
            var bookFile = _mediaFileService.Get(ResolveLegacyBookFileId(id));
            var resource = MapToResource(bookFile);
            if (resource == null || string.IsNullOrWhiteSpace(resource.Path))
            {
                return resource;
            }

            // Prefer persisted tags from DB; fall back to disk read for legacy files without AllTags.
            // Keep the private Bookshelf compatibility projection available to AudioTags while
            // omitting it from the field-agnostic public Tags map.
            Dictionary<string, List<string>> audioTags;
            if (bookFile?.AllTags != null && bookFile.AllTags.Count > 0)
            {
                audioTags = bookFile.AllTags;
            }
            else
            {
                try
                {
                    var fileInfo = new global::System.IO.Abstractions.FileSystem().FileInfo.New(resource.Path);
                    audioTags = fileInfo.Exists
                        ? _metadataTagService.ReadAllTags(fileInfo) ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Failed to read tags for book file: {0}", resource.Path);
                    audioTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                }
            }

            resource.Tags = TagExclusionPolicy.ToPublicTags(audioTags);

            // Keep the BookshelfNG v1 response field for existing clients while the richer
            // field-agnostic tag map remains available through Tags.
            resource.AudioTags = BookshelfAudioTagsCompatibilityMapper.ToParsedTrackInfo(bookFile, audioTags);

            return resource;
        }

        protected override bool ShouldBroadcastSyncForResourceId(int id)
        {
            if (HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility == true)
            {
                return true;
            }

            return _bookshelfLegacyIdentityMapService?.GetMappingsByTargetIds("BookFile", new[] { id }).Count > 0;
        }

        [HttpGet("{id:int}/download")]
        public ActionResult DownloadBookFile(int id)
        {
            var bookFile = _mediaFileService.Get(ResolveLegacyBookFileId(id));

            if (bookFile == null || bookFile.Path.IsNullOrWhiteSpace())
            {
                return NotFound();
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(bookFile.Path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return NotFound();
            }

            if (!IsInsideRootFolder(fullPath))
            {
                _logger.Warn("Refusing to serve book file {0}: path is outside every root folder", id);
                return StatusCode((int)HttpStatusCode.Forbidden);
            }

            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return NotFound();
            }

            // Links anywhere in the path (file or parent directories) must not expose
            // anything outside the library.
            var rootPath = _rootFolderService.GetBestRootFolder(fullPath).Path;
            var realRoot = ResolveRealPath(Path.GetFullPath(rootPath));
            var realFile = ResolveRealPath(fullPath);

            if (realRoot == null || realFile == null ||
                !realFile.StartsWith(realRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                !new FileInfo(realFile).Exists)
            {
                _logger.Warn("Refusing to serve book file {0}: resolved path is outside its root folder", id);
                return StatusCode((int)HttpStatusCode.Forbidden);
            }

            if (!new FileExtensionContentTypeProvider().TryGetContentType(fullPath, out var contentType))
            {
                contentType = "application/octet-stream";
            }

            return PhysicalFile(realFile, contentType, Path.GetFileName(fullPath), enableRangeProcessing: true);
        }

        private static string ResolveRealPath(string fullPath)
        {
            try
            {
                var current = Path.GetPathRoot(fullPath);
                var rest = fullPath.Substring(current.Length);

                foreach (var segment in rest.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, segment);
                    var info = new FileInfo(current);

                    if (info.LinkTarget != null)
                    {
                        var target = info.ResolveLinkTarget(true);
                        if (target == null)
                        {
                            return null;
                        }

                        current = target.FullName;
                    }
                }

                return current;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private bool IsInsideRootFolder(string fullPath)
        {
            return _rootFolderService?.GetBestRootFolder(fullPath) != null;
        }

        [HttpGet]
        public List<BookFileResource> GetBookFiles(int? authorId, [FromQuery] List<int> bookFileIds, [FromQuery(Name = "bookId")] List<int> bookIds, bool? unmapped, string mediaType = null)
        {
            mediaType ??= HttpContext?.GetReadarrFacadeContext()?.MediaType;
            bookFileIds ??= new List<int>();
            bookIds ??= new List<int>();
            _logger.DebugSafe($"[BOOKFILE-API] GetBookFiles called - authorId: {authorId}, bookIds: [{string.Join(",", bookIds)}], bookFileIds: [{string.Join(",", bookFileIds)}], unmapped: {unmapped}, mediaType: {mediaType}");
            var parsedMediaType = MediaTypeParameterParser.ParseOptional(mediaType);
            var normalizedMediaType = parsedMediaType.HasValue ? MediaTypeParameterParser.ToApiValue(parsedMediaType.Value) : null;
            
            if (!authorId.HasValue && !bookFileIds.Any() && !bookIds.Any() && !unmapped.HasValue)
            {
                throw new BadRequestException("authorId, bookId, bookFileIds or unmapped must be provided");
            }

            if (unmapped.HasValue && unmapped.Value)
            {
                var files = normalizedMediaType == null
                    ? _mediaFileService.GetUnmappedFiles()
                    : _mediaFileService.GetUnmappedFiles(normalizedMediaType);
                return MapUnmappedResources(files);
            }

            if (authorId.HasValue && !bookIds.Any())
            {
                var author = _authorService.GetAuthor(authorId.Value);

                // Use mediaType filtering if provided
                if (normalizedMediaType != null)
                {
                    return _mediaFileService.GetFilesByAuthorAndMediaType(authorId.Value, normalizedMediaType).ConvertAll(f => MapToResource(f, author));
                }

                return _mediaFileService.GetFilesByAuthor(authorId.Value).ConvertAll(f => MapToResource(f, author));
            }

            if (bookIds.Any())
            {
                var result = new List<BookFileResource>();
                var resolvedBookIds = HttpContext.ResolveBookshelfLegacyBookIds(_bookshelfLegacyIdentityMapService, bookIds, parsedMediaType);
                foreach (var bookId in resolvedBookIds)
                {
                    var book = _bookService.GetBook(bookId);
                    if (book == null || (parsedMediaType.HasValue && book.MediaType != parsedMediaType.Value))
                    {
                        continue;
                    }

                    var bookAuthor = _authorService.GetAuthor(book.AuthorId);
                    var bookFiles = _mediaFileService.GetFilesByBook(book.Id);
                    _logger.Debug($"[BOOKFILE-API] Found {bookFiles.Count} files for bookId: {bookId} (Book: {book.Title} by {bookAuthor?.Name})");
                    result.AddRange(bookFiles.ConvertAll(f => MapToResource(f, bookAuthor)));
                }

                _logger.Debug($"[BOOKFILE-API] Returning {result.Count} total files for {bookIds.Count} books");
                return result;
            }
            else
            {
                // trackfiles will come back with the author already populated
                var bookFiles = _mediaFileService.Get(ResolveLegacyBookFileIds(bookFileIds));
                return bookFiles.ConvertAll(e => MapToResource(e));
            }
        }

        [RestPutById]
        public ActionResult<BookFileResource> SetQuality([FromBody] BookFileResource bookFileResource)
        {
            var sourceFileId = bookFileResource.Id;
            var bookFile = _mediaFileService.Get(ResolveLegacyBookFileId(sourceFileId));
            bookFile.Quality = bookFileResource.Quality;
            _mediaFileService.Update(bookFile);

            if (HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility == true)
            {
                var resource = MapToResource(_mediaFileService.Get(bookFile.Id));
                return AcceptedAtAction(
                    nameof(GetResourceByIdWithErrorHandler),
                    new { id = sourceFileId },
                    resource);
            }

            return Accepted(bookFile.Id);
        }

        [HttpPut("editor")]
        public IActionResult SetQuality([FromBody] BookFileListResource resource)
        {
            var bookFiles = _mediaFileService.Get(ResolveLegacyBookFileIds(resource.BookFileIds));

            foreach (var bookFile in bookFiles)
            {
                if (resource.Quality != null)
                {
                    bookFile.Quality = resource.Quality;
                }
            }

            _mediaFileService.Update(bookFiles);

            return Accepted(bookFiles.ConvertAll(f => MapToResource(f, bookFiles.First().Author)));
        }

        [RestDeleteById]
        public void DeleteBookFile(int id)
        {
            var bookFile = _mediaFileService.Get(ResolveLegacyBookFileId(id));

            if (bookFile == null)
            {
                throw new NzbDroneClientException(HttpStatusCode.NotFound, "Book file not found");
            }

            if (bookFile.EditionId > 0 && bookFile.Author != null)
            {
                _mediaFileDeletionService.DeleteTrackFile(bookFile.Author, bookFile);
            }
            else
            {
                _mediaFileDeletionService.DeleteTrackFile(bookFile, "Unmapped_Files");
            }
        }

        [HttpDelete("bulk")]
        public object DeleteTrackFiles([FromBody] BookFileListResource resource)
        {
            var bookFiles = _mediaFileService.Get(ResolveLegacyBookFileIds(resource.BookFileIds));

            foreach (var bookFile in bookFiles)
            {
                if (bookFile.EditionId > 0 && bookFile.Author != null)
                {
                    _mediaFileDeletionService.DeleteTrackFile(bookFile.Author, bookFile);
                }
                else
                {
                    _mediaFileDeletionService.DeleteTrackFile(bookFile, "Unmapped_Files");
                }
            }

            return new { };
        }

	        [NonAction]
	        public void Handle(BookFileAddedEvent message)
	        {
	            if (IsImportActive())
	            {
	                return;
	            }

	            try
	            {
	                TryBroadcastForFile(message.BookFile);
            }
            catch (Exception ex)
            {
                // Log the error with full details to help diagnose
                _logger.Error(ex, "BookFileController failed processing BookFileAddedEvent - BookFile ID: {0}, Path: {1}, EditionId: {2}", message.BookFile?.Id, message.BookFile?.Path, message.BookFile?.EditionId);

                _logger.Error("Additional details - Author is null: {0}, Edition is null: {1}",
                    message.BookFile?.Author == null,
                    message.BookFile?.Edition == null);

                // Re-throw to maintain existing behavior
                throw;
            }
        }

	        [NonAction]
	        public void Handle(BookFileDeletedEvent message)
	        {
	            if (IsImportActive())
	            {
	                return;
	            }

	            BroadcastResourceChange(ModelAction.Deleted, MapToResource(message.BookFile));
	        }

            [NonAction]
            public void Handle(BookFileUpdatedEvent message)
            {
                try
                {
                    BroadcastUpdated(message.BookFile);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "BookFileController failed processing BookFileUpdatedEvent - BookFile ID: {0}, Path: {1}", message.BookFile?.Id, message.BookFile?.Path);
                    throw;
                }
            }

	        [NonAction]
	        public void Handle(BookFilesAddedEvent message)
	        {
	            if (IsImportActive())
	            {
	                return;
	            }

	            if (message?.BookFiles == null || message.BookFiles.Count == 0)
	            {
	                return;
	            }

            // Choose a representative file for this author group; broadcast only if author changed
            // Prefer a file that already has Author populated.
            var candidate = message.BookFiles.FirstOrDefault(f => f?.Author != null) ?? message.BookFiles.First();
            try
            {
                TryBroadcastForFile(candidate);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "BookFileController failed processing BookFilesAddedEvent (batch) - Candidate BookFile ID: {0}, Path: {1}, EditionId: {2}", candidate?.Id, candidate?.Path, candidate?.EditionId);
            }
        }

        [NonAction]
        public void Handle(ImportSummaryEvent message)
        {
            // Reset between import sessions
            lock (_authorBroadcastLock)
            {
                _lastBroadcastAuthorId = null;
            }
            _logger.Debug("[UI-BROADCAST] Reset last author after ImportSummaryEvent for folder: {0}", message.FolderPath);
        }

	        [NonAction]
	        public void Handle(ImportStageProgressEvent message)
	        {
	            if (message.CommandId.HasValue)
	            {
	                lock (_importStateLock)
	                {
	                    if (message.Stage == ImportStage.ImportComplete)
	                    {
	                        _activeImportCommands.Remove(message.CommandId.Value);
	                    }
	                    else
	                    {
	                        _activeImportCommands.Add(message.CommandId.Value);
	                    }
	                }
	            }

	            if (message.Stage == ImportStage.ImportComplete)
	            {
	                lock (_authorBroadcastLock)
	                {
	                    _lastBroadcastAuthorId = null;
	                }
	                BroadcastResourceChange(ModelAction.Sync);
		                _logger.Debug("[UI-BROADCAST] Reset last author on ImportComplete stage");
		            }
		        }

	        [NonAction]
	        public void Handle(CommandExecutedEvent message)
	        {
	            var commandId = message?.Command?.Id ?? 0;
	            if (commandId <= 0)
	            {
	                return;
	            }

	            var shouldSync = false;
	            lock (_importStateLock)
	            {
	                if (_activeImportCommands.Remove(commandId))
	                {
	                    shouldSync = _activeImportCommands.Count == 0;
	                }
	            }

	            if (shouldSync)
	            {
	                lock (_authorBroadcastLock)
	                {
	                    _lastBroadcastAuthorId = null;
	                }

	                BroadcastResourceChange(ModelAction.Sync);
	                _logger.Debug("[UI-BROADCAST] Reset last author when import command {0} completed", commandId);
	            }
	        }
	    }
	}
