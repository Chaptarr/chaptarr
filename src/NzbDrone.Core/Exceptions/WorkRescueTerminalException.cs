using System.Net;

namespace NzbDrone.Core.Exceptions
{
    public class WorkRescueTerminalException : NzbDroneClientException
    {
        public string ProviderId { get; }

        public WorkRescueTerminalException(string providerId, string message)
            : base(HttpStatusCode.UnprocessableEntity, "{0}", message)
        {
            ProviderId = providerId;
        }
    }
}
