using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class UpgradeMediaFileServiceFixture
    {
        private class DiskProviderProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    nameof(IDiskProvider.GetParentFolder) => Path.GetDirectoryName((string)args[0]),
                    nameof(IDiskProvider.FolderExists) => true,
                    nameof(IDiskProvider.FileExists) => true,
                    nameof(IDiskProvider.FileExistsCanonical) => false,
                    _ => throw new NotImplementedException($"Test proxy does not implement IDiskProvider.{targetMethod?.Name}")
                };
            }
        }

        private sealed class RecordingRecycleBinProvider : IRecycleBinProvider
        {
            public List<string> DeletedFiles { get; } = new();

            public void DeleteFile(string path, string subfolder = "") => DeletedFiles.Add(path);
            public void DeleteFolder(string path) => throw new NotImplementedException();
            public void Empty() => throw new NotImplementedException();
            public void Cleanup() => throw new NotImplementedException();
        }

        private class MediaFileServiceProxy : DispatchProxy
        {
            public List<BookFile> Deleted { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMediaFileService.Delete))
                {
                    Deleted.Add((BookFile)args[0]);
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement IMediaFileService.{targetMethod?.Name}");
            }
        }

        private sealed class StubBookFileMover : IMoveBookFiles
        {
            public bool Moved { get; private set; }

            public BookFile MoveBookFile(BookFile bookFile, LocalBook localBook)
            {
                Moved = true;
                return bookFile;
            }

            public BookFile MoveBookFile(BookFile bookFile, Author author, bool forceRename = false, RenameBatchContext renameBatchContext = null) => throw new NotImplementedException();
            public BookFile CopyBookFile(BookFile bookFile, LocalBook localBook) => throw new NotImplementedException();
            public string GetImportDestinationPath(BookFile bookFile, LocalBook localBook) => throw new NotImplementedException();
        }

        private class RootFolderServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IRootFolderService.GetBestRootFolder))
                {
                    return new RootFolder { Id = 1, Path = "/books" };
                }

                throw new NotImplementedException($"Test proxy does not implement IRootFolderService.{targetMethod?.Name}");
            }
        }

        private class NoOpProxy<T> : DispatchProxy
            where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.ReturnType == typeof(void)
                    ? null
                    : targetMethod?.ReturnType?.IsValueType == true
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
            }
        }

        private class ThrowingProxy<T> : DispatchProxy
            where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test should not call {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class RecordingDiskProviderProxy : DispatchProxy
        {
            public List<string> DeletedFiles { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDiskProvider.DeleteFile))
                {
                    DeletedFiles.Add((string)args[0]);
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement IDiskProvider.{targetMethod?.Name}");
            }
        }

        private class RecordingCalibreProxy : DispatchProxy
        {
            public BookFile ReturnedBookFile { get; } = new()
            {
                CalibreId = 42,
                Path = "/calibre/Imported.epub"
            };

            public List<BookFile> DeletedBooks { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(ICalibreProxy.AddAndConvert))
                {
                    return ReturnedBookFile;
                }

                if (targetMethod?.Name == nameof(ICalibreProxy.DeleteBook))
                {
                    DeletedBooks.Add((BookFile)args[0]);
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement ICalibreProxy.{targetMethod?.Name}");
            }
        }

        private class PartiallyCreatedCalibreProxy : DispatchProxy
        {
            public Exception ImportException { get; } = new InvalidOperationException("Calibre conversion failed.");
            public Exception RollbackException { get; } = new InvalidOperationException("Calibre rollback failed.");
            public List<BookFile> DeletedBooks { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(ICalibreProxy.AddAndConvert))
                {
                    ((BookFile)args[0]).CalibreId = 42;
                    throw ImportException;
                }

                if (targetMethod?.Name == nameof(ICalibreProxy.DeleteBook))
                {
                    DeletedBooks.Add((BookFile)args[0]);
                    throw RollbackException;
                }

                throw new NotImplementedException($"Test proxy does not implement ICalibreProxy.{targetMethod?.Name}");
            }
        }

        [Test]
        public void should_not_delete_a_loose_path_match_while_replacing_its_stale_row()
        {
            var stale = new BookFile
            {
                Id = 1,
                Path = "/books/Author/Philosopher’s Stone/Book.m4b"
            };
            var replacement = new BookFile
            {
                Id = 2,
                Path = "/downloads/Book.m4b"
            };
            var author = new Author { Id = 1, Path = "/books/Author" };
            var book = new Book
            {
                Id = 2,
                Author = author,
                BookFiles = new List<BookFile> { stale }
            };
            var localBook = new LocalBook
            {
                Author = author,
                Book = book,
                Path = replacement.Path
            };
            var recycleBin = new RecordingRecycleBinProvider();
            var mediaFileService = DispatchProxy.Create<IMediaFileService, MediaFileServiceProxy>();
            var mediaProxy = (MediaFileServiceProxy)(object)mediaFileService;
            var mover = new StubBookFileMover();
            var subject = new UpgradeMediaFileService(
                recycleBin,
                mediaFileService,
                DispatchProxy.Create<IMetadataTagService, NoOpProxy<IMetadataTagService>>(),
                mover,
                DispatchProxy.Create<IDiskProvider, DiskProviderProxy>(),
                DispatchProxy.Create<IRootFolderService, RootFolderServiceProxy>(),
                DispatchProxy.Create<ICalibreProxy, ThrowingProxy<ICalibreProxy>>(),
                LogManager.GetCurrentClassLogger());

            Assert.DoesNotThrow(() => subject.UpgradeBookFile(replacement, localBook));

            Assert.Multiple(() =>
            {
                Assert.That(recycleBin.DeletedFiles, Is.Empty);
                Assert.That(mediaProxy.Deleted, Is.EqualTo(new[] { stale }));
                Assert.That(mover.Moved, Is.True);
            });
        }

        [Test]
        public void should_delete_the_original_download_only_after_a_new_calibre_import_commits()
        {
            var diskProvider = DispatchProxy.Create<IDiskProvider, RecordingDiskProviderProxy>();
            var diskProxy = (RecordingDiskProviderProxy)(object)diskProvider;
            var calibre = DispatchProxy.Create<ICalibreProxy, RecordingCalibreProxy>();
            var calibreProxy = (RecordingCalibreProxy)(object)calibre;
            var subject = new UpgradeMediaFileService(
                DispatchProxy.Create<IRecycleBinProvider, NoOpProxy<IRecycleBinProvider>>(),
                DispatchProxy.Create<IMediaFileService, NoOpProxy<IMediaFileService>>(),
                DispatchProxy.Create<IMetadataTagService, NoOpProxy<IMetadataTagService>>(),
                DispatchProxy.Create<IMoveBookFiles, NoOpProxy<IMoveBookFiles>>(),
                diskProvider,
                DispatchProxy.Create<IRootFolderService, NoOpProxy<IRootFolderService>>(),
                calibre,
                LogManager.GetCurrentClassLogger());
            var bookFile = new BookFile { Path = "/downloads/Imported.epub" };
            var rootFolder = new RootFolder
            {
                IsCalibreLibrary = true,
                CalibreSettings = new()
            };

            var import = subject.PrepareCalibreImport(bookFile, rootFolder);
            subject.CompleteCalibreImport(import);

            Assert.Multiple(() =>
            {
                Assert.That(import.SourcePath, Is.EqualTo("/downloads/Imported.epub"));
                Assert.That(import.BookFile, Is.SameAs(calibreProxy.ReturnedBookFile));
                Assert.That(diskProxy.DeletedFiles, Is.EqualTo(new[] { "/downloads/Imported.epub" }));
            });
        }

        [Test]
        public void should_not_delete_the_source_after_a_copy_only_calibre_import_commits()
        {
            var diskProvider = DispatchProxy.Create<IDiskProvider, RecordingDiskProviderProxy>();
            var diskProxy = (RecordingDiskProviderProxy)(object)diskProvider;
            var calibre = DispatchProxy.Create<ICalibreProxy, RecordingCalibreProxy>();
            var subject = new UpgradeMediaFileService(
                DispatchProxy.Create<IRecycleBinProvider, NoOpProxy<IRecycleBinProvider>>(),
                DispatchProxy.Create<IMediaFileService, NoOpProxy<IMediaFileService>>(),
                DispatchProxy.Create<IMetadataTagService, NoOpProxy<IMetadataTagService>>(),
                DispatchProxy.Create<IMoveBookFiles, NoOpProxy<IMoveBookFiles>>(),
                diskProvider,
                DispatchProxy.Create<IRootFolderService, NoOpProxy<IRootFolderService>>(),
                calibre,
                LogManager.GetCurrentClassLogger());
            var rootFolder = new RootFolder { IsCalibreLibrary = true, CalibreSettings = new() };

            var import = subject.PrepareCalibreImport(new BookFile { Path = "/downloads/Imported.epub" }, rootFolder, copyOnly: true);
            subject.CompleteCalibreImport(import);

            Assert.That(diskProxy.DeletedFiles, Is.Empty);
        }

        [Test]
        public void should_delete_the_created_calibre_book_when_database_persistence_fails()
        {
            var calibre = DispatchProxy.Create<ICalibreProxy, RecordingCalibreProxy>();
            var calibreProxy = (RecordingCalibreProxy)(object)calibre;
            var subject = new UpgradeMediaFileService(
                DispatchProxy.Create<IRecycleBinProvider, NoOpProxy<IRecycleBinProvider>>(),
                DispatchProxy.Create<IMediaFileService, NoOpProxy<IMediaFileService>>(),
                DispatchProxy.Create<IMetadataTagService, NoOpProxy<IMetadataTagService>>(),
                DispatchProxy.Create<IMoveBookFiles, NoOpProxy<IMoveBookFiles>>(),
                DispatchProxy.Create<IDiskProvider, RecordingDiskProviderProxy>(),
                DispatchProxy.Create<IRootFolderService, NoOpProxy<IRootFolderService>>(),
                calibre,
                LogManager.GetCurrentClassLogger());
            var rootFolder = new RootFolder { IsCalibreLibrary = true, CalibreSettings = new() };

            var import = subject.PrepareCalibreImport(new BookFile { Path = "/downloads/Imported.epub" }, rootFolder);
            subject.RollbackCalibreImport(import);

            Assert.That(calibreProxy.DeletedBooks, Is.EqualTo(new[] { calibreProxy.ReturnedBookFile }));
        }

        [Test]
        public void should_preserve_both_failures_when_partial_calibre_import_rollback_fails()
        {
            var calibre = DispatchProxy.Create<ICalibreProxy, PartiallyCreatedCalibreProxy>();
            var calibreProxy = (PartiallyCreatedCalibreProxy)(object)calibre;
            var subject = new UpgradeMediaFileService(
                DispatchProxy.Create<IRecycleBinProvider, NoOpProxy<IRecycleBinProvider>>(),
                DispatchProxy.Create<IMediaFileService, NoOpProxy<IMediaFileService>>(),
                DispatchProxy.Create<IMetadataTagService, NoOpProxy<IMetadataTagService>>(),
                DispatchProxy.Create<IMoveBookFiles, NoOpProxy<IMoveBookFiles>>(),
                DispatchProxy.Create<IDiskProvider, RecordingDiskProviderProxy>(),
                DispatchProxy.Create<IRootFolderService, NoOpProxy<IRootFolderService>>(),
                calibre,
                LogManager.GetCurrentClassLogger());
            var bookFile = new BookFile { Path = "/downloads/Imported.epub" };
            var rootFolder = new RootFolder { IsCalibreLibrary = true, CalibreSettings = new() };

            var exception = Assert.Throws<AggregateException>(() => subject.PrepareCalibreImport(bookFile, rootFolder));

            Assert.Multiple(() =>
            {
                Assert.That(exception.InnerExceptions, Is.EqualTo(new[] { calibreProxy.ImportException, calibreProxy.RollbackException }));
                Assert.That(calibreProxy.DeletedBooks, Is.EqualTo(new[] { bookFile }));
                Assert.That(bookFile.CalibreId, Is.EqualTo(42));
            });
        }
    }
}
