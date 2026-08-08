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
        IEnumerable<ExtraFile> MoveFilesAfterRename(Author author, List<BookFile> bookFiles);
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
        public abstract IEnumerable<ExtraFile> MoveFilesAfterRename(Author author, List<BookFile> bookFiles);
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

        protected TExtraFile MoveFile(Author author, BookFile bookFile, TExtraFile extraFile, string fileNameSuffix = null)
        {
            _logger.Trace("Renaming extra file: {0}", extraFile);

            var newFolder = Path.GetDirectoryName(bookFile.Path);
            var filenameBuilder = new StringBuilder(Path.GetFileNameWithoutExtension(bookFile.Path));

            if (fileNameSuffix.IsNotNullOrWhiteSpace())
            {
                filenameBuilder.Append(fileNameSuffix);
            }

            filenameBuilder.Append(extraFile.Extension);

            var newFileName = Path.Combine(newFolder, filenameBuilder.ToString());
            var preferredBase = ExtraFilePathHelper.GetPreferredBasePath(author, bookFile);
            var existingFileName = ExtraFilePathHelper.ResolveFullPath(author, extraFile.RelativePath, _diskProvider.FileExists, preferredBase);
            var existingFolder = Path.GetDirectoryName(existingFileName);

            if (newFileName.PathNotEquals(existingFileName))
            {
                try
                {
                    _logger.Trace("Renaming extra file: {0} to {1}", extraFile, newFileName);

                    _diskProvider.MoveFile(existingFileName, newFileName);

                    if (!ExtraFilePathHelper.TryGetRelativePath(author, newFileName, out var relativePath, out _, preferredBase))
                    {
                        throw new NotParentException("Unable to resolve extra file path '{0}' under any author base path", newFileName);
                    }

                    extraFile.RelativePath = relativePath;

                    _logger.Trace("Renamed extra file from: {0}", extraFile);

                    return extraFile;
                }
                catch (FileAlreadyExistsException) when (!existingFolder.PathEquals(newFolder))
                {
                    try
                    {
                        var availableName = GetAvailableFilename(newFileName);
                        _logger.Debug("Destination file exists, moving extra file to alternate name: {0}", availableName);

                        _diskProvider.MoveFile(existingFileName, availableName);

                        if (!ExtraFilePathHelper.TryGetRelativePath(author, availableName, out var relativePath, out _, preferredBase))
                        {
                            throw new NotParentException("Unable to resolve extra file path '{0}' under any author base path", availableName);
                        }

                        extraFile.RelativePath = relativePath;

                        _logger.Trace("Renamed extra file from: {0}", extraFile);

                        return extraFile;
                    }
                    catch (Exception retryEx)
                    {
                        _logger.Warn(retryEx, "Unable to move file after rename: {0}", existingFileName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Unable to move file after rename: {0}", existingFileName);
                }
            }

            return null;
        }

        private string GetAvailableFilename(string destinationPath)
        {
            if (destinationPath.IsNullOrWhiteSpace())
            {
                return destinationPath;
            }

            var directory = Path.GetDirectoryName(destinationPath);
            var filenameWithoutExtension = Path.GetFileNameWithoutExtension(destinationPath);
            var extension = Path.GetExtension(destinationPath);

            if (directory.IsNullOrWhiteSpace() || filenameWithoutExtension.IsNullOrWhiteSpace())
            {
                return destinationPath;
            }

            for (var i = 1; i < 1000; i++)
            {
                var candidate = Path.Combine(directory, $"{filenameWithoutExtension} ({i}){extension}");
                if (!_diskProvider.FileExists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(directory, $"{filenameWithoutExtension} ({Guid.NewGuid():N}){extension}");
        }
    }
}
