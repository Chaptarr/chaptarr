using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Blocklisting
{
    public interface IBlocklistService
    {
        bool Blocklisted(int authorId, ReleaseInfo release);
        bool BlocklistedTorrentHash(int authorId, string hash);
        PagingSpec<Blocklist> Paged(PagingSpec<Blocklist> pagingSpec);
        void Block(RemoteBook remoteEpisode, string message);
        void Delete(int id);
        void Delete(List<int> ids);
    }

    public class BlocklistService : IBlocklistService,

                                    IExecute<ClearBlocklistCommand>,
                                    IHandle<DownloadFailedEvent>,
                                    IHandleAsync<AuthorDeletedEvent>
    {
        private readonly IBlocklistRepository _blocklistRepository;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly Logger _logger;

        public BlocklistService(IBlocklistRepository blocklistRepository,
                                IAuthorService authorService,
                                IBookService bookService,
                                Logger logger)
        {
            _blocklistRepository = blocklistRepository;
            _authorService = authorService;
            _bookService = bookService;
            _logger = logger;
        }

        public bool Blocklisted(int authorId, ReleaseInfo release)
        {
            if (release.DownloadProtocol == DownloadProtocol.Torrent)
            {
                if (release is not TorrentInfo torrentInfo)
                {
                    return false;
                }

                if (torrentInfo.InfoHash.IsNotNullOrWhiteSpace())
                {
                    var blocklistedByTorrentInfohash = _blocklistRepository.BlocklistedByTorrentInfoHash(authorId, torrentInfo.InfoHash);

                    return blocklistedByTorrentInfohash.Any(b => SameTorrent(b, torrentInfo));
                }

                return _blocklistRepository.BlocklistedByTitle(authorId, release.Title)
                    .Where(b => b.Protocol == DownloadProtocol.Torrent)
                    .Any(b => SameTorrent(b, torrentInfo));
            }

            return _blocklistRepository.BlocklistedByTitle(authorId, release.Title)
                .Where(b => b.Protocol == DownloadProtocol.Usenet)
                .Any(b => SameNzb(b, release));
        }

        public bool BlocklistedTorrentHash(int authorId, string hash)
        {
            return _blocklistRepository.BlocklistedByTorrentInfoHash(authorId, hash).Any(b =>
                b.TorrentInfoHash.Equals(hash, StringComparison.InvariantCultureIgnoreCase));
        }

        public PagingSpec<Blocklist> Paged(PagingSpec<Blocklist> pagingSpec)
        {
            if (pagingSpec == null)
            {
                throw new ArgumentNullException(nameof(pagingSpec));
            }

            var sortKey = pagingSpec.SortKey?.Trim();
            if (sortKey.IsNotNullOrWhiteSpace() && sortKey.Contains('.'))
            {
                if (IsAuthorSortKey(sortKey))
                {
                    return GetPagedSortedByAuthor(pagingSpec);
                }

                // Blocklist paging does not join any other tables; qualified sort keys will break SQL ordering.
                // Fall back to base-table sorting to ensure the blocklist always loads.
                pagingSpec.SortKey = nameof(Blocklist.Date);
            }

            return _blocklistRepository.GetPaged(pagingSpec);
        }

        public void Block(RemoteBook remoteEpisode, string message)
        {
            var blocklist = new Blocklist
            {
                // Collect all provider IDs for the author
                AuthorProviderIds = GetAuthorProviderIds(remoteEpisode.Author),
                // Collect all provider IDs for the books
                BookProviderIds = GetBookProviderIds(remoteEpisode.Books),
                SourceTitle = remoteEpisode.Release.Title,
                Quality = remoteEpisode.ParsedBookInfo.Quality,
                Date = DateTime.UtcNow,
                PublishedDate = remoteEpisode.Release.PublishDate,
                Size = remoteEpisode.Release.Size,
                Indexer = remoteEpisode.Release.Indexer,
                Protocol = remoteEpisode.Release.DownloadProtocol,
                Message = message
            };

            if (remoteEpisode.Release is TorrentInfo torrentRelease)
            {
                blocklist.TorrentInfoHash = torrentRelease.InfoHash;
            }

            _blocklistRepository.Insert(blocklist);
        }
        
        private List<string> GetAuthorProviderIds(Author author)
        {
            return AuthorIdentity.GetProviderIdentityTokenList(author);
        }
        
        private List<string> GetBookProviderIds(List<Book> books)
        {
            var ids = new List<string>();
            
            foreach (var book in books)
            {
                if (!string.IsNullOrWhiteSpace(book.GoodreadsWorkId))
                    ids.Add($"grw:{ProviderIdHelper.StripPrefix(book.GoodreadsWorkId)}");
                if (!string.IsNullOrWhiteSpace(book.HardcoverBookId))
                    ids.Add(ProviderIdHelper.Canonicalize(book.HardcoverBookId, "hc"));
                if (!string.IsNullOrWhiteSpace(book.OpenLibraryWorkId))
                    ids.Add($"olw:{ProviderIdHelper.StripPrefix(book.OpenLibraryWorkId)}");
                if (book.RemoteProviderIds != null)
                    ids.AddRange(book.RemoteProviderIds.Where(id => id.IsNotNullOrWhiteSpace()).Select(id => id.Trim()));

                foreach (var edition in (book.Editions ?? new List<Edition>()).Where(e => e != null))
                {
                    if (edition.GoodreadsEditionId.HasValue)
                        ids.Add($"gre:{edition.GoodreadsEditionId.Value}");
                    if (!string.IsNullOrWhiteSpace(edition.OpenLibraryEditionId))
                        ids.Add($"ole:{ProviderIdHelper.StripPrefix(edition.OpenLibraryEditionId)}");
                    if (!string.IsNullOrWhiteSpace(edition.GoogleBooksEditionId))
                        ids.Add(ProviderIdHelper.Canonicalize(edition.GoogleBooksEditionId, "gb"));
                    if (!string.IsNullOrWhiteSpace(edition.Asin))
                        ids.Add($"amz:{edition.Asin}");
                    if (!string.IsNullOrWhiteSpace(edition.AudibleASIN))
                        ids.Add($"aud:{edition.AudibleASIN}");
                    if (!string.IsNullOrWhiteSpace(edition.Isbn10))
                        ids.Add($"isbn10:{edition.Isbn10}");
                    if (!string.IsNullOrWhiteSpace(edition.Isbn13))
                        ids.Add($"isbn13:{edition.Isbn13}");
                }

                var fallbackGoodreadsEditionId = BookEditionIdentity.GetGoodreadsEditionProviderId(book);
                if (!string.IsNullOrWhiteSpace(fallbackGoodreadsEditionId))
                    ids.Add($"gre:{ProviderIdHelper.StripPrefix(fallbackGoodreadsEditionId)}");

                var fallbackOpenLibraryEditionId = BookEditionIdentity.GetOpenLibraryEditionId(book);
                if (!string.IsNullOrWhiteSpace(fallbackOpenLibraryEditionId))
                    ids.Add($"ole:{ProviderIdHelper.StripPrefix(fallbackOpenLibraryEditionId)}");

                var fallbackGoogleBooksEditionId = BookEditionIdentity.GetGoogleBooksEditionId(book);
                if (!string.IsNullOrWhiteSpace(fallbackGoogleBooksEditionId))
                    ids.Add(ProviderIdHelper.Canonicalize(fallbackGoogleBooksEditionId, "gb"));

                var fallbackAsin = BookEditionIdentity.GetAsin(book);
                if (!string.IsNullOrWhiteSpace(fallbackAsin))
                    ids.Add($"amz:{fallbackAsin}");

                var fallbackAudibleAsin = BookEditionIdentity.GetAudibleAsin(book);
                if (!string.IsNullOrWhiteSpace(fallbackAudibleAsin))
                    ids.Add($"aud:{fallbackAudibleAsin}");

                var fallbackIsbn10 = BookEditionIdentity.GetIsbn10(book);
                if (!string.IsNullOrWhiteSpace(fallbackIsbn10))
                    ids.Add($"isbn10:{fallbackIsbn10}");

                var fallbackIsbn13 = BookEditionIdentity.GetIsbn13(book);
                if (!string.IsNullOrWhiteSpace(fallbackIsbn13))
                    ids.Add($"isbn13:{fallbackIsbn13}");
            }
            
            return ids.Distinct().ToList();
        }

        public void Delete(int id)
        {
            _blocklistRepository.Delete(id);
        }

        public void Delete(List<int> ids)
        {
            _blocklistRepository.DeleteMany(ids);
        }

        private bool SameNzb(Blocklist item, ReleaseInfo release)
        {
            if (item.PublishedDate == release.PublishDate)
            {
                return true;
            }

            if (!HasSameIndexer(item, release.Indexer) &&
                HasSamePublishedDate(item, release.PublishDate) &&
                HasSameSize(item, release.Size))
            {
                return true;
            }

            return false;
        }

        private bool SameTorrent(Blocklist item, TorrentInfo release)
        {
            if (release.InfoHash.IsNotNullOrWhiteSpace())
            {
                return release.InfoHash.Equals(item.TorrentInfoHash, StringComparison.InvariantCultureIgnoreCase);
            }

            return HasSameIndexer(item, release.Indexer);
        }

        private bool HasSameIndexer(Blocklist item, string indexer)
        {
            if (item.Indexer.IsNullOrWhiteSpace())
            {
                return true;
            }

            return item.Indexer.Equals(indexer, StringComparison.InvariantCultureIgnoreCase);
        }

        private bool HasSamePublishedDate(Blocklist item, DateTime publishedDate)
        {
            if (!item.PublishedDate.HasValue)
            {
                return true;
            }

            return item.PublishedDate.Value.AddMinutes(-2) <= publishedDate &&
                   item.PublishedDate.Value.AddMinutes(2) >= publishedDate;
        }

        private bool HasSameSize(Blocklist item, long size)
        {
            if (!item.Size.HasValue)
            {
                return true;
            }

            var difference = Math.Abs(item.Size.Value - size);

            return difference <= 2.Megabytes();
        }

        public void Execute(ClearBlocklistCommand message)
        {
            _blocklistRepository.Purge();
        }

        public void Handle(DownloadFailedEvent message)
        {
            var authorProviderIds = GetAuthorProviderIdsForFailedDownload(message);
            var bookProviderIds = GetBookProviderIdsForFailedDownload(message);
            var protocol = (DownloadProtocol)Convert.ToInt32(message.Data.GetValueOrDefault("protocol"));
            var torrentInfoHash = message.TrackedDownload?.Protocol == DownloadProtocol.Torrent
                ? message.TrackedDownload.DownloadItem.DownloadId
                : message.Data.GetValueOrDefault("torrentInfoHash");

            if (authorProviderIds.Count == 0)
            {
                _logger.Warn("[BLOCKLIST] Failed download '{0}' has no resolvable author provider identity; its blocklist entry cannot match future releases", message.SourceTitle);
            }

            if (protocol == DownloadProtocol.Torrent && torrentInfoHash.IsNullOrWhiteSpace())
            {
                _logger.Warn("[BLOCKLIST] Failed torrent '{0}' has no torrent info hash; its blocklist entry cannot match future hash-bearing candidates", message.SourceTitle);
            }

            var blocklist = new Blocklist
            {
                AuthorProviderIds = authorProviderIds,
                BookProviderIds = bookProviderIds,
                SourceTitle = message.SourceTitle,
                Quality = message.Quality,
                Date = DateTime.UtcNow,
                PublishedDate = ParseDateTimeOrNull(message.Data.GetValueOrDefault("publishedDate")),
                Size = ParseLongOrNull(message.Data.GetValueOrDefault("size")),
                Indexer = message.Data.GetValueOrDefault("indexer"),
                Protocol = protocol,
                Message = message.Message,
                TorrentInfoHash = torrentInfoHash
            };

            if (Enum.TryParse(message.Data.GetValueOrDefault("indexerFlags"), true, out IndexerFlags flags))
            {
                blocklist.IndexerFlags = flags;
            }

            _blocklistRepository.Insert(blocklist);
        }

        private List<string> GetAuthorProviderIdsForFailedDownload(DownloadFailedEvent message)
        {
            if (message == null || message.AuthorId <= 0)
            {
                return new List<string>();
            }

            try
            {
                var author = _authorService.GetAuthor(message.AuthorId);

                if (author == null)
                {
                    return new List<string>();
                }

                return GetAuthorProviderIds(author);
            }
            catch
            {
                return new List<string>();
            }
        }

        private List<string> GetBookProviderIdsForFailedDownload(DownloadFailedEvent message)
        {
            if (message?.BookIds == null || message.BookIds.Count == 0)
            {
                return new List<string>();
            }

            try
            {
                var bookIds = message.BookIds.Where(id => id > 0).Distinct().ToList();
                if (bookIds.Count == 0)
                {
                    return new List<string>();
                }

                var books = _bookService.GetBooks(bookIds);
                if (books == null || books.Count == 0)
                {
                    return new List<string>();
                }

                return GetBookProviderIds(books);
            }
            catch
            {
                return new List<string>();
            }
        }

        private DateTime? ParseDateTimeOrNull(string value)
        {
            if (value.IsNullOrWhiteSpace())
            {
                return null;
            }

            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var dateTime))
            {
                return dateTime;
            }

            if (DateTime.TryParse(value, out dateTime))
            {
                return dateTime;
            }

            return null;
        }

        private long? ParseLongOrNull(string value)
        {
            if (value.IsNullOrWhiteSpace())
            {
                return null;
            }

            if (long.TryParse(value, out var parsed))
            {
                return parsed;
            }

            return null;
        }

        public void HandleAsync(AuthorDeletedEvent message)
        {
            var blocklisted = _blocklistRepository.BlocklistedByAuthor(message.Author.Id);

            _blocklistRepository.DeleteMany(blocklisted);
        }

        private static bool IsAuthorSortKey(string sortKey)
        {
            return sortKey.Equals("authors.sortName", StringComparison.OrdinalIgnoreCase) ||
                   sortKey.Equals("author.sortName", StringComparison.OrdinalIgnoreCase) ||
                   sortKey.Equals("authors.SortName", StringComparison.OrdinalIgnoreCase);
        }

        private PagingSpec<Blocklist> GetPagedSortedByAuthor(PagingSpec<Blocklist> pagingSpec)
        {
            var items = _blocklistRepository.All().ToList();

            if (pagingSpec.FilterExpressions != null && pagingSpec.FilterExpressions.Count > 0)
            {
                foreach (var filter in pagingSpec.FilterExpressions)
                {
                    items = items.Where(filter.Compile()).ToList();
                }
            }

            var totalRecords = items.Count;

            if (totalRecords == 0)
            {
                pagingSpec.TotalRecords = 0;
                pagingSpec.Records = new List<Blocklist>();
                return pagingSpec;
            }

            var authorsByProviderId = BuildAuthorByProviderIdMap();

            string ResolveAuthorSortName(Blocklist blocklist)
            {
                if (blocklist?.AuthorProviderIds == null || blocklist.AuthorProviderIds.Count == 0)
                {
                    return null;
                }

                foreach (var providerId in blocklist.AuthorProviderIds)
                {
                    if (providerId.IsNullOrWhiteSpace())
                    {
                        continue;
                    }

                    if (authorsByProviderId.TryGetValue(providerId.Trim(), out var author) && author != null)
                    {
                        return author.SortName;
                    }
                }

                return null;
            }

            var decorated = items.Select(item => new
            {
                Item = item,
                SortName = ResolveAuthorSortName(item)
            });

            // Sort with unknown authors last, then tie-break by Date descending for stability.
            var ordered = pagingSpec.SortDirection == SortDirection.Descending
                ? decorated.OrderBy(x => x.SortName.IsNullOrWhiteSpace())
                           .ThenByDescending(x => x.SortName, StringComparer.InvariantCultureIgnoreCase)
                           .ThenByDescending(x => x.Item.Date)
                : decorated.OrderBy(x => x.SortName.IsNullOrWhiteSpace())
                           .ThenBy(x => x.SortName, StringComparer.InvariantCultureIgnoreCase)
                           .ThenByDescending(x => x.Item.Date);

            var skip = Math.Max(pagingSpec.Page - 1, 0) * pagingSpec.PageSize;
            pagingSpec.TotalRecords = totalRecords;
            pagingSpec.Records = ordered.Skip(skip).Take(pagingSpec.PageSize).Select(x => x.Item).ToList();

            return pagingSpec;
        }

        private Dictionary<string, Author> BuildAuthorByProviderIdMap()
        {
            var map = new Dictionary<string, Author>(StringComparer.OrdinalIgnoreCase);
            var authors = _authorService.GetAllAuthors() ?? new List<Author>();

            foreach (var author in authors)
            {
                foreach (var providerId in AuthorIdentity.GetProviderIdentityTokenList(author))
                {
                    Add(providerId);
                }

                void Add(string providerId)
                {
                    if (providerId.IsNullOrWhiteSpace())
                    {
                        return;
                    }

                    var key = providerId.Trim();
                    if (!map.ContainsKey(key))
                    {
                        map[key] = author;
                    }
                }
            }

            return map;
        }
    }
}
