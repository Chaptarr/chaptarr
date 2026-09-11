using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Extras.Files
{
    public interface IManageExtraFiles
    {
        int Order { get; }
        IEnumerable<ExtraFile> CreateAfterAuthorScan(Author author, List<BookFile> bookFiles);
        IEnumerable<ExtraFile> CreateAfterBookImport(Author author, BookFile bookFile);
        IEnumerable<ExtraFile> CreateAfterBookImport(Author author, Book book, string authorFolder, string bookFolder);
        IEnumerable<ExtraFile> MoveFilesAfterRename(Author author, List<RenamedBookFile> bookFiles);
        ExtraFile Import(Author author, BookFile bookFile, string path, string extension, bool readOnly);
    }

    public abstract class ExtraFileManager<TExtraFile> : IManageExtraFiles
        where TExtraFile : ExtraFile, new()
    {
        private readonly IConfigService _configService;
        private readonly IDiskProvider _diskProvider;
        private readonly IDiskTransferService _diskTransferService;
        private readonly Logger _logger;

        public ExtraFileManager(IConfigService configService,
                                IDiskProvider diskProvider,
                                IDiskTransferService diskTransferService,
                                Logger logger)
        {
            _configService = configService;
            _diskProvider = diskProvider;
            _diskTransferService = diskTransferService;
            _logger = logger;
        }

        public abstract int Order { get; }
        public abstract IEnumerable<ExtraFile> CreateAfterAuthorScan(Author author, List<BookFile> bookFiles);
        public abstract IEnumerable<ExtraFile> CreateAfterBookImport(Author author, BookFile bookFile);
        public abstract IEnumerable<ExtraFile> CreateAfterBookImport(Author author, Book book, string authorFolder, string bookFolder);
        public abstract IEnumerable<ExtraFile> MoveFilesAfterRename(Author author, List<RenamedBookFile> bookFiles);
        public abstract ExtraFile Import(Author author, BookFile bookFile, string path, string extension, bool readOnly);

        protected TExtraFile ImportFile(Author author, BookFile bookFile, string path, bool readOnly, string extension, string fileNameSuffix = null)
        {
            var newFolder = Path.GetDirectoryName(bookFile.Path);
            var filenameBuilder = new StringBuilder(Path.GetFileNameWithoutExtension(bookFile.Path));

            if (fileNameSuffix.IsNotNullOrWhiteSpace())
            {
                filenameBuilder.Append(fileNameSuffix);
            }

            filenameBuilder.Append(extension);

            var newFileName = Path.Combine(newFolder, filenameBuilder.ToString());
            var transferMode = TransferMode.Move;

            if (readOnly)
            {
                transferMode = _configService.CopyUsingHardlinks ? TransferMode.HardLinkOrCopy : TransferMode.Copy;
            }

            _diskTransferService.TransferFile(path, newFileName, transferMode, true);

            var preferredBase = ExtraFilePathHelper.GetPreferredBasePath(author, bookFile);
            if (!ExtraFilePathHelper.TryGetRelativePath(author, newFileName, out var relativePath, out _, preferredBase))
            {
                throw new NotParentException("Unable to resolve extra file path '{0}' under any author base path", newFileName);
            }

            return new TExtraFile
            {
                AuthorId = author.Id,
                BookId = bookFile.Edition.BookId,
                BookFileId = bookFile.Id,
                RelativePath = relativePath,
                Extension = extension
            };
        }

        protected string ResolveRenameSource(TExtraFile extra, RenamedBookFile move)
        {
            if (move?.BookFile == null || move.PreviousPath.IsNullOrWhiteSpace() ||
                move.PreviousAuthorPath.IsNullOrWhiteSpace() || extra.RelativePath.IsNullOrWhiteSpace() ||
                Path.IsPathRooted(extra.RelativePath))
            {
                return null;
            }

            if ((extra.BookFileId.HasValue && extra.BookFileId != move.BookFile.Id) ||
                (extra.BookId.HasValue && extra.BookId != move.BookFile.Edition?.BookId))
            {
                return null;
            }

            // Relative names alone do not locate a sidecar across media roots or copies.
            var source = Path.GetFullPath(Path.Combine(move.PreviousAuthorPath, extra.RelativePath));
            return Path.GetDirectoryName(source).PathEquals(Path.GetDirectoryName(move.PreviousPath)) &&
                _diskProvider.FileExistsCanonical(source) ? source : null;
        }

        protected TExtraFile RelocateFile(Author author, RenamedBookFile move, TExtraFile extra,
            string source, string destination, Action<TExtraFile> persist)
        {
            if (source.PathEquals(destination))
            {
                return null;
            }

            var preferredBase = ExtraFilePathHelper.GetPreferredBasePath(author, move.BookFile);
            if (!ExtraFilePathHelper.TryGetRelativePath(author, destination, out var relativePath, out _, preferredBase))
            {
                _logger.Warn("Keeping sidecar {0}: destination cannot be represented under the author paths", source);
                return null;
            }

            var previousRelativePath = extra.RelativePath;
            try
            {
                // Keep the original until its new tracking is saved. No overwrite or blind rollback
                // may destroy a file that appeared while the filesystem/database operations ran.
                if (!_configService.CopyUsingHardlinks || !_diskProvider.TryCreateHardLink(source, destination))
                {
                    // Do not use transfer rollback here: a failed exclusive copy does not prove
                    // that its destination belongs to this operation and may be deleted.
                    _diskProvider.CopyFile(source, destination, false);
                }

                extra.RelativePath = relativePath;
                persist(extra);
            }
            catch (Exception ex)
            {
                extra.RelativePath = previousRelativePath;
                _logger.Warn(ex, "Sidecar relocation did not complete; preserving source {0} and any destination {1}", source, destination);
                return null;
            }

            try
            {
                _diskProvider.DeleteFile(source);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Sidecar is tracked at {0}, but its original remains at {1}", destination, source);
            }

            return extra;
        }
    }
}
