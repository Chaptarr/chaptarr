using FluentValidation.Validators;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.RootFolders;

using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Validation.Paths
{
    public class RootFolderValidator : NzbDronePropertyValidator
    {
        private readonly IRootFolderService _rootFolderService;

        public RootFolderValidator(IRootFolderService rootFolderService)
        {
            _rootFolderService = rootFolderService;
        }

        protected override string GetDefaultMessageTemplate() => "Path '{path}' is already configured as a root folder";

        protected override bool IsValid(NzbDronePropertyValidatorContext context)
        {
            if (context.PropertyValue == null)
            {
                return true;
            }

            context.MessageFormatter.AppendArgument("path", context.PropertyValue.ToString());

            return !_rootFolderService.All().Exists(r => r.Path.PathEquals(context.PropertyValue.ToString()));
        }
    }
}
