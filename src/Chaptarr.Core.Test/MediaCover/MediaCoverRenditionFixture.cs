using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using Chaptarr.Http.Frontend.Mappers;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaCover.Commands;
using NzbDrone.Core.Messaging.Commands;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace Chaptarr.Core.Test.MediaCover
{
    [TestFixture]
    public class MediaCoverRenditionFixture
    {
        private const string KnownPlaceholderJpegHash = "47736d50a054c80f646c094ecd9c00c3fb12f5c585817fa5a581140c364da30e";
        private const string KnownPlaceholderWebpHash = "db25714c302dcc8ccca766d734947df2931fcc74cbed1656ad2eb470613db981";
        private const string VerifiedRealImageHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private static readonly string ConfigRoot = @"C:\config".AsOsAgnostic();
        internal const string MascotWebpBase64 = "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";

        private class AppFolderProxy : DispatchProxy
        {
            public string AppDataFolder { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_AppDataFolder" => AppDataFolder,
                    "get_TempFolder" => Path.GetTempPath(),
                    "get_StartUpFolder" => AppDataFolder,
                    _ => throw new NotImplementedException(targetMethod?.Name)
                };
            }
        }

        private class ConfigFileProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name == "get_UrlBase"
                    ? string.Empty
                    : throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            public HashSet<string> ExistingPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> TextByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, byte[]> BinaryByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<string> DeletedFolders { get; } = new();
            public Dictionary<string, int> CallCounts { get; } = new(StringComparer.Ordinal);

            public int GetCallCount(string methodName)
            {
                return CallCounts.GetValueOrDefault(methodName);
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                CallCounts[targetMethod.Name] = GetCallCount(targetMethod.Name) + 1;
                var path = args?.FirstOrDefault() as string;

                return targetMethod?.Name switch
                {
                    nameof(IDiskProvider.FileExists) => ExistingPaths.Contains(path),
                    nameof(IDiskProvider.GetFileSize) => BinaryByPath.TryGetValue(path, out var bytes) ? bytes.LongLength : ExistingPaths.Contains(path) ? 1024L : 0L,
                    nameof(IDiskProvider.FileGetLastWrite) => new DateTime(1234),
                    nameof(IDiskProvider.FolderExists) => true,
                    nameof(IDiskProvider.GetFiles) => ExistingPaths.Where(existing =>
                        string.Equals(Path.GetDirectoryName(existing), path, StringComparison.OrdinalIgnoreCase)).ToArray(),
                    nameof(IDiskProvider.EnsureFolder) => null,
                    nameof(IDiskProvider.ReadAllText) => TextByPath.TryGetValue(path, out var value) ? value : null,
                    nameof(IDiskProvider.OpenReadStream) => Open(path),
                    nameof(IDiskProvider.WriteAllText) => WriteText(path, (string)args[1]),
                    nameof(IDiskProvider.SaveStream) => Save((string)args[1], (Stream)args[0]),
                    nameof(IDiskProvider.MoveFile) => Move((string)args[0], (string)args[1]),
                    nameof(IDiskProvider.DeleteFile) => Delete(path),
                    nameof(IDiskProvider.DeleteFolder) => DeleteFolder(path),
                    nameof(IDiskProvider.FileSetLastWriteTime) => null,
                    _ => throw new NotImplementedException(targetMethod?.Name)
                };
            }

            private FileStream Open(string path)
            {
                var temporaryPath = Path.GetTempFileName();
                File.WriteAllBytes(temporaryPath, BinaryByPath.TryGetValue(path, out var bytes) ? bytes : Array.Empty<byte>());
                return new FileStream(
                    temporaryPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    bufferSize: 4096,
                    FileOptions.DeleteOnClose);
            }

            private object WriteText(string path, string value)
            {
                TextByPath[path] = value;
                ExistingPaths.Add(path);
                return null;
            }

            private object Move(string source, string destination)
            {
                ExistingPaths.Remove(source);
                ExistingPaths.Add(destination);
                if (BinaryByPath.Remove(source, out var bytes))
                {
                    BinaryByPath[destination] = bytes;
                }
                return null;
            }

            private object Save(string path, Stream stream)
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                BinaryByPath[path] = buffer.ToArray();
                ExistingPaths.Add(path);
                return null;
            }

            private object Delete(string path)
            {
                ExistingPaths.Remove(path);
                TextByPath.Remove(path);
                BinaryByPath.Remove(path);
                return null;
            }

            private object DeleteFolder(string path)
            {
                DeletedFolders.Add(path);
                ExistingPaths.RemoveWhere(existing => existing.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                return null;
            }
        }

        private class ImageHttpClientProxy : DispatchProxy
        {
            public byte[] ResponseData { get; set; } = Array.Empty<byte>();
            public Queue<byte[]> ResponseQueue { get; } = new();
            public int Calls { get; private set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IHttpClient.Get) && args?[0] is HttpRequest request)
                {
                    Calls++;
                    var responseData = ResponseQueue.Count > 0 ? ResponseQueue.Dequeue() : ResponseData;
                    return new HttpResponse(request, new HttpHeader(), responseData);
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class DeferredCoverProxy : DispatchProxy
        {
            public bool ShouldDefer { get; set; }
            public List<int> MarkedBookIds { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name switch
                {
                    "get_IsCoverDownloadDeferred" => ShouldDefer,
                    nameof(IDeferredCoverService.MarkBookForCoverDownload) => Mark((int)args[0]),
                    _ => throw new NotImplementedException(targetMethod?.Name)
                };
            }

            private bool Mark(int bookId)
            {
                MarkedBookIds.Add(bookId);
                return ShouldDefer;
            }
        }

        private class EventAggregatorProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name == nameof(NzbDrone.Core.Messaging.Events.IEventAggregator.PublishEvent)
                    ? null
                    : throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private sealed class MediaCoverProxyStub : IMediaCoverProxy
        {
            public int ProxyCalls { get; private set; }

            public string RegisterUrl(string url) => "/MediaCoverProxy/hash/cover.jpg";

            public void ProxyRemoteUrls(IEnumerable<NzbDrone.Core.MediaCover.MediaCover> covers)
            {
                ProxyCalls++;
                foreach (var cover in covers)
                {
                    cover.Url = RegisterUrl(cover.Url);
                }
            }

            public string GetUrl(string hash) => throw new NotImplementedException();
            public byte[] GetImage(string hash) => throw new NotImplementedException();
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public List<Author> Authors { get; set; } = new();
            public List<Author> UpdatedAuthors { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.GetAllAuthors))
                {
                    return Authors;
                }

                if (targetMethod?.Name == nameof(IAuthorService.UpdateAuthor) && args?.FirstOrDefault() is Author author)
                {
                    UpdatedAuthors.Add(author);
                    return author;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class CommandQueueProxy : DispatchProxy
        {
            public List<RepairAuthorMediaCoversCommand> Commands { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IManageCommandQueue.Push) &&
                    args?.FirstOrDefault() is RepairAuthorMediaCoversCommand command)
                {
                    Commands.Add(command);
                    return null;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private sealed class CoverMapperStub : IMapCoversToLocal
        {
            public List<int> EnsuredAuthorIds { get; } = new();
            public void ConvertToLocalUrls(int entityId, MediaCoverEntity coverEntity, IEnumerable<NzbDrone.Core.MediaCover.MediaCover> covers, string selectedAuthorImageHash = null) => throw new NotImplementedException();

            public string GetCoverPath(int entityId, MediaCoverEntity coverEntity, MediaCoverTypes coverType, string extension, int? height = null)
            {
                var suffix = height.HasValue ? $"-{height.Value}" : string.Empty;
                return Path.Combine(ConfigRoot, "MediaCover", entityId.ToString(), $"poster{suffix}{extension}");
            }

            public void EnsureAuthorCovers(Author author) => EnsuredAuthorIds.Add(author.Id);
            public void EnsureBookCovers(Book book) => throw new NotImplementedException();
            public Task<EnsureImageResult> EnsureAuthorImage(Author author, NzbDrone.Core.MediaCover.MediaCover cover) => throw new NotImplementedException();
        }

        private class BookServiceProxy : DispatchProxy
        {
            public List<Book> Books { get; set; } = new();
            public List<int> RequestedAuthorIds { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetBooksByAuthor))
                {
                    RequestedAuthorIds.Add((int)args[0]);
                    return Books;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        [TestCase(".jpg")]
        [TestCase(".jpeg")]
        [TestCase(".png")]
        [TestCase(".webp")]
        public void resized_author_cover_should_be_mapped_locally_when_original_was_deleted(string extension)
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            var expectedPath = Path.Combine(ConfigRoot, "MediaCover", "12", $"poster-250{extension}");
            disk.ExistingPaths.Add(expectedPath);
            var identityPath = Path.Combine(ConfigRoot, "MediaCover", "12", "poster.url");
            disk.ExistingPaths.Add(identityPath);
            disk.TextByPath[identityPath] = MediaCoverRendition.BuildAuthorCoverIdentity(
                $"https://images.example/author{extension}",
                VerifiedRealImageHash);
            var proxy = new MediaCoverProxyStub();
            var subject = new MediaCoverService(
                proxy,
                null,
                null,
                null,
                diskProvider,
                appFolder,
                null,
                null,
                config,
                null,
                null,
                LogManager.GetCurrentClassLogger());
            var cover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, $"https://images.example/author{extension}");

            subject.ConvertToLocalUrls(12, MediaCoverEntity.Author, new[] { cover });

            var fileExistsCalls = disk.GetCallCount(nameof(IDiskProvider.FileExists));
            var getFileSizeCalls = disk.GetCallCount(nameof(IDiskProvider.GetFileSize));
            var readAllTextCalls = disk.GetCallCount(nameof(IDiskProvider.ReadAllText));
            var fileGetLastWriteCalls = disk.GetCallCount(nameof(IDiskProvider.FileGetLastWrite));
            var secondCover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, $"https://images.example/author{extension}");

            subject.ConvertToLocalUrls(12, MediaCoverEntity.Author, new[] { secondCover });

            Assert.That(cover.Url, Is.EqualTo($"/MediaCover/12/poster-250{extension}?v={VerifiedRealImageHash}"));
            Assert.That(cover.RemoteUrl, Is.EqualTo($"https://images.example/author{extension}"));
            Assert.That(secondCover.Url, Is.EqualTo(cover.Url));
            Assert.That(proxy.ProxyCalls, Is.Zero);
            Assert.Multiple(() =>
            {
                Assert.That(disk.GetCallCount(nameof(IDiskProvider.FileExists)), Is.EqualTo(fileExistsCalls));
                Assert.That(disk.GetCallCount(nameof(IDiskProvider.GetFileSize)), Is.EqualTo(getFileSizeCalls));
                Assert.That(disk.GetCallCount(nameof(IDiskProvider.ReadAllText)), Is.EqualTo(readAllTextCalls));
                Assert.That(disk.GetCallCount(nameof(IDiskProvider.FileGetLastWrite)), Is.EqualTo(fileGetLastWriteCalls));
            });
        }

        [Test]
        public void author_cover_without_remote_identity_should_not_relabel_stale_local_bytes()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            ((DiskProviderProxy)(object)diskProvider).ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "12", "poster-250.jpg"));
            var proxy = new MediaCoverProxyStub();
            var subject = new MediaCoverService(
                proxy,
                null,
                null,
                null,
                diskProvider,
                appFolder,
                null,
                null,
                config,
                null,
                null,
                LogManager.GetCurrentClassLogger());
            var cover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, "https://images.example/real-author.jpg");

            subject.ConvertToLocalUrls(12, MediaCoverEntity.Author, new[] { cover });

            Assert.That(cover.Url, Is.EqualTo("/MediaCoverProxy/hash/cover.jpg"));
            Assert.That(cover.RemoteUrl, Is.EqualTo("https://images.example/real-author.jpg"));
            Assert.That(proxy.ProxyCalls, Is.EqualTo(1));
        }

        [Test]
        public void legacy_author_cover_without_verified_content_identity_should_not_be_relabelled()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "12", "poster-250.png"));
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "12", "poster-500.png"));
            var subject = new MediaCoverService(
                mediaCoverProxy: null,
                resizer: null,
                bookService: null,
                httpClient: null,
                diskProvider: diskProvider,
                appFolderInfo: appFolder,
                coverExistsSpecification: null,
                configService: null,
                configFileProvider: null,
                eventAggregator: null,
                deferredCoverService: null,
                logger: LogManager.GetCurrentClassLogger());
            var author = new Author
            {
                Id = 12,
                Name = "Legacy Author",
                Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                {
                    new(MediaCoverTypes.Poster, "https://images.example/legacy.png")
                }
            };

            subject.EnsureAuthorCovers(author);

            Assert.That(disk.ExistingPaths, Does.Not.Contain(Path.Combine(ConfigRoot, "MediaCover", "12", "poster.url")));
        }

        [Test]
        public void selected_author_variant_should_map_locally_without_probing_disk_and_leave_other_candidates_remote()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            var remoteUrl = "https://images.example/alternate-author.jpg";
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(remoteUrl))).ToLowerInvariant()[..16];
            var selectedHash = MediaCoverRendition.ComputeStableAuthorImageHash(remoteUrl, MediaCoverTypes.Poster);
            var proxy = new MediaCoverProxyStub();
            var subject = new MediaCoverService(
                proxy,
                null,
                null,
                null,
                diskProvider,
                appFolder,
                null,
                null,
                config,
                null,
                null,
                LogManager.GetCurrentClassLogger());
            var selectedCover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, remoteUrl);
            var otherCover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, "https://images.example/other-author.jpg");

            subject.ConvertToLocalUrls(12, MediaCoverEntity.Author, new[] { selectedCover, otherCover }, selectedHash);

            Assert.That(selectedCover.Url, Is.EqualTo($"/MediaCover/12/poster-{hash}.jpg?v={selectedHash}"));
            Assert.That(selectedCover.RemoteUrl, Is.EqualTo(remoteUrl));
            Assert.That(otherCover.Url, Is.EqualTo("/MediaCoverProxy/hash/cover.jpg"));
            Assert.That(otherCover.RemoteUrl, Is.EqualTo("https://images.example/other-author.jpg"));
            Assert.That(proxy.ProxyCalls, Is.EqualTo(1));
            Assert.That(disk.CallCounts, Is.Empty);
        }

        [Test]
        public void canonical_author_cover_should_use_the_first_real_image_per_cover_type()
        {
            const string rejectedByContent = "https://assets.hardcover.app/author/900001/provider-default.jpg";
            Assert.That(MediaCoverRendition.RegisterKnownPlaceholderImage(rejectedByContent, KnownPlaceholderJpegHash), Is.True);
            var covers = new[]
            {
                new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, "https://images.example/nophoto.jpg"),
                new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, rejectedByContent + "?size=500"),
                new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, "https://images.example/primary.jpg"),
                new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, "https://images.example/alternate.jpg"),
                new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Banner, "https://images.example/banner.jpg")
            };

            var selected = MediaCoverRendition.SelectCanonicalCovers(covers);

            Assert.That(selected.Select(cover => cover.Url), Is.EqualTo(new[]
            {
                "https://images.example/primary.jpg",
                "https://images.example/banner.jpg"
            }));
        }

        [TestCase("47736d50a054c80f646c094ecd9c00c3fb12f5c585817fa5a581140c364da30e")]
        [TestCase("db25714c302dcc8ccca766d734947df2931fcc74cbed1656ad2eb470613db981")]
        [TestCase("8280bac30e108aa599176cc0737e1179e8225fe2b08d98187a6ebcb22b126a6e")]
        [TestCase("38eb593837bb848a936fae31d959d4795f1b846b3cd57d956483d721dda39478")]
        public void all_live_hardcover_mascot_encodings_should_be_known(string contentHash)
        {
            Assert.That(MediaCoverRendition.IsKnownPlaceholderImageHash(contentHash), Is.True);
        }

        [Test]
        public void amazon_shared_default_author_image_should_be_rejected_by_url_and_content()
        {
            const string url = "https://m.media-amazon.com/images/I/01Kv-W2ysOL.png";
            const string contentHash = "a5efe6ec77a9e993915eece1864c4fae3e49e13773f573aa99eb950fd4089b60";

            Assert.That(MediaCoverRendition.IsKnownPlaceholderImageUrl(url), Is.True);
            Assert.That(MediaCoverRendition.IsKnownPlaceholderImageUrl(url + "?size=500#author"), Is.True);
            Assert.That(MediaCoverRendition.IsKnownPlaceholderImageHash(contentHash), Is.True);
            Assert.That(MediaCoverRendition.SelectCandidates(new[]
            {
                new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, url)
            }), Is.Empty);
        }

        [Test]
        public void downloaded_mascot_bytes_should_register_every_url_for_the_shared_policy()
        {
            const string misleadingJpegUrl = "https://assets.hardcover.app/author/900003/reencoded.jpg?width=270";
            var mascotBytes = Convert.FromBase64String(MascotWebpBase64);

            var rejected = MediaCoverRendition.InspectDownloadedImage(misleadingJpegUrl, mascotBytes, out var contentHash);

            Assert.That(rejected, Is.True);
            Assert.That(contentHash, Is.EqualTo(KnownPlaceholderWebpHash));
            Assert.That(MediaCoverRendition.IsKnownPlaceholderImageUrl("https://assets.hardcover.app/author/900003/reencoded.jpg#alternate"), Is.True);
        }

        [Test]
        public void author_cover_identity_should_require_a_post_download_content_verdict()
        {
            const string url = "https://images.example/verified-author.jpg";
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            var identityPath = Path.Combine(ConfigRoot, "MediaCover", "12", "poster.url");
            disk.ExistingPaths.Add(identityPath);
            disk.TextByPath[identityPath] = url;

            Assert.That(MediaCoverRendition.StoredRemoteUrlMatches(identityPath, url, diskProvider), Is.False,
                "Legacy URL-only sidecars have not proved the bytes and must be revalidated once.");

            disk.TextByPath[identityPath] = MediaCoverRendition.BuildAuthorCoverIdentity(url, VerifiedRealImageHash);

            Assert.That(MediaCoverRendition.StoredRemoteUrlMatches(identityPath, url, diskProvider), Is.True);
        }

        [Test]
        public async Task on_demand_author_image_should_reject_known_provider_placeholder()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var httpClient = DispatchProxy.Create<IHttpClient, ImageHttpClientProxy>();
            ((ImageHttpClientProxy)(object)httpClient).ResponseData = Convert.FromBase64String(MascotWebpBase64);
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var subject = new MediaCoverService(
                mediaCoverProxy: null,
                resizer: null,
                bookService: null,
                httpClient: httpClient,
                diskProvider: diskProvider,
                appFolderInfo: appFolder,
                coverExistsSpecification: null,
                configService: null,
                configFileProvider: null,
                eventAggregator: null,
                deferredCoverService: null,
                logger: LogManager.GetCurrentClassLogger(),
                authorService: authorService);

            var placeholderUrl = "https://assets.hardcover.app/author/900002/extension-lies.jpg";
            var author = new Author
            {
                Id = 900002,
                Name = "No Photo Author",
                Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                {
                    new(MediaCoverTypes.Poster, placeholderUrl)
                }
            };

            var result = await subject.EnsureAuthorImage(
                author,
                author.Images.Single());

            Assert.That(result.State, Is.EqualTo("error"));
            Assert.That(result.ErrorCode, Is.EqualTo("placeholder_image"));
            Assert.That(MediaCoverRendition.IsKnownPlaceholderImageUrl(placeholderUrl), Is.True);
            Assert.That(author.Images, Is.Empty);
            Assert.That(((AuthorServiceProxy)(object)authorService).UpdatedAuthors, Has.Count.EqualTo(1));
            Assert.That(((DiskProviderProxy)(object)diskProvider).BinaryByPath, Is.Empty);
        }

        [Test]
        public async Task legacy_on_demand_mascot_file_should_be_rejected_without_a_network_request()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            var httpClient = DispatchProxy.Create<IHttpClient, ImageHttpClientProxy>();
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            const string placeholderUrl = "https://assets.hardcover.app/author/900006/legacy-default.jpg";
            var urlHash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(placeholderUrl))).ToLowerInvariant()[..16];
            var variantPath = Path.Combine(ConfigRoot, "MediaCover", "900006", $"poster-{urlHash}.jpg");
            disk.ExistingPaths.Add(variantPath);
            disk.BinaryByPath[variantPath] = Convert.FromBase64String(MascotWebpBase64);
            var subject = new MediaCoverService(
                mediaCoverProxy: null,
                resizer: null,
                bookService: null,
                httpClient: httpClient,
                diskProvider: diskProvider,
                appFolderInfo: appFolder,
                coverExistsSpecification: null,
                configService: null,
                configFileProvider: null,
                eventAggregator: null,
                deferredCoverService: null,
                logger: LogManager.GetCurrentClassLogger(),
                authorService: authorService);
            var author = new Author
            {
                Id = 900006,
                Name = "Legacy Placeholder Author",
                Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                {
                    new(MediaCoverTypes.Poster, placeholderUrl)
                }
            };

            var result = await subject.EnsureAuthorImage(author, author.Images.Single());

            Assert.That(result.ErrorCode, Is.EqualTo("placeholder_image"));
            Assert.That(disk.ExistingPaths, Does.Not.Contain(variantPath));
            Assert.That(((ImageHttpClientProxy)(object)httpClient).Calls, Is.Zero);
            Assert.That(author.Images, Is.Empty);
        }

        [Test]
        public void canonical_download_should_reject_mascot_and_persist_the_real_fallback_identity()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            var httpClient = DispatchProxy.Create<IHttpClient, ImageHttpClientProxy>();
            var responses = (ImageHttpClientProxy)(object)httpClient;
            var mascotBytes = Convert.FromBase64String(MascotWebpBase64);
            var realImageBytes = BuildPngBytes(32, 48, new Rgba32(30, 120, 210));
            responses.ResponseQueue.Enqueue(mascotBytes); // range/header probe
            responses.ResponseQueue.Enqueue(mascotBytes); // full download
            responses.ResponseQueue.Enqueue(realImageBytes); // range/header probe
            responses.ResponseQueue.Enqueue(realImageBytes); // full download
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var subject = new MediaCoverService(
                mediaCoverProxy: null,
                resizer: null,
                bookService: null,
                httpClient: httpClient,
                diskProvider: diskProvider,
                appFolderInfo: appFolder,
                coverExistsSpecification: null,
                configService: null,
                configFileProvider: null,
                eventAggregator: null,
                deferredCoverService: null,
                logger: LogManager.GetCurrentClassLogger(),
                authorService: authorService);
            const string mascotUrl = "https://assets.hardcover.app/author/900007/provider-default.jpg";
            const string realUrl = "https://assets.hardcover.app/author/900007/real-fallback.jpg";
            var author = new Author
            {
                Id = 900007,
                Name = "Fallback Author",
                Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                {
                    new(MediaCoverTypes.Poster, mascotUrl),
                    new(MediaCoverTypes.Poster, realUrl)
                }
            };

            subject.EnsureAuthorCovers(author);

            var imagePath = Path.Combine(ConfigRoot, "MediaCover", "900007", "poster.jpg");
            var identityPath = Path.Combine(ConfigRoot, "MediaCover", "900007", "poster.url");
            Assert.That(author.Images.Select(image => image.Url), Is.EqualTo(new[] { realUrl }));
            Assert.That(disk.BinaryByPath[imagePath], Is.EqualTo(realImageBytes));
            Assert.That(MediaCoverRendition.TryParseAuthorCoverIdentity(disk.TextByPath[identityPath], out var storedUrl, out var storedHash), Is.True);
            Assert.That(storedUrl, Is.EqualTo(realUrl));
            Assert.That(storedHash, Is.EqualTo(MediaCoverRendition.ComputeContentSha256(realImageBytes)));
            Assert.That(responses.Calls, Is.EqualTo(4));
            Assert.That(((AuthorServiceProxy)(object)authorService).UpdatedAuthors, Has.Count.EqualTo(1));
        }

        [Test]
        public void rejected_preferred_photo_should_not_delete_a_verified_real_fallback()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            const string mascotUrl = "https://assets.hardcover.app/author/900009/provider-default.jpg";
            const string realUrl = "https://assets.hardcover.app/author/900009/real-fallback.jpg";
            disk.ExistingPaths.UnionWith(new[]
            {
                Path.Combine(ConfigRoot, "MediaCover", "900009", "poster-250.jpg"),
                Path.Combine(ConfigRoot, "MediaCover", "900009", "poster-500.jpg"),
                Path.Combine(ConfigRoot, "MediaCover", "900009", "poster.url")
            });
            disk.TextByPath[Path.Combine(ConfigRoot, "MediaCover", "900009", "poster.url")] = MediaCoverRendition.BuildAuthorCoverIdentity(realUrl, VerifiedRealImageHash);
            var httpClient = DispatchProxy.Create<IHttpClient, ImageHttpClientProxy>();
            ((ImageHttpClientProxy)(object)httpClient).ResponseData = Convert.FromBase64String(MascotWebpBase64);
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var subject = new MediaCoverService(
                mediaCoverProxy: null,
                resizer: null,
                bookService: null,
                httpClient: httpClient,
                diskProvider: diskProvider,
                appFolderInfo: appFolder,
                coverExistsSpecification: null,
                configService: null,
                configFileProvider: null,
                eventAggregator: null,
                deferredCoverService: null,
                logger: LogManager.GetCurrentClassLogger(),
                authorService: authorService);
            var author = new Author
            {
                Id = 900009,
                Name = "Fallback Author",
                Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                {
                    new(MediaCoverTypes.Poster, mascotUrl),
                    new(MediaCoverTypes.Poster, realUrl)
                }
            };

            subject.EnsureAuthorCovers(author);

            Assert.That(author.Images.Select(image => image.Url), Is.EqualTo(new[] { realUrl }));
            Assert.That(disk.ExistingPaths, Does.Contain(Path.Combine(ConfigRoot, "MediaCover", "900009", "poster-250.jpg")));
            Assert.That(disk.ExistingPaths, Does.Contain(Path.Combine(ConfigRoot, "MediaCover", "900009", "poster-500.jpg")));
            Assert.That(disk.ExistingPaths, Does.Contain(Path.Combine(ConfigRoot, "MediaCover", "900009", "poster.url")));
            Assert.That(((ImageHttpClientProxy)(object)httpClient).Calls, Is.EqualTo(2));
        }

        [Test]
        public void verified_author_renditions_should_not_be_downloaded_again()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            const string realUrl = "https://images.example/persisted-author.jpg";
            disk.ExistingPaths.UnionWith(new[]
            {
                Path.Combine(ConfigRoot, "MediaCover", "900008", "poster-250.jpg"),
                Path.Combine(ConfigRoot, "MediaCover", "900008", "poster-500.jpg"),
                Path.Combine(ConfigRoot, "MediaCover", "900008", "poster.url")
            });
            disk.TextByPath[Path.Combine(ConfigRoot, "MediaCover", "900008", "poster.url")] = MediaCoverRendition.BuildAuthorCoverIdentity(realUrl, VerifiedRealImageHash);
            var httpClient = DispatchProxy.Create<IHttpClient, ImageHttpClientProxy>();
            var subject = new MediaCoverService(
                mediaCoverProxy: null,
                resizer: null,
                bookService: null,
                httpClient: httpClient,
                diskProvider: diskProvider,
                appFolderInfo: appFolder,
                coverExistsSpecification: null,
                configService: null,
                configFileProvider: null,
                eventAggregator: null,
                deferredCoverService: null,
                logger: LogManager.GetCurrentClassLogger());
            var author = new Author
            {
                Id = 900008,
                Name = "Persisted Author",
                Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                {
                    new(MediaCoverTypes.Poster, realUrl)
                }
            };

            subject.EnsureAuthorCovers(author);

            Assert.That(((ImageHttpClientProxy)(object)httpClient).Calls, Is.Zero);
        }

        [Test]
        public void book_cover_candidates_should_come_only_from_the_monitored_edition()
        {
            var book = new Book
            {
                Id = 12,
                Editions = new List<Edition>
                {
                    new()
                    {
                        Id = 1,
                        Monitored = false,
                        Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                        {
                            new(MediaCoverTypes.Cover, "https://images.example/unmonitored.jpg")
                        }
                    },
                    new()
                    {
                        Id = 2,
                        Monitored = true,
                        Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                        {
                            new(MediaCoverTypes.Cover, "https://images.example/monitored.png")
                        }
                    }
                }
            };

            var selected = MediaCoverRendition.SelectMonitoredBookCovers(book);

            Assert.That(selected, Has.Count.EqualTo(1));
            Assert.That(selected.Single().Edition.Id, Is.EqualTo(2));
            Assert.That(selected.Single().Cover.Url, Is.EqualTo("https://images.example/monitored.png"));
        }

        [Test]
        public void unmonitored_editions_should_never_be_borrowed_when_the_monitored_edition_has_no_cover()
        {
            var book = new Book
            {
                Id = 12,
                Editions = new List<Edition>
                {
                    new() { Id = 1, Monitored = true },
                    new()
                    {
                        Id = 2,
                        Monitored = false,
                        Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                        {
                            new(MediaCoverTypes.Cover, "https://images.example/unmonitored.jpg")
                        }
                    }
                }
            };

            Assert.That(MediaCoverRendition.SelectMonitoredBookCovers(book), Is.Empty);
        }

        [Test]
        public void selecting_a_coverless_edition_should_remove_the_previous_edition_local_cover()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover.jpg"));
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover-metadata.json"));
            disk.TextByPath[Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover-metadata.json")] =
                "{\"SelectedEdition\":{\"EditionProviderId\":\"gr:edition:old\",\"CoverUrl\":\"https://images.example/old.jpg\"}}";
            var deferred = DispatchProxy.Create<IDeferredCoverService, DeferredCoverProxy>();
            var subject = new MediaCoverService(
                null,
                null,
                null,
                null,
                diskProvider,
                appFolder,
                null,
                null,
                config,
                null,
                deferred,
                LogManager.GetCurrentClassLogger());
            var book = new Book
            {
                Id = 12,
                Title = "Coverless edition",
                Editions = new List<Edition>
                {
                    new()
                    {
                        Id = 2,
                        ForeignEditionId = "gr:edition:new",
                        Monitored = true,
                        Images = new List<NzbDrone.Core.MediaCover.MediaCover>()
                    }
                }
            };

            subject.EnsureBookCovers(book);

            Assert.That(disk.DeletedFolders, Is.EqualTo(new[] { Path.Combine(ConfigRoot, "MediaCover", "Books", "12") }));
            Assert.That(disk.ExistingPaths, Does.Not.Contain(Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover.jpg")));
        }

        [Test]
        public void transient_cover_absence_for_the_same_edition_should_retain_the_previous_local_cover()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover.jpg"));
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover-metadata.json"));
            disk.TextByPath[Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover-metadata.json")] =
                "{\"SelectedEdition\":{\"EditionProviderId\":\"gr:edition:same\",\"CoverUrl\":\"https://images.example/old.jpg\"}}";
            var deferred = DispatchProxy.Create<IDeferredCoverService, DeferredCoverProxy>();
            var subject = new MediaCoverService(
                null,
                null,
                null,
                null,
                diskProvider,
                appFolder,
                null,
                null,
                config,
                null,
                deferred,
                LogManager.GetCurrentClassLogger());
            var book = new Book
            {
                Id = 12,
                Title = "Temporarily unavailable cover",
                Editions = new List<Edition>
                {
                    new()
                    {
                        Id = 2,
                        ForeignEditionId = "gr:edition:same",
                        Monitored = true,
                        Images = new List<NzbDrone.Core.MediaCover.MediaCover>()
                    }
                }
            };

            subject.EnsureBookCovers(book);

            Assert.That(disk.DeletedFolders, Is.Empty);
            Assert.That(disk.ExistingPaths, Does.Contain(Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover.jpg")));
        }

        [TestCase("cover-250.jpg", "cover.jpg")]
        [TestCase("cover-500.jpeg", "cover.jpeg")]
        [TestCase("cover-250.webp", "cover.webp")]
        [TestCase("cover-250.avif", "cover.avif")]
        [TestCase("cover-250.jfif", "cover.jfif")]
        public void rendition_fallback_should_not_depend_on_an_extension_allowlist(string rendition, string original)
        {
            var folder = Path.Combine(ConfigRoot, "MediaCover", "Books", "12");

            Assert.That(MediaCoverRendition.GetOriginalPath(Path.Combine(folder, rendition)), Is.EqualTo(Path.Combine(folder, original)));
        }

        [TestCase("/MediaCover/Books/12/cover-250.jpeg?lastWrite=1234")]
        [TestCase("/MediaCover/Books/12/cover-250.jfif")]
        [TestCase("/MediaCover/Books/12/cover-250.webp")]
        public void local_cover_routes_should_accept_the_shared_supported_image_extensions(string path)
        {
            Assert.That(MediaCoverRendition.IsSupportedImagePath(path), Is.True);
        }

        [TestCase("/MediaCover/Books/12/cover-metadata.json")]
        [TestCase("/MediaCover/Books/12/readme.txt")]
        [TestCase("/MediaCover/Books/12/no-extension")]
        public void local_cover_routes_should_not_expose_non_image_files(string path)
        {
            Assert.That(MediaCoverRendition.IsSupportedImagePath(path), Is.False);
        }

        [Test]
        public void books_page_jpeg_rendition_request_should_fall_back_to_the_real_original()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover.jpeg"));
            var mapper = new MediaCoverMapper(appFolder, diskProvider, LogManager.GetCurrentClassLogger());

            var mapped = mapper.Map("/MediaCover/Books/12/cover-500.jpeg");

            Assert.That(mapped, Is.EqualTo(Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover.jpeg")));
            Assert.That(mapper.CanHandle("/MediaCover/Books/12/cover-metadata.json"), Is.False);
        }

        [TestCase(".png", "PNG")]
        [TestCase(".jpeg", "JPEG")]
        [TestCase(".jfif", "JPEG")]
        [TestCase(".webp", "Webp")]
        public void image_resizer_should_preserve_the_destination_file_format(string extension, string expectedFormat)
        {
            var folder = Path.Combine(Path.GetTempPath(), "chaptarr-image-resizer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var source = Path.Combine(folder, "source.png");
            var destination = Path.Combine(folder, "resized" + extension);

            try
            {
                using (var image = new Image<Rgba32>(20, 40))
                {
                    image.Save(source, new PngEncoder());
                }

                new ImageResizer(null, null).Resize(source, destination, 10);

                Assert.That(Image.DetectFormat(destination)?.Name, Is.EqualTo(expectedFormat));
                Assert.That(Image.Identify(destination)?.Height, Is.EqualTo(10));
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        [Test]
        public void missing_local_rendition_should_keep_the_remote_proxy_fallback()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var proxy = new MediaCoverProxyStub();
            var subject = new MediaCoverService(
                proxy,
                null,
                null,
                null,
                diskProvider,
                appFolder,
                null,
                null,
                config,
                null,
                null,
                LogManager.GetCurrentClassLogger());
            var cover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, "https://images.example/author.jpg");

            subject.ConvertToLocalUrls(12, MediaCoverEntity.Author, new[] { cover });

            Assert.That(cover.Url, Is.EqualTo("/MediaCoverProxy/hash/cover.jpg"));
            Assert.That(proxy.ProxyCalls, Is.EqualTo(1));
        }

        [Test]
        public void book_cover_should_not_map_a_stale_local_file_after_monitored_edition_changes()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover.jpg"));
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover-metadata.json"));
            disk.TextByPath[Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover-metadata.json")] =
                "{\"selectedEdition\":{\"coverUrl\":\"https://images.example/old.jpg\"}}";
            var proxy = new MediaCoverProxyStub();
            var subject = new MediaCoverService(
                proxy,
                null,
                null,
                null,
                diskProvider,
                appFolder,
                null,
                null,
                config,
                null,
                null,
                LogManager.GetCurrentClassLogger());
            var cover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, "https://images.example/new.jpg");

            subject.ConvertToLocalUrls(12, MediaCoverEntity.Book, new[] { cover });

            Assert.That(cover.Url, Is.EqualTo("/MediaCoverProxy/hash/cover.jpg"));
            Assert.That(proxy.ProxyCalls, Is.EqualTo(1));
        }

        [Test]
        public void missing_book_cover_metadata_should_be_negative_cached_without_reprobing_disk()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            var proxy = new MediaCoverProxyStub();
            var subject = new MediaCoverService(
                proxy,
                null,
                null,
                null,
                diskProvider,
                appFolder,
                null,
                null,
                config,
                null,
                null,
                LogManager.GetCurrentClassLogger());
            var firstCover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, "https://images.example/current.jpg");

            subject.ConvertToLocalUrls(12, MediaCoverEntity.Book, new[] { firstCover });
            var fileExistsCalls = disk.GetCallCount(nameof(IDiskProvider.FileExists));
            var secondCover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, "https://images.example/current.jpg");

            subject.ConvertToLocalUrls(12, MediaCoverEntity.Book, new[] { secondCover });

            Assert.That(firstCover.Url, Is.EqualTo("/MediaCoverProxy/hash/cover.jpg"));
            Assert.That(secondCover.Url, Is.EqualTo(firstCover.Url));
            Assert.That(proxy.ProxyCalls, Is.EqualTo(2));
            Assert.That(fileExistsCalls, Is.EqualTo(1));
            Assert.That(disk.GetCallCount(nameof(IDiskProvider.FileExists)), Is.EqualTo(fileExistsCalls));
            Assert.That(disk.GetCallCount(nameof(IDiskProvider.ReadAllText)), Is.Zero);
            Assert.That(disk.GetCallCount(nameof(IDiskProvider.FileGetLastWrite)), Is.Zero);
        }

        [Test]
        public void lifecycle_verified_book_cover_should_map_locally_without_rechecking_disk()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var config = DispatchProxy.Create<IConfigFileProvider, ConfigFileProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover-metadata.json"));
            var downloadedAt = new DateTime(2026, 8, 7, 1, 2, 3, DateTimeKind.Utc);
            disk.TextByPath[Path.Combine(ConfigRoot, "MediaCover", "Books", "12", "cover-metadata.json")] =
                $"{{\"selectedEdition\":{{\"coverUrl\":\"https://images.example/current.jpg\",\"downloadedAt\":\"{downloadedAt:O}\"}}}}";
            var proxy = new MediaCoverProxyStub();
            var subject = new MediaCoverService(
                proxy,
                null,
                null,
                null,
                diskProvider,
                appFolder,
                null,
                null,
                config,
                null,
                null,
                LogManager.GetCurrentClassLogger());
            var cover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, "https://images.example/current.jpg");

            subject.ConvertToLocalUrls(12, MediaCoverEntity.Book, new[] { cover });

            var fileExistsCalls = disk.GetCallCount(nameof(IDiskProvider.FileExists));
            var getFileSizeCalls = disk.GetCallCount(nameof(IDiskProvider.GetFileSize));
            var readAllTextCalls = disk.GetCallCount(nameof(IDiskProvider.ReadAllText));
            var fileGetLastWriteCalls = disk.GetCallCount(nameof(IDiskProvider.FileGetLastWrite));
            var secondCover = new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, "https://images.example/current.jpg");

            subject.ConvertToLocalUrls(12, MediaCoverEntity.Book, new[] { secondCover });

            Assert.That(cover.Url, Is.EqualTo($"/MediaCover/Books/12/cover.jpg?v={downloadedAt.Ticks}"));
            Assert.That(secondCover.Url, Is.EqualTo(cover.Url));
            Assert.That(proxy.ProxyCalls, Is.Zero);
            Assert.Multiple(() =>
            {
                Assert.That(disk.GetCallCount(nameof(IDiskProvider.FileExists)), Is.EqualTo(fileExistsCalls));
                Assert.That(disk.GetCallCount(nameof(IDiskProvider.GetFileSize)), Is.EqualTo(getFileSizeCalls));
                Assert.That(disk.GetCallCount(nameof(IDiskProvider.ReadAllText)), Is.EqualTo(readAllTextCalls));
                Assert.That(disk.GetCallCount(nameof(IDiskProvider.FileGetLastWrite)), Is.EqualTo(fileGetLastWriteCalls));
            });
        }

        [Test]
        public void repair_should_skip_honest_local_authors_and_process_every_missing_or_unproven_author_in_one_pass()
        {
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Authors = new List<Author>
            {
                CreateAuthor(1),
                CreateAuthor(2),
                CreateAuthor(3),
                CreateAuthor(4)
            };
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "1", "poster-250.jpg"));
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "1", "poster-500.jpg"));
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "1", "poster.url"));
            disk.TextByPath[Path.Combine(ConfigRoot, "MediaCover", "1", "poster.url")] = MediaCoverRendition.BuildAuthorCoverIdentity(
                "https://images.example/author.jpg",
                VerifiedRealImageHash);
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "4", "poster-250.jpg"));
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "4", "poster-500.jpg"));
            var commandQueue = DispatchProxy.Create<IManageCommandQueue, CommandQueueProxy>();
            var queue = (CommandQueueProxy)(object)commandQueue;
            var coverMapper = new CoverMapperStub();
            var subject = new RepairAuthorMediaCoversService(
                authorService,
                coverMapper,
                diskProvider,
                commandQueue,
                LogManager.GetCurrentClassLogger());

            subject.Execute(new RepairAuthorMediaCoversCommand());

            Assert.That(coverMapper.EnsuredAuthorIds, Is.EqualTo(new[] { 2, 3, 4 }));
            Assert.That(queue.Commands, Is.Empty);
        }

        [Test]
        public void repair_should_accept_a_local_fallback_provider_with_a_different_extension()
        {
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Authors = new List<Author>
            {
                new()
                {
                    Id = 1,
                    Name = "Author 1",
                    Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                    {
                        new(MediaCoverTypes.Poster, "https://images.example/preferred.jpg"),
                        new(MediaCoverTypes.Poster, "https://images.example/fallback.png")
                    }
                }
            };
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var disk = (DiskProviderProxy)(object)diskProvider;
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "1", "poster-250.png"));
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "1", "poster-500.png"));
            disk.ExistingPaths.Add(Path.Combine(ConfigRoot, "MediaCover", "1", "poster.url"));
            disk.TextByPath[Path.Combine(ConfigRoot, "MediaCover", "1", "poster.url")] = MediaCoverRendition.BuildAuthorCoverIdentity(
                "https://images.example/fallback.png",
                VerifiedRealImageHash);
            var commandQueue = DispatchProxy.Create<IManageCommandQueue, CommandQueueProxy>();
            var coverMapper = new CoverMapperStub();
            var subject = new RepairAuthorMediaCoversService(
                authorService,
                coverMapper,
                diskProvider,
                commandQueue,
                LogManager.GetCurrentClassLogger());

            subject.Execute(new RepairAuthorMediaCoversCommand());

            Assert.That(coverMapper.EnsuredAuthorIds, Is.Empty);
        }

        [Test]
        public void repair_should_remove_stored_placeholder_and_rebuild_from_real_fallback()
        {
            const string placeholder = "https://assets.hardcover.app/author/900004/provider-default.jpg";
            const string realPhoto = "https://i.gr-assets.com/images/S/compressed.photo.goodreads.com/authors/1534005630i/900004._UY200_.jpg";
            Assert.That(MediaCoverRendition.RegisterKnownPlaceholderImage(placeholder, KnownPlaceholderJpegHash), Is.True);
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var authors = (AuthorServiceProxy)(object)authorService;
            authors.Authors = new List<Author>
            {
                new()
                {
                    Id = 900004,
                    Name = "Fallback Author",
                    Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                    {
                        new(MediaCoverTypes.Poster, placeholder),
                        new(MediaCoverTypes.Poster, realPhoto)
                    }
                }
            };
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var commandQueue = DispatchProxy.Create<IManageCommandQueue, CommandQueueProxy>();
            var coverMapper = new CoverMapperStub();
            var subject = new RepairAuthorMediaCoversService(
                authorService,
                coverMapper,
                diskProvider,
                commandQueue,
                LogManager.GetCurrentClassLogger());

            subject.Execute(new RepairAuthorMediaCoversCommand());

            Assert.That(coverMapper.EnsuredAuthorIds, Is.EqualTo(new[] { 900004 }));
            Assert.That(authors.UpdatedAuthors, Has.Count.EqualTo(1));
            Assert.That(authors.UpdatedAuthors.Single().Images.Select(image => image.Url), Is.EqualTo(new[] { realPhoto }));
        }

        [Test]
        public void repair_should_be_queued_immediately_after_startup()
        {
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var commandQueue = DispatchProxy.Create<IManageCommandQueue, CommandQueueProxy>();
            var queue = (CommandQueueProxy)(object)commandQueue;
            var subject = new RepairAuthorMediaCoversService(
                authorService,
                new CoverMapperStub(),
                diskProvider,
                commandQueue,
                LogManager.GetCurrentClassLogger());

            subject.Handle(new ApplicationStartedEvent());

            Assert.That(queue.Commands, Has.Count.EqualTo(1));
        }

        [Test]
        public void author_refresh_should_reconcile_every_child_book_cover()
        {
            var appFolder = DispatchProxy.Create<IAppFolderInfo, AppFolderProxy>();
            ((AppFolderProxy)(object)appFolder).AppDataFolder = ConfigRoot;
            var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
            var books = (BookServiceProxy)(object)bookService;
            books.Books = Enumerable.Range(1, 3)
                .Select(id => new Book { Id = id, Title = $"Book {id}" })
                .ToList();
            var deferredCoverService = DispatchProxy.Create<IDeferredCoverService, DeferredCoverProxy>();
            var deferred = (DeferredCoverProxy)(object)deferredCoverService;
            deferred.ShouldDefer = true;
            var eventAggregator = DispatchProxy.Create<NzbDrone.Core.Messaging.Events.IEventAggregator, EventAggregatorProxy>();
            var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
            var subject = new MediaCoverService(
                mediaCoverProxy: null,
                resizer: null,
                bookService: bookService,
                httpClient: null,
                diskProvider: diskProvider,
                appFolderInfo: appFolder,
                coverExistsSpecification: null,
                configService: null,
                configFileProvider: null,
                eventAggregator: eventAggregator,
                deferredCoverService: deferredCoverService,
                logger: LogManager.GetCurrentClassLogger());
            var author = new Author
            {
                Id = 42,
                Name = "Cover Lifecycle Author",
                Images = new List<NzbDrone.Core.MediaCover.MediaCover>()
            };

            subject.HandleAsync(new NzbDrone.Core.Books.Events.AuthorRefreshCompleteEvent(author));

            Assert.That(books.RequestedAuthorIds, Is.EqualTo(new[] { 42 }));
            Assert.That(deferred.MarkedBookIds, Is.EqualTo(new[] { 1, 2, 3 }));
        }

        private static Author CreateAuthor(int id)
        {
            return new Author
            {
                Id = id,
                Name = $"Author {id}",
                Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                {
                    new(MediaCoverTypes.Poster, "https://images.example/author.jpg")
                }
            };
        }

        private static byte[] BuildPngBytes(int width, int height, Rgba32 color)
        {
            using var image = new Image<Rgba32>(width, height, color);
            using var stream = new MemoryStream();
            image.Save(stream, new PngEncoder());
            return stream.ToArray();
        }
    }
}
