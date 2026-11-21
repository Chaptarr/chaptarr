using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Housekeeping.Housekeepers;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Releases;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tags;

namespace Chaptarr.Core.Test.Housekeeping
{
    [TestFixture]
    [NonParallelizable]
    public class SavedMediaTagUsageFixture
    {
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void manual_delete_should_refuse_saved_media_references_before_any_author_exists(bool postgres, bool root)
        {
            WithLibrary(postgres, library =>
            {
                if (root) library.SeedRoot();
                else library.SeedLists();
                var id = root ? 4 : 1;
                Assert.Multiple(() =>
                {
                    Assert.That(() => library.Service().Delete(id), Throws.TypeOf<ModelConflictException>());
                    Assert.That(library.Tags.All().Select(tag => tag.Id), Does.Contain(id));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void details_and_delete_should_recognize_saved_list_media_tags_without_authors(bool postgres)
        {
            WithLibrary(postgres, library =>
            {
                var lists = library.SeedLists();
                var service = library.Service();
                Assert.That(service.Details(1).ImportListIds, Is.EquivalentTo(lists.Select(list => list.Id)));
                Assert.That(service.Details(2).ImportListIds, Is.EquivalentTo(lists.Select(list => list.Id)));
                Assert.That(service.Details(3).ImportListIds, Is.EquivalentTo(lists.Select(list => list.Id)));
                var all = service.Details();
                Assert.That(all.Single(tag => tag.Id == 1).ImportListIds, Is.EquivalentTo(lists.Select(list => list.Id)));
                Assert.That(all.Single(tag => tag.Id == 2).ImportListIds, Is.EquivalentTo(lists.Select(list => list.Id)));
                Assert.That(() => service.Delete(1), Throws.TypeOf<ModelConflictException>());
                Assert.That(library.Tags.Get(1).Label, Is.EqualTo("tag-1"));
                foreach (var list in lists)
                {
                    ((DualMediaImportListSettings)list.Settings).AudiobookTags.Clear();
                    library.Lists.UpdateSettings(list);
                }
                Assert.That(service.Details(1).InUse, Is.False);
                service.Delete(1);
                Assert.That(library.Tags.All().Select(tag => tag.Id), Does.Not.Contain(1));
                Assert.That(service.Details(2).InUse, Is.True);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void details_and_delete_should_recognize_saved_root_media_tags(bool postgres)
        {
            WithLibrary(postgres, library =>
            {
                var root = library.SeedRoot();
                var service = library.Service();
                foreach (var id in new[] { 3, 4, 5 })
                {
                    Assert.That(service.Details(id).RootFolderIds, Is.EqualTo(new[] { root.Id }));
                    Assert.That(service.Details().Single(tag => tag.Id == id).RootFolderIds, Is.EqualTo(new[] { root.Id }));
                }
                Assert.That(library.RootService.AllForTag(4).Select(folder => folder.Id), Is.EqualTo(new[] { root.Id }));
                Assert.That(() => service.Delete(4), Throws.TypeOf<ModelConflictException>());
                root.SetAudiobookSettings(new MediaTypeSettings { Tags = new List<int>() });
                library.Roots.SetFields(root, folder => folder.AudiobookSettings);
                Assert.That(service.Details(4).InUse, Is.False);
                service.Delete(4);
                Assert.That(service.Details(5).InUse, Is.True);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void cleanup_should_preserve_list_media_only_tags_and_remove_unused_tags(bool postgres)
        {
            WithLibrary(postgres, library =>
            {
                library.SeedLists();
                library.Clean();
                Assert.That(library.Tags.All().Select(tag => tag.Id), Is.EquivalentTo(new[] { 1, 2, 3 }));
                foreach (var list in library.Lists.All())
                {
                    ((DualMediaImportListSettings)list.Settings).AudiobookTags.Clear();
                    library.Lists.UpdateSettings(list);
                }
                library.Clean();
                Assert.That(library.Tags.All().Select(tag => tag.Id), Is.EquivalentTo(new[] { 2, 3 }));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void cleanup_should_preserve_existing_root_media_tags_and_global_tags(bool postgres)
        {
            WithLibrary(postgres, library =>
            {
                library.SeedRoot();
                library.Clean();
                Assert.That(library.Tags.All().Select(tag => tag.Id), Is.EquivalentTo(new[] { 3, 4, 5 }));
            });
        }

        [TestCase(false, "{broken")]
        [TestCase(true, "{broken")]
        [TestCase(false, "{\"audiobookTags\":\"not-an-array\"}")]
        [TestCase(true, "{\"audiobookTags\":\"not-an-array\"}")]
        public void cleanup_should_fail_before_deleting_any_tags_when_saved_list_references_cannot_be_read(bool postgres, string settings)
        {
            WithLibrary(postgres, library =>
            {
                library.SeedLists();
                library.ReplaceListSettings(settings);
                Assert.Multiple(() =>
                {
                    Assert.That(() => library.Clean(), Throws.InstanceOf<Newtonsoft.Json.JsonException>());
                    Assert.That(library.Tags.All().Select(tag => tag.Id), Is.EquivalentTo(Enumerable.Range(1, 6)));
                });
            });
        }

        [TestCase(false, 1)]
        [TestCase(true, 1)]
        [TestCase(false, 100)]
        [TestCase(true, 100)]
        public void bulk_details_should_load_saved_owner_catalogs_once(bool postgres, int count)
        {
            WithLibrary(postgres, library =>
            {
                var lists = library.SeedLists(count);
                var root = library.SeedRoot();
                library.Readers.Clear();
                var details = library.Service().Details();
                Assert.That(details.Single(tag => tag.Id == 1).ImportListIds, Is.EquivalentTo(lists.Select(list => list.Id)));
                Assert.That(details.Single(tag => tag.Id == 4).RootFolderIds, Is.EqualTo(new[] { root.Id }));
                Assert.That(library.Readers.Count(sql => sql.Contains("\"ImportLists\"")), Is.EqualTo(1));
                Assert.That(library.Readers.Count(sql => sql.Contains("\"RootFolders\"")), Is.EqualTo(1));
                TestContext.Progress.WriteLine($"Saved media tag details {(postgres ? "PostgreSQL" : "SQLite")}: {count} lists, one list SELECT and one root SELECT for six tags.");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void unreferenced_tags_should_remain_deletable_with_null_or_empty_media_settings(bool postgres)
        {
            WithLibrary(postgres, library =>
            {
                library.Roots.Insert(new RootFolder { Path = @"C:\EmptyRoot".AsOsAgnostic(), AudiobookSettings = null, EbookSettings = "{}" });
                var service = library.Service();
                Assert.That(service.Details(6).InUse, Is.False);
                service.Delete(6);
                Assert.That(library.Tags.All().Select(tag => tag.Id), Does.Not.Contain(6));
                library.Clean();
                Assert.That(library.Tags.All(), Is.Empty);
            });
        }

        private sealed class Library
        {
            private readonly IMainDatabase _database;
            public List<string> Readers { get; } = new();
            public TagRepository Tags { get; }
            public ImportListRepository Lists { get; }
            public RootFolderRepository Roots { get; }
            public RootFolderService RootService { get; }

            public Library(Func<IDbConnection> open, DatabaseType type)
            {
                var db = InterfaceProxy.Proxy<IDatabase>((method, args) => method.Name switch
                {
                    "get_DatabaseType" => type,
                    nameof(IDatabase.OpenConnection) => CommandObserver.Wrap(open(), command => Readers.Add(command.CommandText)),
                    _ => throw new NotImplementedException(method.Name)
                });
                _database = new MainDatabase(db);
                var events = InterfaceProxy.Proxy<IEventAggregator>((_, _) => null);
                Tags = new TagRepository(_database, events);
                Lists = new ImportListRepository(_database, events);
                Roots = new RootFolderRepository(_database, events);
                RootService = new RootFolderService(Roots, null, null, null, LogManager.GetCurrentClassLogger());
                for (var id = 1; id <= 6; id++) Tags.Insert(new Tag { Label = "tag-" + id });
            }

            public List<ImportListDefinition> SeedLists(int? count = null)
            {
                var types = typeof(DualMediaImportListSettings).Assembly.GetTypes()
                    .Where(type => !type.IsAbstract && typeof(DualMediaImportListSettings).IsAssignableFrom(type)).OrderBy(type => type.Name).ToList();
                Assert.That(types, Has.Count.EqualTo(6));
                var lists = new List<ImportListDefinition>();
                for (var index = 0; index < (count ?? types.Count); index++)
                {
                    var settings = (DualMediaImportListSettings)Activator.CreateInstance(types[index % types.Count]);
                    settings.AudiobookTags = new List<int> { 1, 1 };
                    settings.EbookTags = new List<int> { 2 };
                    settings.MonitorAudiobooks = false;
                    settings.MonitorEbooks = true;
                    lists.Add(Lists.Insert(new ImportListDefinition { Name = "Saved list " + index, Implementation = "Saved", Settings = settings, Tags = new HashSet<int> { 3 }, RootFolderPath = string.Empty }));
                }
                Assert.That(Lists.All().All(list => list.Settings is DualMediaImportListSettings), Is.True);
                return lists;
            }

            public RootFolder SeedRoot()
            {
                var root = new RootFolder { Path = @"C:\SavedRoot".AsOsAgnostic(), DefaultTags = new HashSet<int> { 3 } };
                root.SetAudiobookSettings(new MediaTypeSettings { Tags = new List<int> { 4, 4 } });
                root.SetEbookSettings(new MediaTypeSettings { Tags = new List<int> { 5 } });
                return Roots.Insert(root);
            }

            public void Clean()
            {
                Readers.Clear();
                new CleanupUnusedTags(_database).Clean();
                Assert.That(Readers.Count(sql => sql.Contains("\"ImportLists\"")), Is.EqualTo(1));
            }

            public void ReplaceListSettings(string settings)
            {
                using var connection = _database.OpenConnection();
                connection.Execute("UPDATE \"ImportLists\" SET \"Settings\"=@Settings", new { Settings = settings });
            }

            public TagService Service() => new(Tags,
                InterfaceProxy.Proxy<IEventAggregator>((_, _) => null),
                Empty<IDelayProfileService>(),
                InterfaceProxy.Proxy<IImportListFactory>((method, args) => method.Name switch
                {
                    nameof(IImportListFactory.All) => Lists.All(),
                    nameof(IImportListFactory.AllForTag) => Lists.All().Where(list => list.Tags.Contains((int)args[0])).ToList(),
                    _ => throw new NotImplementedException(method.Name)
                }),
                Empty<INotificationFactory>(), Empty<IReleaseProfileService>(), Empty<IAuthorService>(),
                Empty<IIndexerFactory>(), RootService, Empty<IDownloadClientFactory>());
        }

        private static T Empty<T>() where T : class => InterfaceProxy.Proxy<T>((method, args) =>
            method.Name == nameof(IAuthorService.GetAllAuthorTags) ? new Dictionary<int, List<int>>() : Activator.CreateInstance(method.ReturnType));

        private static void WithLibrary(bool postgres, Action<Library> test)
        {
            DualDatabaseFixture.Run(postgres, database => test(new Library(database.OpenConnection, database.DatabaseType)));
        }
    }
}
