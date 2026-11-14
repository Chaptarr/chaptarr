using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Books
{
    public abstract class RefreshEntityServiceBase<TEntity, TChild>
        where TEntity : ModelBase
    {
        private readonly Logger _logger;
        protected RefreshEntityServiceBase(Logger logger)
        {
            _logger = logger;
        }

        public enum UpdateResult
        {
            None,
            Standard,
            UpdateTags
        }

        public class SortedChildren
        {
            public SortedChildren()
            {
                UpToDate = new List<TChild>();
                Added = new List<TChild>();
                Updated = new List<TChild>();
                Matched = new List<Tuple<TChild, TChild>>();
                Merged = new List<Tuple<TChild, TChild>>();
                Deleted = new List<TChild>();
            }

            public List<TChild> UpToDate { get; set; }
            public List<TChild> Added { get; set; }
            public List<TChild> Updated { get; set; }
            public List<Tuple<TChild, TChild>> Matched { get; set; }
            public List<Tuple<TChild, TChild>> Merged { get; set; }
            public List<TChild> Deleted { get; set; }

            public List<TChild> All => UpToDate.Concat(Added).Concat(Updated).Concat(Merged.Select(x => x.Item1)).Concat(Deleted).ToList();
            public List<TChild> Future => UpToDate.Concat(Added).Concat(Updated).ToList();
            public List<TChild> Old => Merged.Select(x => x.Item1).Concat(Deleted).ToList();
        }

        public class RemoteData
        {
            public TEntity Entity { get; set; }
            // Metadata is now integrated into Author model
        }

        public class RefreshContext
        {
        }

        protected virtual RefreshContext CreateContext() => new RefreshContext();

        protected virtual void LogProgress(TEntity local)
        {
        }

        protected abstract RemoteData GetRemoteData(TEntity local, List<TEntity> remote, Author data, RefreshContext context);

        protected virtual void EnsureNewParent(TEntity local, TEntity remote)
        {
        }

        protected abstract bool IsMerge(TEntity local, TEntity remote);

        protected virtual bool ShouldDelete(TEntity local)
        {
            return true;
        }

        protected abstract UpdateResult UpdateEntity(TEntity local, TEntity remote);

        protected virtual UpdateResult MoveEntity(TEntity local, TEntity remote)
        {
            return UpdateEntity(local, remote);
        }

        protected virtual UpdateResult MergeEntity(TEntity local, TEntity target, TEntity remote)
        {
            DeleteEntity(local, false);
            return UpdateResult.UpdateTags;
        }

        protected abstract TEntity GetEntityByForeignId(TEntity local);
        protected abstract void SaveEntity(TEntity local);
        protected abstract void DeleteEntity(TEntity local, bool deleteFiles);

        protected abstract List<TChild> GetRemoteChildren(TEntity local, TEntity remote, RefreshContext context);
        protected abstract List<TChild> GetLocalChildren(TEntity entity, List<TChild> remoteChildren, RefreshContext context);
        protected abstract Tuple<TChild, List<TChild>> GetMatchingExistingChildren(List<TChild> existingChildren, TChild remote, RefreshContext context);

        protected abstract void PrepareNewChild(TChild child, TEntity entity);
        protected abstract void PrepareExistingChild(TChild local, TChild remote, TEntity entity);

        protected virtual bool AreChildrenUpToDate(TChild local, TChild remote)
        {
            return local != null && local.Equals(remote);
        }

        /// <summary>
        /// Creates a new local child instance from a remote child.
        /// Remote DTOs must be treated as immutable in Chaptarr because a single remote entity can map to multiple local instances.
        /// </summary>
        protected virtual TChild CreateChildForAdd(TChild remoteChild, TEntity entity)
        {
            return remoteChild;
        }

        protected virtual void ProcessChildren(TEntity entity, SortedChildren children)
        {
        }

        protected abstract void AddChildren(List<TChild> children);
        protected abstract bool RefreshChildren(SortedChildren localChildren, List<TChild> remoteChildren, Author remoteData, bool forceChildRefresh, bool forceUpdateFileTags, DateTime? lastUpdate, RefreshContext context);

        protected virtual void PublishEntityUpdatedEvent(TEntity entity)
        {
        }

        protected virtual void PublishRefreshCompleteEvent(TEntity entity, Author remoteData)
        {
        }

        protected virtual void PublishChildrenUpdatedEvent(TEntity entity, List<TChild> newChildren, List<TChild> updateChildren, List<TChild> deleteChildren)
        {
        }

        public bool RefreshEntityInfo(TEntity local, List<TEntity> remoteItems, Author remoteData, bool forceChildRefresh, bool forceUpdateFileTags, DateTime? lastUpdate, Action<TEntity> saveEntity = null, Action<TEntity> entityDeleted = null, RefreshContext context = null)
        {
            context ??= CreateContext();
            var updated = false;

            LogProgress(local);

            var data = GetRemoteData(local, remoteItems, remoteData, context);
            var remote = data.Entity;

            if (remote == null)
            {
                if (ShouldDelete(local))
                {
                    _logger.Warn($"{typeof(TEntity).Name} {local} not found in metadata and is being deleted");
                    DeleteEntity(local, false);
                    entityDeleted?.Invoke(local);
                    return false;
                }
                else
                {
                    _logger.Error($"{typeof(TEntity).Name} {local} was not found, it may have been removed from Metadata sources.");
                    return false;
                }
            }

            // Author metadata is now integrated into Author model
            // No separate metadata update needed

            // Validate that the parent object exists (remote data might specify a different one)
            EnsureNewParent(local, remote);

            UpdateResult result;
            if (IsMerge(local, remote))
            {
                // get entity we're merging into
                var target = GetEntityByForeignId(remote);

                if (target == null || target.Id == local.Id)
                {
                    // target == null:     no existing entity with the remote's IDs → just move (update IDs in place)
                    // target.Id == local:  GetEntityByForeignId found the SAME entity via a shared provider ID →
                    //                      this is a provider-ID change, not a duplicate.  Move, don't self-merge.
                    _logger.Trace($"Moving {typeof(TEntity).Name} {local} to {remote}");
                    result = MoveEntity(local, remote);
                }
                else
                {
                    _logger.Trace($"Merging {typeof(TEntity).Name} {local} into {target}");
                    result = MergeEntity(local, target, remote);
                    entityDeleted?.Invoke(local);

                    // having merged local into target, do update for target using remote
                    local = target;
                }

                (saveEntity ?? SaveEntity)(local);
            }
            else
            {
                _logger.Trace($"Updating {typeof(TEntity).Name} {local}");
                result = UpdateEntity(local, remote);
            }

            updated |= result >= UpdateResult.Standard;
            forceUpdateFileTags |= result == UpdateResult.UpdateTags;

            _logger.Trace($"updated: {updated} forceUpdateFileTags: {forceUpdateFileTags}");

            var remoteChildren = GetRemoteChildren(local, remote, context);
            updated |= SortChildren(local, remoteChildren, remoteData, forceChildRefresh, forceUpdateFileTags, lastUpdate, context);

            // Do this last so entity only marked as refreshed if refresh of children completed successfully
            _logger.Trace($"Saving {typeof(TEntity).Name} {local}");
            (saveEntity ?? SaveEntity)(local);

            if (updated)
            {
                PublishEntityUpdatedEvent(local);
            }

            PublishRefreshCompleteEvent(local, remoteData);

            _logger.Debug($"Finished {typeof(TEntity).Name} refresh for {local}");

            return updated;
        }

        public bool RefreshEntityInfo(List<TEntity> localList, List<TEntity> remoteItems, Author remoteData, bool forceChildRefresh, bool forceUpdateFileTags)
        {
            var updated = false;
            foreach (var entity in localList)
            {
                updated |= RefreshEntityInfo(entity, remoteItems, remoteData, forceChildRefresh, forceUpdateFileTags, null);
            }

            return updated;
        }


        protected bool SortChildren(TEntity entity, List<TChild> remoteChildren, Author remoteData, bool forceChildRefresh, bool forceUpdateFileTags, DateTime? lastUpdate, RefreshContext context = null)
        {
            context ??= CreateContext();
            var sortedChildren = ClassifyChildren(entity, remoteChildren, context);

            ProcessChildren(entity, sortedChildren);

            // Add in the new children (we have checked that foreign IDs don't clash)
            AddChildren(sortedChildren.Added);

            var completed = false;
            try
            {
                PrepareChildrenForRefresh(entity, sortedChildren, remoteChildren, context);
                var updated = RefreshChildren(sortedChildren, remoteChildren, remoteData, forceChildRefresh, forceUpdateFileTags, lastUpdate, context);
                completed = true;
                ValidateChildrenRefresh(entity, context);
                return updated;
            }
            finally
            {
                // AddChildren has committed these rows even when a later child fails. Publish
                // their event; completion/ETag acknowledgement still requires full success.
                PublishChildrenUpdatedEvent(entity, sortedChildren.Added,
                    completed ? sortedChildren.Updated : new List<TChild>(),
                    completed ? sortedChildren.Deleted : new List<TChild>());
            }
        }
        protected virtual void PrepareChildrenForRefresh(TEntity entity, SortedChildren children, List<TChild> remote, RefreshContext context)
        {
        }

        protected virtual void ValidateChildrenRefresh(TEntity entity, RefreshContext context)
        {
        }

        protected SortedChildren ClassifyChildren(TEntity entity, List<TChild> remoteChildren, RefreshContext context)
        {
            var localChildren = GetLocalChildren(entity, remoteChildren, context);

            var sortedChildren = new SortedChildren();
            sortedChildren.Deleted.AddRange(localChildren);

            foreach (var remoteChild in remoteChildren)
            {
                var tuple = GetMatchingExistingChildren(localChildren, remoteChild, context);
                var existingChild = tuple.Item1;
                var mergedChildren = tuple.Item2;

                if (existingChild != null)
                {
                    sortedChildren.Deleted.Remove(existingChild);
                    // Consume the matched local child so it cannot be matched again by a later remote child.
                    // This prevents the same DB row from ending up in multiple buckets (Updated/Merged/Deleted)
                    // during a single refresh pass.
                    localChildren.Remove(existingChild);

                    sortedChildren.Matched.Add(Tuple.Create(existingChild, remoteChild));
                    PrepareExistingChild(existingChild, remoteChild, entity);

                    if (AreChildrenUpToDate(existingChild, remoteChild))
                    {
                        sortedChildren.UpToDate.Add(existingChild);
                    }
                    else
                    {
                        sortedChildren.Updated.Add(existingChild);
                    }

                    // note the children that are going to be merged into existingChild
                    foreach (var child in mergedChildren)
                    {
                        sortedChildren.Merged.Add(Tuple.Create(child, existingChild));
                        sortedChildren.Deleted.Remove(child);
                        localChildren.Remove(child);
                    }
                }
                else
                {
                    var newChild = CreateChildForAdd(remoteChild, entity);
                    PrepareNewChild(newChild, entity);
                    sortedChildren.Added.Add(newChild);

                    // note the children that will be merged into remoteChild (once added)
                    foreach (var child in mergedChildren)
                    {
                        sortedChildren.Merged.Add(Tuple.Create(child, newChild));
                        sortedChildren.Deleted.Remove(child);
                        localChildren.Remove(child);
                    }
                }
            }

            if (typeof(TChild) != typeof(object))
            {
                _logger.Debug("{0} {1} {2}s up to date. Adding {3}, Updating {4}, Merging {5}, Deleting {6}.",
                              entity,
                              sortedChildren.UpToDate.Count,
                              typeof(TChild).Name.ToLower(),
                              sortedChildren.Added.Count,
                              sortedChildren.Updated.Count,
                              sortedChildren.Merged.Count,
                              sortedChildren.Deleted.Count);
            }

            return sortedChildren;
        }
    }
}
