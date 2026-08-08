using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Blocklisting
{
    public interface IBlocklistRepository : IBasicRepository<Blocklist>
    {
        List<Blocklist> BlocklistedByTitle(int authorId, string sourceTitle);
        List<Blocklist> BlocklistedByTorrentInfoHash(int authorId, string torrentInfoHash);
        List<Blocklist> BlocklistedByAuthor(int authorId);
    }

    public class BlocklistRepository : BasicRepository<Blocklist>, IBlocklistRepository
    {
        public BlocklistRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public List<Blocklist> BlocklistedByTitle(int authorId, string sourceTitle)
        {
            var author = LoadAuthor(authorId);
            if (author == null)
            {
                return new List<Blocklist>();
            }

            var authorProviderIds = GetAuthorProviderIds(author);
            if (authorProviderIds.Count == 0)
            {
                return new List<Blocklist>();
            }

            // In-memory filter due to JSON-serialized list fields
            var all = Query(Builder());
            return all.Where(b => b.AuthorProviderIds != null &&
                                  b.AuthorProviderIds.Intersect(authorProviderIds, StringComparer.InvariantCultureIgnoreCase).Any() &&
                                  !string.IsNullOrWhiteSpace(b.SourceTitle) &&
                                  b.SourceTitle.IndexOf(sourceTitle ?? string.Empty, StringComparison.InvariantCultureIgnoreCase) >= 0)
                      .ToList();
        }

        public List<Blocklist> BlocklistedByTorrentInfoHash(int authorId, string torrentInfoHash)
        {
            var author = LoadAuthor(authorId);
            if (author == null)
            {
                return new List<Blocklist>();
            }

            var authorProviderIds = GetAuthorProviderIds(author);
            if (authorProviderIds.Count == 0)
            {
                return new List<Blocklist>();
            }

            var all = Query(Builder());
            return all.Where(b => b.AuthorProviderIds != null &&
                                  b.AuthorProviderIds.Intersect(authorProviderIds, StringComparer.InvariantCultureIgnoreCase).Any() &&
                                  !string.IsNullOrWhiteSpace(b.TorrentInfoHash) &&
                                  b.TorrentInfoHash.IndexOf(torrentInfoHash ?? string.Empty, StringComparison.InvariantCultureIgnoreCase) >= 0)
                      .ToList();
        }

        public List<Blocklist> BlocklistedByAuthor(int authorId)
        {
            var author = LoadAuthor(authorId);
            if (author == null)
            {
                return new List<Blocklist>();
            }

            var authorProviderIds = GetAuthorProviderIds(author);
            if (authorProviderIds.Count == 0)
            {
                return new List<Blocklist>();
            }

            var all = Query(Builder());
            return all.Where(b => b.AuthorProviderIds != null &&
                                  b.AuthorProviderIds.Intersect(authorProviderIds, StringComparer.InvariantCultureIgnoreCase).Any())
                      .ToList();
        }

        private Author LoadAuthor(int authorId)
        {
            var builder = new SqlBuilder(_database.DatabaseType)
                .Where<Author>(a => a.Id == authorId);

            return _database.Query<Author>(builder).FirstOrDefault();
        }

        private List<string> GetAuthorProviderIds(Author author)
        {
            var ids = new List<string>();

            // Provider ID fields already contain the prefix (e.g., "gr:12345", "hc:abc123")
            // so we add them directly without adding another prefix
            if (!string.IsNullOrWhiteSpace(author.GoodreadsAuthorId)) ids.Add(author.GoodreadsAuthorId);
            if (!string.IsNullOrWhiteSpace(author.HardcoverAuthorId)) ids.Add(author.HardcoverAuthorId);
            if (!string.IsNullOrWhiteSpace(author.OpenLibraryAuthorId)) ids.Add(author.OpenLibraryAuthorId);
            if (!string.IsNullOrWhiteSpace(author.AudnexusAuthorId)) ids.Add(author.AudnexusAuthorId);
            if (!string.IsNullOrWhiteSpace(author.GoogleBooksAuthorId)) ids.Add(author.GoogleBooksAuthorId);
            if (author.RemoteProviderIds != null)
            {
                ids.AddRange(author.RemoteProviderIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()));
            }

            return ids;
        }
    }
}
