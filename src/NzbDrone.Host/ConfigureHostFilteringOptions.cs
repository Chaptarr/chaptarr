using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Options;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Host
{
    public class ConfigureHostFilteringOptions : IConfigureOptions<HostFilteringOptions>
    {
        private readonly IConfigFileProvider _configFileProvider;
        private readonly Logger _logger;

        public ConfigureHostFilteringOptions(IConfigFileProvider configFileProvider, Logger logger)
        {
            _configFileProvider = configFileProvider;
            _logger = logger;
        }

        public void Configure(HostFilteringOptions options)
        {
            var allowedHosts = AllowedHostsParser.Parse(_configFileProvider.AllowedHosts);
            var bindAddresses = allowedHosts.Where(AllowedHostsParser.IsBindAddress).ToList();

            if (bindAddresses.Any())
            {
                _logger.Warn("Ignoring {0} in Allowed Hosts, bind addresses are not host names", string.Join(", ", bindAddresses));

                allowedHosts = allowedHosts.Except(bindAddresses).ToList();
            }

            options.AllowEmptyHosts = true;

            if (allowedHosts.Empty())
            {
                options.AllowedHosts = new List<string> { "*" };

                _logger.Info("Allowed Hosts is not configured, accepting requests for any host");

                return;
            }

            var recoveryHosts = new List<string> { "localhost", "127.0.0.1", "[::1]", Environment.MachineName };
            try
            {
                recoveryHosts.Add(Dns.GetHostName());
            }
            catch (Exception error)
            {
                _logger.Warn(error, "Unable to obtain the server hostname; localhost and the machine name remain allowed");
            }

            options.AllowedHosts = allowedHosts.Concat(recoveryHosts.Where(host => !string.IsNullOrWhiteSpace(host)))
                                              .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            _logger.Info("Accepting requests for hosts: {0}", string.Join(", ", options.AllowedHosts));
        }

    }
}
