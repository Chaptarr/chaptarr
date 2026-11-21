using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Processes;

namespace NzbDrone.Host
{
    public class SingleInstancePolicy
    {
        private readonly IProcessProvider _processProvider;
        private readonly Action _launchWebUI;
        private readonly Logger _logger;

        private SingleInstancePolicy(IProcessProvider processProvider, Action launchWebUI, Logger logger)
        {
            _processProvider = processProvider;
            _launchWebUI = launchWebUI;
            _logger = logger;
        }

        public static FileStream AcquireAppData(IAppFolderInfo folders, IStartupContext startupContext,
            IProcessProvider processProvider, Action launchWebUI, Logger logger)
        {
            // Existing-process actions must run before ownership, and ownership must precede any AppData mutation.
            if (!startupContext.Flags.Contains(StartupContext.RESTART) &&
                !startupContext.Flags.Contains(StartupContext.NO_SINGLE_INSTANCE_CHECK))
            {
                var policy = new SingleInstancePolicy(processProvider, launchWebUI, logger);
                if (startupContext.Flags.Contains(StartupContext.TERMINATE))
                {
                    policy.KillAllOtherInstance();
                }
                else if (startupContext.Args.ContainsKey(StartupContext.APPDATA))
                {
                    policy.WarnIfAlreadyRunning();
                }
                else
                {
                    policy.PreventStartIfAlreadyRunning();
                }
            }

            return AcquireAppData(folders);
        }

        public static FileStream AcquireAppData(IAppFolderInfo folders)
        {
            Directory.CreateDirectory(folders.AppDataFolder);
            try
            {
                // Keep the file in place: unlinking a held lock allows another process to lock a different inode.
                var stream = new FileStream(folders.GetAppDataLockFile(), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    OperatingSystem.IsWindows() ? FileShare.ReadWrite : FileShare.None);
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        stream.Lock(0, 1);
                    }
                    return stream;
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            catch (IOException ex)
            {
                throw new TerminateApplicationException($"Cannot exclusively open the data directory '{folders.AppDataFolder}'. Another instance may be using it. {ex.Message}");
            }
        }

        private void PreventStartIfAlreadyRunning()
        {
            if (IsAlreadyRunning())
            {
                _logger.Warn("Another instance of Chaptarr is already running.");
                _launchWebUI();
                throw new TerminateApplicationException("Another instance is already running");
            }
        }

        private void KillAllOtherInstance()
        {
            foreach (var processId in GetOtherNzbDroneProcessIds())
            {
                _processProvider.Kill(processId);
            }
        }

        private void WarnIfAlreadyRunning()
        {
            if (IsAlreadyRunning())
            {
                _logger.Debug("Another instance of Chaptarr is already running.");
            }
        }

        private bool IsAlreadyRunning()
        {
            return GetOtherNzbDroneProcessIds().Any();
        }

        private List<int> GetOtherNzbDroneProcessIds()
        {
            try
            {
                var currentId = _processProvider.GetCurrentProcess().Id;

                var otherProcesses = _processProvider.FindProcessByName(ProcessProvider.CHAPTARR_CONSOLE_PROCESS_NAME)
                                                     .Union(_processProvider.FindProcessByName(ProcessProvider.CHAPTARR_PROCESS_NAME))
                                                     .Select(c => c.Id)
                                                     .Except(new[] { currentId })
                                                     .ToList();

                if (otherProcesses.Any())
                {
                    _logger.Info("{0} instance(s) of Chaptarr are running", otherProcesses.Count);
                }

                return otherProcesses;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to check for multiple instances of Chaptarr.");
                return new List<int>();
            }
        }
    }
}
