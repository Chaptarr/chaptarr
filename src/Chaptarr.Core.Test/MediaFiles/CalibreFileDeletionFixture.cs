using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class CalibreFileDeletionFixture
    {
        [Test]
        public void selected_formats_do_not_delete_other_calibre_formats([Values] bool bulk, [Values] bool knownId)
        {
            var directory = Directory.CreateTempSubdirectory("chaptarr-calibre-delete-").FullName;
            try
            {
                var formats = new List<string>();
                var calls = 0;
                var removedRows = 0;
                var root = new RootFolder { Path = directory, IsCalibreLibrary = true, CalibreSettings = new CalibreSettings() };
                var selected = new BookFile { Id = 1, Path = Path.Combine(directory, "Dune.epub"), CalibreId = knownId ? 42 : 0 };
                var other = Path.Combine(directory, "Dune.mobi");
                File.WriteAllText(selected.Path, "epub");
                File.WriteAllText(other, "mobi");
                var calibre = Proxy<ICalibreProxy>((m, a) =>
                {
                    Assert.That(m.Name, Is.EqualTo(nameof(ICalibreProxy.RemoveFormats)), "Never delete the whole Calibre book");
                    Assert.That(a[0], Is.EqualTo(42));
                    calls++;
                    formats.AddRange((IEnumerable<string>)a[1]);
                    return null;
                });
                var service = new MediaFileDeletionService(
                    Proxy<IDiskProvider>((m, a) => m.Name switch
                    {
                        nameof(IDiskProvider.FileExistsCanonical) => File.Exists((string)a[0]),
                        nameof(IDiskProvider.FolderExists) => Directory.Exists((string)a[0]),
                        nameof(IDiskProvider.FolderEmpty) => false,
                        nameof(IDiskProvider.GetDirectories) => new[] { Path.Combine(directory, "author") },
                        _ => throw new AssertionException(m.Name)
                    }),
                    Proxy<IRecycleBinProvider>((m, a) => throw new AssertionException("Never delete Calibre bytes directly")),
                    Proxy<IMediaFileService>((m, a) => { removedRows++; return null; }), null, Proxy<IConfigService>((m, a) => false),
                    Proxy<IEventAggregator>((m, a) => null),
                    Proxy<IRootFolderService>((m, a) => m.Name == nameof(IRootFolderService.All) ? new List<RootFolder> { root } : root),
                    calibre, LogManager.GetCurrentClassLogger(), null, null,
                    Proxy<IMetadataFileService>((m, a) => null), Proxy<IOtherExtraFileService>((m, a) => null));
                void Delete()
                {
                    if (bulk)
                    {
                        // Two tracked copies share one Calibre format; only one request owns it.
                        service.DeleteBookFiles(new List<Book> { new() { BookFiles = new List<BookFile>
                        {
                            selected, new() { Id = 2, Path = selected.Path, CalibreId = selected.CalibreId }
                        } } });
                    }
                    else service.DeleteTrackFile(selected);
                }

                if (knownId) Delete();
                else Assert.Catch(Delete);
                Assert.That(calls, Is.EqualTo(knownId ? 1 : 0));
                Assert.That(formats, Is.EqualTo(knownId ? new[] { "EPUB" } : Array.Empty<string>()));
                if (!knownId) Assert.That(removedRows, Is.Zero);
                Assert.That(File.ReadAllText(other), Is.EqualTo("mobi"));
            }
            finally { Directory.Delete(directory, true); }
        }
        [TestCase(false)]
        [TestCase(true)]
        public void all_calibre_groups_are_validated_before_deletion_and_owned_replicas_are_removed(bool invalidLastGroup)
        {
            var directory = Directory.CreateTempSubdirectory("chaptarr-calibre-batch-").FullName;
            try
            {
                var roots = new[] { "first", "second" }.Select(name => new RootFolder {
                    Path = Directory.CreateDirectory(Path.Combine(directory, name)).FullName,
                    IsCalibreLibrary = true, CalibreSettings = new CalibreSettings() }).ToList();
                var files = roots.Select((root, index) => new BookFile {
                    Id = index + 1, Path = Path.Combine(root.Path, "Dune.epub"),
                    CalibreId = invalidLastGroup && index == 1 ? 0 : 42 + index,
                    ReplicaPaths = new List<string> { Path.Combine(root.Path, "copy.epub") }
                }).ToList();
                foreach (var file in files)
                {
                    File.WriteAllText(file.Path, "managed by Calibre");
                    File.WriteAllText(file.ReplicaPaths.Single(), "managed copy");
                }
                var operations = new List<string>();
                var service = new MediaFileDeletionService(
                    Proxy<IDiskProvider>((m, a) => m.Name switch {
                        nameof(IDiskProvider.FileExistsCanonical) => File.Exists((string)a[0]),
                        nameof(IDiskProvider.FolderExists) => Directory.Exists((string)a[0]),
                        nameof(IDiskProvider.FolderEmpty) => false,
                        nameof(IDiskProvider.GetDirectories) => new[] { "author" },
                        _ => throw new AssertionException(m.Name)
                    }),
                    Proxy<IRecycleBinProvider>((m, a) => {
                        Assert.That(files.Any(file => file.Path == (string)a[0]), Is.False, "Calibre alone deletes its primary format");
                        operations.Add("replica"); File.Delete((string)a[0]); return null;
                    }), null, null, Proxy<IConfigService>((_, _) => false), null,
                    Proxy<IRootFolderService>((m, a) => m.Name == nameof(IRootFolderService.All) ? roots :
                        roots.Single(root => ((string)a[0]).StartsWith(root.Path + Path.DirectorySeparatorChar, StringComparison.Ordinal))),
                    Proxy<ICalibreProxy>((m, a) => {
                        Assert.That(m.Name, Is.EqualTo(nameof(ICalibreProxy.RemoveFormats)));
                        Assert.That((IEnumerable<string>)a[1], Is.EqualTo(new[] { "EPUB" }));
                        operations.Add("calibre"); return null;
                    }), LogManager.GetCurrentClassLogger(), null, null,
                    Proxy<IMetadataFileService>((_, _) => { operations.Add("sidecar"); return null; }),
                    Proxy<IOtherExtraFileService>((_, _) => { operations.Add("sidecar"); return null; }));
                void Delete() => service.DeleteBookFiles(new List<Book> { new() { BookFiles = files } });
                if (invalidLastGroup)
                {
                    Assert.Throws<InvalidOperationException>(Delete);
                    Assert.That(operations, Is.Empty, "A later invalid Calibre ID must not leave the earlier group or its sidecars deleted");
                }
                else
                {
                    Delete();
                    Assert.That(operations.Count(operation => operation == "calibre"), Is.EqualTo(2));
                    Assert.That(operations.Count(operation => operation == "replica"), Is.EqualTo(2));
                }
                Assert.That(files.All(file => File.Exists(file.Path)), Is.True);
                Assert.That(files.All(file => File.Exists(file.ReplicaPaths.Single())), Is.EqualTo(invalidLastGroup));
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
