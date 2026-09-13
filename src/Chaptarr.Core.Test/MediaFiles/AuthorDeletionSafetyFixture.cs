using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Chaptarr.Api.V1.Author;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class AuthorDeletionSafetyFixture
    {
        public class CallbackProxy : DispatchProxy
        {
            public Func<MethodInfo, object[], object> Call { get; set; }
            protected override object Invoke(MethodInfo method, object[] args) => Call(method, args);
        }

        internal static T Proxy<T>(Func<MethodInfo, object[], object> call) where T : class
        {
            var proxy = DispatchProxy.Create<T, CallbackProxy>();
            ((CallbackProxy)(object)proxy).Call = call;
            return proxy;
        }

        private sealed class Events : IEventAggregator
        {
            public List<IEvent> Published { get; } = new();
            public void PublishEvent<T>(T message) where T : class, IEvent => Published.Add(message);
        }

        private sealed class Sandbox : IDisposable
        {
            public string Temp { get; } = Path.Combine(Path.GetTempPath(), "chaptarr-delete-" + Guid.NewGuid().ToString("N"));
            public List<Author> Authors { get; } = new();
            public List<RootFolder> Roots { get; } = new();
            public List<BookFile> Files { get; } = new();
            public List<MetadataFile> Extras { get; } = new();
            public List<string> DeletedFolders { get; } = new();
            public List<(List<BookFile> Files, CalibreSettings Settings)> CalibreCalls { get; } = new();
            public bool FailCalibre { get; set; }
            public bool FailExclusion { get; set; }
            public bool FailExclusionValidation { get; set; }
            public List<List<Author>> ExcludedAuthorBatches { get; } = new();
            public bool FailDisk { get; set; }
            public bool LeaveFolderBehind { get; set; }
            public string RecycleBin { get; set; } = string.Empty;
            public int FileQueries { get; private set; }
            public int RootQueries { get; private set; }
            public Events Events { get; } = new();
            public AuthorService Service { get; }

            public string P(string relative) => Path.Combine(Temp, relative.AsOsAgnostic());

            public Sandbox()
            {
                Directory.CreateDirectory(Temp);
                var roots = Proxy<IRootFolderService>((m, a) =>
                {
                    if (m.Name == nameof(IRootFolderService.All) ||
                        (m.Name == nameof(IRootFolderService.GetBestRootFolder) && a.Length == 1))
                    {
                        RootQueries++;
                    }

                    return m.Name switch
                    {
                        nameof(IRootFolderService.All) => Roots,
                        nameof(IRootFolderService.GetBestRootFolder) => (a.Length == 2 ? (List<RootFolder>)a[1] : Roots)
                            .Where(r => r.Path.PathEquals((string)a[0]) || r.Path.IsParentPath((string)a[0]))
                            .OrderByDescending(r => r.Path.Length).FirstOrDefault(),
                        _ => throw new NotImplementedException(m.Name)
                    };
                });
                var media = Proxy<IMediaFileService>((m, a) =>
                {
                    switch (m.Name)
                    {
                        case nameof(IMediaFileService.GetFilePathOwnership): return Files.ToList();
                        case nameof(IMediaFileService.GetFilesByAuthor):
                            FileQueries++;
                            return Files.Where(f => f.Author?.Id == (int)a[0]).ToList();
                        case nameof(IMediaFileService.DeleteRecordsForAuthorDeletion):
                            foreach (var file in (List<BookFile>)a[0]) Files.Remove(file);
                            return null;
                        default: throw new NotImplementedException(m.Name);
                    }
                });
                var repository = Proxy<IAuthorRepository>((m, a) =>
                {
                    switch (m.Name)
                    {
                        case nameof(IAuthorRepository.Get):
                            return Authors.Where(x => ((IEnumerable<int>)a[0]).Contains(x.Id)).ToList();
                        case nameof(IAuthorRepository.All): return Authors.ToList();
                        case nameof(IAuthorRepository.DeleteMany):
                            Authors.RemoveAll(x => ((IEnumerable<int>)a[0]).Contains(x.Id));
                            return null;
                        default: throw new NotImplementedException(m.Name);
                    }
                });
                var realDisk = new NzbDrone.Windows.Disk.DiskProvider();
                var disk = Proxy<IDiskProvider>((m, a) =>
                {
                    foreach (var path in a.OfType<string>())
                    {
                        if (!Temp.IsParentPath(path)) throw new InvalidOperationException("Sandbox boundary violation");
                    }

                    if (m.Name == nameof(IDiskProvider.DeleteFolder) || m.Name == nameof(IDiskProvider.DeleteFile))
                    {
                        if (FailDisk) throw new IOException("Synthetic disk failure");
                        if (m.Name == nameof(IDiskProvider.DeleteFolder) && LeaveFolderBehind) return null;
                        if (m.Name == nameof(IDiskProvider.DeleteFolder)) DeletedFolders.Add((string)a[0]);
                    }

                    try { return m.Invoke(realDisk, a); }
                    catch (TargetInvocationException ex) { throw ex.InnerException; }
                });
                var config = Proxy<IConfigService>((m, a) => m.Name switch
                {
                    "get_RecycleBin" => RecycleBin,
                    "get_DeleteEmptyFolders" => true,
                    _ => throw new NotImplementedException(m.Name)
                });
                var realTransfer = new DiskTransferService(disk, LogManager.GetCurrentClassLogger());
                var transfer = Proxy<IDiskTransferService>((method, args) =>
                {
                    if (LeaveFolderBehind && method.Name == nameof(IDiskTransferService.TransferFolder)) return TransferMode.Move;
                    try { return method.Invoke(realTransfer, args); }
                    catch (TargetInvocationException ex) { throw ex.InnerException; }
                });
                var recycle = new RecycleBinProvider(transfer, disk, config, LogManager.GetCurrentClassLogger());
                var calibre = Proxy<ICalibreProxy>((m, a) =>
                {
                    if (m.Name != nameof(ICalibreProxy.DeleteBooks)) throw new NotImplementedException(m.Name);
                    CalibreCalls.Add((((List<BookFile>)a[0]).ToList(), (CalibreSettings)a[1]));
                    if (FailCalibre) throw new IOException("Synthetic Calibre failure");
                    return null;
                });
                var extras = Proxy<IExtraFileRepository<MetadataFile>>((m, a) => m.Name switch
                {
                    nameof(IExtraFileRepository<MetadataFile>.All) => Extras.ToList(),
                    nameof(IExtraFileRepository<MetadataFile>.GetFilesByAuthor) => Extras.Where(e => e.AuthorId == (int)a[0]).ToList(),
                    _ => throw new NotImplementedException(m.Name)
                });
                var deletion = new MediaFileDeletionService(disk, recycle, media, repository, config, Events,
                    roots, calibre, LogManager.GetCurrentClassLogger(), extras);
                var exclusions = AuthorDeletionSafetyFixture.Proxy<IImportListExclusionService>((method, args) =>
                {
                    if (method.Name == nameof(IImportListExclusionService.ValidateAuthorExclusions))
                    {
                        if (FailExclusionValidation) throw new IOException("Synthetic exclusion validation failure");
                        Assert.That(Events.Published, Is.Empty);
                        Assert.That(DeletedFolders, Is.Empty);
                        return null;
                    }

                    if (method.Name == nameof(IImportListExclusionService.AddAuthorExclusions))
                    {
                        if (FailExclusion) throw new IOException("Synthetic exclusion persistence failure");
                        Assert.That(Events.Published.OfType<AuthorDeletedEvent>(), Is.Empty);
                        ExcludedAuthorBatches.Add(((IEnumerable<Author>)args[0]).ToList());
                        return null;
                    }
                    if (method.Name == nameof(IImportListExclusionService.All)) return new List<ImportListExclusion>();
                    throw new NotImplementedException(method.Name);
                });
                Service = new AuthorService(repository, Events, null, roots, null, new CacheManager(), null, media,
                    LogManager.GetCurrentClassLogger(), exclusions, deleteMediaFiles: new Lazy<IDeleteMediaFiles>(() => deletion));
            }

            public RootFolder Root(string relative, bool calibre = false)
            {
                var root = new RootFolder
                {
                    Id = Roots.Count + 1,
                    Path = P(relative),
                    IsCalibreLibrary = calibre,
                    CalibreSettings = calibre ? new CalibreSettings { Library = relative } : null
                };
                Roots.Add(root);
                Directory.CreateDirectory(root.Path);
                return root;
            }

            public Author Author(string legacy, string audio = null, string ebook = null)
            {
                var author = new Author
                {
                    Id = Authors.Count + 1,
                    Name = "Synthetic Author",
                    Path = P(legacy),
                    AudiobookPath = audio == null ? null : P(audio),
                    EbookPath = ebook == null ? null : P(ebook)
                };
                Authors.Add(author);
                return author;
            }

            public string Seed(string relative)
            {
                var path = P(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "disposable safety fixture");
                return path;
            }

            public BookFile Track(Author author, string relative, int calibreId = 0)
            {
                var file = new BookFile
                {
                    Id = Files.Count + 1,
                    Author = author,
                    Path = Seed(relative),
                    CalibreId = calibreId,
                    EditionId = 1,
                    Edition = new Edition { Id = 1, BookId = author.Id }
                };
                Files.Add(file);
                return file;
            }

            public void Dispose() => Directory.Delete(Temp, true);
        }

        [TestCase(1)]
        [TestCase(10)]
        [TestCase(1000)]
        public void author_deletion_uses_one_root_snapshot_for_all_files(int fileCount)
        {
            using var s = new Sandbox();
            s.Root("ebooks");
            var author = s.Author(@"ebooks\Jim Butcher");
            for (var i = 0; i < fileCount; i++)
            {
                s.Track(author, $@"ebooks\Jim Butcher\Grave Peril-{i}.epub");
            }

            s.Service.DeleteAuthor(author.Id, true);

            Assert.That(s.RootQueries, Is.EqualTo(1));
            Assert.That(s.Files, Is.Empty);
            Assert.That(Directory.Exists(author.Path), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void unresolved_calibre_identity_refuses_the_whole_delete_before_any_physical_work(bool mixed)
        {
            using var s = new Sandbox();
            s.Root("calibre", true);
            s.Root("audio");
            var author = s.Author(@"audio\Frank Herbert", @"audio\Frank Herbert", @"calibre\Frank Herbert");
            s.Track(author, @"audio\Frank Herbert\Dune.m4b");
            var unidentified = s.Track(author, @"calibre\Frank Herbert\Dune Messiah.epub");
            if (mixed) s.Track(author, @"calibre\Frank Herbert\Dune.epub", 7);
            var paths = s.Files.Select(file => file.Path).ToList();

            var error = Assert.Throws<NzbDroneClientException>(() => s.Service.DeleteAuthor(author.Id, true));

            Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(error.Message, Does.Contain(unidentified.Path).And.Contain("Calibre"));
            Assert.That(s.CalibreCalls, Is.Empty);
            Assert.That(s.DeletedFolders, Is.Empty);
            Assert.That(s.Authors, Has.Count.EqualTo(1));
            Assert.That(s.Files, Has.Count.EqualTo(paths.Count));
            Assert.That(paths.All(File.Exists), Is.True);
        }

        [TestCase("true", true)]
        [TestCase("false", false)]
        [TestCase(null, false)]
        public void json_controller_service_honors_checkbox_before_teardown(string flag, bool deleteFiles)
        {
            using var s = new Sandbox();
            s.Root("audio"); s.Root("ebooks");
            var author = s.Author(@"audio\A", @"audio\A", @"ebooks\A");
            var audio = s.Track(author, @"audio\A\Book.m4b").Path;
            var ebook = s.Track(author, @"ebooks\A\Book.epub").Path;
            var json = "{\"authorIds\":[1]" + (flag == null ? "" : ",\"deleteFiles\":" + flag) + "}";
            var resource = JsonSerializer.Deserialize<AuthorEditorResource>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            new AuthorEditorController(s.Service, null, null, null, null).DeleteAuthor(resource);
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(audio), Is.EqualTo(!deleteFiles));
                Assert.That(File.Exists(ebook), Is.EqualTo(!deleteFiles));
                Assert.That(s.Events.Published.OfType<AuthorDeletedEvent>().Single().DeleteFiles, Is.EqualTo(deleteFiles));
                Assert.That(s.Authors, Is.Empty);
                if (!deleteFiles) Assert.That(s.FileQueries, Is.Zero);
            });
        }

        [TestCase("true", true)]
        [TestCase("false", false)]
        [TestCase(null, false)]
        public void bulk_delete_forwards_only_the_requested_import_list_exclusion(string flag, bool expected)
        {
            using var s = new Sandbox();
            s.Root("media");
            var first = s.Author(@"media\A");
            var second = s.Author(@"media\B");
            first.GoodreadsAuthorId = "gr:author-1";
            second.GoodreadsAuthorId = "gr:author-2";
            var json = "{\"authorIds\":[1,2]" + (flag == null ? "" : ",\"addImportListExclusion\":" + flag) + "}";
            var resource = JsonSerializer.Deserialize<AuthorEditorResource>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            new AuthorEditorController(s.Service, null, null, null, null).DeleteAuthor(resource);
            var events = s.Events.Published.OfType<AuthorDeletedEvent>().ToList();
            Assert.That(events, Has.Count.EqualTo(2));
            Assert.That(events.All(e => e.AddImportListExclusion == expected && !e.DeleteFiles), Is.True);
            Assert.That(s.ExcludedAuthorBatches, Has.Count.EqualTo(expected ? 1 : 0));
            if (expected) Assert.That(s.ExcludedAuthorBatches.Single(), Is.EqualTo(new[] { first, second }));
        }

        [Test]
        public void successful_checked_delete_persists_exclusion_before_author_teardown()
        {
            using var s = new Sandbox(); s.Root("media");
            var author = s.Author(@"media\Frank Herbert", @"media\Frank Herbert");
            author.Name = "Frank Herbert";
            author.GoodreadsAuthorId = "gr:58";
            var file = s.Track(author, @"media\Frank Herbert\Dune.m4b");

            s.Service.DeleteAuthor(author.Id, deleteFiles: true, addImportListExclusion: true);

            Assert.That(File.Exists(file.Path), Is.False);
            Assert.That(s.Authors, Is.Empty);
            Assert.That(s.ExcludedAuthorBatches.Single(), Is.EqualTo(new[] { author }));
            Assert.That(s.Events.Published.OfType<AuthorDeletedEvent>().Count(), Is.EqualTo(1));
        }

        [Test]
        public void exclusion_persistence_failure_after_physical_delete_keeps_database_author_and_skips_teardown()
        {
            using var s = new Sandbox(); s.Root("media");
            var author = s.Author(@"media\Frank Herbert", @"media\Frank Herbert");
            author.Name = "Frank Herbert";
            author.GoodreadsAuthorId = "gr:58";
            var file = s.Track(author, @"media\Frank Herbert\Dune.m4b");
            s.FailExclusion = true;

            Assert.Throws<IOException>(() => s.Service.DeleteAuthor(author.Id, deleteFiles: true, addImportListExclusion: true));

            Assert.That(File.Exists(file.Path), Is.False);
            Assert.That(s.Authors, Does.Contain(author));
            Assert.That(s.Events.Published.OfType<AuthorDeletedEvent>(), Is.Empty);
        }

        [Test]
        public void exclusion_identity_validation_failure_precedes_physical_delete()
        {
            using var s = new Sandbox(); s.Root("media");
            var author = s.Author(@"media\Frank Herbert", @"media\Frank Herbert");
            var file = s.Track(author, @"media\Frank Herbert\Dune.m4b");
            s.FailExclusionValidation = true;

            Assert.Throws<IOException>(() => s.Service.DeleteAuthor(author.Id, deleteFiles: true, addImportListExclusion: true));

            Assert.That(File.Exists(file.Path), Is.True);
            Assert.That(s.Authors, Does.Contain(author));
            Assert.That(s.Events.Published.OfType<AuthorDeletedEvent>(), Is.Empty);
        }

        [TestCase("audio", false)]
        [TestCase("ebooks", false)]
        [TestCase("audio", true)]
        [TestCase("ebooks", true)]
        public void empty_root_refuses_deletion_before_removing_any_files_or_inventory(string media, bool standalone)
        {
            using var s = new Sandbox();
            s.Root("available");
            var missing = s.Root(media);
            var author = s.Author(@"available\A", @"available\A", standalone ? null : media + @"\A");
            var kept = s.Track(author, @"available\A\Keep.m4b");
            s.Track(author, media + @"\A\Offline.epub");
            var offline = s.P("offline");
            Directory.Move(missing.Path, offline);
            Directory.CreateDirectory(missing.Path);
            var error = Assert.Throws<NzbDroneClientException>(() => s.Service.DeleteAuthor(author.Id, true, addImportListExclusion: true));
            Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(error.Message, Does.Contain(missing.Path).And.Contain("empty"));
            Assert.That(File.Exists(kept.Path), Is.True);
            Assert.That(s.Authors, Has.Count.EqualTo(1));
            Assert.That(s.Files, Has.Count.EqualTo(2));
            Assert.That(s.DeletedFolders, Is.Empty);
            Assert.That(s.ExcludedAuthorBatches, Is.Empty);
            Assert.That(Directory.GetFiles(offline, "*", SearchOption.AllDirectories), Has.Length.EqualTo(1));
        }

        [Test]
        public void populated_flat_root_is_not_mistaken_for_an_unavailable_share()
        {
            using var s = new Sandbox();
            var root = s.Root("ebooks");
            var author = s.Author(@"ebooks\A");
            var file = s.Track(author, @"ebooks\Book.epub");
            Assert.That(Directory.GetDirectories(root.Path), Is.Empty);
            s.Service.DeleteAuthor(author.Id, true);
            Assert.That(File.Exists(file.Path), Is.False);
            Assert.That(s.Authors, Is.Empty);
            Assert.That(s.Files, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void explicit_folder_delete_includes_untracked_contents_once(bool nested)
        {
            using var s = new Sandbox(); s.Root("media");
            var author = s.Author(@"media\A", @"media\A", nested ? @"media\A\ebooks" : @"media\A");
            var audio = s.Seed(@"media\A\Book.m4b");
            var ebook = s.Seed(@"media\A\ebooks\Book.epub");
            var other = s.Seed(@"media\A\.user-notes");
            s.Service.DeleteAuthor(author.Id, true);
            Assert.That(File.Exists(audio) || File.Exists(ebook) || File.Exists(other), Is.False);
            Assert.That(s.DeletedFolders, Has.Count.EqualTo(1));
        }

        [TestCase("legacy", false)]
        [TestCase("legacy", true)]
        [TestCase("ebook", false)]
        [TestCase("ebook", true)]
        [TestCase("audio", false)]
        [TestCase("audio", true)]
        public void retained_author_paths_refuse_the_whole_delete_before_touching_contents(string field, bool nested)
        {
            using var s = new Sandbox(); s.Root("media"); s.Root("other");
            var selected = s.Author(@"media\A");
            var shared = nested ? @"media\A\B" : @"media\A";
            s.Author(field == "legacy" ? shared : @"other\B", field == "audio" ? shared : null, field == "ebook" ? shared : null);
            var keep = s.Seed(shared + @"\Keep.epub");
            var remove = s.Track(selected, @"media\A\Remove.m4b").Path;
            var error = Assert.Throws<NzbDroneClientException>(() => s.Service.DeleteAuthor(selected.Id, true));
            Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(error.Message, Does.Contain(s.P(shared)));
            Assert.That(File.Exists(keep), Is.True);
            Assert.That(File.Exists(remove), Is.True);
            Assert.That(s.Authors, Does.Contain(selected));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void retained_physical_ownership_protects_out_of_base_files_and_replicas(bool replica)
        {
            using var s = new Sandbox(); s.Root("media"); s.Root("other");
            var selected = s.Author(@"media\A");
            var retained = s.Author(@"other\B");
            var keep = s.Seed(@"media\A\Keep.epub");
            var file = s.Track(retained, replica ? @"other\B\Primary.epub" : @"media\A\Keep.epub");
            if (replica) file.ReplicaPaths = new List<string> { keep };
            var remove = s.Track(selected, @"media\A\Remove.m4b").Path;
            var error = Assert.Throws<NzbDroneClientException>(() => s.Service.DeleteAuthor(selected.Id, true));
            Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(error.Message, Does.Contain(keep));
            Assert.That(File.Exists(keep), Is.True);
            Assert.That(File.Exists(remove), Is.True);
            Assert.That(s.Authors, Does.Contain(selected));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void checked_folder_delete_removes_unmapped_contents_and_records(bool nested, bool bulk)
        {
            using var s = new Sandbox(); s.Root("media");
            var author = s.Author(@"media\A");
            var remove = s.Seed(nested ? @"media\A\Book\Unmapped.epub" : @"media\A\Unmapped.epub");
            var sidecar = s.Seed(@"media\A\.notes");
            var keep = s.Seed(@"media\AB\Unmapped.epub");
            s.Files.Add(new BookFile { Id = 1, Path = remove });
            s.Files.Add(new BookFile { Id = 2, Path = keep });
            if (bulk) s.Service.DeleteAuthors(new List<int> { author.Id }, true);
            else s.Service.DeleteAuthor(author.Id, true);
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(remove), Is.False);
                Assert.That(File.Exists(sidecar), Is.False);
                Assert.That(Directory.Exists(author.Path), Is.False);
                Assert.That(File.Exists(keep), Is.True);
                Assert.That(s.Files.Select(file => file.Id), Is.EqualTo(new[] { 2 }));
                Assert.That(s.Authors, Is.Empty);
                Assert.That(s.DeletedFolders, Is.EqualTo(new[] { author.Path }));
            });
        }

        [Test]
        public void unchecked_folder_delete_keeps_unmapped_files_and_records()
        {
            using var s = new Sandbox(); s.Root("media");
            var author = s.Author(@"media\A");
            var file = new BookFile { Id = 1, Path = s.Seed(@"media\A\Unmapped.epub") };
            s.Files.Add(file);
            s.Service.DeleteAuthor(author.Id, false);
            Assert.That(File.Exists(file.Path), Is.True);
            Assert.That(s.Files, Does.Contain(file));
            Assert.That(s.DeletedFolders, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void shared_folder_sidecar_is_deleted_only_when_no_remaining_media_needs_it(bool remaining)
        {
            using var s = new Sandbox(); s.Root("media");
            var author = s.Author(@"media\A");
            s.Author(@"media\Shared");
            s.Track(author, @"media\Shared\Title\Book.epub");
            var cover = s.Seed(@"media\Shared\Title\cover.jpg");
            if (remaining) s.Seed(@"media\Shared\Title\Untracked.M4B");
            s.Extras.Add(new MetadataFile { Id = 1, AuthorId = author.Id, BookId = author.Id, RelativePath = @"..\Shared\Title\cover.jpg".AsOsAgnostic() });
            s.Service.DeleteAuthor(author.Id, true);
            Assert.That(File.Exists(cover), Is.EqualTo(remaining));
        }

        [Test]
        public void retained_sidecar_outside_its_authors_base_prevents_folder_deletion()
        {
            using var s = new Sandbox(); s.Root("media");
            var selected = s.Author(@"media\A");
            var retained = s.Author(@"media\B");
            var keep = s.Seed(@"media\A\cover.jpg");
            s.Extras.Add(new MetadataFile { Id = 1, AuthorId = retained.Id, RelativePath = @"..\A\cover.jpg".AsOsAgnostic() });
            var error = Assert.Throws<NzbDroneClientException>(() => s.Service.DeleteAuthor(selected.Id, true));
            Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(error.Message, Does.Contain(keep));
            Assert.That(File.Exists(keep), Is.True);
            Assert.That(s.Authors, Does.Contain(selected));
        }

        [Test]
        public void complete_bulk_selection_can_remove_its_shared_folder()
        {
            using var s = new Sandbox(); s.Root("media");
            s.Author(@"media\Shared"); s.Author(@"media\Shared");
            var path = s.Seed(@"media\Shared\Book.epub");
            s.Service.DeleteAuthors(new List<int> { 1, 2 }, true);
            Assert.That(File.Exists(path), Is.False);
            Assert.That(s.DeletedFolders, Has.Count.EqualTo(1));
            Assert.That(s.Authors, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void configured_root_and_its_parent_are_never_author_folder_targets(bool parent)
        {
            using var s = new Sandbox(); s.Root(@"media\root");
            var author = s.Author(parent ? "media" : @"media\root");
            var keep = s.Seed(@"media\root\Untracked.epub");
            var error = Assert.Throws<NzbDroneClientException>(() => s.Service.DeleteAuthor(author.Id, true));
            Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(File.Exists(keep), Is.True);
            Assert.That(s.Authors, Does.Contain(author));
        }

        [Test]
        public void author_folder_below_a_directory_link_cannot_delete_the_external_target()
        {
            using var s = new Sandbox();
            s.Root("media");
            var keep = s.Seed(@"outside\Author\Keep.txt");
            var link = s.P(@"media\linked");
            try { Directory.CreateSymbolicLink(link, s.P("outside")); }
            catch (UnauthorizedAccessException) { Assert.Ignore("Directory-link creation requires permission on this platform"); }
            try
            {
                var author = s.Author(@"media\linked\Author");
                var file = s.Track(author, @"media\linked\Author\Book.epub");
                Assert.That(() => s.Service.DeleteAuthor(author.Id, true), Throws.TypeOf<NzbDroneClientException>());
                Assert.Multiple(() =>
                {
                    Assert.That(File.Exists(keep), Is.True);
                    Assert.That(File.Exists(file.Path), Is.True);
                    Assert.That(s.Authors, Does.Contain(author));
                });
            }
            finally { if (Directory.Exists(link)) Directory.Delete(link); }
        }

        [Test]
        public void tracked_out_of_base_file_and_replica_are_deleted_without_sweeping_their_folder()
        {
            using var s = new Sandbox(); s.Root("audio"); s.Root("ebooks");
            var author = s.Author(@"audio\A");
            Directory.CreateDirectory(author.Path);
            var file = s.Track(author, @"ebooks\Title First\Book.epub");
            var replica = s.Seed(@"ebooks\Copy\Book.epub");
            file.ReplicaPaths = new List<string> { replica };
            var keep = s.Seed(@"ebooks\Title First\notes.txt");
            s.Service.DeleteAuthor(author.Id, true);
            Assert.That(File.Exists(file.Path) || File.Exists(replica), Is.False);
            Assert.That(File.Exists(keep), Is.True);
        }

        [Test]
        public void calibre_ids_are_partitioned_by_actual_library_and_regular_audio_is_deleted()
        {
            using var s = new Sandbox(); s.Root("audio");
            var one = s.Root("first", true); var two = s.Root("second", true);
            var author = s.Author(@"first\A", @"audio\A", @"second\A");
            var audio = s.Track(author, @"audio\A\Book.m4b");
            var first = s.Track(author, @"first\A\One.epub", 7);
            var second = s.Track(author, @"second\A\Two.epub", 7);
            s.Service.DeleteAuthor(author.Id, true);
            Assert.That(s.CalibreCalls, Has.Count.EqualTo(2));
            Assert.That(s.CalibreCalls.Single(c => c.Settings == one.CalibreSettings).Files, Is.EqualTo(new[] { first }));
            Assert.That(s.CalibreCalls.Single(c => c.Settings == two.CalibreSettings).Files, Is.EqualTo(new[] { second }));
            Assert.That(File.Exists(audio.Path), Is.False);
            Assert.That(File.Exists(first.Path) && File.Exists(second.Path), Is.True, "Calibre owns physical removal, not the filesystem deleter");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void physical_failure_does_not_teardown_author_or_dependent_inventory(bool calibre)
        {
            using var s = new Sandbox(); s.Root("media", calibre);
            var author = s.Author(@"media\A");
            var file = s.Track(author, @"media\A\Book.epub", calibre ? 7 : 0);
            s.FailCalibre = calibre; s.FailDisk = !calibre;
            Assert.Throws<IOException>(() => s.Service.DeleteAuthor(author.Id, true));
            Assert.That(s.Authors, Does.Contain(author));
            Assert.That(s.Files, Does.Contain(file));
            Assert.That(s.Events.Published.OfType<AuthorDeletedEvent>(), Is.Empty);
            Assert.That(File.Exists(file.Path), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void invalid_later_target_is_refused_before_any_physical_deletion(bool missingCalibreSettings)
        {
            using var s = new Sandbox(); s.Root("media");
            var first = s.Author(@"media\A");
            var second = s.Author(@"media\B");
            var file = s.Track(first, @"media\OutsideAuthorBase\Good.epub");
            if (missingCalibreSettings)
            {
                s.Root("calibre", true).CalibreSettings = null;
                s.Track(second, @"calibre\B\Book.epub", 1);
            }
            else
            {
                s.Track(second, @"unconfigured\Book.epub");
            }

            var error = Assert.Throws<NzbDroneClientException>(() => s.Service.DeleteAuthors(new List<int> { first.Id, second.Id }, true));
            Assert.That(error.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(File.Exists(file.Path), Is.True);
            Assert.That(s.Authors, Has.Count.EqualTo(2));
            Assert.That(s.Files, Has.Count.EqualTo(2));
            Assert.That(s.CalibreCalls, Is.Empty);
            Assert.That(s.Events.Published, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void incomplete_folder_deletion_does_not_report_success_or_delete_inventory(bool recycling)
        {
            using var s = new Sandbox(); s.Root("media");
            if (recycling) s.RecycleBin = s.P("recycle");
            var author = s.Author(@"media\A");
            var file = new BookFile { Id = 1, Path = s.Seed(@"media\A\Unmapped.epub") };
            s.Files.Add(file);
            s.LeaveFolderBehind = true;
            Assert.Throws<IOException>(() => s.Service.DeleteAuthor(author.Id, true));
            Assert.That(File.Exists(file.Path), Is.True);
            Assert.That(s.Files, Does.Contain(file));
            Assert.That(s.Authors, Does.Contain(author));
            Assert.That(s.Events.Published, Is.Empty);
        }

        [Test]
        public void checked_delete_recycles_both_format_folders_with_all_contents_without_overwriting()
        {
            using var s = new Sandbox(); s.Root("audio"); s.Root("ebooks");
            s.RecycleBin = s.P("recycle");
            var existing = s.Seed(@"recycle\A\cover.jpg");
            var author = s.Author(@"audio\A", @"audio\A", @"ebooks\A");
            var audio = s.Seed(@"audio\A\Unmapped.m4b");
            var ebook = s.Seed(@"ebooks\A\Unmapped.epub");
            s.Seed(@"audio\A\.notes");
            s.Seed(@"ebooks\A\cover.jpg");
            s.Files.Add(new BookFile { Id = 1, Path = audio });
            s.Files.Add(new BookFile { Id = 2, Path = ebook });
            s.Service.DeleteAuthor(author.Id, true);
            Assert.Multiple(() =>
            {
                Assert.That(Directory.Exists(author.AudiobookPath), Is.False);
                Assert.That(Directory.Exists(author.EbookPath), Is.False);
                Assert.That(File.Exists(existing), Is.True);
                Assert.That(Directory.GetDirectories(s.RecycleBin), Has.Length.EqualTo(3));
                Assert.That(Directory.GetFiles(s.RecycleBin, "*", SearchOption.AllDirectories)
                    .Select(Path.GetFileName), Is.EquivalentTo(new[] { "cover.jpg", "cover.jpg", ".notes", "Unmapped.m4b", "Unmapped.epub" }));
                Assert.That(s.Files, Is.Empty);
            });
        }
    }
}
