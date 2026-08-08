using System;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Exceptions;

namespace NzbDrone.Core.ThingiProvider
{
    public interface IPendingProviderSecretService
    {
        string Create(string secret);
        string Resolve(string value, bool consume);
        bool IsPendingSecret(string value);
    }

    public class PendingProviderSecretService : IPendingProviderSecretService
    {
        private const string Prefix = "chaptarr-pending-secret:";
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

        private readonly ICached<string> _cache;

        public PendingProviderSecretService(ICacheManager cacheManager)
        {
            _cache = cacheManager.GetCache<string>(GetType());
        }

        public string Create(string secret)
        {
            if (secret.IsNullOrWhiteSpace())
            {
                return secret;
            }

            var key = Guid.NewGuid().ToString("N");
            _cache.Set(key, secret, Lifetime);

            return Prefix + key;
        }

        public string Resolve(string value, bool consume)
        {
            if (!IsPendingSecret(value))
            {
                return value;
            }

            var key = value.Substring(Prefix.Length);
            var secret = _cache.Find(key);

            if (secret.IsNullOrWhiteSpace())
            {
                throw new BadRequestException("Provider authorization expired. Please sign in again.");
            }

            if (consume)
            {
                _cache.Remove(key);
            }

            return secret;
        }

        public bool IsPendingSecret(string value)
        {
            return value.IsNotNullOrWhiteSpace() &&
                   value.StartsWith(Prefix, StringComparison.Ordinal);
        }
    }
}
