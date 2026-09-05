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
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
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

            public BookFileMovePlan GetOrganizeDestination(BookFile bookFile, Author author, bool moveToCanonicalAuthorFolder, RenameBatchContext renameBatchContext = null) => throw new NotImplementedException();
            public BookFile MoveBookFile(BookFile bookFile, Author author, BookFileMovePlan plan, RenameBatchContext renameBatchContext = null) => throw new NotImplementedException();
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
                LogManager.GetCurrentClassLogger(),
                null);

            Assert.DoesNotThrow(() => subject.UpgradeBookFile(replacement, localBook));

            Assert.Multiple(() =>
            {
                Assert.That(recycleBin.DeletedFiles, Is.Empty);
                Assert.That(mediaProxy.Deleted, Is.EqualTo(new[] { stale }));
                Assert.That(mover.Moved, Is.True);
            });
        }

        private class QualityProfileServiceProxy : DispatchProxy
        {
            public static QualityProfile Profile { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == "Get")
                {
                    return Profile;
                }

                throw new NotImplementedException($"Test proxy does not implement IQualityProfileService.{targetMethod?.Name}");
            }
        }

        private static QualityProfile AudiobookProfile()
        {
            return new QualityProfile
            {
                Id = 7,
                Name = "Audiobook",
                UpgradeAllowed = true,
                Cutoff = Quality.M4B.Id,
                Items = new List<QualityProfileQualityItem>
                {
                    new QualityProfileQualityItem { Quality = Quality.MP3, Allowed = true },
                    new QualityProfileQualityItem { Quality = Quality.M4B, Allowed = true }
                }
            };
        }

        private static (UpgradeMediaFileService subject, RecordingRecycleBinProvider bin, MediaFileServiceProxy media, StubBookFileMover mover) BuildGuardedSubject()
        {
            QualityProfileServiceProxy.Profile = AudiobookProfile();
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
                LogManager.GetCurrentClassLogger(),
                DispatchProxy.Create<NzbDrone.Core.Profiles.Qualities.IQualityProfileService, QualityProfileServiceProxy>());
            return (subject, recycleBin, mediaProxy, mover);
        }

        private static LocalBook BuildLocalBook(BookFile existing, string incomingPath)
        {
            var author = new Author { Id = 1, Path = "/books/Author", AudiobookQualityProfileId = 7 };
            var book = new Book
            {
                Id = 2,
                Author = author,
                MediaType = BookMediaType.Audiobook,
                BookFiles = new List<BookFile> { existing }
            };
            return new LocalBook { Author = author, Book = book, Path = incomingPath };
        }

        [Test]
        public void should_refuse_to_delete_existing_file_that_outranks_the_incoming_file()
        {
            var existingM4b = new BookFile
            {
                Id = 1,
                Path = "/books/Author/Book (2003)/Book.m4b",
                Quality = new QualityModel(Quality.M4B)
            };
            var incomingMp3 = new BookFile
            {
                Id = 2,
                Path = "/downloads/Book.mp3",
                Quality = new QualityModel(Quality.MP3)
            };
            var localBook = BuildLocalBook(existingM4b, incomingMp3.Path);
            var (subject, bin, media, mover) = BuildGuardedSubject();

            Assert.Throws<InvalidOperationException>(() => subject.UpgradeBookFile(incomingMp3, localBook));

            Assert.Multiple(() =>
            {
                Assert.That(bin.DeletedFiles, Is.Empty);
                Assert.That(media.Deleted, Is.Empty);
                Assert.That(mover.Moved, Is.False);
            });
        }

        [Test]
        public void should_still_replace_existing_file_when_incoming_file_is_an_upgrade()
        {
            var existingMp3 = new BookFile
            {
                Id = 1,
                Path = "/books/Author/Book (2003)/Book.mp3",
                Quality = new QualityModel(Quality.MP3)
            };
            var incomingM4b = new BookFile
            {
                Id = 2,
                Path = "/downloads/Book.m4b",
                Quality = new QualityModel(Quality.M4B)
            };
            var localBook = BuildLocalBook(existingMp3, incomingM4b.Path);
            var (subject, bin, media, mover) = BuildGuardedSubject();

            Assert.DoesNotThrow(() => subject.UpgradeBookFile(incomingM4b, localBook));

            Assert.Multiple(() =>
            {
                Assert.That(media.Deleted, Is.EqualTo(new[] { existingMp3 }));
                Assert.That(mover.Moved, Is.True);
            });
        }

    }
}
