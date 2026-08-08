using System;
using FluentValidation.Validators;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;

namespace NzbDrone.Core.Validation.Paths
{
    public class FolderWritableValidator : PropertyValidator
    {
        private readonly IDiskProvider _diskProvider;

        public FolderWritableValidator(IDiskProvider diskProvider)
        {
            _diskProvider = diskProvider;
        }

        protected override string GetDefaultMessageTemplate() =>
            "Folder '{path}' is not writable by user '{user}'. Permission denied - please ensure the Chaptarr process has write access to this directory and its subdirectories.{dockerHint} Chaptarr determines this by attempting to create and delete a temporary file in the folder.";

        protected override bool IsValid(PropertyValidatorContext context)
        {
            if (context.PropertyValue == null)
            {
                return false;
            }

            context.MessageFormatter.AppendArgument("path", context.PropertyValue.ToString());
            context.MessageFormatter.AppendArgument("user", ProcessUserInfo.GetUserNameWithIds());
            var dockerEnv = ProcessUserInfo.GetDockerUserEnvSummary();
            var dockerHint = dockerEnv == null ? string.Empty : $" (env: {dockerEnv})";
            context.MessageFormatter.AppendArgument("dockerHint", dockerHint);

            return _diskProvider.FolderWritable(context.PropertyValue.ToString());
        }
    }
}
