using System;
using FluentValidation.Validators;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;

namespace NzbDrone.Core.Validation.Paths
{
    public class FolderReadableValidator : PropertyValidator
    {
        private readonly IDiskProvider _diskProvider;
        private string _errorMessage;

        public FolderReadableValidator(IDiskProvider diskProvider)
        {
            _diskProvider = diskProvider;
        }

        protected override string GetDefaultMessageTemplate() => _errorMessage ?? "Folder '{path}' is not accessible.";

        protected override bool IsValid(PropertyValidatorContext context)
        {
            if (context.PropertyValue == null)
            {
                return false;
            }

            var path = context.PropertyValue.ToString();

            context.MessageFormatter.AppendArgument("path", path);
            context.MessageFormatter.AppendArgument("user", ProcessUserInfo.GetUserNameWithIds());
            var dockerEnv = ProcessUserInfo.GetDockerUserEnvSummary();
            var dockerHint = dockerEnv == null ? string.Empty : $" (env: {dockerEnv})";
            context.MessageFormatter.AppendArgument("dockerHint", dockerHint);

            try
            {
                // First check if the folder exists
                if (!_diskProvider.FolderExists(path))
                {
                    _errorMessage = "Folder '{path}' does not exist. Please create the directory first or check the path.";
                    context.MessageFormatter.AppendArgument("path", path);
                    context.MessageFormatter.AppendArgument("user", ProcessUserInfo.GetUserNameWithIds());
                    return false;
                }

                // Try to list the directory contents to verify read access
                var files = _diskProvider.GetFileInfos(path, false);

                // If we can get the file list, we have read access
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // Add more specific error information
                _errorMessage = "Folder '{path}' is not readable by user '{user}'. Permission denied - please ensure the Chaptarr process has read access to this directory and its subdirectories.{dockerHint}";
                context.MessageFormatter.AppendArgument("path", path);
                context.MessageFormatter.AppendArgument("user", ProcessUserInfo.GetUserNameWithIds());
                return false;
            }
            catch (Exception)
            {
                // Other exceptions mean we can't read the folder
                _errorMessage = "Folder '{path}' is not accessible. An error occurred while checking folder permissions.";
                return false;
            }
        }
    }
}
