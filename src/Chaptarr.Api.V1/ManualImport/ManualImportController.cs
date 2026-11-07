using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http;
using Chaptarr.Http.Middleware;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Api.V1.ManualImport
{
    [V1ApiController]
    public class ManualImportController : Controller
    {
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IManualImportService _manualImportService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IBookshelfLegacyIdentityMapService _legacyIdentityMapService;
        private readonly Logger _logger;

        public ManualImportController(IManualImportService manualImportService,
                                  IAuthorService authorService,
                                  IEditionService editionService,
                                  IBookService bookService,
                                  IMediaFileService mediaFileService,
                                  IRootFolderService rootFolderService,
                                  Logger logger,
                                  IBookshelfLegacyIdentityMapService legacyIdentityMapService = null)
        {
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _manualImportService = manualImportService;
            _mediaFileService = mediaFileService;
            _rootFolderService = rootFolderService;
            _legacyIdentityMapService = legacyIdentityMapService;
            _logger = logger;
        }

        [HttpPost] // nosemgrep: csharp.dotnet.security.mvc-missing-antiforgery.mvc-missing-antiforgery -- API fallback auth accepts API keys or bearer tokens, never browser cookies.
        [Consumes("application/json")]
        public IActionResult UpdateItems([FromBody] List<ManualImportUpdateResource> resource)
        {
            if (resource == null || resource.Count == 0)
            {
                throw new BadRequestException("Request body can't be empty");
            }

            return Accepted(UpdateImportItems(resource));
        }

        [HttpGet]
        public List<ManualImportResource> GetMediaFiles(string folder, string downloadId, int? authorId, bool filterExistingFiles = true, bool replaceExistingFiles = true, string mediaType = null, [FromQuery] List<int> bookFileIds = null)
        {
            NzbDrone.Core.Books.Author author = null;

            if (authorId > 0)
            {
                author = _authorService.GetAuthor(authorId.Value);
            }

            var filter = filterExistingFiles ? FilterFilesType.Matched : FilterFilesType.None;
            IReadOnlyCollection<string> exactPaths = null;

            if (bookFileIds?.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(folder))
                {
                    throw new BadRequestException("folder must be provided when bookFileIds are used");
                }

                if (!string.IsNullOrWhiteSpace(downloadId))
                {
                    throw new BadRequestException("bookFileIds cannot be combined with downloadId");
                }

                var normalizedMediaType = MediaTypeParameterParser.NormalizeOptional(mediaType);
                var requestedIds = bookFileIds
                    .Where(id => id > 0)
                    .ToHashSet();
                var unmappedFiles = normalizedMediaType == null
                    ? _mediaFileService.GetUnmappedFiles()
                    : _mediaFileService.GetUnmappedFiles(normalizedMediaType);
                var selectedUnits = BookImportUnitGroupingService.BuildUnmappedUnits(
                        unmappedFiles,
                        path => _rootFolderService?.GetBestRootFolderPath(path))
                    .Where(unit => unit.Files.Any(file => requestedIds.Contains(file.Id)))
                    .ToList();

                if (selectedUnits.Count > 1)
                {
                    throw new BadRequestException("bookFileIds must belong to one import unit");
                }

                if (selectedUnits.Count == 1)
                {
                    var selectedUnit = selectedUnits[0];
                    folder = selectedUnit.RootPath;
                    exactPaths = selectedUnit.Files
                        .Select(file => file.Path)
                        .Distinct(PathEqualityComparer.Instance)
                        .ToList();
                }
                else
                {
                    // Stale or invalid IDs must never fall back to scanning the supplied folder.
                    exactPaths = Array.Empty<string>();
                }
            }

            List<ManualImportResource> resources;

            try
            {
                resources = _manualImportService.GetMediaFiles(folder, downloadId, author, filter, replaceExistingFiles, HttpContext.RequestAborted, exactPaths)
                    .Select(item => AddQualityWeight(item.ToResource(HttpContext?.GetReadarrFacadeContext())))
                    .ToList();
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                _logger.Debug("Manual import preview request was cancelled by the client");
                return new List<ManualImportResource>();
            }

            if (!string.IsNullOrWhiteSpace(mediaType))
            {
                IReadOnlySet<string> allowedExtensions;
                var parsedMediaType = MediaTypeParameterParser.ParseRequired(mediaType);

                if (parsedMediaType == BookMediaType.Audiobook)
                {
                    allowedExtensions = MediaFileExtensions.AudioExtensions;
                }
                else
                {
                    allowedExtensions = MediaFileExtensions.TextExtensions;
                }

                resources = resources
                    .Where(r => allowedExtensions.Contains(Path.GetExtension(r.Path ?? string.Empty)))
                    .ToList();
            }

            ProjectLegacyIdentities(resources);
            return resources;
        }

        private ManualImportResource AddQualityWeight(ManualImportResource item)
        {
            if (item.Quality != null)
            {
                item.QualityWeight = Quality.DefaultQualityDefinitions.Single(q => q.Quality == item.Quality.Quality).Weight;
                item.QualityWeight += item.Quality.Revision.Real * 10;
                item.QualityWeight += item.Quality.Revision.Version;
            }

            return item;
        }

        private List<ManualImportResource> UpdateImportItems(List<ManualImportUpdateResource> resources)
        {
            var items = new List<ManualImportItem>();
            var facadeContext = HttpContext?.GetReadarrFacadeContext();
            foreach (var resource in resources)
            {
                var author = resource.AuthorId.HasValue ? _authorService.GetAuthor(resource.AuthorId.Value) : null;
                var mediaType = GetImportMediaType(resource.Path, facadeContext?.MediaType);
                var edition = resource.EditionId.HasValue
                    ? ResolveLocalEdition(resource.EditionId, null, mediaType)
                    : ResolveForeignEdition(resource.ForeignEditionId, null);

                if (edition != null && mediaType.HasValue &&
                    edition.IsEbook != (mediaType.Value == BookMediaType.Ebook))
                {
                    throw new BadRequestException("Selected edition does not match the requested media type.");
                }

                if (!mediaType.HasValue && edition != null)
                {
                    mediaType = edition.IsEbook ? BookMediaType.Ebook : BookMediaType.Audiobook;
                }

                var resolvedBookIds = resource.BookId.HasValue
                    ? HttpContext.ResolveBookshelfLegacyBookIds(_legacyIdentityMapService, new[] { resource.BookId.Value }, mediaType)
                    : new List<int>();
                if (resource.BookId.HasValue && resolvedBookIds.Count == 0)
                {
                    throw new BadRequestException("Book ID does not match the requested media type.");
                }

                NzbDrone.Core.Books.Book book = null;
                if (edition != null)
                {
                    if (resolvedBookIds.Count > 0 && !resolvedBookIds.Contains(edition.BookId))
                    {
                        throw new BadRequestException("Selected edition does not belong to selected book.");
                    }

                    book = _bookService.GetBook(edition.BookId);
                }
                else if (resolvedBookIds.Count == 1)
                {
                    book = _bookService.GetBook(resolvedBookIds[0]);
                }
                else if (resolvedBookIds.Count > 1)
                {
                    throw new BadRequestException("Book ID matches both audiobook and ebook. Select an edition or provide a mediaType.");
                }

                items.Add(new ManualImportItem
                {
                    Id = resource.Id,
                    Path = resource.Path,
                    Name = resource.Name,
                    Author = author,
                    Book = book,
                    Edition = edition,
                    SuggestedForeignAuthorId = resource.SuggestedForeignAuthorId,
                    SuggestedAuthorName = resource.SuggestedAuthorName,
                    SuggestedForeignBookId = resource.SuggestedForeignBookId,
                    SuggestedBookTitle = resource.SuggestedBookTitle,
                    SuggestedForeignEditionId = resource.SuggestedForeignEditionId,
                    SuggestedEditionTitle = resource.SuggestedEditionTitle,
                    Quality = resource.Quality,
                    ReleaseGroup = resource.ReleaseGroup,
                    IndexerFlags = resource.IndexerFlags,
                    DownloadId = resource.DownloadId,
                    AdditionalFile = resource.AdditionalFile,
                    ReplaceExistingFiles = resource.ReplaceExistingFiles,
                    DisableReleaseSwitching = resource.DisableReleaseSwitching,
                    Rejections = new List<Rejection>(),
                    Warnings = resource.Warnings ?? new List<string>()
                });
            }

            var updatedItems = _manualImportService.UpdateItems(items)
                .Select(item => item.ToResource(facadeContext))
                .ToList();
            ProjectLegacyIdentities(updatedItems);
            return updatedItems;
        }

        private void ProjectLegacyIdentities(List<ManualImportResource> resources)
        {
            if (HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility != true ||
                _legacyIdentityMapService == null || resources == null || resources.Count == 0)
            {
                return;
            }

            var bookIds = resources
                .SelectMany(resource => new[] { resource.Book?.Id ?? 0, resource.Edition?.BookId ?? 0 })
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            var bookMappingsByTargetId = _legacyIdentityMapService.GetMappingsByTargetIds("Book", bookIds)
                .GroupBy(mapping => mapping.TargetId)
                .ToDictionary(group => group.Key, group => group.First());
            var editionIds = resources
                .SelectMany(resource => new[] { resource.Edition?.Id ?? 0, resource.EditionId ?? 0 })
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            var editionMappingsByTargetId = _legacyIdentityMapService.GetMappingsByTargetIds("Edition", editionIds)
                .GroupBy(mapping => mapping.TargetId)
                .ToDictionary(group => group.Key, group => group.First());
            var authorMetadataMappingsByTargetId = _legacyIdentityMapService
                .GetMappingsByTargetIds("AuthorMetadata", resources
                    .SelectMany(resource => new[] { resource.Book?.AuthorId ?? 0, resource.Author?.Id ?? 0 }))
                .GroupBy(mapping => mapping.TargetId)
                .Where(group => group.Select(mapping => mapping.SourceId).Distinct().Count() == 1)
                .ToDictionary(group => group.Key, group => group.First().SourceId);

            foreach (var resource in resources)
            {
                if (resource.Book != null && bookMappingsByTargetId.TryGetValue(resource.Book.Id, out var bookMapping))
                {
                    var workBooks = _legacyIdentityMapService.ResolveBookIds(new[] { bookMapping.SourceId });
                    var targetBooks = _bookService.GetBooks(workBooks).Where(book => book != null).ToList();
                    resource.Book.Id = bookMapping.SourceId;
                    resource.Book.LocalBookId = bookMapping.SourceId.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
                    resource.Book.MediaType = null;
                    resource.Book.AudiobookMonitored = null;
                    resource.Book.EbookMonitored = null;
                    resource.Book.LocalAudiobookBooks = null;
                    resource.Book.LocalEbookBooks = null;
                    if (targetBooks.Count > 0)
                    {
                        resource.Book.Monitored = targetBooks.Any(book => book.IsMonitored());
                        resource.Book.HasFiles = targetBooks.Any(book => book.HasFiles) || resource.Book.HasFiles;
                    }
                }

                if (resource.Book != null && authorMetadataMappingsByTargetId.TryGetValue(resource.Book.AuthorId, out var legacyMetadataId))
                {
                    resource.Book.AuthorMetadataId = legacyMetadataId;
                    if (resource.Book.Author != null)
                    {
                        resource.Book.Author.AuthorMetadataId = legacyMetadataId;
                    }
                }

                if (resource.Author != null && authorMetadataMappingsByTargetId.TryGetValue(resource.Author.Id, out var legacyAuthorMetadataId))
                {
                    resource.Author.AuthorMetadataId = legacyAuthorMetadataId;
                }

                if (resource.Edition != null)
                {
                    if (editionMappingsByTargetId.TryGetValue(resource.Edition.Id, out var editionMapping))
                    {
                        resource.Edition.Id = editionMapping.SourceId;
                    }

                    if (bookMappingsByTargetId.TryGetValue(resource.Edition.BookId, out var editionBookMapping))
                    {
                        resource.Edition.BookId = editionBookMapping.SourceId;
                    }
                }

                if (resource.EditionId.HasValue && editionMappingsByTargetId.TryGetValue(resource.EditionId.Value, out var suggestedEditionMapping))
                {
                    resource.EditionId = suggestedEditionMapping.SourceId;
                }
            }
        }

        private Edition ResolveLocalEdition(int? editionId, Book book, BookMediaType? mediaType)
        {
            if (!editionId.HasValue || editionId.Value <= 0)
            {
                return null;
            }

            var localEditionId = editionId.Value;
            if (HttpContext?.GetReadarrFacadeContext()?.IsBookshelfCompatibility == true && _legacyIdentityMapService != null)
            {
                var mappings = _legacyIdentityMapService.GetMappings("Edition", new[] { localEditionId });
                if (mappings.Count > 0)
                {
                    var matchingMappings = mappings.Where(mapping =>
                        (!mediaType.HasValue || !mapping.MediaType.HasValue || mapping.MediaType.Value == (int)mediaType.Value) &&
                        (book == null || mapping.TargetId == book.Id)).ToList();
                    if (matchingMappings.Count != 1)
                    {
                        throw new BadRequestException($"Selected edition {editionId.Value} could not be resolved for the requested media type.");
                    }

                    localEditionId = matchingMappings[0].TargetId;
                }
            }

            Edition edition;
            try
            {
                edition = _editionService.GetEdition(localEditionId);
            }
            catch (Exception ex)
            {
                throw new BadRequestException($"Selected edition {editionId.Value} could not be found: {ex.Message}");
            }

            if (edition == null)
            {
                throw new BadRequestException($"Selected edition {editionId.Value} could not be found.");
            }

            if (book != null && edition.BookId != book.Id)
            {
                throw new BadRequestException($"Selected edition {editionId.Value} does not belong to selected book {book.Id}.");
            }

            return edition;
        }

        private Edition ResolveForeignEdition(string foreignEditionId, Book book)
        {
            if (string.IsNullOrWhiteSpace(foreignEditionId) ||
                string.Equals(foreignEditionId.Trim(), "0", global::System.StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var facadeContext = HttpContext?.GetReadarrFacadeContext();
            var normalizedForeignEditionId = ReadarrFacadeProviderIdTranslator.NormalizeBareProviderId(foreignEditionId, facadeContext);
            var edition = _editionService.GetEditionByForeignEditionId(normalizedForeignEditionId);

            if (edition == null)
            {
                throw new BadRequestException($"Selected foreign edition '{foreignEditionId}' could not be found.");
            }

            if (book != null && edition.BookId != book.Id)
            {
                throw new BadRequestException($"Selected foreign edition '{foreignEditionId}' does not belong to selected book {book.Id}.");
            }

            return edition;
        }

        private static BookMediaType? GetImportMediaType(string path, string facadeMediaType)
        {
            var scopedMediaType = MediaTypeParameterParser.ParseOptional(facadeMediaType);
            if (scopedMediaType.HasValue)
            {
                return scopedMediaType;
            }

            var extension = Path.GetExtension(path ?? string.Empty)?.ToLowerInvariant();
            if (MediaFileExtensions.AudioExtensions.Contains(extension))
            {
                return BookMediaType.Audiobook;
            }

            if (MediaFileExtensions.TextExtensions.Contains(extension))
            {
                return BookMediaType.Ebook;
            }

            return null;
        }
    }
}
