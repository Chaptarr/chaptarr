using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http;
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
        private readonly Logger _logger;

        public ManualImportController(IManualImportService manualImportService,
                                  IAuthorService authorService,
                                  IEditionService editionService,
                                  IBookService bookService,
                                  IMediaFileService mediaFileService,
                                  IRootFolderService rootFolderService,
                                  Logger logger)
        {
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _manualImportService = manualImportService;
            _mediaFileService = mediaFileService;
            _rootFolderService = rootFolderService;
            _logger = logger;
        }

        [HttpPost]
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
                    .ToResource()
                    .Select(AddQualityWeight)
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
            foreach (var resource in resources)
            {
                var author = resource.AuthorId.HasValue ? _authorService.GetAuthor(resource.AuthorId.Value) : null;
                var book = resource.BookId.HasValue ? _bookService.GetBook(resource.BookId.Value) : null;
                var edition = ResolveLocalEdition(resource.EditionId, book);

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

            return _manualImportService.UpdateItems(items).Select(x => x.ToResource()).ToList();
        }

        private Edition ResolveLocalEdition(int? editionId, Book book)
        {
            if (!editionId.HasValue || editionId.Value <= 0 || book == null)
            {
                return null;
            }

            Edition edition;
            try
            {
                edition = _editionService.GetEdition(editionId.Value);
            }
            catch (Exception ex)
            {
                throw new BadRequestException($"Selected edition {editionId.Value} could not be found: {ex.Message}");
            }

            if (edition == null)
            {
                throw new BadRequestException($"Selected edition {editionId.Value} could not be found.");
            }

            if (edition.BookId != book.Id)
            {
                throw new BadRequestException($"Selected edition {editionId.Value} does not belong to selected book {book.Id}.");
            }

            return edition;
        }
    }
}
