using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Api.V1.Commands;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace Chaptarr.Api.V1.Author
{
    public class AuthorMediaMoveRequestResource : RestResource
    {
        public string Format { get; set; }
        public string DestinationPath { get; set; }
        public string PreviewToken { get; set; }
    }

    public class AuthorMediaMoveBatchRequestResource
    {
        public List<int> AuthorIds { get; set; }
        public string Format { get; set; }
        public string DestinationRootPath { get; set; }
        public string PreviewToken { get; set; }
    }

    [V1ApiController("author/media-move")]
    [Authorize(Policy = "AdminApiKey")]
    public class AuthorMediaMoveController : Controller
    {
        private readonly IAuthorMediaMoveService _authorMediaMoveService;
        private readonly IAuthorService _authorService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly ResourceValidator<AuthorMediaMoveRequestResource> _validator;
        private readonly ResourceValidator<AuthorMediaMoveBatchRequestResource> _batchValidator;

        public AuthorMediaMoveController(IAuthorMediaMoveService authorMediaMoveService,
                                         IAuthorService authorService,
                                         IManageCommandQueue commandQueueManager,
                                         RootFolderValidator rootFolderValidator,
                                         MappedNetworkDriveValidator mappedNetworkDriveValidator,
                                         AuthorPathValidator authorPathValidator,
                                         AuthorAncestorValidator authorAncestorValidator,
                                         RecycleBinValidator recycleBinValidator,
                                         SystemFolderValidator systemFolderValidator)
        {
            _authorMediaMoveService = authorMediaMoveService;
            _authorService = authorService;
            _commandQueueManager = commandQueueManager;

            _validator = new ResourceValidator<AuthorMediaMoveRequestResource>();
            _validator.RuleFor(resource => resource.Id).ValidId();
            _validator.RuleFor(resource => resource.Format)
                      .NotEmpty()
                      .Must(format => string.Equals(format, "ebook", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(format, "audiobook", StringComparison.OrdinalIgnoreCase))
                      .WithMessage("Format must be 'ebook' or 'audiobook'.");
            _validator.RuleFor(resource => resource.DestinationPath)
                      .Cascade(CascadeMode.Stop)
                      .NotEmpty()
                      .IsValidPath()
                      .SetValidator(rootFolderValidator)
                      .SetValidator(mappedNetworkDriveValidator)
                      .SetValidator(authorPathValidator)
                      .SetValidator(authorAncestorValidator)
                      .SetValidator(recycleBinValidator)
                      .SetValidator(systemFolderValidator);

            _batchValidator = new ResourceValidator<AuthorMediaMoveBatchRequestResource>();
            _batchValidator.RuleFor(resource => resource.AuthorIds)
                           .Cascade(CascadeMode.Stop)
                           .NotNull()
                           .NotEmpty()
                           .Must(ids => ids.Count <= 1000)
                           .WithMessage("Select no more than 1,000 authors for a single move.")
                           .Must(ids => ids.All(id => id > 0))
                           .WithMessage("Author IDs must be positive numbers.")
                           .Must(ids => ids.Distinct().Count() == ids.Count)
                           .WithMessage("Author IDs must be unique.");
            _batchValidator.RuleFor(resource => resource.Format)
                           .NotEmpty()
                           .Must(format => string.Equals(format, "ebook", StringComparison.OrdinalIgnoreCase) ||
                                           string.Equals(format, "audiobook", StringComparison.OrdinalIgnoreCase))
                           .WithMessage("Format must be 'ebook' or 'audiobook'.");
            _batchValidator.RuleFor(resource => resource.DestinationRootPath)
                           .Cascade(CascadeMode.Stop)
                           .NotEmpty()
                           .IsValidPath()
                           .SetValidator(mappedNetworkDriveValidator)
                           .SetValidator(systemFolderValidator);
        }

        [HttpPost("preview")]
        public ActionResult<AuthorMediaMovePreview> Preview([FromBody] AuthorMediaMoveRequestResource resource)
        {
            ValidateResource(resource);

            return Ok(_authorMediaMoveService.Preview(resource.Id, resource.Format, resource.DestinationPath));
        }

        [HttpPost("start")]
        public ActionResult<CommandResource> Start([FromBody] AuthorMediaMoveRequestResource resource)
        {
            ValidateResource(resource);

            if (resource.PreviewToken.IsNullOrWhiteSpace())
            {
                throw new BadRequestException("Preview the move before starting it.");
            }

            var preview = _authorMediaMoveService.Preview(resource.Id, resource.Format, resource.DestinationPath);

            if (!string.Equals(resource.PreviewToken, preview.PreviewToken, StringComparison.Ordinal))
            {
                return Conflict(new { message = "The library changed after this preview. Preview the move again.", preview });
            }

            if (!preview.CanMove)
            {
                return Conflict(new { message = "This move has conflicts or no movable files.", preview });
            }

            var author = _authorService.GetAuthor(resource.Id);
            var formatPath = resource.DestinationPath.PathEquals(author.Path) ? null : resource.DestinationPath;
            if (preview.Format == "ebook")
            {
                author.EbookPath = formatPath;
            }
            else
            {
                author.AudiobookPath = formatPath;
            }

            _authorService.UpdateAuthor(author);

            var command = _commandQueueManager.Push(new MoveAuthorMediaCommand
            {
                AuthorId = author.Id,
                Format = preview.Format,
                SourcePath = preview.SourcePath,
                DestinationPath = preview.DestinationPath,
                PreviewToken = preview.PreviewToken,
                Files = preview.Files
            },
                CommandPriority.Normal,
                CommandTrigger.Manual);

            return Accepted(command.ToResource());
        }

        [HttpPost("bulk/preview")]
        public ActionResult<AuthorMediaMoveBatchPreview> PreviewBatch([FromBody] AuthorMediaMoveBatchRequestResource resource)
        {
            ValidateBatchResource(resource);
            ValidateBatchDestinationPaths(resource);

            return Ok(_authorMediaMoveService.PreviewBatch(resource.AuthorIds, resource.Format, resource.DestinationRootPath));
        }

        [HttpPost("bulk/start")]
        public ActionResult<CommandResource> StartBatch([FromBody] AuthorMediaMoveBatchRequestResource resource)
        {
            ValidateBatchResource(resource);
            ValidateBatchDestinationPaths(resource);

            if (resource.PreviewToken.IsNullOrWhiteSpace())
            {
                throw new BadRequestException("Preview the move before starting it.");
            }

            var preview = _authorMediaMoveService.PreviewBatch(resource.AuthorIds, resource.Format, resource.DestinationRootPath);

            if (!string.Equals(resource.PreviewToken, preview.PreviewToken, StringComparison.Ordinal))
            {
                return Conflict(new { message = "The library changed after this preview. Preview the move again.", preview });
            }

            if (!preview.CanMove)
            {
                return Conflict(new { message = "This move has conflicts or no movable files.", preview });
            }

            var command = new MoveAuthorMediaBatchCommand
            {
                Format = preview.Format,
                DestinationRootPath = preview.DestinationRootPath,
                PreviewToken = preview.PreviewToken
            };

            foreach (var authorPreview in preview.Authors.Where(author => author.CanMove))
            {
                var author = _authorService.GetAuthor(authorPreview.AuthorId);
                var formatPath = authorPreview.DestinationPath.PathEquals(author.Path) ? null : authorPreview.DestinationPath;
                if (preview.Format == "ebook")
                {
                    author.EbookPath = formatPath;
                }
                else
                {
                    author.AudiobookPath = formatPath;
                }

                _authorService.UpdateAuthor(author);

                command.Authors.Add(new AuthorMediaMoveBatchItem
                {
                    AuthorId = author.Id,
                    SourcePath = authorPreview.SourcePath,
                    DestinationPath = authorPreview.DestinationPath,
                    Files = authorPreview.Files
                });
            }

            var queuedCommand = _commandQueueManager.Push(command, CommandPriority.Normal, CommandTrigger.Manual);
            return Accepted(queuedCommand.ToResource());
        }

        private void ValidateBatchDestinationPaths(AuthorMediaMoveBatchRequestResource resource)
        {
            foreach (var authorId in resource.AuthorIds)
            {
                var author = _authorService.GetAuthor(authorId);
                var folderName = Path.GetFileName(author.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                if (string.IsNullOrWhiteSpace(folderName))
                {
                    throw new BadRequestException($"Unable to determine the library folder for {author.Name}.");
                }

                ValidateResource(new AuthorMediaMoveRequestResource
                {
                    Id = authorId,
                    Format = resource.Format,
                    DestinationPath = Path.Combine(resource.DestinationRootPath, folderName)
                });
            }
        }

        private void ValidateBatchResource(AuthorMediaMoveBatchRequestResource resource)
        {
            if (resource == null)
            {
                throw new BadRequestException("Request body can't be empty");
            }

            var result = _batchValidator.Validate(resource);
            if (!result.IsValid)
            {
                throw new ValidationException(result.Errors);
            }
        }

        private void ValidateResource(AuthorMediaMoveRequestResource resource)
        {
            if (resource == null)
            {
                throw new BadRequestException("Request body can't be empty");
            }

            var result = _validator.Validate(resource);
            if (!result.IsValid)
            {
                throw new ValidationException(result.Errors);
            }
        }
    }
}
