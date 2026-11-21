using System;
using System.IO;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Extras.Metadata.Files
{
    public interface IMetadataFileService : IExtraFileService<MetadataFile>
    {
    }

    public class MetadataFileService : ExtraFileService<MetadataFile>, IMetadataFileService
    {
        private readonly IDiskProvider _diskProvider;

        protected override bool PrepareForReplacement(MetadataFile extra, string path)
        {
            try
            {
                if (MetadataFileHash.IsCurrent(extra, path, _diskProvider,
                    extra.Type == MetadataType.AuthorImage || extra.Type == MetadataType.BookImage)) return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.Warn(ex, "Keeping tracked sidecar without overwrite permission after hash verification failed: {0}", path);
            }

            extra.Hash = null;
            return false;
        }

        public MetadataFileService(IExtraFileRepository<MetadataFile> repository, IAuthorService authorService, IDiskProvider diskProvider, IRecycleBinProvider recycleBinProvider, Logger logger)
            : base(repository, authorService, diskProvider, recycleBinProvider, logger)
        {
            _diskProvider = diskProvider;
        }
    }
}
