using System;
using System.Collections.Generic;
using NLog;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Host
{
    public class FirstRunAuthenticationSetup
    {
        private readonly IConfigFileProvider _configFileProvider;
        private readonly Lazy<IUserService> _userService;
        private readonly Logger _logger;

        public FirstRunAuthenticationSetup(IConfigFileProvider configFileProvider, Lazy<IUserService> userService, Logger logger)
        {
            _configFileProvider = configFileProvider;
            _userService = userService;
            _logger = logger;
        }

        public void EnsureInitialAccountSetup()
        {
            if (_configFileProvider.AuthenticationMethod == AuthenticationType.None || _userService.Value.FindUser() != null)
            {
                return;
            }

            _configFileProvider.SaveConfigDictionary(new Dictionary<string, object>
            {
                { nameof(IConfigFileProvider.AuthenticationMethod), AuthenticationType.None }
            });

            if (_configFileProvider.AuthenticationMethod == AuthenticationType.None)
            {
                _logger.Info("Disabled authentication because no user account exists. The initial authentication setup will be shown in the UI.");
            }
            else
            {
                _logger.Warn("No user account exists, but the configured authentication method could not be changed. Review the authentication configuration before exposing this listener.");
            }
        }
    }
}
