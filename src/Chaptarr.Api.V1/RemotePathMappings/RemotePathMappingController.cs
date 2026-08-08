using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Validation.Paths;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.RemotePathMappings
{
    [V1ApiController]
    public class RemotePathMappingController : RestController<RemotePathMappingResource>
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly IRemotePathMappingService _remotePathMappingService;
        private readonly IDownloadClientFactory _downloadClientFactory;
        private readonly IRootFolderService _rootFolderService;
        private readonly IDiskProvider _diskProvider;

        public RemotePathMappingController(IRemotePathMappingService remotePathMappingService,
                                       IDownloadClientFactory downloadClientFactory,
                                       IRootFolderService rootFolderService,
                                       IDiskProvider diskProvider,
                                       PathExistsValidator pathExistsValidator,
                                       MappedNetworkDriveValidator mappedNetworkDriveValidator)
        {
            _remotePathMappingService = remotePathMappingService;
            _downloadClientFactory = downloadClientFactory;
            _rootFolderService = rootFolderService;
            _diskProvider = diskProvider;

            SharedValidator.RuleFor(c => c.Host)
                           .NotEmpty()
                           .When(c => c.DownloadClientId <= 0);

            SharedValidator.RuleFor(c => c.DownloadClientId)
                           .GreaterThanOrEqualTo(0);

            // We cannot use IsValidPath here, because it's a remote path, possibly other OS.
            SharedValidator.RuleFor(c => c.RemotePath)
                           .NotEmpty();

            SharedValidator.RuleFor(c => c.LocalPath)
                .Cascade(CascadeMode.Stop)
                .IsValidPath()
                .SetValidator(mappedNetworkDriveValidator)
                .SetValidator(pathExistsValidator)
                .SetValidator(new SystemFolderValidator())
                .NotEqual("/")
                .WithMessage("Cannot be set to '/'");
        }

        protected override RemotePathMappingResource GetResourceById(int id)
        {
            return _remotePathMappingService.Get(id).ToResource();
        }

        [RestPostById]
        [Consumes("application/json")]
        public ActionResult<RemotePathMappingResource> CreateMapping([FromBody] RemotePathMappingResource resource)
        {
            var model = resource.ToModel();

            return Created(_remotePathMappingService.Add(model).Id);
        }

        [HttpGet]
        [Produces("application/json")]
        public List<RemotePathMappingResource> GetMappings()
        {
            return _remotePathMappingService.All().ToResource();
        }

        [HttpGet("suggestions")]
        [Produces("application/json")]
        public RemotePathMappingSuggestionsResource GetSuggestions([FromQuery] int downloadClientId = 0, [FromQuery] string host = null)
        {
            var mappings = _remotePathMappingService.All();
            var result = new RemotePathMappingSuggestionsResource
            {
                ChaptarrPaths = BuildChaptarrPathSuggestions(mappings),
                DownloadClientPaths = BuildDownloadClientPathSuggestions(mappings, downloadClientId, host)
            };
            if (downloadClientId > 0)
            {
                var downloadClientDefinitions = new List<DownloadClientDefinition>();

                try
                {
                    downloadClientDefinitions = GetSuggestionDownloadClients(downloadClientId);
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "Unable to load download client for remote path mapping suggestions.");
                }

                foreach (var definition in downloadClientDefinitions)
                {
                    try
                    {
                        var client = _downloadClientFactory.GetInstance(definition);
                        var status = client.GetStatus();
                        var clientHost = GetDownloadClientHost(definition);
                        var outputRootFolders = status?.OutputRootFolders ?? new List<OsPath>();

                        result.DownloadClientPaths = result.DownloadClientPaths
                            .Concat(outputRootFolders
                                .Where(p => !p.IsEmpty)
                                .Select(p => _remotePathMappingService.RemapLocalToRemote(definition.Id, clientHost, p).FullPath))
                            .ToList();
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug(ex, "Unable to probe download client {0} for remote path mapping suggestions.", definition.Name);
                    }
                }
            }

            result.DownloadClientPaths = NormalizeSuggestions(result.DownloadClientPaths);

            return result;
        }

        [RestDeleteById]
        public void DeleteMapping(int id)
        {
            _remotePathMappingService.Remove(id);
        }

        [RestPutById]
        public ActionResult<RemotePathMappingResource> UpdateMapping([FromBody] RemotePathMappingResource resource)
        {
            var mapping = resource.ToModel();

            return Accepted(_remotePathMappingService.Update(mapping).ToResource());
        }

        [HttpPost("test")]
        [Consumes("application/json")]
        [Produces("application/json")]
        public ActionResult<RemotePathMappingTestResource> TestMapping([FromBody] RemotePathMappingTestResource resource)
        {
            var result = _remotePathMappingService.Test(resource.ToModel()).ToResource();

            EnrichDownloadClientTest(result);

            return result;
        }

        private List<string> BuildChaptarrPathSuggestions(List<RemotePathMapping> mappings)
        {
            return NormalizeSuggestions(
                mappings.Select(m => m.LocalPath)
                    .Concat(_rootFolderService.All().Select(r => r.Path)));
        }

        private List<string> BuildDownloadClientPathSuggestions(List<RemotePathMapping> mappings, int downloadClientId, string host)
        {
            var scoped = mappings.Where(m =>
                    downloadClientId > 0 ?
                        m.DownloadClientId == downloadClientId :
                        m.DownloadClientId == 0 &&
                        (string.IsNullOrWhiteSpace(host) || host.Equals(m.Host, StringComparison.InvariantCultureIgnoreCase)))
                .Select(m => m.RemotePath);

            return NormalizeSuggestions(scoped);
        }

        private static List<string> NormalizeSuggestions(IEnumerable<string> suggestions)
        {
            return suggestions
                .Where(p => !string.IsNullOrWhiteSpace(p) && p != "/")
                .Distinct(StringComparer.InvariantCultureIgnoreCase)
                .OrderBy(p => p)
                .ToList();
        }

        private static string GetDownloadClientHost(DownloadClientDefinition definition)
        {
            return definition?.Settings?.GetType().GetProperty("Host")?.GetValue(definition.Settings)?.ToString();
        }

        private List<DownloadClientDefinition> GetSuggestionDownloadClients(int downloadClientId)
        {
            if (downloadClientId <= 0)
            {
                return new List<DownloadClientDefinition>();
            }

            return new List<DownloadClientDefinition> { _downloadClientFactory.Get(downloadClientId) };
        }

        private void EnrichDownloadClientTest(RemotePathMappingTestResource result)
        {
            if (result.DownloadClientId <= 0)
            {
                return;
            }

            var downloadClientErrors = new List<string>();
            var observedRoots = new List<OsPath>();
            var observedItems = new List<OsPath>();
            var downloadClientPathChecked = false;

            List<DownloadClientDefinition> downloadClientDefinitions;

            try
            {
                downloadClientDefinitions = GetSuggestionDownloadClients(result.DownloadClientId);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Unable to load download client for remote path mapping test.");
                result.DownloadClientTestError = "Could not probe the selected download client. See logs for details.";
                return;
            }

            foreach (var definition in downloadClientDefinitions)
            {
                IDownloadClient client;
                var clientHost = GetDownloadClientHost(definition);

                try
                {
                    client = _downloadClientFactory.GetInstance(definition);
                }
                catch (Exception ex)
                {
                    downloadClientErrors.Add(definition.Name);
                    Logger.Debug(ex, "Unable to create download client {0} for remote path mapping test.", definition.Name);
                    continue;
                }

                try
                {
                    var status = client.GetStatus();

                    observedRoots.AddRange((status?.OutputRootFolders ?? new List<OsPath>())
                        .Where(p => !p.IsEmpty)
                        .Select(p => _remotePathMappingService.RemapLocalToRemote(definition.Id, clientHost, p)));
                    downloadClientPathChecked = true;
                }
                catch (Exception ex)
                {
                    downloadClientErrors.Add(definition.Name);
                    Logger.Debug(ex, "Unable to probe download client {0} status for remote path mapping test.", definition.Name);
                }

                try
                {
                    observedItems.AddRange(client.GetItems()
                        .Where(i => !i.OutputPath.IsEmpty)
                        .Select(i => _remotePathMappingService.RemapLocalToRemote(definition.Id, clientHost, i.OutputPath)));
                }
                catch (Exception ex)
                {
                    downloadClientErrors.Add(definition.Name);
                    Logger.Debug(ex, "Unable to probe download client {0} items for remote path mapping test.", definition.Name);
                }
            }

            result.DownloadClientPathChecked = downloadClientPathChecked;

            var remotePath = new OsPath(result.RemotePath);
            var matchedRoot = observedRoots.FirstOrDefault(p => PathsOverlap(remotePath, p));

            if (!matchedRoot.IsEmpty)
            {
                result.DownloadClientPathMatched = true;
                result.DownloadClientMatchedPath = matchedRoot.FullPath;
            }

            var matchedItem = observedItems.FirstOrDefault(p => remotePath.Contains(p));

            if (!matchedItem.IsEmpty)
            {
                var mappedItemPath = MapWithCandidate(result, matchedItem);
                var mappedItemPathExists = _diskProvider.FolderExists(mappedItemPath) || _diskProvider.FileExists(mappedItemPath);
                var writablePath = _diskProvider.FolderExists(mappedItemPath) ? mappedItemPath : new OsPath(mappedItemPath).Directory.FullPath;

                result.DownloadClientItemPathChecked = true;
                result.DownloadClientItemMappedPath = mappedItemPath;
                result.DownloadClientItemPathExists = mappedItemPathExists;
                result.DownloadClientItemPathWritable = mappedItemPathExists && _diskProvider.FolderWritable(writablePath);
            }

            result.DownloadClientTestError = downloadClientErrors.Any() ?
                $"Could not probe {downloadClientErrors.Distinct(StringComparer.InvariantCultureIgnoreCase).Count()} download client(s). See logs for details." :
                null;
        }

        private static bool PathsOverlap(OsPath first, OsPath second)
        {
            return !first.IsEmpty && !second.IsEmpty && (first.Contains(second) || second.Contains(first));
        }

        private static string MapWithCandidate(RemotePathMappingTestResource candidate, OsPath remotePath)
        {
            var remoteRoot = new OsPath(candidate.RemotePath);

            if (!remoteRoot.Contains(remotePath))
            {
                return candidate.MappedPath;
            }

            return (new OsPath(candidate.LocalPath) + (remotePath - remoteRoot)).FullPath;
        }
    }
}
