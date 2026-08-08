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
        private readonly Logger _logger;

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
        /// Extras that belong to the book rather than to one of its files — the cover a whole book
        /// folder shares, for instance — are not reachable from any single book file, so they have to
        /// be cleared here or they outlive the book and keep its folder from ever being empty.
        /// </summary>
        public void Handle(BookDeletedEvent message)
        {
            var book = message.Book;

            if (book == null || book.Id <= 0)
            {
                return;
            }

            var authorId = book.AuthorId;

            if (message.DeleteFiles)
            {
                var author = book.Author ?? GetAuthorOrNull(authorId);

                if (author != null)
                {
                    var preferredBase = author.GetPathForMediaType(book.MediaType == BookMediaType.Audiobook);

                    foreach (var extra in _repository.GetFilesByBook(authorId, book.Id))
                    {
                        RecycleExtraFile(author, extra, preferredBase);
                    }
                }
            }

            // The rows go regardless: they point at a book that no longer exists.
            _repository.DeleteForBook(authorId, book.Id);
        }

        public void Handle(BookFileDeletedEvent message)
        {
            var bookFile = message.BookFile;

            if (message.Reason == DeleteMediaFileReason.NoLinkedEpisodes)
            {
                _logger.Debug("Removing track file from DB as part of cleanup routine, not deleting extra files from disk.");
            }
            else
            {
                var author = bookFile.Author;
                var preferredBase = ExtraFilePathHelper.GetPreferredBasePath(author, bookFile);

                foreach (var extra in _repository.GetFilesByBookFile(bookFile.Id))
                {
                    RecycleExtraFile(author, extra, preferredBase);
                }
            }

            _logger.Debug("Deleting Extra from database for book file: {0}", bookFile);
            _repository.DeleteForBookFile(bookFile.Id);
        }

        private void RecycleExtraFile(Author author, TExtraFile extra, string preferredBase)
        {
            var path = ExtraFilePathHelper.ResolveFullPath(author, extra.RelativePath, _diskProvider.FileExists, preferredBase);

            if (path.IsNullOrWhiteSpace() || !_diskProvider.FileExists(path))
            {
                return;
            }

            // Send to the recycling bin so they can be recovered if necessary
            var basePath = ExtraFilePathHelper.GetAuthorBasePaths(author)
                .Where(p => p.IsParentPath(path) || p.PathEquals(path))
                .OrderByDescending(p => p.Length)
                .FirstOrDefault() ?? preferredBase;

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
