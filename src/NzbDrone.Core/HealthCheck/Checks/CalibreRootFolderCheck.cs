using System;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Localization;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.HealthCheck.Checks
{
    [CheckOn(typeof(ModelEvent<RootFolder>))]
    [CheckOn(typeof(ModelEvent<RemotePathMapping>))]
    public class CalibreRootFolderCheck : HealthCheckBase
    {
        private readonly IDiskProvider _diskProvider;
        private readonly IRootFolderService _rootFolderService;
        private readonly ICalibreProxy _calibreProxy;
        private readonly Logger _logger;
        private readonly IOsInfo _osInfo;

        public CalibreRootFolderCheck(IDiskProvider diskProvider,
                                      IRootFolderService rootFolderService,
                                      ICalibreProxy calibreProxy,
                                      IOsInfo osInfo,
                                      Logger logger,
                                      ILocalizationService localizationService)
            : base(localizationService)
        {
            _diskProvider = diskProvider;
            _rootFolderService = rootFolderService;
            _calibreProxy = calibreProxy;
            _logger = logger;
            _osInfo = osInfo;
        }

        public override HealthCheck Check()
        {
            var rootFolders = _rootFolderService.All().Where(x => x.IsCalibreLibrary);

            HealthCheck warning = null;
            HealthCheck error = null;
            void RecordError(string message, string wikiFragment)
            {
                _logger.Warn("{0}", message);
                error ??= new HealthCheck(GetType(), HealthCheckResult.Error, message, wikiFragment);
            }

            foreach (var folder in rootFolders)
            {
                try
                {
                    var calibreIsLocal = folder.CalibreSettings.Host == "127.0.0.1" || folder.CalibreSettings.Host == "localhost";

                    // Retain the released identity-cache producer until scans resolve Calibre IDs themselves.
                    var files = _calibreProxy.GetAllBookFilePaths(folder.CalibreSettings);
                    if (files.Any())
                    {
                        var file = files.First();

                        // This directory structure is forced by calibre
                        var bookFolder = Path.GetDirectoryName(file);
                        var authorFolder = Path.GetDirectoryName(bookFolder);
                        var libraryFolder = Path.GetDirectoryName(authorFolder);

                        var osPath = new OsPath(libraryFolder);

                        if (!osPath.IsValid)
                        {
                            if (!calibreIsLocal)
                            {
                                RecordError($"Remote calibre for root folder {folder.Name} reports files in {libraryFolder} but this is not a valid {_osInfo.Name} path.  Review your remote path mappings and root folder settings.", "#bad-remote-path-mapping");
                                continue;
                            }
                            else if (_osInfo.IsDocker)
                            {
                                RecordError($"You are using docker; calibre for root folder {folder.Name} reports files in {libraryFolder} but this is not a valid {_osInfo.Name} path.  Review your remote path mappings and download client settings.", "#docker-bad-remote-path-mapping");
                                continue;
                            }
                            else
                            {
                                RecordError($"Local calibre server for root folder {folder.Name} reports files in {libraryFolder} but this is not a valid {_osInfo.Name} path.  Review your download client settings.", "#bad-download-client-settings");
                                continue;
                            }
                        }

                        if (!_diskProvider.FolderExists(libraryFolder))
                        {
                            if (_osInfo.IsDocker)
                            {
                                RecordError($"You are using docker; calibre server for root folder {folder.Name} places downloads in {libraryFolder} but this directory does not appear to exist inside the container.  Review your remote path mappings and container volume settings.", "#docker-bad-remote-path-mapping");
                                continue;
                            }
                            else if (!calibreIsLocal)
                            {
                                RecordError($"Remote calibre server for root folder {folder.Name} places downloads in {libraryFolder} but this directory does not appear to exist.  Likely missing or incorrect remote path mapping.", "#bad-remote-path-mapping");
                                continue;
                            }
                            else
                            {
                                RecordError($"Calibre server for root folder {folder.Name} places downloads in {libraryFolder} but Chaptarr cannot see this directory.  You may need to adjust the folder's permissions or add a remote path mapping if calibre is running in docker", "#permissions-error");
                                continue;
                            }
                        }

                        if (!_diskProvider.FileExists(file))
                        {
                            if (_osInfo.IsDocker)
                            {
                                RecordError($"You are using docker; calibre server for root folder {folder.Name} listed file {file} but this file does not appear to exist inside the container.  Review permissions for {libraryFolder} and PUID/PGID container settings", "#docker-bad-remote-path-mapping");
                                continue;
                            }
                            else if (!calibreIsLocal)
                            {
                                RecordError($"Remote calibre server for root folder {folder.Name} listed file {file} but this file does not appear to exist.  Review permissions for {libraryFolder}", "#permissions-error");
                                continue;
                            }
                            else
                            {
                                RecordError($"Calibre server for root folder {folder.Name} listed file {file} but Chaptarr cannot see this file.  Review permissions for {libraryFolder}", "#permissions-error");
                                continue;
                            }
                        }

                        if (!libraryFolder.PathEquals(folder.Path))
                        {
                            RecordError($"Calibre for root folder {folder.Name} reports files in {libraryFolder} but this is not the same as the root folder path {folder.Path} you chose.  You may need to edit any remote path mapping or delete the root folder and re-create with the correct path", "#calibre-root-does-not-match");
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warn("Unable to check Calibre root folder {0}: {1}", folder.Name, ex is CalibreException ? ex.Message : CalibreLibraryGate.GetFailureReason(ex, libraryRequest: true));
                    _logger.Debug(ex, "Calibre root-folder check failed for {0}", folder.Name);
                    warning ??= new HealthCheck(GetType(), HealthCheckResult.Warning,
                        $"Unable to check Calibre root folder {folder.Name}. Check the Calibre connection and settings.");
                }
            }

            return error ?? warning ?? new HealthCheck(GetType());
        }
    }
}
