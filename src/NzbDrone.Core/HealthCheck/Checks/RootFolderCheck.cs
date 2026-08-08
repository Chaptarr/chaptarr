using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.HealthCheck.Checks
{
    [CheckOn(typeof(AuthorDeletedEvent))]
    [CheckOn(typeof(AuthorMovedEvent))]
    [CheckOn(typeof(TrackImportedEvent), CheckOnCondition.FailedOnly)]
    [CheckOn(typeof(TrackImportFailedEvent), CheckOnCondition.SuccessfulOnly)]
    public class RootFolderCheck : HealthCheckBase
    {
        private readonly IAuthorService _authorService;
        private readonly IImportListFactory _importListFactory;
        private readonly IDiskProvider _diskProvider;
        private readonly IRootFolderService _rootFolderService;

        public RootFolderCheck(IAuthorService authorService, IImportListFactory importListFactory, IDiskProvider diskProvider, IRootFolderService rootFolderService, ILocalizationService localizationService)
            : base(localizationService)
        {
            _authorService = authorService;
            _importListFactory = importListFactory;
            _diskProvider = diskProvider;
            _rootFolderService = rootFolderService;
        }

        public override HealthCheck Check()
        {
            var rootFolders = _authorService.AllAuthorPaths()
                                                           .Select(s => _rootFolderService.GetBestRootFolderPath(s.Value))
                                                           .Distinct();

            var missingRootFolders = rootFolders.Where(s => !s.IsPathValid(PathValidationType.CurrentOs) || !SafeFolderExists(s))
                .ToList();

            missingRootFolders.AddRange(_importListFactory.All()
                .Where(l => l.Enable)
                .SelectMany(GetImportListRootFolders)
                .Distinct()
                .Where(s => !s.IsPathValid(PathValidationType.CurrentOs) || !SafeFolderExists(s))
                .ToList());

            missingRootFolders = missingRootFolders.Distinct().ToList();

            if (missingRootFolders.Any())
            {
                if (missingRootFolders.Count == 1)
                {
                    return new HealthCheck(GetType(), HealthCheckResult.Error, string.Format(_localizationService.GetLocalizedString("RootFolderCheckSingleMessage"), missingRootFolders.First()), "#missing-root-folder");
                }

                var message = string.Format(_localizationService.GetLocalizedString("RootFolderCheckMultipleMessage"), string.Join(" | ", missingRootFolders));
                return new HealthCheck(GetType(), HealthCheckResult.Error, message, "#missing-root-folder");
            }

            return new HealthCheck(GetType());
        }

        private bool SafeFolderExists(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                return _diskProvider.FolderExists(path);
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<string> GetImportListRootFolders(ImportListDefinition importList)
        {
            if (importList == null)
            {
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(importList.RootFolderPath))
            {
                yield return importList.RootFolderPath;
            }

            if (importList.Settings is IMediaTypeRootFolderSettings mediaSettings)
            {
                if (!string.IsNullOrWhiteSpace(mediaSettings.AudiobookRootFolderPath))
                {
                    yield return mediaSettings.AudiobookRootFolderPath;
                }

                if (!string.IsNullOrWhiteSpace(mediaSettings.EbookRootFolderPath))
                {
                    yield return mediaSettings.EbookRootFolderPath;
                }
            }
        }
    }
}
