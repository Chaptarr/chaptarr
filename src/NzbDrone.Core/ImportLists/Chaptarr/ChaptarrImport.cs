using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.ImportLists.Chaptarr
{
    public class ChaptarrImport : ImportListBase<ChaptarrSettings>
    {
        private readonly IChaptarrV1Proxy _chaptarrV1Proxy;
        public override string Name => "Chaptarr";

        public override ImportListType ListType => ImportListType.Program;
        public override TimeSpan MinRefreshInterval => TimeSpan.FromMinutes(15);

        public ChaptarrImport(IChaptarrV1Proxy chaptarrV1Proxy,
                            IImportListStatusService importListStatusService,
                            IConfigService configService,
                            IParsingService parsingService,
                            Logger logger)
            : base(importListStatusService, configService, parsingService, logger)
        {
            _chaptarrV1Proxy = chaptarrV1Proxy;
        }

        public override IList<ImportListItemInfo> Fetch()
        {
            var authorsAndBooks = new List<ImportListItemInfo>();

            try
            {
                var remoteBooks = _chaptarrV1Proxy.GetBooks(Settings) ?? new List<ChaptarrBook>();
                var remoteAuthors = _chaptarrV1Proxy.GetAuthors(Settings) ?? new List<ChaptarrAuthor>();

                var authorDict = remoteAuthors
                    .Where(a => a != null)
                    .GroupBy(a => a.Id)
                    .ToDictionary(g => g.Key, g => g.First());

                foreach (var remoteBook in remoteBooks)
                {
                    if (remoteBook == null)
                    {
                        continue;
                    }

                    if (!authorDict.TryGetValue(remoteBook.AuthorId, out var remoteAuthor) || remoteAuthor == null)
                    {
                        _logger.Debug("Chaptarr import list: skipping remote book '{0}' because author id {1} was not present in authors payload", remoteBook.Title, remoteBook.AuthorId);
                        continue;
                    }

                    var mediaType = remoteBook.MediaType?.Trim().ToLowerInvariant();
                    var qualityProfileId = mediaType == "audiobook"
                        ? remoteAuthor.AudiobookQualityProfileId ?? remoteAuthor.QualityProfileId
                        : mediaType == "ebook"
                            ? remoteAuthor.EbookQualityProfileId ?? remoteAuthor.QualityProfileId
                            : remoteAuthor.QualityProfileId;
                    if (Settings.ProfileIds.Any() && !Settings.ProfileIds.Contains(qualityProfileId))
                    {
                        continue;
                    }

                    var remoteTags = remoteAuthor.Tags ?? new HashSet<int>();
                    if (Settings.TagIds.Any() && !Settings.TagIds.Any(remoteTags.Contains))
                    {
                        continue;
                    }

                    var rootFolderPath = mediaType == "audiobook"
                        ? remoteAuthor.AudiobookRootFolderPath ?? remoteAuthor.RootFolderPath ?? string.Empty
                        : mediaType == "ebook"
                            ? remoteAuthor.EbookRootFolderPath ?? remoteAuthor.RootFolderPath ?? string.Empty
                            : remoteAuthor.RootFolderPath ?? string.Empty;
                    if (Settings.RootFolderPaths.Any() && !Settings.RootFolderPaths.Any(rootFolder => rootFolderPath.ContainsIgnoreCase(rootFolder)))
                    {
                        continue;
                    }

                    var bookMonitored = mediaType == "audiobook"
                        ? remoteBook.AudiobookMonitored ?? remoteBook.Monitored
                        : mediaType == "ebook"
                            ? remoteBook.EbookMonitored ?? remoteBook.Monitored
                            : remoteBook.Monitored;
                    var authorMonitored = mediaType == "audiobook"
                        ? remoteAuthor.AudiobookMonitored ?? remoteAuthor.Monitored
                        : mediaType == "ebook"
                            ? remoteAuthor.EbookMonitored ?? remoteAuthor.Monitored
                            : remoteAuthor.Monitored;
                    if (!bookMonitored || !authorMonitored)
                    {
                        continue;
                    }

                    authorsAndBooks.Add(new ImportListItemInfo
                    {
                        BookProviderId = remoteBook.ForeignBookId,
                        Book = remoteBook.Title,
                        EditionProviderId = remoteBook.ForeignEditionId,
                        Author = remoteAuthor.AuthorName,
                        AuthorProviderId = remoteAuthor.ForeignAuthorId
                    });
                }

                _importListStatusService.RecordSuccess(Definition.Id);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "List Import Sync Task Failed for List [{0}]", Definition.Name);
                _importListStatusService.RecordFailure(Definition.Id);
            }

            return CleanupListItems(authorsAndBooks);
        }

        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            // Return early if there is not an API key
            if (Settings.ApiKey.IsNullOrWhiteSpace())
            {
                return new
                {
                    options = new List<object>()
                };
            }

            Settings.Validate().Filter("ApiKey").ThrowOnError();

            if (action == "getProfiles")
            {
                var devices = _chaptarrV1Proxy.GetProfiles(Settings);

                return new
                {
                    options = devices.OrderBy(d => d.Name, StringComparer.InvariantCultureIgnoreCase)
                        .Select(d => new
                        {
                            Value = d.Id,
                            Name = d.Name
                        })
                };
            }

            if (action == "getTags")
            {
                var devices = _chaptarrV1Proxy.GetTags(Settings);

                return new
                {
                    options = devices.OrderBy(d => d.Label, StringComparer.InvariantCultureIgnoreCase)
                        .Select(d => new
                        {
                            Value = d.Id,
                            Name = d.Label
                        })
                };
            }

            if (action == "getRootFolders")
            {
                var remoteRootFolders = _chaptarrV1Proxy.GetRootFolders(Settings);

                return new
                {
                    options = remoteRootFolders.OrderBy(d => d.Path, StringComparer.InvariantCultureIgnoreCase)
                        .Select(d => new
                        {
                            value = d.Path,
                            name = d.Path
                        })
                };
            }

            return new { };
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            failures.AddIfNotNull(_chaptarrV1Proxy.Test(Settings));
        }
    }
}
