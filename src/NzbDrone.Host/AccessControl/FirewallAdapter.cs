using System;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Host.AccessControl
{
    public interface IFirewallAdapter
    {
        void MakeAccessible();
    }

    public class FirewallAdapter : IFirewallAdapter
    {
        private readonly IConfigFileProvider _configFileProvider;
        private readonly Logger _logger;

        public FirewallAdapter(IConfigFileProvider configFileProvider, Logger logger)
        {
            _configFileProvider = configFileProvider;
            _logger = logger;
        }

        public void MakeAccessible()
        {
            if (OsInfo.IsNotWindows)
            {
                return;
            }

            if (IsFirewallEnabled())
            {
                if (!IsNzbDronePortOpen(_configFileProvider.Port))
                {
                    _logger.Debug("Opening Port for Chaptarr: {0}", _configFileProvider.Port);
                    OpenFirewallPort(_configFileProvider.Port);
                }

                if (_configFileProvider.EnableSsl && !IsNzbDronePortOpen(_configFileProvider.SslPort))
                {
                    _logger.Debug("Opening SSL Port for Chaptarr: {0}", _configFileProvider.SslPort);
                    OpenFirewallPort(_configFileProvider.SslPort);
                }
            }
        }

        private bool IsNzbDronePortOpen(int port)
        {
            if (OsInfo.IsNotWindows)
            {
                return false;
            }

            try
            {
                var netFwMgrType = Type.GetTypeFromProgID("HNetCfg.FwMgr", false);
                if (netFwMgrType == null)
                {
                    return false;
                }

                var mgr = Activator.CreateInstance(netFwMgrType);
                if (mgr == null)
                {
                    return false;
                }

                // Use dynamic to avoid compile-time dependency on Windows types
                dynamic localPolicy = ((dynamic)mgr).LocalPolicy;
                dynamic profile = localPolicy.GetProfileByType(1); // NET_FW_PROFILE_STANDARD = 1
                dynamic ports = profile.GloballyOpenPorts;

                foreach (var p in ports)
                {
                    if (p.Port == port)
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to check for open port in firewall");
            }

            return false;
        }

        private void OpenFirewallPort(int portNumber)
        {
            if (OsInfo.IsNotWindows)
            {
                return;
            }

            try
            {
                var type = Type.GetTypeFromProgID("HNetCfg.FWOpenPort", false);
                if (type == null)
                {
                    return;
                }

                var port = Activator.CreateInstance(type);
                if (port == null)
                {
                    return;
                }

                // Use dynamic to avoid compile-time dependency on Windows types
                dynamic portObj = port;
                portObj.Port = portNumber;
                portObj.Name = "Chaptarr";
                portObj.Protocol = 6; // NET_FW_IP_PROTOCOL_TCP = 6
                portObj.Enabled = true;

                var netFwMgrType = Type.GetTypeFromProgID("HNetCfg.FwMgr", false);
                if (netFwMgrType == null)
                {
                    return;
                }

                var mgr = Activator.CreateInstance(netFwMgrType);
                if (mgr == null)
                {
                    return;
                }

                dynamic localPolicy = ((dynamic)mgr).LocalPolicy;
                dynamic profile = localPolicy.GetProfileByType(1); // NET_FW_PROFILE_STANDARD = 1
                profile.GloballyOpenPorts.Add(portObj);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to open port in firewall for Chaptarr " + portNumber);
            }
        }

        private bool IsFirewallEnabled()
        {
            if (OsInfo.IsNotWindows)
            {
                return false;
            }

            try
            {
                var netFwMgrType = Type.GetTypeFromProgID("HNetCfg.FwMgr", false);
                if (netFwMgrType == null)
                {
                    return false;
                }

                var mgr = Activator.CreateInstance(netFwMgrType);
                if (mgr == null)
                {
                    return false;
                }

                dynamic localPolicy = ((dynamic)mgr).LocalPolicy;
                dynamic profile = localPolicy.GetProfileByType(1); // NET_FW_PROFILE_STANDARD = 1
                return profile.FirewallEnabled;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to check if the firewall is enabled");
                return false;
            }
        }
    }
}
