using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Profiles.Metadata;

namespace Chaptarr.Core.Test.Profiles.Metadata
{
    [TestFixture]
    public class MetadataProfileServiceMissingProfileFixture
    {
        private sealed class StubMetadataProfileRepository : IMetadataProfileRepository
        {
            public bool Exists(int id) => false;

            public MetadataProfile Get(int id) => throw new AssertionException("Get() must not be called when profile does not exist");

            public MetadataProfile Insert(MetadataProfile model) => throw new AssertionException("Insert() should not be called in this test");
            public MetadataProfile Update(MetadataProfile model) => throw new AssertionException("Update() should not be called in this test");
            public MetadataProfile Upsert(MetadataProfile model) => throw new AssertionException("Upsert() should not be called in this test");
            public void SetFields(MetadataProfile model, params System.Linq.Expressions.Expression<System.Func<MetadataProfile, object>>[] properties) => throw new AssertionException("SetFields() should not be called in this test");
            public void Delete(MetadataProfile model) => throw new AssertionException("Delete() should not be called in this test");
            public void Delete(int id) => throw new AssertionException("Delete() should not be called in this test");
            public IEnumerable<MetadataProfile> All() => new List<MetadataProfile>();
            public int Count() => 0;
            public MetadataProfile Find(int id) => null;
            public IEnumerable<MetadataProfile> Get(IEnumerable<int> ids) => new List<MetadataProfile>();
            public void InsertMany(IList<MetadataProfile> model) => throw new AssertionException("InsertMany() should not be called in this test");
            public void InsertMany(IList<MetadataProfile> model, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new AssertionException("InsertMany() should not be called in this test");
            public void UpdateMany(IList<MetadataProfile> model) => throw new AssertionException("UpdateMany() should not be called in this test");
            public void SetFields(IList<MetadataProfile> models, params System.Linq.Expressions.Expression<System.Func<MetadataProfile, object>>[] properties) => throw new AssertionException("SetFields() should not be called in this test");
            public void DeleteMany(List<MetadataProfile> model) => throw new AssertionException("DeleteMany() should not be called in this test");
            public void DeleteMany(IEnumerable<int> ids) => throw new AssertionException("DeleteMany() should not be called in this test");
            public void Purge(bool vacuum = false) => throw new AssertionException("Purge() should not be called in this test");
            public bool HasItems() => false;
            public MetadataProfile Single() => throw new AssertionException("Single() should not be called in this test");
            public MetadataProfile SingleOrDefault() => null;
            public PagingSpec<MetadataProfile> GetPaged(PagingSpec<MetadataProfile> pagingSpec) => throw new AssertionException("GetPaged() should not be called in this test");
        }

        [Test]
        public void should_not_throw_when_metadata_profile_is_missing()
        {
            var author = new Author
            {
                Id = 163,
                Name = "Evan Currie",
                Books = new List<Book>
                {
                    new Book { Title = "Odyssey One", Editions = new List<Edition> { new Edition { Title = "Odyssey One" } } }
                }
            };

            var service = new MetadataProfileService(
                profileRepository: new StubMetadataProfileRepository(),
                authorService: null,
                bookService: null,
                editionService: null,
                mediaFileService: null,
                importListFactory: null,
                rootFolderService: null,
                termMatcherService: null,
                eventAggregator: null,
                logger: LogManager.GetCurrentClassLogger());

            Assert.DoesNotThrow(() => service.FilterBooks(author, profileId: 4));

            var result = service.FilterBooks(author, profileId: 4);
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].Title, Is.EqualTo("Odyssey One"));
        }
    }
}
