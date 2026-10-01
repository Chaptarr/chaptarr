using FluentValidation.Validators;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;

using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Validation.Paths
{
    public class SystemFolderValidator : NzbDronePropertyValidator
    {
        protected override string GetDefaultMessageTemplate() => "Path '{path}' is {relationship} system folder {systemFolder}";

        protected override bool IsValid(NzbDronePropertyValidatorContext context)
        {
            var folder = context.PropertyValue.ToString();
            context.MessageFormatter.AppendArgument("path", folder);

            foreach (var systemFolder in SystemFolders.GetSystemFolders())
            {
                context.MessageFormatter.AppendArgument("systemFolder", systemFolder);

                if (systemFolder.PathEquals(folder))
                {
                    context.MessageFormatter.AppendArgument("relationship", "set to");

                    return false;
                }

                if (systemFolder.IsParentPath(folder))
                {
                    context.MessageFormatter.AppendArgument("relationship", "child of");

                    return false;
                }
            }

            return true;
        }
    }
}
