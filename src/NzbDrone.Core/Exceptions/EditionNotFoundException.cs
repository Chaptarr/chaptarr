using NzbDrone.Common.Exceptions;

namespace NzbDrone.Core.Exceptions
{
    public class EditionNotFoundException : NzbDroneException
    {
        public string ForeignEditionId { get; set; }

        public EditionNotFoundException(string foreignEditionId)
            : base($"Edition with id {foreignEditionId} was not found, it may have been removed from metadata server.")
        {
            ForeignEditionId = foreignEditionId;
        }

        public EditionNotFoundException(string foreignEditionId, string message, params object[] args)
            : base(message, args)
        {
            ForeignEditionId = foreignEditionId;
        }

        public EditionNotFoundException(string foreignEditionId, string message)
            : base(message)
        {
            ForeignEditionId = foreignEditionId;
        }
    }
}
