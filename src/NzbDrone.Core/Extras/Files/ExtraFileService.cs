using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Extras;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Extras.Files
{
    public interface IExtraFileService<TExtraFile>
        where TExtraFile : ExtraFile, new()
    {
        List<TExtraFile> GetFilesByAuthor(int authorId);
        List<TExtraFile> GetFilesByBookFile(int bookFileId);
        TExtraFile FindByPath(int authorId, string path);
        void Upsert(TExtraFile extraFile);
        void Upsert(List<TExtraFile> extraFiles);
        void Delete(int id);
        void DeleteMany(IEnumerable<int> ids);
        void DeleteBookFiles(Book book);
    }

    public abstract class ExtraFileService<TExtraFile> : IExtraFileService<TExtraFile>,
                                                         IHandleAsync<AuthorDeletedEvent>,
                                                         IHandle<BookDeletedEvent>,
                                                         IHandle<BookFileDeletedEvent>
        where TExtraFile : ExtraFile, new()
    {
        private readonly IExtraFileRepository<TExtraFile> _repository;
        private readonly IAuthorService _authorService;
        private readonly IDiskProvider _diskProvider;
        private readonly IRecycleBinProvider _recycleBinProvider;
        protected readonly Logger _logger;

        public ExtraFileService(IExtraFileRepository<TExtraFile> repository,
                                IAuthorService authorService,
                                IDiskProvider diskProvider,
                                IRecycleBinProvider recycleBinProvider,
                                Logger logger)
        {
            _repository = repository;
            _authorService = authorService;
            _diskProvider = diskProvider;
            _recycleBinProvider = recycleBinProvider;
            _logger = logger;
        }

        public List<TExtraFile> GetFilesByAuthor(int authorId)
        {
            return _repository.GetFilesByAuthor(authorId);
        }

        public List<TExtraFile> GetFilesByBookFile(int bookFileId)
        {
            return _repository.GetFilesByBookFile(bookFileId);
        }

        public TExtraFile FindByPath(int authorId, string path)
        {
            return _repository.FindByPath(authorId, path);
        }

        public void Upsert(TExtraFile extraFile)
        {
            Upsert(new List<TExtraFile> { extraFile });
        }

        public void Upsert(List<TExtraFile> extraFiles)
        {
            extraFiles.ForEach(m =>
            {
                m.LastUpdated = DateTime.UtcNow;

                if (m.Id == 0)
                {
                    m.Added = m.LastUpdated;
                }
            });

            _repository.InsertMany(extraFiles.Where(m => m.Id == 0).ToList());
            _repository.UpdateMany(extraFiles.Where(m => m.Id > 0).ToList());
        }

        public void Delete(int id)
        {
            _repository.Delete(id);
        }

        public void DeleteMany(IEnumerable<int> ids)
        {
            _repository.DeleteMany(ids);
        }

        public void HandleAsync(AuthorDeletedEvent message)
        {
            _logger.Debug("Deleting Extra from database for author: {0}", message.Author);
            _repository.DeleteForAuthor(message.Author.Id);
        }

        /// <summary>
        /// Remove the book's extra-file inventory after deletion commits. Mandatory physical
        /// cleanup runs before that commit through DeleteBookFiles.
        /// </summary>
        public void Handle(BookDeletedEvent message)
        {
            var book = message.Book;

            if (book == null || book.Id <= 0)
            {
                return;
            }

            _repository.DeleteForBook(book.AuthorId, book.Id);
        }

        public void DeleteBookFiles(Book book)
        {
            var author = book.Author ?? GetAuthorOrNull(book.AuthorId);
            if (author == null) return;

            foreach (var extra in _repository.GetFilesByBook(book.AuthorId, book.Id))
            {
                var path = ExtraFilePathHelper.ResolveOwnedSidecarPath(author, extra, book.BookFiles ?? new List<BookFile>(), _diskProvider);
                RecycleExtraFile(author, extra, path, new[] { path });
            }
        }

        public void Handle(BookFileDeletedEvent message)
        {
            var bookFile = message.BookFile;

            if (message.Reason == DeleteMediaFileReason.MissingFromDisk)
            {
                var kept = (message.Context?.GetExtras(_repository, bookFile.Id) ?? _repository.GetFilesByBookFile(bookFile.Id)).ToList();
                foreach (var extra in kept)
                {
                    extra.BookId ??= bookFile.Edition?.BookId;
                    extra.BookFileId = null;
                }

                if (kept.Count > 0) Upsert(kept);
                _logger.Debug("Kept {0} sidecar records after the primary file went missing: {1}", kept.Count, bookFile.Path);
                return;
            }

            if (!message.Reason.RemovesFiles())
            {
                _logger.Debug("Removing track file from DB as part of cleanup routine, not deleting extra files from disk.");
            }
            else
            {
                var author = bookFile.Author;

                var retained = new List<TExtraFile>();
                var removed = new List<int>();
                var explicitDeletion = message.Reason == DeleteMediaFileReason.Manual || message.Reason == DeleteMediaFileReason.ManualOverride;
                var extras = explicitDeletion
                    ? message.Context?.GetExtrasForDeletion(_repository, bookFile) ?? _repository.GetFilesForDeletion(new[] { bookFile })
                    : message.Context?.GetExtras(_repository, bookFile.Id) ?? _repository.GetFilesByBookFile(bookFile.Id);
                try
                {
                    foreach (var extra in extras)
                    {
                        var path = ExtraFilePathHelper.ResolveOwnedSidecarPath(author, extra, new[] { bookFile }, _diskProvider);
                        var successor = path == null ? null : message.Context?.FindReplacement(bookFile.Edition?.BookId ?? 0, path);
                        var retainOwnership = false;
                        var preserveInPlace = false;
                        try
                        {
                            retainOwnership = successor != null && _diskProvider.FileExistsCanonical(successor.Path);
                            if (path != null && message.Context?.HasReplacement(bookFile.Edition?.BookId ?? 0) == true)
                            {
                                preserveInPlace = !PrepareForReplacement(extra, path);
                            }
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            _logger.Warn(ex, "Keeping sidecar tracking after replacement verification failed: {0}", path);
                            preserveInPlace = true;
                        }
                        var confirmedReplacement = message.ConfirmedAssociatedPaths?.Any(confirmed => confirmed.PathEquals(path)) == true;
                        if ((retainOwnership || preserveInPlace) && !confirmedReplacement)
                        {
                            // A same-folder successor inherits tracking. Edited metadata in an old
                            // folder remains attached to the book, without claiming the new file.
                            extra.BookId ??= bookFile.Edition?.BookId;
                            extra.BookFileId = retainOwnership ? successor.Id : null;
                            retained.Add(extra);
                            continue;
                        }

                        if (RecycleExtraFile(author, extra, path, message.ConfirmedAssociatedPaths ?? new[] { path }))
                        {
                            removed.Add(extra.Id);
                            message.Context?.MarkExtraRemoved(extra);
                        }
                        else if (explicitDeletion && extra.BookFileId.HasValue)
                        {
                            // A shared/unresolved sidecar stays tracked after its producer is removed.
                            extra.BookId ??= bookFile.Edition?.BookId;
                            extra.BookFileId = null;
                            retained.Add(extra);
                        }
                    }
                }
                finally
                {
                    // Keep successful recycling reflected in the database even if a later file fails.
                    if (removed.Count > 0) _repository.DeleteMany(removed);
                }

                if (retained.Count > 0)
                {
                    Upsert(retained);
                }
            }

            _logger.Debug("Deleting Extra from database for book file: {0}", bookFile);
            _repository.DeleteForBookFile(bookFile.Id);
        }

        protected virtual bool PrepareForReplacement(TExtraFile extra, string path) => true;

        private bool RecycleExtraFile(Author author, TExtraFile extra, string path, IReadOnlyCollection<string> confirmedPaths)
        {
            if (path.IsNullOrWhiteSpace())
            {
                _logger.Warn("Keeping sidecar with unresolved physical ownership: {0}", extra.RelativePath);
                return false;
            }

            if (!confirmedPaths.Any(confirmed => confirmed.PathEquals(path)))
            {
                _logger.Warn("Keeping sidecar not included in replacement confirmation: {0}", path);
                return false;
            }

            // Send to the recycling bin so they can be recovered if necessary
            var basePath = ExtraFilePathHelper.GetAuthorBasePaths(author)
                .Where(p => p.IsParentPath(path) || p.PathEquals(path))
                .OrderByDescending(p => p.Length)
                .FirstOrDefault();

            string subfolder;
            try
            {
                var root = _diskProvider.GetParentFolder(basePath);
                subfolder = root.GetRelativePath(_diskProvider.GetParentFolder(path));
            }
            catch
            {
                subfolder = string.Empty;
            }

            _recycleBinProvider.DeleteFile(path, subfolder);
            return true;
        }

        private Author GetAuthorOrNull(int authorId)
        {
            if (authorId <= 0)
            {
                return null;
            }

            try
            {
                return _authorService.GetAuthor(authorId);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Author {0} no longer available while clearing book extras", authorId);
                return null;
            }
        }
    }
}
