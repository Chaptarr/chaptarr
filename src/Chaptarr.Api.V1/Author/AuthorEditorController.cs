using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http;
using Chaptarr.Http.Middleware;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Organizer;
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
        private readonly IBuildFileNames _fileNames;

        public AuthorEditorController(
            IAuthorService authorService,
            IManageCommandQueue commandQueueManager,
            IRootFolderService rootFolderService,
            IQualityProfileService qualityProfileService,
            IMetadataProfileService metadataProfileService,
            IBuildFileNames fileNames)
        {
            _authorService = authorService;
            _commandQueueManager = commandQueueManager;
            _rootFolderService = rootFolderService;
            _qualityProfileService = qualityProfileService;
            _metadataProfileService = metadataProfileService;
            _fileNames = fileNames;
        }

        [HttpPut]
        public IActionResult SaveAll([FromBody] AuthorEditorResource resource)
        {
            var selectedMediaType = MediaTypeParameterParser.ResolveWriteScope(MediaTypeParameterParser.NormalizeOptional(resource.MediaType), HttpContext?.GetReadarrFacadeContext()?.MediaType);
            var authorsToUpdate = _authorService.GetAuthors(resource.AuthorIds);
            var previousSyncByAuthorId = authorsToUpdate.ToDictionary(author => author.Id, author => author.SyncMonitoredAcrossFormats == true);
            var previousRootFoldersByAuthorId = authorsToUpdate.ToDictionary(author => author.Id, author => (author.AudiobookRootFolderPath, author.EbookRootFolderPath));
            var moves = new List<MoveAuthorCommand>();
            var rootFolders = _rootFolderService.All();
            var validationError = GetValidationError(resource, rootFolders, authorsToUpdate);
            if (validationError.IsNotNullOrWhiteSpace())
            {
                return BadRequest(validationError);
            }

            var linkedChoices = authorsToUpdate.ToDictionary(author => author.Id, author =>
            {
                var projected = new NzbDrone.Core.Books.Author
                {
                    SyncMonitoredAcrossFormats = author.SyncMonitoredAcrossFormats,
                    AudiobookMonitorNewItems = author.AudiobookMonitorNewItems,
                    EbookMonitorNewItems = author.EbookMonitorNewItems,
                    AudiobookRootFolderPath = resource.AudiobookRootFolderPath.IsNotNullOrWhiteSpace() ? resource.AudiobookRootFolderPath : author.AudiobookRootFolderPath,
                    EbookRootFolderPath = resource.EbookRootFolderPath.IsNotNullOrWhiteSpace() ? resource.EbookRootFolderPath : author.EbookRootFolderPath
                };
                var eligible = AuthorMonitoringSyncPolicy.IsEligible(projected, rootFolders);
                AuthorMonitoringSyncPolicy.ApplyNewItemChoices(projected,
                    eligible ? resource.SyncMonitoredAcrossFormats : false,
                    resource.AudiobookMonitorNewItems, resource.EbookMonitorNewItems);
                return (projected.AudiobookMonitorNewItems, projected.EbookMonitorNewItems);
            });

            var syncEnabledCount = 0;
            var syncSkippedCount = 0;

            foreach (var author in authorsToUpdate)
            {
                var audiobookConfigured = IsMediaTypeConfigured(author.AudiobookRootFolderPath, resource.AudiobookRootFolderPath);
                var ebookConfigured = IsMediaTypeConfigured(author.EbookRootFolderPath, resource.EbookRootFolderPath);

                if (audiobookConfigured && resource.AudiobookMonitored.HasValue)
                    author.AudiobookMonitored = resource.AudiobookMonitored.Value;
                if (audiobookConfigured)
                    author.AudiobookMonitorNewItems = linkedChoices[author.Id].AudiobookMonitorNewItems;
                if (ebookConfigured && resource.EbookMonitored.HasValue)
                    author.EbookMonitored = resource.EbookMonitored.Value;
                if (ebookConfigured)
                    author.EbookMonitorNewItems = linkedChoices[author.Id].EbookMonitorNewItems;

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

                if (resource.MoveFiles)
                {
                    var audioFolder = resource.AudiobookRootFolderPath.IsNullOrWhiteSpace() ? null :
                        Path.Combine(resource.AudiobookRootFolderPath, _fileNames.GetAuthorFolder(author, mediaType: "audiobook"));
                    var ebookFolder = resource.EbookRootFolderPath.IsNullOrWhiteSpace() ? null :
                        Path.Combine(resource.EbookRootFolderPath, _fileNames.GetAuthorFolder(author, mediaType: "ebook"));
                    moves.AddRange(AuthorPathBuilder.ChangeFolders(author, audioFolder, ebookFolder, rootFolders, true));
                }
                else
                {
                    // The shared path builder preserves the folder relative to its actual configured root.
                    if (resource.AudiobookRootFolderPath.IsNotNullOrWhiteSpace())
                    {
                        author.AudiobookRootFolderPath = resource.AudiobookRootFolderPath;
                    }

                    if (resource.EbookRootFolderPath.IsNotNullOrWhiteSpace())
                    {
                        author.EbookRootFolderPath = resource.EbookRootFolderPath;
                    }
                }

                if (resource.SyncMonitoredAcrossFormats.HasValue)
                {
                    if (resource.SyncMonitoredAcrossFormats.Value)
                    {
                        if (AuthorMonitoringSyncPolicy.IsEligible(author, rootFolders))
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
                    var audiobookTags = new HashSet<int>(author.GetTagsForMediaType(BookMediaType.Audiobook));
                    var ebookTags = new HashSet<int>(author.GetTagsForMediaType(BookMediaType.Ebook));

                    var targets = selectedMediaType == "audiobook" ? new[] { audiobookTags } :
                        selectedMediaType == "ebook" ? new[] { ebookTags } : new[] { audiobookTags, ebookTags };
                    foreach (var tags in targets)
                    {
                        switch (resource.ApplyTags)
                        {
                            case ApplyTags.Add:
                                tags.UnionWith(resource.Tags);
                                break;
                            case ApplyTags.Remove:
                                tags.ExceptWith(resource.Tags);
                                break;
                            case ApplyTags.Replace:
                                tags.Clear();
                                tags.UnionWith(resource.Tags);
                                break;
                        }
                    }

                    author.AudiobookTags = audiobookTags;
                    author.EbookTags = ebookTags;
                    author.Tags = author.GetCombinedTags();
                }
            }

            var updatedAuthors = _authorService.UpdateAuthors(authorsToUpdate, !resource.MoveFiles);
            if (moves.Count > 0)
            {
                moves.ForEach(move => move.Trigger = CommandTrigger.Manual);
                _commandQueueManager.PushMany(moves);
            }

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
                .Where(author => author.SyncMonitoredAcrossFormats == true && AuthorMonitoringSyncPolicy.IsEligible(author, rootFolders))
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

        private string GetValidationError(AuthorEditorResource resource, List<RootFolder> rootFolders, List<NzbDrone.Core.Books.Author> authors)
        {
            if (resource.AudiobookQualityProfileId.HasValue && !_qualityProfileService.Exists(resource.AudiobookQualityProfileId.Value, authors.All(author => author.AudiobookQualityProfileId == resource.AudiobookQualityProfileId) ? null : ProfileType.Audiobook))
            {
                return $"Audiobook quality profile {resource.AudiobookQualityProfileId.Value} does not exist or is incompatible with audiobook";
            }

            if (resource.EbookQualityProfileId.HasValue && !_qualityProfileService.Exists(resource.EbookQualityProfileId.Value, authors.All(author => author.EbookQualityProfileId == resource.EbookQualityProfileId) ? null : ProfileType.Ebook))
            {
                return $"Ebook quality profile {resource.EbookQualityProfileId.Value} does not exist or is incompatible with ebook";
            }

            if (resource.MetadataProfileId.HasValue && !_metadataProfileService.Exists(resource.MetadataProfileId.Value))
            {
                return $"Metadata profile {resource.MetadataProfileId.Value} does not exist";
            }

            if (resource.AudiobookMetadataProfileId.HasValue && !_metadataProfileService.Exists(resource.AudiobookMetadataProfileId.Value, authors.All(author => !NzbDrone.Core.Configuration.ConfigValueValidation.HasChanged(author.AudiobookMetadataProfileId, resource.AudiobookMetadataProfileId)) ? null : MetadataProfileType.Audiobook))
            {
                return $"Audiobook metadata profile {resource.AudiobookMetadataProfileId.Value} does not exist or is incompatible with audiobook";
            }

            if (resource.EbookMetadataProfileId.HasValue && !_metadataProfileService.Exists(resource.EbookMetadataProfileId.Value, authors.All(author => !NzbDrone.Core.Configuration.ConfigValueValidation.HasChanged(author.EbookMetadataProfileId, resource.EbookMetadataProfileId)) ? null : MetadataProfileType.Ebook))
            {
                return $"Ebook metadata profile {resource.EbookMetadataProfileId.Value} does not exist or is incompatible with ebook";
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

        [HttpDelete]
        [ProducesResponseType(200)]
        [ProducesResponseType(typeof(ApiErrorResource), 400)]
        [ProducesResponseType(typeof(ApiErrorResource), 409)]
        public object DeleteAuthor([FromBody] AuthorEditorResource resource, string mediaType = null)
        {
            var scope = MediaTypeParameterParser.ParseDeletionScope(resource.ApplyToBothFormats, HttpContext.GetReadarrFacadeContext()?.MediaType, resource.MediaType, mediaType);
            _authorService.DeleteAuthors(resource.AuthorIds, resource.DeleteFiles, resource.AddImportListExclusion, scope, resource.ApplyToBothFormats, resource.DeleteOtherAuthorsFiles);

            return new { };
        }
    }
}
