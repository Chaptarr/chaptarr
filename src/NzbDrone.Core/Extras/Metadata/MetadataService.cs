using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaCover;

namespace NzbDrone.Core.Extras.Metadata
{
    public class MetadataService : ExtraFileManager<MetadataFile>
    {
        private readonly IMetadataFactory _metadataFactory;
        private readonly ICleanMetadataService _cleanMetadataService;
        private readonly IRecycleBinProvider _recycleBinProvider;
        private readonly IOtherExtraFileRenamer _otherExtraFileRenamer;
        private readonly IDiskTransferService _diskTransferService;
        private readonly IDiskProvider _diskProvider;
        private readonly IHttpClient _httpClient;
        private readonly IConfigService _configService;
        private readonly IMediaFileAttributeService _mediaFileAttributeService;
        private readonly IMetadataFileService _metadataFileService;
        private readonly IBookService _bookService;
        private readonly Logger _logger;

        public MetadataService(IConfigService configService,
                               IDiskProvider diskProvider,
                               IDiskTransferService diskTransferService,
                               IRecycleBinProvider recycleBinProvider,
                               IOtherExtraFileRenamer otherExtraFileRenamer,
                               IMetadataFactory metadataFactory,
                               ICleanMetadataService cleanMetadataService,
                               IHttpClient httpClient,
                               IMediaFileAttributeService mediaFileAttributeService,
                               IMetadataFileService metadataFileService,
                               IBookService bookService,
                               Logger logger)
            : base(configService, diskProvider, diskTransferService, recycleBinProvider, logger)
        {
            _metadataFactory = metadataFactory;
            _cleanMetadataService = cleanMetadataService;
            _otherExtraFileRenamer = otherExtraFileRenamer;
            _recycleBinProvider = recycleBinProvider;
            _diskTransferService = diskTransferService;
            _diskProvider = diskProvider;
            _httpClient = httpClient;
            _configService = configService;
            _mediaFileAttributeService = mediaFileAttributeService;
            _metadataFileService = metadataFileService;
            _bookService = bookService;
            _logger = logger;
        }

        public override int Order => 0;

        public override IEnumerable<ExtraFile> CreateAfterAuthorScan(Author author, List<BookFile> bookFiles)
        {
            var metadataFiles = _metadataFileService.GetFilesByAuthor(author.Id);
            _cleanMetadataService.Clean(author);

            if (!_diskProvider.FolderExists(author.Path))
            {
                _logger.Info("Author folder does not exist, skipping metadata creation");
                return Enumerable.Empty<MetadataFile>();
            }

            var files = new List<MetadataFile>();

            foreach (var consumer in _metadataFactory.Enabled())
            {
                var consumerFiles = GetMetadataFilesForConsumer(consumer, metadataFiles);

                files.AddIfNotNull(ProcessAuthorMetadata(consumer, author, consumerFiles));
                files.AddRange(ProcessAuthorImages(consumer, author, consumerFiles));

                foreach (var bookFile in bookFiles)
                {
                    files.AddRange(ProcessBookImages(consumer, author, bookFile, consumerFiles));
                    files.AddIfNotNull(ProcessBookMetadata(consumer, author, bookFile, consumerFiles));
                }
            }

            _metadataFileService.Upsert(files);

            return files;
        }

        public override IEnumerable<ExtraFile> CreateAfterBookImport(Author author, BookFile bookFile)
        {
            var files = new List<MetadataFile>();

            foreach (var consumer in _metadataFactory.Enabled())
            {
                files.AddRange(ProcessBookImages(consumer, author, bookFile, new List<MetadataFile>()));
                files.AddIfNotNull(ProcessBookMetadata(consumer, author, bookFile, new List<MetadataFile>()));
            }

            _metadataFileService.Upsert(files);

            return files;
        }

        public override IEnumerable<ExtraFile> CreateAfterBookImport(Author author, Book book, string authorFolder, string bookFolder)
        {
            var metadataFiles = _metadataFileService.GetFilesByAuthor(author.Id);

            if (authorFolder.IsNullOrWhiteSpace() && bookFolder.IsNullOrWhiteSpace())
            {
                return new List<MetadataFile>();
            }

            var files = new List<MetadataFile>();

            foreach (var consumer in _metadataFactory.Enabled())
            {
                var consumerFiles = GetMetadataFilesForConsumer(consumer, metadataFiles);

                if (authorFolder.IsNotNullOrWhiteSpace())
                {
                    files.AddIfNotNull(ProcessAuthorMetadata(consumer, author, consumerFiles));
                    files.AddRange(ProcessAuthorImages(consumer, author, consumerFiles));
                }
            }

            _metadataFileService.Upsert(files);

            return files;
        }

        public override IEnumerable<ExtraFile> MoveFilesAfterRename(Author author, List<RenamedBookFile> bookFiles)
        {
            var metadataFiles = _metadataFileService.GetFilesByAuthor(author.Id);
            var movedFiles = new List<MetadataFile>();

            foreach (var consumer in _metadataFactory.GetAvailableProviders())
            {
                // Resolve every original before mutating tracking; a later destination must not
                // claim a sidecar already moved for another copy of the same book.
                var consumerFiles = GetMetadataFilesForConsumer(consumer, metadataFiles)
                    .Where(extra => extra.Type == MetadataType.BookImage || extra.Type == MetadataType.BookMetadata)
                    .ToList();
                var plans = consumerFiles
                    .SelectMany(extra => bookFiles.Select(move => new
                    {
                        Extra = extra,
                        Move = move,
                        Source = ResolveRenameSource(extra, move),
                        Destination = extra.BookFileId.HasValue
                            ? consumer.GetFilenameAfterMove(author, move.BookFile, extra)
                            : consumer.GetFilenameAfterMove(author, Path.GetDirectoryName(move.BookFile.Path), extra)
                    }))
                    .Where(plan => plan.Source != null)
                    .ToList();

                WarnUnresolvedRenameSources(consumerFiles, bookFiles, plans.Select(plan => plan.Extra.Id).ToHashSet());

                foreach (var group in plans.GroupBy(plan => plan.Source, PathEqualityComparer.Instance))
                {
                    var destinations = group.Select(plan => plan.Destination).Distinct(PathEqualityComparer.Instance).ToList();
                    var rows = group.Select(plan => plan.Extra.Id).Distinct().Count();
                    var plan = group.First();
                    var remainingMedia = _diskProvider.GetFiles(Path.GetDirectoryName(plan.Source), false)
                        .Any(path => MediaFileExtensions.AllExtensions.Contains(Path.GetExtension(path)));

                    // A fixed-name sidecar may still serve an unmoved file despite its producer ID.
                    // Ambiguous/shared sources remain intact; cleanup must not force them to move.
                    if (destinations.Count != 1 || rows != 1 || remainingMedia)
                    {
                        _logger.Warn("Keeping shared or ambiguous sidecar after rename: {0}", plan.Source);
                        continue;
                    }

                    movedFiles.AddIfNotNull(RelocateFile(author, plan.Move, plan.Extra, plan.Source,
                        plan.Destination, _metadataFileService.Upsert));
                }
            }

            return movedFiles;
        }

        public override ExtraFile Import(Author author, BookFile bookFile, string path, string extension, bool readOnly, bool replaceExisting)
        {
            return null;
        }

        private List<MetadataFile> GetMetadataFilesForConsumer(IMetadata consumer, List<MetadataFile> authorMetadata)
        {
            return authorMetadata.Where(c => c.Consumer == consumer.GetType().Name).ToList();
        }

        private MetadataFile ProcessAuthorMetadata(IMetadata consumer, Author author, List<MetadataFile> existingMetadataFiles)
        {
            var authorMetadata = consumer.AuthorMetadata(author);

            if (authorMetadata == null)
            {
                return null;
            }

            var hash = authorMetadata.Contents.SHA256Hash();

            var metadata = GetMetadataFile(author, existingMetadataFiles, e => e.Type == MetadataType.AuthorMetadata) ??
                               new MetadataFile
                               {
                                   AuthorId = author.Id,
                                   Consumer = consumer.GetType().Name,
                                   Type = MetadataType.AuthorMetadata
                               };

            if (hash == metadata.Hash)
            {
                if (authorMetadata.RelativePath != metadata.RelativePath)
                {
                    metadata.RelativePath = authorMetadata.RelativePath;

                    return metadata;
                }

                return null;
            }

            var fullPath = Path.Combine(author.Path, authorMetadata.RelativePath);

            _otherExtraFileRenamer.RenameOtherExtraFile(author, fullPath);

            _logger.Debug("Writing Author Metadata to: {0}", fullPath);
            SaveMetadataFile(fullPath, authorMetadata.Contents);

            metadata.Hash = hash;
            metadata.RelativePath = authorMetadata.RelativePath;
            metadata.Extension = Path.GetExtension(fullPath);

            return metadata;
        }

        private MetadataFile ProcessBookMetadata(IMetadata consumer, Author author, BookFile bookFile, List<MetadataFile> existingMetadataFiles)
        {
            var trackMetadata = consumer.BookMetadata(author, bookFile);

            if (trackMetadata == null)
            {
                return null;
            }

            var basePath = ExtraFilePathHelper.GetPreferredBasePath(author, bookFile);
            var fullPath = Path.Combine(basePath, trackMetadata.RelativePath);
            var hash = trackMetadata.Contents.SHA256Hash();

            var existingMetadata = GetMetadataFile(author, existingMetadataFiles, c => c.Type == MetadataType.BookMetadata &&
                                                                                  c.BookFileId == bookFile.Id);

            if (existingMetadata != null)
            {
                var existingFullPath = ExtraFilePathHelper.ResolveFullPath(author, existingMetadata.RelativePath, _diskProvider.FileExists, basePath);
                if (fullPath.PathNotEquals(existingFullPath))
                {
                    if (!_diskProvider.FileExists(existingFullPath))
                    {
                        _logger.Debug("Existing book metadata file missing, skipping move: {0}", existingFullPath);
                        return null;
                    }

                    if (!IsTrackedTextFileCurrent(existingMetadata, existingFullPath))
                    {
                        ReleaseMetadataOwnership(existingMetadata, existingFullPath);
                        return null;
                    }

                    if (_diskProvider.FileExists(fullPath))
                    {
                        _logger.Debug("Book metadata destination already exists, skipping move: {0}", fullPath);
                        return null;
                    }

                    _diskTransferService.TransferFile(existingFullPath, fullPath, TransferMode.Move);
                    existingMetadata.RelativePath = trackMetadata.RelativePath;
                }
            }

            if (_diskProvider.FileExists(fullPath))
            {
                if (!HasTrackedHash(existingMetadata))
                {
                    _logger.Debug("Book metadata already exists and is not tracked by this metadata provider, skipping: {0}", fullPath);
                    return null;
                }

                if (!IsTrackedTextFileCurrent(existingMetadata, fullPath))
                {
                    ReleaseMetadataOwnership(existingMetadata, fullPath);
                    return null;
                }

                if (hash == GetTrackedFileHash(existingMetadata))
                {
                    return null;
                }

                if (!trackMetadata.OverwriteExisting)
                {
                    _logger.Debug("Book metadata already exists: {0}", fullPath);
                    return null;
                }
            }

            var metadata = existingMetadata ??
                           new MetadataFile
                           {
                               AuthorId = author.Id,
                               BookId = bookFile.Edition.BookId,
                               BookFileId = bookFile.Id,
                               Consumer = consumer.GetType().Name,
                               Type = MetadataType.BookMetadata,
                               RelativePath = trackMetadata.RelativePath,
                               Extension = Path.GetExtension(fullPath)
                           };

            if (hash == GetTrackedFileHash(metadata))
            {
                return null;
            }

            _otherExtraFileRenamer.RenameOtherExtraFile(author, fullPath);

            _logger.Debug("Writing Track Metadata to: {0}", fullPath);
            SaveMetadataFile(fullPath, trackMetadata.Contents);

            metadata.Hash = hash;

            return metadata;
        }

        private List<MetadataFile> ProcessAuthorImages(IMetadata consumer, Author author, List<MetadataFile> existingMetadataFiles)
        {
            var result = new List<MetadataFile>();

            foreach (var image in consumer.AuthorImages(author))
            {
                var fullPath = Path.Combine(author.Path, image.RelativePath);

                if (_diskProvider.FileExists(fullPath))
                {
                    _logger.Debug("Author image already exists: {0}", fullPath);
                    continue;
                }

                _otherExtraFileRenamer.RenameOtherExtraFile(author, fullPath);

                var metadata = GetMetadataFile(author, existingMetadataFiles, c => c.Type == MetadataType.AuthorImage &&
                                                                              c.RelativePath == image.RelativePath) ??
                               new MetadataFile
                               {
                                   AuthorId = author.Id,
                                   Consumer = consumer.GetType().Name,
                                   Type = MetadataType.AuthorImage,
                                   RelativePath = image.RelativePath,
                                   Extension = Path.GetExtension(fullPath)
                               };

                DownloadImage(author, image);

                result.Add(metadata);
            }

            return result;
        }

        private List<MetadataFile> ProcessBookImages(IMetadata consumer, Author author, BookFile bookFile, List<MetadataFile> existingMetadataFiles)
        {
            var result = new List<MetadataFile>();
            var basePath = ExtraFilePathHelper.GetPreferredBasePath(author, bookFile);
            if (basePath.IsNullOrWhiteSpace())
            {
                return result;
            }

            foreach (var image in consumer.BookImages(author, bookFile))
            {
                var fullPath = Path.Combine(basePath, image.RelativePath);
                var sourceHash = image.Url.SHA256Hash();
                var existingMetadata = existingMetadataFiles.FirstOrDefault(c => c.Type == MetadataType.BookImage &&
                                                                                 c.BookId == bookFile.Edition.BookId &&
                                                                                 c.RelativePath == image.RelativePath);

                if (_diskProvider.FileExists(fullPath))
                {
                    if (!HasTrackedHash(existingMetadata))
                    {
                        _logger.Debug("Book image already exists and is not tracked by this metadata provider, skipping: {0}", fullPath);
                        continue;
                    }

                    if (!IsTrackedBinaryFileCurrent(existingMetadata, fullPath))
                    {
                        ReleaseMetadataOwnership(existingMetadata, fullPath);
                        continue;
                    }

                    if (sourceHash == GetTrackedSourceHash(existingMetadata))
                    {
                        _logger.Debug("Book image already exists: {0}", fullPath);
                        continue;
                    }

                    if (!image.OverwriteExisting)
                    {
                        _logger.Debug("Book image already exists: {0}", fullPath);
                        continue;
                    }
                }

                _otherExtraFileRenamer.RenameOtherExtraFile(author, fullPath);

                var metadata = GetMetadataFile(author, existingMetadataFiles, c => c.Type == MetadataType.BookImage &&
                                                                                  c.BookId == bookFile.Edition.BookId &&
                                                                                  c.RelativePath == image.RelativePath) ??
                               new MetadataFile
                               {
                                   AuthorId = author.Id,
                                   BookId = bookFile.Edition.BookId,
                                   Consumer = consumer.GetType().Name,
                                   Type = MetadataType.BookImage,
                                   RelativePath = image.RelativePath,
                                   Extension = Path.GetExtension(fullPath)
                               };

                if (!DownloadImage(author, image, basePath, image.OverwriteExisting) || !_diskProvider.FileExists(fullPath))
                {
                    continue;
                }

                metadata.Hash = BuildTrackedHash(GetBinaryFileHash(fullPath), sourceHash);

                result.Add(metadata);
            }

            return result;
        }

        private bool DownloadImage(Author author, ImageFileResult image)
        {
            return DownloadImage(author, image, author.Path);
        }

        private bool DownloadImage(Author author, ImageFileResult image, string basePath)
        {
            return DownloadImage(author, image, basePath, false);
        }

        private bool DownloadImage(Author author, ImageFileResult image, string basePath, bool overwrite)
        {
            var fullPath = Path.Combine(basePath, image.RelativePath);
            var downloaded = true;

            try
            {
                if (image.Url.StartsWith("http"))
                {
                    if (!MediaCoverUrlSafetyPolicy.TryGetSafeImageUrl(image.Url, _configService, out var safeUrl, out var reason))
                    {
                        _logger.Warn("Skipping unsafe metadata image for {0}: {1}", author, reason);
                        return false;
                    }

                    _httpClient.DownloadFile(safeUrl, fullPath);
                }
                else if (_diskProvider.FileExists(image.Url))
                {
                    _diskProvider.CopyFile(image.Url, fullPath, overwrite);
                }
                else
                {
                    downloaded = false;
                }

                if (downloaded)
                {
                    _mediaFileAttributeService.SetFilePermissions(fullPath);
                }

                return downloaded;
            }
            catch (HttpException ex)
            {
                _logger.Warn(ex, "Couldn't download image {0} for {1}. {2}", image.Url, author, ex.Message);
            }
            catch (WebException ex)
            {
                _logger.Warn(ex, "Couldn't download image {0} for {1}. {2}", image.Url, author, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Couldn't download image {0} for {1}", image.Url, author);
            }

            return false;
        }

        private void SaveMetadataFile(string path, string contents)
        {
            _diskProvider.WriteAllText(path, contents);
            _mediaFileAttributeService.SetFilePermissions(path);
        }

        private MetadataFile GetMetadataFile(Author author, List<MetadataFile> existingMetadataFiles, Func<MetadataFile, bool> predicate)
        {
            var matchingMetadataFiles = existingMetadataFiles.Where(predicate).ToList();

            if (matchingMetadataFiles.Empty())
            {
                return null;
            }

            //Remove duplicate metadata files from DB and disk
            foreach (var file in matchingMetadataFiles.Skip(1))
            {
                var path = ExtraFilePathHelper.ResolveFullPath(author, file.RelativePath, _diskProvider.FileExists);

                _logger.Debug("Removing duplicate Metadata file: {0}", path);

                try
                {
                    var basePath = ExtraFilePathHelper.GetAuthorBasePaths(author)
                        .Where(p => p.IsParentPath(path) || p.PathEquals(path))
                        .OrderByDescending(p => p.Length)
                        .FirstOrDefault() ?? author.Path;

                    var root = _diskProvider.GetParentFolder(basePath);
                    var subfolder = root.GetRelativePath(_diskProvider.GetParentFolder(path));
                    _recycleBinProvider.DeleteFile(path, subfolder);
                }
                catch
                {
                    _recycleBinProvider.DeleteFile(path, string.Empty);
                }

                _metadataFileService.Delete(file.Id);
            }

            return matchingMetadataFiles.First();
        }

        private bool IsTrackedTextFileCurrent(MetadataFile metadata, string path)
        {
            return MetadataFileHash.IsCurrent(metadata, path, _diskProvider, binary: false);
        }

        private bool IsTrackedBinaryFileCurrent(MetadataFile metadata, string path)
        {
            return MetadataFileHash.IsCurrent(metadata, path, _diskProvider, binary: true);
        }

        private string GetBinaryFileHash(string path)
        {
            using (var stream = _diskProvider.OpenReadStream(path))
            {
                return stream.SHA256Hash();
            }
        }

        private void ReleaseMetadataOwnership(MetadataFile metadata, string path)
        {
            _logger.Debug("Metadata file changed outside Chaptarr, disabling content overwrites and leaving it untouched: {0}", path);

            if (metadata == null)
            {
                return;
            }

            metadata.Hash = null;

            if (metadata.Id > 0)
            {
                _metadataFileService?.Upsert(metadata);
            }
        }

        private static string BuildTrackedHash(string fileHash, string sourceHash)
        {
            return sourceHash.IsNullOrWhiteSpace() ? fileHash : fileHash + ":" + sourceHash;
        }

        private static string GetTrackedFileHash(MetadataFile metadata) => MetadataFileHash.FileHash(metadata);

        private static string GetTrackedSourceHash(MetadataFile metadata) => MetadataFileHash.SourceHash(metadata);

        private static bool HasTrackedHash(MetadataFile metadata)
        {
            return metadata != null && metadata.Hash.IsNotNullOrWhiteSpace();
        }
    }
}
