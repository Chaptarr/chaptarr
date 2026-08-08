using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Chaptarr.Http.Authentication;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Http.Authentication
{
    public class UiAuthorizationPolicyProvider : IAuthorizationPolicyProvider
    {
        private const string POLICY_NAME = "UI";
        private readonly IConfigFileProvider _config;

        public DefaultAuthorizationPolicyProvider FallbackPolicyProvider { get; }

        public UiAuthorizationPolicyProvider(IOptions<AuthorizationOptions> options,
            IConfigFileProvider config)
        {
            FallbackPolicyProvider = new DefaultAuthorizationPolicyProvider(options);
            _config = config;
        }

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => FallbackPolicyProvider.GetDefaultPolicyAsync();

        public Task<AuthorizationPolicy> GetFallbackPolicyAsync() => FallbackPolicyProvider.GetFallbackPolicyAsync();

        public Task<AuthorizationPolicy> GetPolicyAsync(string policyName)
        {
            if (policyName.Equals(POLICY_NAME, StringComparison.OrdinalIgnoreCase))
            {
                // Use a stable auth scheme so AuthorizationMiddleware caching does not "freeze" the auth method
                // until restart. The policy scheme dynamically forwards to the configured method per request.
                var policy = new AuthorizationPolicyBuilder(AuthenticationBuilderExtensions.UiAuthScheme)
                    .AddRequirements(new BypassableDenyAnonymousAuthorizationRequirement());

                return Task.FromResult(policy.Build());
            }

            return FallbackPolicyProvider.GetPolicyAsync(policyName);
        }
    }
}
