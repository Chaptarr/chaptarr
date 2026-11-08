using System;

namespace NzbDrone.Core.Exceptions
{
    public sealed class AuthorIdentityUnverifiedException : InvalidOperationException
    {
        public bool IsTransient { get; }

        public AuthorIdentityUnverifiedException(string message, bool isTransient = true) : base(message)
        {
            IsTransient = isTransient;
        }

        public AuthorIdentityUnverifiedException(string message, Exception innerException) : base(message, innerException)
        {
            IsTransient = false;
        }
    }
}
