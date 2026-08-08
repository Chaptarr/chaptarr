using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorSyncMetadataServiceFixture
    {
        private sealed class InMemoryAuthorSyncMetadataRepository : IAuthorSyncMetadataRepository
        {
            private readonly List<AuthorSyncMetadata> _items = new();
            private int _nextId = 1;

            public AuthorSyncMetadata FindByAuthorId(int authorId) =>
                _items.SingleOrDefault(x => x.AuthorId == authorId);

            public AuthorSyncMetadata FindByExternalAuthorId(string externalAuthorId) =>
                _items.SingleOrDefault(x => string.Equals(x.ExternalAuthorId, externalAuthorId, StringComparison.OrdinalIgnoreCase));

            public List<AuthorSyncMetadata> FindByAuthorIds(List<int> authorIds) =>
                authorIds == null ? new List<AuthorSyncMetadata>() : _items.Where(x => authorIds.Contains(x.AuthorId)).ToList();

            public List<AuthorSyncMetadata> GetDueForSync(int limit = 100) =>
                _items.Take(limit).ToList();

            public void BulkUpsert(List<AuthorSyncMetadata> syncMetadata)
            {
                if (syncMetadata == null)
                {
                    return;
                }

                foreach (var m in syncMetadata)
                {
                    if (m.Id == 0)
                    {
                        Insert(m);
                    }
                    else
                    {
                        Update(m);
                    }
                }
            }

            public IEnumerable<AuthorSyncMetadata> All() => _items.ToList();
            public int Count() => _items.Count;
            public AuthorSyncMetadata Find(int id) => _items.SingleOrDefault(x => x.Id == id);
            public AuthorSyncMetadata Get(int id) => Find(id);

            public AuthorSyncMetadata Insert(AuthorSyncMetadata model)
            {
                if (model == null)
                {
                    throw new ArgumentNullException(nameof(model));
                }

                EnforceUnique(model, ignoreId: 0);

                model.Id = _nextId++;
                _items.Add(Clone(model));
                return model;
            }

            public AuthorSyncMetadata Update(AuthorSyncMetadata model)
            {
                if (model == null)
                {
                    throw new ArgumentNullException(nameof(model));
                }

                var existing = _items.SingleOrDefault(x => x.Id == model.Id);
                if (existing == null)
                {
                    throw new InvalidOperationException($"No sync-metadata row exists with Id={model.Id}");
                }

                EnforceUnique(model, ignoreId: model.Id);

                CopyInto(existing, model);
                return model;
            }

            public AuthorSyncMetadata Upsert(AuthorSyncMetadata model)
            {
                return model.Id == 0 ? Insert(model) : Update(model);
            }

            public void SetFields(AuthorSyncMetadata model, params System.Linq.Expressions.Expression<Func<AuthorSyncMetadata, object>>[] properties)
            {
                throw new NotImplementedException();
            }

            public void Delete(AuthorSyncMetadata model)
            {
                if (model == null)
                {
                    return;
                }

                Delete(model.Id);
            }

            public void Delete(int id)
            {
                _items.RemoveAll(x => x.Id == id);
            }

            public IEnumerable<AuthorSyncMetadata> Get(IEnumerable<int> ids) => throw new NotImplementedException();
            public void InsertMany(IList<AuthorSyncMetadata> model) => throw new NotImplementedException();
            public void InsertMany(IList<AuthorSyncMetadata> model, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(IList<AuthorSyncMetadata> model) => throw new NotImplementedException();
            public void SetFields(IList<AuthorSyncMetadata> models, params System.Linq.Expressions.Expression<Func<AuthorSyncMetadata, object>>[] properties) => throw new NotImplementedException();
            public void DeleteMany(List<AuthorSyncMetadata> model) => throw new NotImplementedException();
            public void DeleteMany(IEnumerable<int> ids) => throw new NotImplementedException();
            public void Purge(bool vacuum = false) => throw new NotImplementedException();
            public bool HasItems() => _items.Any();
            public AuthorSyncMetadata Single() => throw new NotImplementedException();
            public AuthorSyncMetadata SingleOrDefault() => throw new NotImplementedException();
            public PagingSpec<AuthorSyncMetadata> GetPaged(PagingSpec<AuthorSyncMetadata> pagingSpec) => throw new NotImplementedException();

            private void EnforceUnique(AuthorSyncMetadata model, int ignoreId)
            {
                if (model.AuthorId <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(model.AuthorId));
                }

                if (string.IsNullOrWhiteSpace(model.ExternalAuthorId))
                {
                    throw new ArgumentException("ExternalAuthorId must be set", nameof(model.ExternalAuthorId));
                }

                if (_items.Any(x => x.Id != ignoreId && x.AuthorId == model.AuthorId))
                {
                    throw new InvalidOperationException("UNIQUE constraint failed: AuthorSyncMetadata.AuthorId");
                }

                if (_items.Any(x => x.Id != ignoreId && string.Equals(x.ExternalAuthorId, model.ExternalAuthorId, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("UNIQUE constraint failed: AuthorSyncMetadata.ExternalAuthorId");
                }
            }

            private static AuthorSyncMetadata Clone(AuthorSyncMetadata src)
            {
                var dst = new AuthorSyncMetadata();
                CopyInto(dst, src);
                return dst;
            }

            private static void CopyInto(AuthorSyncMetadata dst, AuthorSyncMetadata src)
            {
                dst.Id = src.Id;
                dst.AuthorId = src.AuthorId;
                dst.ExternalAuthorId = src.ExternalAuthorId;
                dst.ETag = src.ETag;
                dst.ServerVersion = src.ServerVersion;
                dst.LastSyncAttempt = src.LastSyncAttempt;
                dst.LastSuccessfulSync = src.LastSuccessfulSync;
                dst.LastSyncStatus = src.LastSyncStatus;
                dst.LastHttpStatus = src.LastHttpStatus;
                dst.SyncFailureCount = src.SyncFailureCount;
                dst.LastError = src.LastError;
                dst.LastSyncDurationMs = src.LastSyncDurationMs;
                dst.NextSyncNotBefore = src.NextSyncNotBefore;
            }
        }

        [Test]
        public void should_reassign_existing_external_id_row_instead_of_inserting_duplicate()
        {
            var repo = new InMemoryAuthorSyncMetadataRepository();
            var logger = LogManager.GetCurrentClassLogger();
            var sut = new AuthorSyncMetadataService(repo, logger);

            repo.Insert(new AuthorSyncMetadata
            {
                AuthorId = 1,
                ExternalAuthorId = "hc:123",
                ETag = "old"
            });

            var updated = sut.CreateOrUpdateSyncMetadata(authorId: 2, externalAuthorId: "hc:123", etag: "new");

            Assert.That(repo.Count(), Is.EqualTo(1));
            Assert.That(updated.AuthorId, Is.EqualTo(2));
            Assert.That(updated.ExternalAuthorId, Is.EqualTo("hc:123"));
            Assert.That(updated.ETag, Is.EqualTo("new"));
            Assert.That(repo.FindByAuthorId(1), Is.Null);
            Assert.That(repo.FindByAuthorId(2), Is.Not.Null);
        }

        [Test]
        public void should_delete_conflicting_external_id_row_when_updating_existing_author_row()
        {
            var repo = new InMemoryAuthorSyncMetadataRepository();
            var logger = LogManager.GetCurrentClassLogger();
            var sut = new AuthorSyncMetadataService(repo, logger);

            repo.Insert(new AuthorSyncMetadata
            {
                AuthorId = 1,
                ExternalAuthorId = "hc:123"
            });

            repo.Insert(new AuthorSyncMetadata
            {
                AuthorId = 2,
                ExternalAuthorId = "hc:456"
            });

            var updated = sut.CreateOrUpdateSyncMetadata(authorId: 2, externalAuthorId: "hc:123");

            Assert.That(repo.Count(), Is.EqualTo(1));
            Assert.That(updated.AuthorId, Is.EqualTo(2));
            Assert.That(updated.ExternalAuthorId, Is.EqualTo("hc:123"));
            Assert.That(repo.FindByAuthorId(1), Is.Null);
            Assert.That(repo.FindByAuthorId(2), Is.Not.Null);
        }
    }
}
