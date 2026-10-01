using FluentValidation.Validators;
using NzbDrone.Common.Disk;

using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Validation.Paths
{
    public class PathExistsValidator : NzbDronePropertyValidator
    {
        private readonly IDiskProvider _diskProvider;

        public PathExistsValidator(IDiskProvider diskProvider)
        {
            _diskProvider = diskProvider;
        }

        protected override string GetDefaultMessageTemplate() => "Path '{path}' does not exist";

        protected override bool IsValid(NzbDronePropertyValidatorContext context)
        {
            if (context.PropertyValue == null)
            {
                return false;
            }

            context.MessageFormatter.AppendArgument("path", context.PropertyValue.ToString());

            return _diskProvider.FolderExists(context.PropertyValue.ToString());
        }
    }
}
