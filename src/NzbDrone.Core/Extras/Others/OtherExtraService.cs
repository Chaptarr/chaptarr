using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Extras.Others
{
    public class OtherExtraService : ExtraFileManager<OtherExtraFile>
    {
        private readonly IOtherExtraFileService _otherExtraFileService;
        private readonly IMediaFileAttributeService _mediaFileAttributeService;

        public OtherExtraService(IConfigService configService,
                                 IDiskProvider diskProvider,
                                 IDiskTransferService diskTransferService,
                                 IOtherExtraFileService otherExtraFileService,
                                 IMediaFileAttributeService mediaFileAttributeService,
                                 IRecycleBinProvider recycleBinProvider,
                                 Logger logger)
            : base(configService, diskProvider, diskTransferService, recycleBinProvider, logger)
        {
            _otherExtraFileService = otherExtraFileService;
            _mediaFileAttributeService = mediaFileAttributeService;
        }

        public override int Order => 2;

        public override IEnumerable<ExtraFile> CreateAfterAuthorScan(Author author, List<BookFile> bookFiles)
        {
            return Enumerable.Empty<ExtraFile>();
        }

        public override IEnumerable<ExtraFile> CreateAfterBookImport(Author author, BookFile bookFile)
        {
            return Enumerable.Empty<ExtraFile>();
        }

        public override IEnumerable<ExtraFile> CreateAfterBookImport(Author author, Book book, string authorFolder, string bookFolder)
        {
            return Enumerable.Empty<ExtraFile>();
        }

        public override IEnumerable<ExtraFile> MoveFilesAfterRename(Author author, List<RenamedBookFile> bookFiles)
        {
            var extraFiles = _otherExtraFileService.GetFilesByAuthor(author.Id);
            var movedFiles = new List<OtherExtraFile>();
            var resolvedIds = new HashSet<int>();
            foreach (var move in bookFiles)
            {
                foreach (var extra in extraFiles.Where(extra => extra.BookFileId == move.BookFile.Id))
                {
                    var source = ResolveRenameSource(extra, move);
                    if (source == null)
                    {
                        continue;
                    }

                    resolvedIds.Add(extra.Id);
                    var name = Path.GetFileName(source);
                    var oldStem = Path.GetFileNameWithoutExtension(move.PreviousPath);
                    // Preserve independently named notes/artwork; only replace the old media stem.
                    if (!string.IsNullOrEmpty(oldStem) && name.StartsWith(oldStem, StringComparison.OrdinalIgnoreCase) &&
                        (name.Length == oldStem.Length || !char.IsLetterOrDigit(name[oldStem.Length])))
                    {
                        name = Path.GetFileNameWithoutExtension(move.BookFile.Path) + name.Substring(oldStem.Length);
                    }

                    var destination = Path.Combine(Path.GetDirectoryName(move.BookFile.Path), name);
                    movedFiles.AddIfNotNull(RelocateFile(author, move, extra, source, destination, _otherExtraFileService.Upsert));
                }
            }

            WarnUnresolvedRenameSources(extraFiles, bookFiles, resolvedIds);
            return movedFiles;
        }

        public override ExtraFile Import(Author author, BookFile bookFile, string path, string extension, bool readOnly, bool replaceExisting)
        {
            var extraFile = ImportFile(author, bookFile, path, readOnly, extension, replaceExisting);

            _mediaFileAttributeService.SetFilePermissions(path);
            _otherExtraFileService.Upsert(extraFile);

            return extraFile;
        }
    }
}
