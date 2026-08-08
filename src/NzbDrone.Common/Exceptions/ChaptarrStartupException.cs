using System;

namespace NzbDrone.Common.Exceptions
{
    public class ChaptarrStartupException : NzbDroneException
    {
        public ChaptarrStartupException(string message, params object[] args)
            : base("Chaptarr failed to start: " + string.Format(message, args))
        {
        }

        public ChaptarrStartupException(string message)
            : base("Chaptarr failed to start: " + message)
        {
        }

        public ChaptarrStartupException()
            : base("Chaptarr failed to start")
        {
        }

        public ChaptarrStartupException(Exception innerException, string message, params object[] args)
            : base("Chaptarr failed to start: " + string.Format(message, args), innerException)
        {
        }

        public ChaptarrStartupException(Exception innerException, string message)
            : base("Chaptarr failed to start: " + message, innerException)
        {
        }

        public ChaptarrStartupException(Exception innerException)
            : base("Chaptarr failed to start: " + innerException.Message)
        {
        }
    }
}
