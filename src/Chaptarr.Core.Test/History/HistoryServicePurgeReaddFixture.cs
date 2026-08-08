using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;

namespace Chaptarr.Core.Test.History
{
    [TestFixture]
    public class HistoryServicePurgeReaddFixture
    {
        private class HistoryRepositoryProxy : DispatchProxy
        {
            public List<EntityHistory> Items { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IHistoryRepository.GetByAuthor):
                    {
                        var authorId = (int)args[0];
                        var eventType = args[1] == null ? (EntityHistoryEventType?)null : (EntityHistoryEventType)args[1];
                        return Items
                            .Where(history => history.AuthorId == authorId && (!eventType.HasValue || history.EventType == eventType.Value))
                            .OrderByDescending(history => history.Date)
                            .ToList();
                    }

                    case nameof(IHistoryRepository.FindByDownloadId):
                    {
                        var downloadId = ((string)args[0])?.ToUpperInvariant();
                        return Items
                            .Where(history => string.Equals(history.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(history => history.Date)
                            .ToList();
                    }

                    case nameof(IHistoryRepository.UpdateMany):
                        return null;

                    case nameof(IHistoryRepository.DeleteMany):
                        DeleteItems(args[0]);
                        return null;

                    case nameof(IHistoryRepository.DeleteForAuthor):
                        Items.RemoveAll(history => history.AuthorId == (int)args[0]);
                        return null;

                    default:
                        throw new NotImplementedException($"Test proxy does not implement IHistoryRepository.{targetMethod?.Name}");
                }
            }

            private void DeleteItems(object value)
            {
                var ids = value switch
                {
                    IEnumerable<int> integerIds => integerIds.ToHashSet(),
                    IEnumerable<EntityHistory> histories => histories.Select(history => history.Id).ToHashSet(),
                    _ => new HashSet<int>()
                };

                Items.RemoveAll(history => ids.Contains(history.Id));
            }
        }

        private class DownloadHistoryRepositoryProxy : DispatchProxy
        {
            public List<DownloadHistory> Items { get; } = new();
            public int DeleteByAuthorCalls { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IDownloadHistoryRepository.GetByAuthorId):
                        return Items.Where(history => history.AuthorId == (int)args[0]).ToList();

                    case nameof(IDownloadHistoryRepository.FindByDownloadId):
                    {
                        var downloadId = ((string)args[0])?.ToUpperInvariant();
                        return Items
                            .Where(history => string.Equals(history.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(history => history.Date)
                            .ToList();
                    }

                    case nameof(IDownloadHistoryRepository.UpdateMany):
                        return null;

                    case nameof(IDownloadHistoryRepository.DeleteMany):
                        DeleteItems(args[0]);
                        return null;

                    case nameof(IDownloadHistoryRepository.DeleteByAuthorId):
                        DeleteByAuthorCalls++;
                        Items.RemoveAll(history => history.AuthorId == (int)args[0]);
                        return null;

                    default:
                        throw new NotImplementedException($"Test proxy does not implement IDownloadHistoryRepository.{targetMethod?.Name}");
                }
            }

            private void DeleteItems(object value)
            {
                var ids = value switch
                {
                    IEnumerable<int> integerIds => integerIds.ToHashSet(),
                    IEnumerable<DownloadHistory> histories => histories.Select(history => history.Id).ToHashSet(),
                    _ => new HashSet<int>()
                };

                Items.RemoveAll(history => ids.Contains(history.Id));
            }
        }

        [Test]
        public void purge_readd_preserves_imported_download_across_restart_and_rekeys_after_relink()
        {
            var oldAuthor = new Author { Id = 10, Name = "Old Author" };
            var entityRepository = DispatchProxy.Create<IHistoryRepository, HistoryRepositoryProxy>();
            var entityItems = ((HistoryRepositoryProxy)(object)entityRepository).Items;
            var downloadRepository = DispatchProxy.Create<IDownloadHistoryRepository, DownloadHistoryRepositoryProxy>();
            var downloadProxy = (DownloadHistoryRepositoryProxy)(object)downloadRepository;
            var now = DateTime.UtcNow;

            entityItems.AddRange(new[]
            {
                Entity(1, EntityHistoryEventType.Grabbed, 10, 20, 30, "KEEP", now.AddMinutes(-3)),
                Entity(2, EntityHistoryEventType.BookFileImported, 10, 20, 30, "KEEP", now.AddMinutes(-2), "fileId", "40"),
                Entity(3, EntityHistoryEventType.Grabbed, 10, 21, 31, "DROP", now.AddMinutes(-3)),
                Entity(4, EntityHistoryEventType.BookFileImported, 10, 21, 31, "DROP", now.AddMinutes(-2), "FileId", "41")
            });
            downloadProxy.Items.AddRange(new[]
            {
                Download(1, DownloadHistoryEventType.DownloadGrabbed, 10, 20, "KEEP", now.AddMinutes(-3)),
                Download(2, DownloadHistoryEventType.FileImported, 10, 20, "KEEP", now.AddMinutes(-2)),
                Download(3, DownloadHistoryEventType.DownloadImported, 10, 20, "KEEP", now.AddMinutes(-1)),
                Download(4, DownloadHistoryEventType.DownloadGrabbed, 10, 21, "DROP", now.AddMinutes(-3))
            });

            var historyService = new HistoryService(entityRepository, LogManager.GetCurrentClassLogger(), downloadRepository);
            var deletion = new AuthorDeletedEvent(
                oldAuthor,
                false,
                false,
                true,
                new[] { 40 },
                new Dictionary<int, string> { [40] = "hc:edition:stable-audiobook" });

            historyService.Handle(deletion);
            new DownloadHistoryService(downloadRepository, historyService).Handle(deletion);

            Assert.That(entityItems.Select(history => history.DownloadId), Is.EquivalentTo(new[] { "KEEP", "KEEP" }));
            var pendingImport = entityItems.Single(history => history.EventType == EntityHistoryEventType.BookFileImported);
            Assert.That(pendingImport.AuthorId, Is.Zero);
            Assert.That(pendingImport.BookId, Is.Zero);
            Assert.That(pendingImport.EditionId, Is.Zero);
            Assert.That(pendingImport.Data["PurgeReaddPending"], Is.EqualTo(bool.TrueString));
            Assert.That(entityItems.Single(history => history.EventType == EntityHistoryEventType.Grabbed).BookId, Is.EqualTo(20));
            Assert.That(downloadProxy.Items.Select(history => history.DownloadId), Is.All.EqualTo("KEEP"));
            Assert.That(downloadProxy.DeleteByAuthorCalls, Is.Zero, "the ordinary delete handler must not erase the selectively retained rows");

            // A fresh service instance models the post-restart lookup before the queued rematch runs.
            var restartedDownloadHistory = new DownloadHistoryService(downloadRepository, historyService);
            Assert.That(restartedDownloadHistory.DownloadAlreadyImported("keep"), Is.True);
            Assert.That(restartedDownloadHistory.GetLatestDownloadHistoryItem("keep").EventType, Is.EqualTo(DownloadHistoryEventType.DownloadImported));

            var newAuthor = new Author { Id = 60, Name = "New Author" };
            var newBook = new Book { Id = 70, AuthorId = newAuthor.Id, Author = newAuthor, Title = "Book" };
            var newEdition = new Edition { Id = 80, BookId = newBook.Id, Book = newBook, Title = "Book", ForeignEditionId = "hc:edition:stable-audiobook" };
            historyService.Handle(new BookFileAddedEvent(new BookFile
            {
                Id = 40,
                EditionId = newEdition.Id,
                Edition = newEdition,
                Author = newAuthor,
                Path = "/library/Book.m4b"
            }));

            Assert.That(entityItems, Has.All.Matches<EntityHistory>(history => history.AuthorId == 60 && history.BookId == 70));
            Assert.That(entityItems.Single(history => history.EventType == EntityHistoryEventType.BookFileImported).EditionId, Is.EqualTo(80));
            Assert.That(entityItems.Single(history => history.EventType == EntityHistoryEventType.BookFileImported).Data.Keys,
                Has.None.EqualTo("PurgeReaddPending"));
            Assert.That(downloadProxy.Items, Has.All.Matches<DownloadHistory>(history => history.AuthorId == 60 && history.BookId == 70));
            Assert.That(restartedDownloadHistory.DownloadAlreadyImported("keep"), Is.True);
        }

        [Test]
        public void purge_readd_does_not_preserve_fileless_or_zero_file_id_history()
        {
            var author = new Author { Id = 10, Name = "Author" };
            var entityRepository = DispatchProxy.Create<IHistoryRepository, HistoryRepositoryProxy>();
            var entityItems = ((HistoryRepositoryProxy)(object)entityRepository).Items;
            var downloadRepository = DispatchProxy.Create<IDownloadHistoryRepository, DownloadHistoryRepositoryProxy>();
            var downloadItems = ((DownloadHistoryRepositoryProxy)(object)downloadRepository).Items;

            entityItems.Add(Entity(1, EntityHistoryEventType.BookFileImported, 10, 20, 30, "ZERO", DateTime.UtcNow, "FileId", "0"));
            downloadItems.Add(Download(1, DownloadHistoryEventType.DownloadImported, 10, 20, "ZERO", DateTime.UtcNow));

            var service = new HistoryService(entityRepository, LogManager.GetCurrentClassLogger(), downloadRepository);
            service.Handle(new AuthorDeletedEvent(author, false, false, true, new[] { 40 }));

            Assert.That(entityItems, Is.Empty);
            Assert.That(downloadItems, Is.Empty);
        }

        private static EntityHistory Entity(
            int id,
            EntityHistoryEventType eventType,
            int authorId,
            int bookId,
            int editionId,
            string downloadId,
            DateTime date,
            string dataKey = null,
            string dataValue = null)
        {
            var history = new EntityHistory
            {
                Id = id,
                EventType = eventType,
                AuthorId = authorId,
                BookId = bookId,
                EditionId = editionId,
                DownloadId = downloadId,
                Date = date,
                SourceTitle = downloadId
            };

            if (dataKey != null)
            {
                history.Data[dataKey] = dataValue;
            }

            return history;
        }

        private static DownloadHistory Download(
            int id,
            DownloadHistoryEventType eventType,
            int authorId,
            int bookId,
            string downloadId,
            DateTime date)
        {
            return new DownloadHistory
            {
                Id = id,
                EventType = eventType,
                AuthorId = authorId,
                BookId = bookId,
                DownloadId = downloadId,
                Date = date,
                SourceTitle = downloadId
            };
        }
    }
}
