using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NLog;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public class BookshelfRefreshIdentityMapService : IHandle<BookInfoRefreshedEvent>, IHandle<BookAddedEvent>
    {
        private static readonly object MappingGate = new object();
        private readonly IMainDatabase _database;
        private readonly IBookService _books;
        private readonly IConfigService _config;
        private readonly IBookshelfLegacyIdentityMapService _identities;
        private readonly Logger _logger;

        public BookshelfRefreshIdentityMapService(IMainDatabase database, IBookService books,
            IConfigService config, IBookshelfLegacyIdentityMapService identities, Logger logger)
        {
            _database = database;
            _books = books;
            _config = config;
            _identities = identities;
            _logger = logger;
        }

        [EventHandleOrder(EventHandleOrder.First)]
        public void Handle(BookInfoRefreshedEvent message)
        {
            if (_config.BookshelfMatchCompatibility && message.Author?.Id > 0)
            {
                RegisterBooks(_books.GetBooksByAuthor(message.Author.Id));
            }
        }

        [EventHandleOrder(EventHandleOrder.First)]
        public void Handle(BookAddedEvent message)
        {
            if (_config.BookshelfMatchCompatibility && message.Book?.AuthorId > 0)
            {
                RegisterBooks(_books.GetBooksByAuthor(message.Book.AuthorId));
            }
        }

        public void RegisterBooks(IEnumerable<Book> books)
        {
            if (!_config.BookshelfMatchCompatibility)
            {
                return;
            }

            var rows = (books ?? Enumerable.Empty<Book>()).Where(book => book?.Id > 0)
                .DistinctBy(book => book.Id).ToList();
            if (rows.Count == 0)
            {
                return;
            }

            lock (MappingGate)
            {
                var mappings = _identities.GetMappingsByTargetIds("Book", rows.Select(book => book.Id));
                var occupiedSourceIds = _identities.GetMappings("Book", rows.Select(book => book.Id))
                    .Select(mapping => mapping.SourceId).ToHashSet();
                using var connection = _database.OpenConnection();
                using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
                var nextId = Math.Max(rows.Max(book => book.Id), connection.ExecuteScalar<int>(
                    "SELECT COALESCE(MAX(\"SourceId\"), 0) FROM \"BookshelfLegacyIdentityMap\" WHERE \"Source\" = 'BookshelfNG' AND LOWER(\"EntityType\") = 'book'", transaction: transaction));

                // Pair only unambiguous format counterparts. Different pockets in the
                // same format must remain distinct even when the provider reuses a work ID.
                var groups = rows.Select(book => new { Book = book, Tokens = BookEditionIdentity.GetCanonicalWorkProviderIds(book) })
                    .Where(row => row.Tokens.Count > 0)
                    .GroupBy(row => (row.Book.AuthorId, Identity: string.Join("|", row.Tokens.OrderBy(token => token, StringComparer.OrdinalIgnoreCase))));
                foreach (var group in groups)
                {
                    var members = group.Select(row => row.Book).ToList();
                    var memberIds = members.Select(book => book.Id).ToHashSet();
                    var existing = mappings.Where(mapping => memberIds.Contains(mapping.TargetId)).ToList();
                    var sourceIds = existing.Select(mapping => mapping.SourceId).Distinct().ToList();
                    if (sourceIds.Count > 1 || members.GroupBy(book => book.MediaType).Any(format => format.Count() > 1))
                    {
                        _logger.Warn("Skipped ambiguous Bookshelf refresh identity group for author ID {0}: {1} rows, {2} legacy identities",
                            group.Key.AuthorId, members.Count, sourceIds.Count);
                        continue;
                    }

                    var missing = members.Where(book => !existing.Any(mapping => mapping.TargetId == book.Id)).ToList();
                    if (missing.Count == 0)
                    {
                        continue;
                    }

                    // Newly discovered works receive persistent legacy API IDs. Existing
                    // source IDs remain unchanged, including when a format is added later.
                    var legacyId = sourceIds.Count == 1 ? sourceIds[0] : members.Min(book => book.Id);
                    if (sourceIds.Count == 0 && occupiedSourceIds.Contains(legacyId))
                    {
                        legacyId = checked(++nextId);
                    }

                    occupiedSourceIds.Add(legacyId);
                    foreach (var book in missing)
                    {
                        connection.Execute(@"INSERT INTO ""BookshelfLegacyIdentityMap""
(""Source"", ""EntityType"", ""SourceId"", ""TargetId"", ""MediaType"")
VALUES ('BookshelfNG', 'Book', @LegacyId, @TargetId, @MediaType)",
                            new { LegacyId = legacyId, TargetId = book.Id, MediaType = (int)book.MediaType }, transaction);
                    }
                }

                transaction.Commit();
            }
        }
    }
}
