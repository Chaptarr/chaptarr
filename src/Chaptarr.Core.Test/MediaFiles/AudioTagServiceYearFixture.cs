using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Reflection;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.TagExtraction;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class AudioTagServiceYearFixture
    {
        private class InterfaceProxy : DispatchProxy
        {
            public Func<MethodInfo, object[], object> Handler { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return Handler(targetMethod, args);
            }
        }

        private string _tempDirectory;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), $"audio-tag-year-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_tempDirectory, true);
        }

        public static IEnumerable<TestCaseData> TagCases()
        {
            foreach (var extension in new[] { ".mp3", ".m4b", ".flac" })
            {
                yield return new TestCaseData(extension, 1987u, 0, false, 1987u);
                yield return new TestCaseData(extension, 0u, 0, false, 0u);
                yield return new TestCaseData(extension, 1987u, 2024, false, 2024u);
                yield return new TestCaseData(extension, 0u, 2024, false, 2024u);
                yield return new TestCaseData(extension, 1987u, 0, true, 0u);
                yield return new TestCaseData(extension, 1987u, 2024, true, 2024u);
            }
        }

        [TestCaseSource(nameof(TagCases))]
        public void should_write_only_known_edition_years_and_preserve_explicit_scrubbing(
            string extension, uint existingYear, int editionYear, bool scrub, uint expectedYear)
        {
            var path = Path.Combine(_tempDirectory, "Fixture" + extension);
            if (extension == ".mp3")
            {
                File.WriteAllBytes(path, BuildMp3());
            }
            else
            {
                File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "Audio", "silence" + extension), path);
            }

            using (var audio = TagLib.File.Create(path))
            {
                audio.Tag.Title = "Old title";
                audio.Tag.Year = existingYear;
                audio.Save();
            }

            using (var audio = TagLib.File.Create(path))
            {
                Assert.That(audio.Tag.Year, Is.EqualTo(existingYear), "The existing year must be saved before retagging.");
            }

            var author = new Author { Id = 1, Name = "Fixture author" };
            var book = new Book
            {
                Id = 2,
                Title = "Fixture book",
                Author = author,
                MediaType = BookMediaType.Audiobook,
                ReleaseDate = new DateTime(2001, 1, 1),
                PublicationYear = 2001
            };
            var edition = new Edition
            {
                Id = 3,
                Book = book,
                Title = "Updated title",
                Publisher = "Fixture publisher",
                NarratorNames = new List<string> { "First narrator", "Second narrator" },
                ReleaseDate = editionYear == 0 ? null : new DateTime(editionYear, 1, 1)
            };
            var bookFile = new BookFile { Id = 4, Path = path, Author = author, Edition = edition };
            edition.BookFiles = new List<BookFile> { bookFile };
            BookFileRetaggedEvent retagged = null;

            var config = Proxy<IConfigService>((method, args) => method.Name switch
            {
                "get_WriteAudioTags" => WriteAudioTagsType.Sync,
                "get_ScrubAudioTags" => scrub,
                "get_SetPermissionsLinux" => false,
                _ => throw new NotImplementedException(method.Name)
            });
            var mediaFiles = Proxy<IMediaFileService>((method, args) => method.Name switch
            {
                "GetFilesByBook" => new List<BookFile> { bookFile },
                "Update" => bookFile,
                _ => throw new NotImplementedException(method.Name)
            });
            var disk = Proxy<IDiskProvider>((method, args) => method.Name == "GetFileInfo"
                ? new FileSystem().FileInfo.FromFileName((string)args[0])
                : throw new NotImplementedException(method.Name));
            var watcher = Proxy<IRootFolderWatchingService>((method, args) => method.Name == "ReportFileSystemChangeBeginning"
                ? null : throw new NotImplementedException(method.Name));
            var realDisk = RealDiskProvider.Create(LogManager.GetCurrentClassLogger());
            var safety = new FileMutationSafetyService(realDisk, new DiskTransferService(realDisk, LogManager.GetCurrentClassLogger()), new MediaFileAttributeService(config, realDisk, LogManager.GetCurrentClassLogger()), LogManager.GetCurrentClassLogger());
            var events = Proxy<IEventAggregator>((method, args) =>
            {
                if (method.Name != "PublishEvent")
                {
                    throw new NotImplementedException(method.Name);
                }

                retagged = (BookFileRetaggedEvent)args[0];
                return null;
            });
            var logger = LogManager.GetCurrentClassLogger();
            var extraction = new TagExtractionService(new[] { new TagLibSharpExtractor() }, null, logger);
            var subject = new AudioTagService(config, mediaFiles, disk, watcher, safety, events, null, logger, extraction, null);

            var preview = subject.GetRetagPreviewsByBook(book.Id);
            Assert.That(preview, Has.Count.EqualTo(1));

            subject.WriteTags(bookFile, false);

            string updatedTitle;
            uint updatedYear;
            using (var updated = TagLib.File.Create(path))
            {
                updatedTitle = updated.Tag.Title;
                updatedYear = updated.Tag.Year;
            }

            var extracted = subject.ReadAllTags(path);
            Assert.Multiple(() =>
            {
                Assert.That(updatedTitle, Is.EqualTo(edition.Title), "An unrelated tag change must prove the real writer ran.");
                Assert.That(updatedYear, Is.EqualTo(expectedYear));
                Assert.That(retagged, Is.Not.Null);
                Assert.That(retagged.Scrubbed, Is.EqualTo(scrub));
                Assert.That(preview[0].Changes.ContainsKey("YEAR"), Is.EqualTo(editionYear != 0));
                Assert.That(retagged.Diff.ContainsKey("YEAR"), Is.EqualTo(editionYear != 0));

                if (expectedYear == 0)
                {
                    Assert.That(extracted["DATE"], Is.Empty);
                    Assert.That(extracted, Does.Not.ContainKey("ID3v2:TDRC"));
                    Assert.That(extracted, Does.Not.ContainKey("ID3v2:TYER"));
                }
                else
                {
                    Assert.That(extracted["DATE"], Is.EqualTo(new[] { expectedYear.ToString() }));
                }

                if (editionYear != 0)
                {
                    Assert.That(preview[0].Changes["YEAR"].Item2, Is.EqualTo(editionYear.ToString()));
                    Assert.That(retagged.Diff["YEAR"].Item2, Is.EqualTo(editionYear.ToString()));
                }
            });

        }

        private static T Proxy<T>(Func<MethodInfo, object[], object> handler)
            where T : class
        {
            var proxy = DispatchProxy.Create<T, InterfaceProxy>();
            ((InterfaceProxy)(object)proxy).Handler = handler;
            return proxy;
        }

        private static byte[] BuildMp3()
        {
            using var stream = new MemoryStream();
            for (var index = 0; index < 20; index++)
            {
                var frame = new byte[208];
                frame[0] = 0xff;
                frame[1] = 0xf3;
                frame[2] = 0x80;
                frame[3] = 0x40;
                stream.Write(frame, 0, frame.Length);
            }

            return stream.ToArray();
        }
    }
}
