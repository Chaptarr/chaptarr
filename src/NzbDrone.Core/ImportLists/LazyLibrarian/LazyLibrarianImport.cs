using System;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tags;

namespace NzbDrone.Core.ImportLists.LazyLibrarianImport
{
    public class LazyLibrarianImport : HttpImportListBase<LazyLibrarianImportSettings>
    {
        public override string Name => "LazyLibrarian";

        public override ImportListType ListType => ImportListType.Other;
        public override TimeSpan MinRefreshInterval => TimeSpan.FromMinutes(15);
        public override int PageSize => 1000;

        public LazyLibrarianImport(IHttpClient httpClient,
            IImportListStatusService importListStatusService,
            IConfigService configService,
            IParsingService parsingService,
            Lazy<IQualityProfileService> qualityProfileService,
            Lazy<IMetadataProfileService> metadataProfileService,
            Lazy<ITagService> tagService,
            IRootFolderService rootFolderService,
            IRootFolderSettingsResolver rootFolderSettingsResolver,
            Logger logger)
            : base(httpClient, importListStatusService, configService, parsingService, qualityProfileService, metadataProfileService, tagService, rootFolderService, rootFolderSettingsResolver, logger)
        {
        }

        public override IImportListRequestGenerator GetRequestGenerator()
        {
            return new LazyLibrarianImportRequestGenerator { Settings = Settings };
        }

        public override IParseImportListResponse GetParser()
        {
            return new LazyLibrarianImportParser();
        }
    }
}
