using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Chaptarr.Core.Test.Common;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Consumers.AudioBookShelf;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Extras.Metadata
{
    [TestFixture]
    public class MetadataServiceCoverUrlSafetyFixture
    {
        private const string Credential = "fixture-credential-must-not-be-logged";

        private static IEnumerable<TestCaseData> UnsafeCovers()
        {
            var urls = new[]
            {
                ("http://127.0.0.1/cover.jpg", "private_or_local_address"),
                ("http://10.0.0.1/cover.jpg", "private_or_local_address"),
                ("http://169.254.169.254/cover.jpg", "private_or_local_address"),
                ("http://[::]/cover.jpg", "private_or_local_address"),
                ("http://[fc00::1]/cover.jpg", "private_or_local_address"),
                ("http://user:" + Credential + "@8.8.8.8/cover.jpg", "userinfo_not_allowed"),
                ("http-invalid://8.8.8.8/cover.jpg", "invalid_scheme")
            };
            foreach (var media in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            {
                for (var index = 0; index < urls.Length; index++)
                    yield return new TestCaseData(media, urls[index].Item1, urls[index].Item2)
                        .SetName($"ABS_cover_blocks_{media}_{urls[index].Item2}_{index}");
            }
        }

        [TestCaseSource(nameof(UnsafeCovers))]
        public void abs_cover_writer_should_apply_the_existing_url_policy_before_download(BookMediaType media, string url, string reason)
        {
            using var harness = new CoverWriter(media, url, null);
            var result = harness.Subject.CreateAfterBookImport(harness.Author, harness.File).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(harness.Downloads, Is.Empty);
                Assert.That(harness.Files, Is.Empty);
                Assert.That(result, Is.Empty);
                Assert.That(harness.Logs.Logs.Any(message => message.StartsWith("Warn|") && message.Contains(reason)), Is.True);
                Assert.That(string.Join("\n", harness.Logs.Logs), Does.Not.Contain(Credential));
                Assert.That(string.Join("\n", harness.Logs.Logs), Does.Not.Contain(url));
            });
        }

        private static IEnumerable<TestCaseData> AllowedCovers()
        {
            var urls = new[]
            {
                ("http://8.8.8.8/cover.jpg", (string)null),
                ("https://[2001:4860:4860::8888]/cover.jpg", (string)null),
                ("http://10.0.0.1/cover.jpg", "http://10.0.0.1:8080"),
                ("http://127.0.0.1/cover.jpg", "http://localhost:8080")
            };
            foreach (var media in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            {
                for (var index = 0; index < urls.Length; index++)
                    yield return new TestCaseData(media, urls[index].Item1, urls[index].Item2)
                        .SetName($"ABS_cover_allows_{media}_{index}");
            }
        }

        [TestCaseSource(nameof(AllowedCovers))]
        public void abs_cover_writer_should_retain_public_and_configured_metadata_hosts(BookMediaType media, string url, string metadataServer)
        {
            using var harness = new CoverWriter(media, url, metadataServer);
            var result = harness.Subject.CreateAfterBookImport(harness.Author, harness.File).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(harness.Downloads, Is.EqualTo(new[] { new Uri(url).ToString() }));
                Assert.That(harness.Files.Keys.Select(Path.GetFullPath), Is.EqualTo(new[] { harness.CoverPath }));
                Assert.That(result, Has.Count.EqualTo(1));
                Assert.That(((MetadataFile)result.Single()).Hash, Is.Not.Null.And.Not.Empty);
                Assert.That(harness.Logs.Logs.Any(message => message.StartsWith("Warn|")), Is.False);
            });
        }

        [Test]
        public void existing_local_image_copy_should_not_be_treated_as_a_remote_url()
        {
            using var harness = new CoverWriter(BookMediaType.Ebook, null, null);
            var source = @"C:\Source\cover.jpg".AsOsAgnostic();
            harness.Files[source] = new byte[] { 1, 2, 3 };
            var method = typeof(MetadataService).GetMethod("DownloadImage", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(Author), typeof(ImageFileResult), typeof(string), typeof(bool) }, null);
            var result = (bool)method.Invoke(harness.Subject, new object[]
            {
                harness.Author, new ImageFileResult(Path.Combine("Requested Work", "cover.jpg"), source), harness.Author.Path, false
            });

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True);
                Assert.That(harness.Files[harness.CoverPath], Is.EqualTo(harness.Files[source]));
                Assert.That(harness.Downloads, Is.Empty);
                Assert.That(harness.CopyCount, Is.EqualTo(1));
                Assert.That(harness.Logs.Logs.Any(message => message.StartsWith("Warn|")), Is.False);
            });
        }

        private sealed class CoverWriter : IDisposable
        {
            public readonly Dictionary<string, byte[]> Files = new();
            public readonly List<string> Downloads = new();
            public readonly MemoryTarget Logs = new() { Layout = "${level}|${message}" };
            public readonly LogFactory LogFactory = new();
            public readonly Author Author;
            public readonly BookFile File;
            public readonly string CoverPath;
            public readonly MetadataService Subject;
            public int CopyCount;

            public CoverWriter(BookMediaType media, string url, string metadataServer)
            {
                var logging = new LoggingConfiguration();
                logging.AddRule(LogLevel.Debug, LogLevel.Fatal, Logs);
                LogFactory.Configuration = logging;
                var authorPath = @"C:\Library\A Author".AsOsAgnostic();
                Author = new Author { Id = 10, Name = "A Author", Path = authorPath, AudiobookPath = authorPath, EbookPath = authorPath };
                var book = new Book { Id = 20, Title = "Requested Work", MediaType = media, Author = Author };
                var edition = new Edition
                {
                    Id = 30, BookId = book.Id, Book = book, Title = book.Title,
                    Images = url == null ? new() : new() { new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, url) }
                };
                File = new BookFile
                {
                    Id = 40, EditionId = edition.Id, Edition = edition, Part = 1,
                    MediaType = media == BookMediaType.Ebook ? "ebook" : "audiobook",
                    Path = Path.Combine(authorPath, book.Title, media == BookMediaType.Ebook ? "book.epub" : "book.m4b")
                };
                CoverPath = Path.Combine(authorPath, book.Title, "cover.jpg");
                var root = new RootFolder { Id = 1, Path = @"C:\Library".AsOsAgnostic(), FolderType = FolderType.Mixed };
                root.SetAudiobookSettings(new MediaTypeSettings { WriteAudioBookShelfCover = true });
                root.SetEbookSettings(new MediaTypeSettings { WriteAudioBookShelfCover = true });
                var consumer = new AudioBookShelfMetadata(Proxy<IRootFolderService>((method, args) => root), null, null)
                {
                    Definition = new MetadataDefinition { Settings = new AudioBookShelfMetadataSettings() }
                };
                var disk = Proxy<IDiskProvider>((method, args) => method.Name switch
                {
                    nameof(IDiskProvider.FileExists) => Files.ContainsKey((string)args[0]),
                    nameof(IDiskProvider.OpenReadStream) => OpenBytes(Files[(string)args[0]]),
                    nameof(IDiskProvider.CopyFile) => Copy((string)args[0], (string)args[1]),
                    _ => DefaultResult(method)
                });
                var http = Proxy<IHttpClient>((method, args) =>
                {
                    if (method.Name == nameof(IHttpClient.DownloadFile))
                    {
                        Downloads.Add((string)args[0]);
                        Files[(string)args[1]] = new byte[] { 1, 2, 3 };
                    }
                    return DefaultResult(method);
                });
                Subject = new MetadataService(
                    Proxy<IConfigService>((method, args) => method.Name == "get_MetadataServerUrl" ? metadataServer : DefaultResult(method)),
                    disk, null, null, Proxy<IOtherExtraFileRenamer>((method, args) => null),
                    Proxy<IMetadataFactory>((method, args) => new List<IMetadata> { consumer }), null, http,
                    Proxy<IMediaFileAttributeService>((method, args) => null),
                    Proxy<IMetadataFileService>((method, args) => DefaultResult(method)), null,
                    LogFactory.GetLogger(nameof(MetadataServiceCoverUrlSafetyFixture)));
            }

            private object Copy(string source, string destination)
            {
                CopyCount++;
                Files[destination] = Files[source];
                return null;
            }

            private static FileStream OpenBytes(byte[] bytes)
            {
                var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.DeleteOnClose);
                stream.Write(bytes);
                stream.Position = 0;
                return stream;
            }

            private static object DefaultResult(MethodInfo method) => method.ReturnType.IsValueType && method.ReturnType != typeof(void)
                ? Activator.CreateInstance(method.ReturnType) : null;

            public void Dispose() => LogFactory.Dispose();
        }
    }
}
