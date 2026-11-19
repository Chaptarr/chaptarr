using FluentValidation;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace NzbDrone.Core.Configuration
{
    public class MediaManagementPathSettings
    {
        public int Id { get; set; }
        public string RecycleBin { get; set; }
        public string ChmodFolder { get; set; }
    }

    public class MediaManagementPathValidator : AbstractValidator<MediaManagementPathSettings>
    {
        public MediaManagementPathValidator(PathExistsValidator pathExists, FolderChmodValidator chmod,
            FolderWritableValidator writable, AuthorPathValidator author, StartupFolderValidator startup,
            SystemFolderValidator system, RootFolderAncestorValidator ancestor, RootFolderValidator root)
        {
            RuleFor(value => value.RecycleBin).IsValidPath().SetValidator(writable).SetValidator(root)
                .SetValidator(pathExists).SetValidator(author).SetValidator(ancestor).SetValidator(startup).SetValidator(system)
                .When(value => !string.IsNullOrWhiteSpace(value.RecycleBin));
            RuleFor(value => value.ChmodFolder).SetValidator(chmod)
                .When(value => !string.IsNullOrEmpty(value.ChmodFolder) && (OsInfo.IsLinux || OsInfo.IsOsx));
        }
    }
}
