using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Api.V1.Author
{
    [V1ApiController("author/editor")]
    public class AuthorEditorController : Controller
    {
        private readonly IAuthorService _authorService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IRootFolderService _rootFolderService;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly IMetadataProfileService _metadataProfileService;

        public AuthorEditorController(
            IAuthorService authorService,
            IManageCommandQueue commandQueueManager,
            IRootFolderService rootFolderService,
            IQualityProfileService qualityProfileService,
            IMetadataProfileService metadataProfileService)
        {
            _authorService = authorService;
            _commandQueueManager = commandQueueManager;
            _rootFolderService = rootFolderService;
            _qualityProfileService = qualityProfileService;
            _metadataProfileService = metadataProfileService;
        }

        [HttpPut]
        public IActionResult SaveAll([FromBody] AuthorEditorResource resource)
        {
            var authorsToUpdate = _authorService.GetAuthors(resource.AuthorIds);
            var previousSyncByAuthorId = authorsToUpdate.ToDictionary(author => author.Id, author => author.SyncMonitoredAcrossFormats == true);
            var previousRootFoldersByAuthorId = authorsToUpdate.ToDictionary(author => author.Id, author => (author.AudiobookRootFolderPath, author.EbookRootFolderPath));
            var audiobookMoves = new List<BulkMoveAuthor>();
            var ebookMoves = new List<BulkMoveAuthor>();
            var rootFolders = _rootFolderService.All();
            var validationError = GetValidationError(resource, rootFolders);
            if (validationError.IsNotNullOrWhiteSpace())
            {
                return BadRequest(validationError);
            }

            var syncEnabledCount = 0;
            var syncSkippedCount = 0;

            foreach (var author in authorsToUpdate)
            {
                var audiobookConfigured = IsMediaTypeConfigured(author.AudiobookRootFolderPath, resource.AudiobookRootFolderPath);
                var ebookConfigured = IsMediaTypeConfigured(author.EbookRootFolderPath, resource.EbookRootFolderPath);

                if (audiobookConfigured && resource.AudiobookMonitored.HasValue)
                    author.AudiobookMonitored = resource.AudiobookMonitored.Value;
                if (audiobookConfigured && resource.AudiobookMonitorNewItems.HasValue)
                    author.AudiobookMonitorNewItems = resource.AudiobookMonitorNewItems.Value;
                if (ebookConfigured && resource.EbookMonitored.HasValue)
                    author.EbookMonitored = resource.EbookMonitored.Value;
                if (ebookConfigured && resource.EbookMonitorNewItems.HasValue)
                    author.EbookMonitorNewItems = resource.EbookMonitorNewItems.Value;

                if (resource.AudiobookQualityProfileId.HasValue)
                {
                    author.AudiobookQualityProfileId = resource.AudiobookQualityProfileId.Value;
                }

                if (resource.EbookQualityProfileId.HasValue)
                {
                    author.EbookQualityProfileId = resource.EbookQualityProfileId.Value;
                }

                if (resource.MetadataProfileId.HasValue)
                {
                    author.MetadataProfileId = resource.MetadataProfileId.Value;
                }

                if (resource.AudiobookMetadataProfileId.HasValue)
                {
                    author.AudiobookMetadataProfileId = resource.AudiobookMetadataProfileId.Value;
                }

                if (resource.EbookMetadataProfileId.HasValue)
                {
                    author.EbookMetadataProfileId = resource.EbookMetadataProfileId.Value;
                }

                if (resource.AudiobookRootFolderPath.IsNotNullOrWhiteSpace())
                {
                    var sourcePath = author.AudiobookPath;
                    if (sourcePath.IsNullOrWhiteSpace())
                    {
                        var currentAudiobookRoot = author.AudiobookRootFolderPath;

                        // Safety: only fall back to legacy author.Path if it doesn't appear to be the ebook folder.
                        // We also try to ensure it sits under the existing audiobook root (when configured), so we don't
                        // accidentally move the wrong media-type folder.
                        if (author.Path.IsNotNullOrWhiteSpace() &&
                            (author.EbookPath.IsNullOrWhiteSpace() || !author.Path.PathEquals(author.EbookPath)) &&
                            (currentAudiobookRoot.IsNullOrWhiteSpace() || currentAudiobookRoot.PathEquals(author.Path) || currentAudiobookRoot.IsParentPath(author.Path)))
                        {
                            sourcePath = author.Path;
                        }
                    }

                    author.AudiobookRootFolderPath = resource.AudiobookRootFolderPath;

                    if (sourcePath.IsNotNullOrWhiteSpace())
                    {
                        audiobookMoves.Add(new BulkMoveAuthor
                        {
                            AuthorId = author.Id,
                            SourcePath = sourcePath,
                            MediaType = "audiobook"
                        });
                    }
                }

                if (resource.EbookRootFolderPath.IsNotNullOrWhiteSpace())
                {
                    var currentEbookRoot = author.EbookRootFolderPath;
                    author.EbookRootFolderPath = resource.EbookRootFolderPath;

                    var sourcePath = author.EbookPath;
                    if (sourcePath.IsNullOrWhiteSpace())
                    {
                        // Safety: only fall back to legacy author.Path if it doesn't appear to be the audiobook folder.
                        // Also try to ensure it sits under the existing ebook root (when configured).
                        if (author.Path.IsNotNullOrWhiteSpace() &&
                            (author.AudiobookPath.IsNullOrWhiteSpace() || !author.Path.PathEquals(author.AudiobookPath)) &&
                            (currentEbookRoot.IsNullOrWhiteSpace() || currentEbookRoot.PathEquals(author.Path) || currentEbookRoot.IsParentPath(author.Path)))
                        {
                            sourcePath = author.Path;
                        }
                    }

                    if (sourcePath.IsNotNullOrWhiteSpace())
                    {
                        ebookMoves.Add(new BulkMoveAuthor
                        {
                            AuthorId = author.Id,
                            SourcePath = sourcePath,
                            MediaType = "ebook"
                        });
                    }
                }

                if (resource.SyncMonitoredAcrossFormats.HasValue)
                {
                    if (resource.SyncMonitoredAcrossFormats.Value)
                    {
                        if (HasSyncMonitoredAcrossFormatsEligibility(author, rootFolders))
                        {
                            author.SyncMonitoredAcrossFormats = true;
                            syncEnabledCount++;
                        }
                        else
                        {
                            syncSkippedCount++;
                        }
                    }
                    else
                    {
                        author.SyncMonitoredAcrossFormats = false;
                    }
                }

                if (resource.Tags != null)
                {
                    var newTags = resource.Tags;
                    var applyTags = resource.ApplyTags;

                    switch (applyTags)
                    {
                        case ApplyTags.Add:
                            newTags.ForEach(t => author.Tags.Add(t));
                            break;
                        case ApplyTags.Remove:
                            newTags.ForEach(t => author.Tags.Remove(t));
                            break;
                        case ApplyTags.Replace:
                            author.Tags = new HashSet<int>(newTags);
                            break;
                    }
                }
            }

            if (resource.MoveFiles)
            {
                if (resource.AudiobookRootFolderPath.IsNotNullOrWhiteSpace() && audiobookMoves.Any())
                {
                    _commandQueueManager.Push(new BulkMoveAuthorCommand
                    {
                        DestinationRootFolder = resource.AudiobookRootFolderPath,
                        Author = audiobookMoves
                    });
                }

                if (resource.EbookRootFolderPath.IsNotNullOrWhiteSpace() && ebookMoves.Any())
                {
                    _commandQueueManager.Push(new BulkMoveAuthorCommand
                    {
                        DestinationRootFolder = resource.EbookRootFolderPath,
                        Author = ebookMoves
                    });
                }
            }

            var updatedAuthors = _authorService.UpdateAuthors(authorsToUpdate, !resource.MoveFiles);

            var authorIdsToHydrate = updatedAuthors
                .Where(author => previousRootFoldersByAuthorId.TryGetValue(author.Id, out var previous) &&
                                 HasGainedMediaTypeRootFolder(previous.AudiobookRootFolderPath, previous.EbookRootFolderPath, author))
                .Select(author => author.Id)
                .Distinct()
                .ToList();

            if (authorIdsToHydrate.Any())
            {
                // Bulk author edits do not publish AuthorEditedEvent. Queue the same local
                // hydration refresh here, bounded to blank -> set media-type root changes.
                _commandQueueManager.PushMany(authorIdsToHydrate
                    .Select(authorId => new RefreshAuthorCommand(authorId, refreshMetadata: true, rescanFolders: false, forceRefresh: true))
                    .ToList());
            }

            var shouldReconcileEnabledAuthors = resource.SyncMonitoredAcrossFormats == true;
            var authorIdsToReconcile = updatedAuthors
                .Where(author => author.SyncMonitoredAcrossFormats == true && HasSyncMonitoredAcrossFormatsEligibility(author, rootFolders))
                .Where(author => shouldReconcileEnabledAuthors || !previousSyncByAuthorId.GetValueOrDefault(author.Id))
                .Select(author => author.Id)
                .Distinct()
                .ToList();

            if (authorIdsToReconcile.Any())
            {
                _commandQueueManager.Push(new BulkSyncFormatMonitoringCommand(authorIdsToReconcile));
            }

            if (syncSkippedCount > 0)
            {
                Response.Headers["X-Chaptarr-Warning"] = $"Sync was enabled for {syncEnabledCount} author(s). {syncSkippedCount} author(s) were skipped because both audiobook and ebook root folders are required.";
            }

            return Accepted(updatedAuthors.ToResource());
        }

        private string GetValidationError(AuthorEditorResource resource, List<RootFolder> rootFolders)
        {
            if (resource.AudiobookQualityProfileId.HasValue && !_qualityProfileService.Exists(resource.AudiobookQualityProfileId.Value))
            {
                return $"Audiobook quality profile {resource.AudiobookQualityProfileId.Value} does not exist";
            }

            if (resource.EbookQualityProfileId.HasValue && !_qualityProfileService.Exists(resource.EbookQualityProfileId.Value))
            {
                return $"Ebook quality profile {resource.EbookQualityProfileId.Value} does not exist";
            }

            if (resource.MetadataProfileId.HasValue && !_metadataProfileService.Exists(resource.MetadataProfileId.Value))
            {
                return $"Metadata profile {resource.MetadataProfileId.Value} does not exist";
            }

            if (resource.AudiobookMetadataProfileId.HasValue && !_metadataProfileService.Exists(resource.AudiobookMetadataProfileId.Value))
            {
                return $"Audiobook metadata profile {resource.AudiobookMetadataProfileId.Value} does not exist";
            }

            if (resource.EbookMetadataProfileId.HasValue && !_metadataProfileService.Exists(resource.EbookMetadataProfileId.Value))
            {
                return $"Ebook metadata profile {resource.EbookMetadataProfileId.Value} does not exist";
            }

            if (resource.AudiobookRootFolderPath.IsNotNullOrWhiteSpace() && !rootFolders.Any(rootFolder => rootFolder.Path.PathEquals(resource.AudiobookRootFolderPath)))
            {
                return $"Audiobook root folder '{resource.AudiobookRootFolderPath}' is not configured";
            }

            if (resource.EbookRootFolderPath.IsNotNullOrWhiteSpace() && !rootFolders.Any(rootFolder => rootFolder.Path.PathEquals(resource.EbookRootFolderPath)))
            {
                return $"Ebook root folder '{resource.EbookRootFolderPath}' is not configured";
            }

            return null;
        }

        private static bool HasGainedMediaTypeRootFolder(string previousAudiobookRootFolderPath, string previousEbookRootFolderPath, NzbDrone.Core.Books.Author author)
        {
            return (previousAudiobookRootFolderPath.IsNullOrWhiteSpace() && author.AudiobookRootFolderPath.IsNotNullOrWhiteSpace()) ||
                   (previousEbookRootFolderPath.IsNullOrWhiteSpace() && author.EbookRootFolderPath.IsNotNullOrWhiteSpace());
        }

        private static bool IsMediaTypeConfigured(string existingRootFolderPath, string requestedRootFolderPath)
        {
            return existingRootFolderPath.IsNotNullOrWhiteSpace() || requestedRootFolderPath.IsNotNullOrWhiteSpace();
        }

        private static bool HasSyncMonitoredAcrossFormatsEligibility(NzbDrone.Core.Books.Author author, List<RootFolder> rootFolders)
        {
            return HasCompatibleRootFolder(author, rootFolders, BookMediaType.Audiobook) &&
                   HasCompatibleRootFolder(author, rootFolders, BookMediaType.Ebook);
        }

        private static bool HasCompatibleRootFolder(NzbDrone.Core.Books.Author author, List<RootFolder> rootFolders, BookMediaType mediaType)
        {
            if (author == null || rootFolders == null || rootFolders.Count == 0)
            {
                return false;
            }

            var rootFolderPath = mediaType == BookMediaType.Audiobook
                ? author.AudiobookRootFolderPath
                : author.EbookRootFolderPath;

            if (rootFolderPath.IsNullOrWhiteSpace())
            {
                return false;
            }

            var rootFolder = rootFolders.FirstOrDefault(r => r.Path.PathEquals(rootFolderPath));
            if (rootFolder == null)
            {
                return false;
            }

            return rootFolder.FolderType == FolderType.Mixed ||
                   (mediaType == BookMediaType.Audiobook && rootFolder.FolderType == FolderType.Audiobook) ||
                   (mediaType == BookMediaType.Ebook && rootFolder.FolderType == FolderType.Ebook);
        }

        [HttpDelete]
        public object DeleteAuthor([FromBody] AuthorEditorResource resource)
        {
            _authorService.DeleteAuthors(resource.AuthorIds, false);

            return new { };
        }
    }
}
