using System.Threading;
using Chaptarr.Http;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Housekeeping.Housekeepers;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaCover.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Config
{
    [V1ApiController("config/mediamanagement")]
    public class MediaManagementConfigController : ConfigController<MediaManagementConfigResource>
    {
        private readonly IManageCommandQueue _commandQueue;
        private readonly CleanupOrphanedBookCovers _coverCleanup;

        public MediaManagementConfigController(IConfigService configService,
                                           PathExistsValidator pathExistsValidator,
                                           FolderChmodValidator folderChmodValidator,
                                           FolderWritableValidator folderWritableValidator,
                                           AuthorPathValidator authorPathValidator,
                                           StartupFolderValidator startupFolderValidator,
                                           SystemFolderValidator systemFolderValidator,
                                           RootFolderAncestorValidator rootFolderAncestorValidator,
                                           RootFolderValidator rootFolderValidator,
                                           IManageCommandQueue commandQueue,
                                           CleanupOrphanedBookCovers coverCleanup)
            : base(configService)
        {
            _commandQueue = commandQueue;
            _coverCleanup = coverCleanup;
            SharedValidator.RuleFor(c => c.CoverDownloadScope).IsInEnum();
            SharedValidator.RuleFor(c => c.RecycleBin).IsValidPath()
                                                      .SetValidator(folderWritableValidator)
                                                      .SetValidator(rootFolderValidator)
                                                      .SetValidator(pathExistsValidator)
                                                      .SetValidator(authorPathValidator)
                                                      .SetValidator(rootFolderAncestorValidator)
                                                      .SetValidator(startupFolderValidator)
                                                      .SetValidator(systemFolderValidator)
                                                      .When(c => !string.IsNullOrWhiteSpace(c.RecycleBin));
            SharedValidator.RuleFor(c => c.RecycleBinCleanupDays).GreaterThanOrEqualTo(0);
            SharedValidator.RuleFor(c => c.ChmodFolder).SetValidator(folderChmodValidator).When(c => !string.IsNullOrEmpty(c.ChmodFolder) && (OsInfo.IsLinux || OsInfo.IsOsx));
            SharedValidator.RuleFor(c => c.MinimumFreeSpaceWhenImporting).GreaterThanOrEqualTo(100);
        }

        [HttpGet("covercleanup")]
        public ActionResult<BookCoverCleanupResource> GetCoverCleanupEstimate(CancellationToken cancellationToken)
        {
            var estimate = _coverCleanup.GetLibraryCleanupEstimate(cancellationToken);

            return Ok(new BookCoverCleanupResource
            {
                Bytes = estimate.Bytes,
                BookCount = estimate.BookCount
            });
        }

        [RestPutById]
        public override ActionResult<MediaManagementConfigResource> SaveConfig([FromBody] MediaManagementConfigResource resource)
        {
            var previousScope = _configService.CoverDownloadScope;
            var response = base.SaveConfig(resource);
            if (previousScope != _configService.CoverDownloadScope &&
                _configService.CoverDownloadScope == CoverDownloadScope.LibraryBooks)
            {
                _commandQueue.Push(new CleanupBookCoversCommand());
            }

            return response;
        }

        protected override MediaManagementConfigResource ToResource(IConfigService model)
        {
            return MediaManagementConfigResourceMapper.ToResource(model);
        }
    }
}
