using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.ImportLists.Exclusions;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    public class ImportListExclusionServiceFixture
    {
        private class RepositoryProxy : DispatchProxy
        {
            public List<ImportListExclusion> Existing { get; set; } = new List<ImportListExclusion>();
            public List<ImportListExclusion> Inserted { get; } = new List<ImportListExclusion>();
            public List<int> DeletedIds { get; } = new List<int>();
            public int InsertManyCalls { get; private set; }
            public int DeleteManyCalls { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IImportListExclusionRepository.All):
                        return Existing;

                    case nameof(IImportListExclusionRepository.InsertMany):
                        InsertManyCalls++;
                        Inserted.AddRange((IEnumerable<ImportListExclusion>)args[0]);
                        return null;

                    case nameof(IImportListExclusionRepository.DeleteMany):
                        DeleteManyCalls++;
                        DeletedIds.AddRange((IEnumerable<int>)args[0]);
                        return null;

                    default:
                        throw new NotImplementedException($"Test proxy does not implement IImportListExclusionRepository.{targetMethod?.Name}");
                }
            }
        }

        [Test]
        public void should_batch_author_exclusion_provider_ids_and_skip_existing_ids()
        {
            var repository = DispatchProxy.Create<IImportListExclusionRepository, RepositoryProxy>();
            var repoProxy = (RepositoryProxy)(object)repository;
            repoProxy.Existing = new List<ImportListExclusion>
            {
                new ImportListExclusion { Id = 1, ForeignId = "gr:2", Name = "Existing" }
            };
            var service = new ImportListExclusionService(repository, LogManager.GetCurrentClassLogger());

            var author = new Author
            {
                Name = "Test Author",
                HardcoverAuthorId = "hc:1",
                GoodreadsAuthorId = "gr:2"
            };

            service.HandleAsync(new AuthorDeletedEvent(author, deleteFiles: false, addImportListExclusion: true));

            Assert.That(repoProxy.InsertManyCalls, Is.EqualTo(1));
            Assert.That(repoProxy.Inserted.Select(e => e.ForeignId), Is.EqualTo(new[] { "hc:1" }));
            Assert.That(repoProxy.Inserted[0].Name, Is.EqualTo("Test Author"));
        }

        [Test]
        public void should_batch_book_exclusion_scope_replacement_when_applying_to_both_formats()
        {
            var repository = DispatchProxy.Create<IImportListExclusionRepository, RepositoryProxy>();
            var repoProxy = (RepositoryProxy)(object)repository;
            repoProxy.Existing = new List<ImportListExclusion>
            {
                new ImportListExclusion { Id = 10, ForeignId = "gr:123", Name = "Existing Audio", MediaType = BookMediaType.Audiobook },
                new ImportListExclusion { Id = 11, ForeignId = "gr:123", Name = "Existing Ebook", MediaType = BookMediaType.Ebook }
            };
            var service = new ImportListExclusionService(repository, LogManager.GetCurrentClassLogger());

            var book = new Book
            {
                Title = "Test Book",
                MediaType = BookMediaType.Audiobook,
                GoodreadsWorkId = "gr:123",
                Author = new Author { Name = "Test Author" }
            };

            service.HandleAsync(new BookDeletedEvent(book, deleteFiles: false, addImportListExclusion: true, applyToBothFormats: true));

            Assert.That(repoProxy.DeleteManyCalls, Is.EqualTo(1));
            Assert.That(repoProxy.DeletedIds, Is.EquivalentTo(new[] { 10, 11 }));
            Assert.That(repoProxy.InsertManyCalls, Is.EqualTo(1));
            Assert.That(repoProxy.Inserted, Has.Count.EqualTo(1));
            Assert.That(repoProxy.Inserted[0].ForeignId, Is.EqualTo("gr:123"));
            Assert.That(repoProxy.Inserted[0].MediaType, Is.Null);
        }
    }
}
