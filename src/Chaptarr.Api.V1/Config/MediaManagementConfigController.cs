using System;
using System.Net;
using System.Threading;
using Chaptarr.Http;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Housekeeping.Housekeepers;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace Chaptarr.Api.V1.Config
{
    [V1ApiController("config/mediamanagement")]
    public class MediaManagementConfigController : ConfigController<MediaManagementConfigResource>
    {
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
                                           CleanupOrphanedBookCovers coverCleanup)
            : base(configService)
        {
            _coverCleanup = coverCleanup;
            var paths = new MediaManagementPathValidator(pathExistsValidator, folderChmodValidator, folderWritableValidator,
                authorPathValidator, startupFolderValidator, systemFolderValidator, rootFolderAncestorValidator, rootFolderValidator);
            SharedValidator.RuleFor(value => value).Custom((value, context) =>
            {
                foreach (var error in paths.Validate(new MediaManagementPathSettings { Id = value.Id, RecycleBin = ConfigValueValidation.HasChanged(value.RecycleBin, _configService.RecycleBin) ? value.RecycleBin : null, ChmodFolder = ConfigValueValidation.HasChanged(value.ChmodFolder, _configService.ChmodFolder) ? value.ChmodFolder : null }).Errors)
                {
                    context.AddFailure(error);
                }
            });
        }

        [HttpGet("covercleanup")]
        public ActionResult<BookCoverCleanupResource> GetCoverCleanupEstimate(CancellationToken cancellationToken)
        {
            try
            {
                var estimate = _coverCleanup.GetLibraryCleanupEstimate(cancellationToken);

                return Ok(new BookCoverCleanupResource
                {
                    Bytes = estimate.Bytes,
                    BookCount = estimate.BookCount
                });
            }
            catch (TimeoutException e)
            {
                // An exhausted estimate budget is an expected client-visible failure, already logged by the estimator.
                throw new NzbDroneClientException(HttpStatusCode.RequestTimeout, e.Message, e);
            }
        }

        protected override MediaManagementConfigResource ToResource(IConfigService model)
        {
            return MediaManagementConfigResourceMapper.ToResource(model);
        }
    }
}
