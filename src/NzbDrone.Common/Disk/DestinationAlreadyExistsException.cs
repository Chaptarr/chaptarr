using System;
using System.IO;
using System.Runtime.Serialization;

namespace NzbDrone.Common.Disk
{
    public class DestinationAlreadyExistsException : IOException
    {
        public DestinationAlreadyExistsException()
        {
        }

        public DestinationAlreadyExistsException(string message)
            : base(message)
        {
        }

        public DestinationAlreadyExistsException(string message, int hresult)
            : base(message, hresult)
        {
        }

        public DestinationAlreadyExistsException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
        // Hopefully this constructor is unused, since its deprecated now in the base class
        // protected DestinationAlreadyExistsException(SerializationInfo info, StreamingContext context)
        //     : base(info, context)
        // {
        // }
    }
}
