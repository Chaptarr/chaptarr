using System.Collections.Generic;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles
{
    internal static class TagServiceTestFactory
    {
        // Reader-only fixtures do not exercise command selection or result reporting.
        // Command fixtures supply their actual selection and reporting dependencies.
        public static MetadataTagService Metadata(IAudioTagService audio, IEBookTagService ebook, Logger logger,
            ILocalizationService localization, IFileTagCacheRepository cache = null,
            ICommandResultReporter commandResultReporter = null, IMediaFileService media = null, IAuthorService authors = null)
        {
            return new MetadataTagService(audio, ebook, logger, localization,
                commandResultReporter ?? Proxy<ICommandResultReporter>((_, _) => null),
                media ?? Proxy<IMediaFileService>((_, _) => new List<BookFile>()),
                authors ?? Proxy<IAuthorService>((_, _) => new List<Author>()), cache);
        }
    }
}
