using System;
using System.Collections.Generic;
using System.Net;

namespace NzbDrone.Core.Exceptions
{
    public class AuthorTerminalException : NzbDroneClientException
    {
        private static readonly HashSet<string> KnownCodes = new(StringComparer.Ordinal)
        {
            "author_provider_unsupported",
            "author_identifier_not_author",
            "author_provider_record_missing",
            "author_no_primary_works",
            "author_identity_ambiguous"
        };

        public string Code { get; }
        public string ProviderId { get; }
        public string ResolvedProviderId { get; }
        public bool Reopenable { get; }

        public AuthorTerminalException(string code, string providerId, string resolvedProviderId, string message, bool reopenable)
            : base(HttpStatusCode.UnprocessableEntity, "{0}: {1}", code, message)
        {
            if (!IsKnownCode(code))
            {
                throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown author terminal code");
            }

            Code = code;
            ProviderId = providerId;
            ResolvedProviderId = resolvedProviderId;
            Reopenable = reopenable;
        }

        public static bool IsKnownCode(string code)
        {
            return !string.IsNullOrWhiteSpace(code) && KnownCodes.Contains(code);
        }
    }
}
