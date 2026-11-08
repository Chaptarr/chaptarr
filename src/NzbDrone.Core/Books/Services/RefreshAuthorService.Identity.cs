using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace NzbDrone.Core.Books
{
    public partial class RefreshAuthorService
    {
        private Author ReconcilePublishedAuthorSplit(Author local, Author published)
        {
            var localIds = AuthorIdentity.GetProviderIdentityTokens(local);
            var publishedIds = AuthorIdentity.GetProviderIdentityTokens(published);
            var removedIds = localIds.Except(publishedIds, StringComparer.OrdinalIgnoreCase).ToList();
            if (removedIds.Count == 0) return local;

            var canonical = GetPreferredAuthorIdentifier(published);
            var snapshots = new Dictionary<string, Author>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in removedIds)
            {
                try
                {
                    // Only identity changes need these extra fetches. No work is moved on a
                    // 304, a pruned/missing book, or a failed request.
                    var result = _authorInfo.RefreshAuthorInfo(id, bypassEtag: true);
                    if (result?.Terminal is { Code: "author_provider_record_missing", Reopenable: false, ResolvedProviderId: null })
                    {
                        _logger.Warn("[AUTHOR-SPLIT] Provider ID {0} for author {1} is no longer served ({2}); accepting the published identity without that retired ID. Local files and books are retained.", id, local.Id, result.Terminal.Code);
                        continue;
                    }
                    var owner = result?.HasChanges == true ? result.Author : null;
                    var ownerId = GetPreferredAuthorIdentifier(owner);
                    if (ownerId == null)
                    {
                        throw new AuthorIdentityUnverifiedException($"Cannot verify ownership of {id} for author {local.Id}; refresh will retry without changing identity.",
                            isTransient: result == null || result.HttpStatus == null || (int)result.HttpStatus == 429 || (int)result.HttpStatus >= 500);
                    }
                    if (string.Equals(canonical, ownerId, StringComparison.OrdinalIgnoreCase)) continue;
                    snapshots[ownerId] = owner;
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "[AUTHOR-SPLIT] Cannot verify ownership of {0} for author {1}; no ownership change inferred", id, local.Id);
                    throw;
                }
            }
            if (snapshots.Count == 0) return local;

            var allOwners = snapshots.Values.Prepend(published).ToList();
            var workOwners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var editionOwners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var owner in allOwners)
            {
                var ownerId = GetPreferredAuthorIdentifier(owner);
                foreach (var book in owner.Books ?? new List<Book>())
                {
                    foreach (var token in BookIdentity.GetStableWorkProviderIdentityTokens(book)) AddOwner(workOwners, token, ownerId);
                    foreach (var edition in book.Editions ?? new List<Edition>())
                        foreach (var token in BookEditionIdentity.GetRemoteEditionRehomeTokens(edition)) AddOwner(editionOwners, token, ownerId);
                }
            }

            var localAuthors = _authorService.GetAllAuthors() ?? new List<Author>();
            var locks = new Stack<IDisposable>();
            try
            {
                foreach (var author in localAuthors.Where(author => author.Id == local.Id ||
                    snapshots.ContainsKey(GetPreferredAuthorIdentifier(author) ?? string.Empty)).Append(local)
                    .DistinctBy(author => author.Id).OrderBy(author => author.Id))
                    locks.Push(BookService.EnterWantedEditionLock(author, _logger, default));
                local = _authorRepository.Get(local.Id);
                var books = _bookService.GetBooksByAuthor(local.Id);
                var editions = _editionService.GetEditionsByBook(books.Select(book => book.Id).ToList());
                var byBook = editions.GroupBy(edition => edition.BookId).ToDictionary(group => group.Key, group => group.ToList());
                foreach (var pair in snapshots)
                {
                    var remote = pair.Value;
                    var worksByEdition = (remote.Books ?? new List<Book>()).SelectMany(book =>
                        (book.Editions ?? new List<Edition>()).SelectMany(edition => BookEditionIdentity.GetRemoteEditionRehomeTokens(edition)
                            .Select(token => (Token: token, Book: book))))
                        .ToLookup(item => item.Token, item => item.Book, StringComparer.OrdinalIgnoreCase);
                    var wholeBooks = new List<Book>();
                    var partialBooks = new List<(Book Book, Book Remote, List<Edition> Editions)>();
                    foreach (var book in books)
                    {
                        var candidates = byBook.GetValueOrDefault(book.Id) ?? new List<Edition>();
                        var workOwner = UniqueOwner(workOwners, BookIdentity.GetStableWorkProviderIdentityTokens(book));
                        var assignments = candidates.ToDictionary(edition => edition.Id,
                            edition => FindOwners(editionOwners, BookEditionIdentity.GetEditionRehomeTokens(edition)));
                        var moving = candidates.Where(edition => assignments[edition.Id].Count == 1 && assignments[edition.Id].Contains(pair.Key)).ToList();
                        if (assignments.Values.Any(owners => owners.Count > 1))
                            _logger.Warn("[AUTHOR-SPLIT] Author {0}, book {1}: published authors claim the same edition; retaining ambiguous ownership", local.Id, book.Id);
                        if (moving.Count == candidates.Count && moving.Count > 0 ||
                            workOwner == pair.Key && assignments.Values.All(owners => owners.Count == 0 || owners.Count == 1 && owners.Contains(pair.Key)))
                            wholeBooks.Add(book);
                        else if (moving.Count > 0)
                        {
                            var assignmentsByWork = moving.Select(edition => new
                            {
                                Edition = edition,
                                Works = BookEditionIdentity.GetEditionRehomeTokens(edition).SelectMany(token => worksByEdition[token])
                                    .Where(work => work.MediaType == book.MediaType).Distinct().ToList()
                            }).ToList();
                            foreach (var group in assignmentsByWork.Where(item => item.Works.Count == 1).GroupBy(item => item.Works[0]))
                                partialBooks.Add((book, group.Key, group.Select(item => item.Edition).ToList()));
                            if (assignmentsByWork.Any(item => item.Works.Count != 1))
                                _logger.Warn("[AUTHOR-SPLIT] Author {0}, book {1}: some edition owners are known but their destination pocket is ambiguous; retaining those editions", local.Id, book.Id);
                        }
                    }

                    // No positive book/edition evidence means no local author is created or changed.
                    if (wholeBooks.Count == 0 && partialBooks.Count == 0) continue;
                    var target = localAuthors.SingleOrDefault(author => author.Id != local.Id &&
                        string.Equals(GetPreferredAuthorIdentifier(author), pair.Key, StringComparison.OrdinalIgnoreCase));
                    var newAuthor = target == null;
                    target = target == null ? local.ShallowClone() : _authorRepository.Get(target.Id);
                    var storedSlug = target.TitleSlug;
                    target.UseMetadataFrom(remote);
                    ApplyPublishedProviderIds(target, remote);
                    if (newAuthor)
                    {
                        target.Id = 0;
                        target.TitleSlug = string.IsNullOrWhiteSpace(remote.TitleSlug) ? "author-" + pair.Key.Replace(':', '-') : remote.TitleSlug;
                        target.AddOptions = new AddAuthorOptions { Monitor = null };
                        target.Added = DateTime.UtcNow;
                    }
                    target.TitleSlug ??= storedSlug;
                    AuthorService.NormalizeAuthorDbFields(target, _logger);

                    var source = local.ShallowClone();
                    ApplyPublishedProviderIds(source, published);
                    var affectedBooks = wholeBooks.Select(book => book.Id).Concat(partialBooks.Select(item => item.Book.Id)).ToList();
                    var settings = $"{wholeBooks.Count} books moved; {partialBooks.Sum(item => item.Editions.Count)} editions moved from shared pockets. Files stayed in place; monitoring and pins retained.";
                    _authorService.UpdateAuthor(source, (connection, transaction) =>
                    {
                        // Release unique scalar IDs inside the same transaction before creating the new owner.
                        connection.Execute(@"UPDATE ""Authors"" SET ""HardcoverAuthorId"" = @HardcoverAuthorId,
                            ""GoodreadsAuthorId"" = @GoodreadsAuthorId, ""AudnexusAuthorId"" = @AudnexusAuthorId,
                            ""OpenLibraryAuthorId"" = @OpenLibraryAuthorId, ""GoogleBooksAuthorId"" = @GoogleBooksAuthorId WHERE ""Id"" = @Id", source, transaction);
                        if (newAuthor)
                            _authorRepository.InsertMany(new[] { target }, connection, transaction);
                        else
                            _authorRepository.Update(target, connection, transaction);
                        var writer = new LibraryIdentityWriter(connection, transaction, _mainDatabase.DatabaseType, _editionSelector);
                        writer.ReassignBooks(wholeBooks.Select(book => book.Id).ToArray(), target.Id, local.Id);
                        foreach (var item in partialBooks)
                        {
                            // Keep local copy/pin settings. The new pocket is an ownership split of that
                            // instance, never a fresh author-add or a Monitor All operation.
                            var pocket = RefreshEntityCopy.CloneBook(item.Book, false);
                            pocket.UseMetadataFrom(item.Remote);
                            pocket.Author = target;
                            pocket.AuthorId = target.Id;
                            pocket.TitleSlug = (item.Remote.TitleSlug ?? item.Book.TitleSlug) + "-split-" + item.Book.Id + "-" + item.Editions[0].Id;
                            pocket.AddOptions.SearchForNewBook = false;
                            _bookRepository.InsertMany(new[] { pocket }, connection, transaction);
                            writer.MoveEditions(item.Editions.Select(edition => new LibraryIdentityWriter.EditionMove(edition.Id, pocket.Id)).ToArray());
                            affectedBooks.Add(pocket.Id);
                        }
                        WriteIdentityHistory(connection, transaction, local, target, true, settings);
                    });
                    // Later owners must see the remaining editions after this committed split,
                    // so the last owner can retain/reassign the populated book row itself.
                    foreach (var item in partialBooks)
                    {
                        var movedIds = item.Editions.Select(edition => edition.Id).ToHashSet();
                        byBook[item.Book.Id].RemoveAll(edition => movedIds.Contains(edition.Id));
                    }
                    _authorService.ClearAuthorCache();
                    _logger.Info("[AUTHOR-SPLIT] {0} (author {1}) → {2} (author {3}, canonical {4}): {5}", local.Name, local.Id, target.Name, target.Id, pair.Key, settings);
                    PublishIdentityChange(new LibraryIdentityChangedEvent(new[] { local.Id, target.Id }, affectedBooks)
                        { SourceAuthor = local, TargetAuthor = target, IsSplit = true, SettingsResolution = settings });
                    local = _authorService.GetAuthor(local.Id);
                    books.RemoveAll(book => wholeBooks.Any(moved => moved.Id == book.Id));
                }
            }
            finally
            {
                while (locks.Count > 0) locks.Pop().Dispose();
            }
            return local;
        }

        private static void ApplyPublishedProviderIds(Author author, Author published)
        {
            author.HardcoverAuthorId = published.HardcoverAuthorId;
            author.GoodreadsAuthorId = published.GoodreadsAuthorId;
            author.AudnexusAuthorId = published.AudnexusAuthorId;
            author.OpenLibraryAuthorId = published.OpenLibraryAuthorId;
            author.GoogleBooksAuthorId = published.GoogleBooksAuthorId;
            author.RemoteProviderIds = AuthorIdentity.GetProviderIdentityTokens(published);
        }

        private static void AddOwner(Dictionary<string, HashSet<string>> index, string token, string owner)
        {
            if (!index.TryGetValue(token, out var owners)) index[token] = owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            owners.Add(owner);
        }

        private static string UniqueOwner(Dictionary<string, HashSet<string>> index, IEnumerable<string> tokens)
        {
            var owners = FindOwners(index, tokens);
            return owners.Count == 1 ? owners.Single() : null;
        }

        private static HashSet<string> FindOwners(Dictionary<string, HashSet<string>> index, IEnumerable<string> tokens)
        {
            var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in tokens)
                if (index.TryGetValue(token, out var values)) owners.UnionWith(values);
            return owners;
        }
    }
}
