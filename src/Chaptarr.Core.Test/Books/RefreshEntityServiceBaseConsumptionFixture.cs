using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class RefreshEntityServiceBaseConsumptionFixture
    {
        private sealed class TestRefreshService : RefreshEntityServiceBase<Author, Book>
        {
            private readonly List<Book> _localChildren;

            public SortedChildren LastSortedChildren { get; private set; }
            public RemoteData RemoteDataForRefresh { get; set; }
            public bool ShouldDeleteResult { get; set; } = true;
            public bool IsMergeResult { get; set; }
            public Author MergeTarget { get; set; }
            public bool Deleted { get; private set; }
            public bool? DeletedWithFiles { get; private set; }

            public TestRefreshService(List<Book> localChildren, Logger logger)
                : base(logger)
            {
                _localChildren = localChildren;
            }

            public SortedChildren SortChildrenPublic(Author author, List<Book> remoteChildren)
            {
                SortChildren(author, remoteChildren, remoteData: null, forceChildRefresh: false, forceUpdateFileTags: false, lastUpdate: null);
                return LastSortedChildren;
            }

            protected override RemoteData GetRemoteData(Author local, List<Author> remote, Author data)
            {
                return RemoteDataForRefresh ?? throw new NotImplementedException();
            }

            protected override bool ShouldDelete(Author local)
            {
                return ShouldDeleteResult;
            }

            protected override bool IsMerge(Author local, Author remote)
            {
                return IsMergeResult;
            }

            protected override UpdateResult UpdateEntity(Author local, Author remote)
            {
                return UpdateResult.None;
            }

            protected override Author GetEntityByForeignId(Author local)
            {
                return MergeTarget;
            }

            protected override void SaveEntity(Author local)
            {
            }

            protected override void DeleteEntity(Author local, bool deleteFiles)
            {
                Deleted = true;
                DeletedWithFiles = deleteFiles;
            }

            protected override List<Book> GetRemoteChildren(Author local, Author remote)
            {
                return remote?.Books ?? new List<Book>();
            }

            protected override List<Book> GetLocalChildren(Author entity, List<Book> remoteChildren)
            {
                return new List<Book>(_localChildren);
            }

            protected override Tuple<Book, List<Book>> GetMatchingExistingChildren(List<Book> existingChildren, Book remote)
            {
                var matches = existingChildren
                    .Where(b => b.MediaType == remote.MediaType && b.GoodreadsWorkId == remote.GoodreadsWorkId)
                    .OrderBy(b => b.Id)
                    .ToList();

                if (!matches.Any())
                {
                    return Tuple.Create<Book, List<Book>>(null, new List<Book>());
                }

                return Tuple.Create(matches.First(), new List<Book>());
            }

            protected override void PrepareNewChild(Book child, Author entity)
            {
            }

            protected override void PrepareExistingChild(Book local, Book remote, Author entity)
            {
            }

            protected override bool AreChildrenUpToDate(Book local, Book remote)
            {
                return true;
            }

            protected override void AddChildren(List<Book> children)
            {
            }

            protected override bool RefreshChildren(SortedChildren localChildren, List<Book> remoteChildren, Author remoteData, bool forceChildRefresh, bool forceUpdateFileTags, DateTime? lastUpdate)
            {
                LastSortedChildren = localChildren;
                return false;
            }
        }

        private class RecordingBookServiceProxy : DispatchProxy
        {
            public int? DeletedBookId { get; private set; }
            public bool? DeletedWithFiles { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IBookService.DeleteBook))
                {
                    DeletedBookId = (int)args[0];
                    DeletedWithFiles = (bool)args[1];
                    return null;
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        private sealed class TestRefreshBookService : RefreshBookService
        {
            public TestRefreshBookService(IBookService bookService, Logger logger)
                : base(
                    bookService,
                    authorService: null,
                    rootFolderService: null,
                    editionService: null,
                    authorInfo: null,
                    bookInfo: null,
                    refreshEditionService: null,
                    mediaFileService: null,
                    historyService: null,
                    eventAggregator: null,
                    checkIfBookShouldBeRefreshed: null,
                    editionSelector: null,
                    editionMetadataProfileFilter: null,
                    mediaCoverService: null,
                    logger)
            {
            }

            public void DeleteEntityPublic(Book local, bool deleteFiles)
            {
                DeleteEntity(local, deleteFiles);
            }
        }

        [Test]
        public void sort_children_should_not_match_same_local_child_twice()
        {
            var local1 = new Book
            {
                Id = 1,
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:1",
                HardcoverBookId = "hc:456",
                Title = "Local 1"
            };

            var local2 = new Book
            {
                Id = 2,
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:1",
                HardcoverBookId = "hc:789",
                Title = "Local 2"
            };

            var remote1 = new Book
            {
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:1",
                HardcoverBookId = "hc:456",
                Title = "Remote 1"
            };

            var remote2 = new Book
            {
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:1",
                HardcoverBookId = "hc:789",
                Title = "Remote 2"
            };

            var service = new TestRefreshService(new List<Book> { local1, local2 }, LogManager.GetCurrentClassLogger());

            var sorted = service.SortChildrenPublic(new Author { Id = 1, Name = "Test" }, new List<Book> { remote1, remote2 });

            Assert.Multiple(() =>
            {
                var upToDateIds = sorted.UpToDate.Select(b => b.Id).ToList();

                Assert.That(upToDateIds, Is.EquivalentTo(new[] { 1, 2 }));
                Assert.That(upToDateIds.Distinct().Count(), Is.EqualTo(upToDateIds.Count));
                Assert.That(sorted.Deleted, Is.Empty);
                Assert.That(sorted.Merged, Is.Empty);
                Assert.That(sorted.Added, Is.Empty);
            });
        }

        [Test]
        public void missing_metadata_delete_should_not_delete_files()
        {
            var service = new TestRefreshService(new List<Book>(), LogManager.GetCurrentClassLogger())
            {
                RemoteDataForRefresh = new RefreshEntityServiceBase<Author, Book>.RemoteData(),
                ShouldDeleteResult = true
            };

            var result = service.RefreshEntityInfo(
                new Author { Id = 1, Name = "Missing" },
                new List<Author>(),
                remoteData: null,
                forceChildRefresh: false,
                forceUpdateFileTags: false,
                lastUpdate: null);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.False);
                Assert.That(service.Deleted, Is.True);
                Assert.That(service.DeletedWithFiles, Is.False);
            });
        }

        [Test]
        public void metadata_merge_delete_should_not_delete_files()
        {
            var service = new TestRefreshService(new List<Book>(), LogManager.GetCurrentClassLogger())
            {
                RemoteDataForRefresh = new RefreshEntityServiceBase<Author, Book>.RemoteData
                {
                    Entity = new Author { Id = 2, Name = "Remote", Books = new List<Book>() }
                },
                IsMergeResult = true,
                MergeTarget = new Author { Id = 3, Name = "Target", Books = new List<Book>() }
            };

            service.RefreshEntityInfo(
                new Author { Id = 1, Name = "Local", Books = new List<Book>() },
                new List<Author>(),
                remoteData: null,
                forceChildRefresh: false,
                forceUpdateFileTags: false,
                lastUpdate: null);

            Assert.Multiple(() =>
            {
                Assert.That(service.Deleted, Is.True);
                Assert.That(service.DeletedWithFiles, Is.False);
            });
        }

        [Test]
        public void refresh_book_delete_entity_should_honor_delete_files_argument()
        {
            var bookService = DispatchProxy.Create<IBookService, RecordingBookServiceProxy>();
            var recorder = (RecordingBookServiceProxy)(object)bookService;
            var service = new TestRefreshBookService(bookService, LogManager.GetCurrentClassLogger());

            service.DeleteEntityPublic(new Book { Id = 42, Title = "Safe Delete" }, deleteFiles: false);

            Assert.Multiple(() =>
            {
                Assert.That(recorder.DeletedBookId, Is.EqualTo(42));
                Assert.That(recorder.DeletedWithFiles, Is.False);
            });
        }
    }
}
