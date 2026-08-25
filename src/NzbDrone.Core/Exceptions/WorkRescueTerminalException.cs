using NzbDrone.Common.Exceptions;

namespace NzbDrone.Core.Exceptions
{
    public class WorkRescueTerminalException : NzbDroneException
    {
        public string ProviderId { get; }

        public WorkRescueTerminalException(string providerId, string message)
            : base("{0}", message)
        {
            ProviderId = providerId;
        }
    }
}
