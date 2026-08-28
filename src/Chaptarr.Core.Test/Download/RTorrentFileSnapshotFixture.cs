using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.RTorrent;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.TorrentInfo;
using NzbDrone.Core.RemotePathMappings;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class RTorrentFileSnapshotFixture
    {
        private class HttpClientQueueProxy : DispatchProxy
        {
            public Queue<Func<HttpRequest, HttpResponse>> Responses { get; } = new();
            public List<HttpRequest> Requests { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IHttpClient.Execute) && args?.Length == 1 && args[0] is HttpRequest request)
                {
                    Requests.Add(request);

                    if (Responses.Count == 0)
                    {
                        throw new InvalidOperationException("Test IHttpClient has no queued responses.");
                    }

                    return Responses.Dequeue()(request);
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(IHttpClient).Name}.{targetMethod?.Name}");
            }
        }

        private class TestTorrentFileInfoReader : ITorrentFileInfoReader
        {
            public string GetHashFromTorrentFile(byte[] fileContents)
            {
                return "hash";
            }
        }

        private class TestProxy : IRTorrentProxy
        {
            public List<string> RequestedFileHashes { get; } = new();
            public List<RTorrentFile> Files { get; set; } = new();
            public Exception FilesException { get; set; }

            public string GetVersion(RTorrentSettings settings) => throw new NotImplementedException();
            public List<RTorrentTorrent> GetTorrents(RTorrentSettings settings) => throw new NotImplementedException();

            public List<RTorrentFile> GetTorrentFiles(string hash, RTorrentSettings settings)
            {
                RequestedFileHashes.Add(hash);

                if (FilesException != null)
                {
                    throw FilesException;
                }

                return Files;
            }

            public void AddTorrentFromUrl(string torrentUrl, string label, RTorrentPriority priority, string directory, RTorrentSettings settings) => throw new NotImplementedException();
            public void AddTorrentFromFile(string fileName, byte[] fileContent, string label, RTorrentPriority priority, string directory, RTorrentSettings settings) => throw new NotImplementedException();
            public void RemoveTorrent(string hash, RTorrentSettings settings) => throw new NotImplementedException();
            public void SetTorrentLabel(string hash, string label, RTorrentSettings settings) => throw new NotImplementedException();
            public bool HasHashTorrent(string hash, RTorrentSettings settings) => throw new NotImplementedException();
            public void PushTorrentUniqueView(string hash, string view, RTorrentSettings settings) => throw new NotImplementedException();
        }

        private class PrefixRemotePathMappingService : IRemotePathMappingService
        {
            private readonly string _remoteRoot;
            private readonly string _localRoot;

            public PrefixRemotePathMappingService(string remoteRoot = null, string localRoot = null)
            {
                _remoteRoot = remoteRoot;
                _localRoot = localRoot;
            }

            public List<RemotePathMapping> All() => throw new NotImplementedException();
            public RemotePathMapping Add(RemotePathMapping mapping) => throw new NotImplementedException();
            public void Remove(int id) => throw new NotImplementedException();
            public RemotePathMapping Get(int id) => throw new NotImplementedException();
            public RemotePathMapping Update(RemotePathMapping mapping) => throw new NotImplementedException();
            public OsPath RemapRemoteToLocal(string host, OsPath remotePath) => Remap(remotePath, _remoteRoot, _localRoot);
            public OsPath RemapLocalToRemote(string host, OsPath localPath) => Remap(localPath, _localRoot, _remoteRoot);
            public OsPath RemapRemoteToLocal(int downloadClientId, string host, OsPath remotePath) => Remap(remotePath, _remoteRoot, _localRoot);
            public OsPath RemapLocalToRemote(int downloadClientId, string host, OsPath localPath) => Remap(localPath, _localRoot, _remoteRoot);
            public RemotePathMappingTestResult Test(RemotePathMapping mapping) => throw new NotImplementedException();

            private static OsPath Remap(OsPath path, string fromRoot, string toRoot)
            {
                if (path.IsEmpty || string.IsNullOrWhiteSpace(fromRoot) || string.IsNullOrWhiteSpace(toRoot))
                {
                    return path;
                }

                if (!path.FullPath.StartsWith(fromRoot, StringComparison.InvariantCultureIgnoreCase))
                {
                    return path;
                }

                return new OsPath(toRoot + path.FullPath.Substring(fromRoot.Length));
            }
        }

        private class TestRTorrent : RTorrent
        {
            public TestRTorrent(IRTorrentProxy proxy, IRemotePathMappingService remotePathMappingService)
                : base(proxy,
                    new TestTorrentFileInfoReader(),
                    httpClient: null,
                    configService: null,
                    diskProvider: null,
                    remotePathMappingService: remotePathMappingService,
                    downloadSeedConfigProvider: null,
                    rTorrentDirectoryValidator: null,
                    blocklistService: null,
                    logger: LogManager.GetCurrentClassLogger())
            {
            }
        }

        private static (RTorrentProxy Proxy, HttpClientQueueProxy HttpClient) CreateProxy()
        {
            var httpClient = DispatchProxy.Create<IHttpClient, HttpClientQueueProxy>();
            var httpClientProxy = (HttpClientQueueProxy)(object)httpClient;

            return (new RTorrentProxy(httpClient), httpClientProxy);
        }

        private static RTorrentSettings Settings()
        {
            return new RTorrentSettings
            {
                Host = "rtorrent",
                Port = 5000
            };
        }

        private static HttpResponse XmlResponse(HttpRequest request, string content)
        {
            return new HttpResponse(request, new HttpHeader { ContentType = "text/xml" }, content, HttpStatusCode.OK);
        }

        private static TestRTorrent CreateClient(TestProxy proxy, IRemotePathMappingService remotePathMappingService)
        {
            return new TestRTorrent(proxy, remotePathMappingService)
            {
                Definition = new DownloadClientDefinition
                {
                    Id = 19,
                    Name = "rTorrent",
                    Settings = new RTorrentSettings
                    {
                        Host = "rtorrent"
                    }
                }
            };
        }

        [Test]
        public void should_fetch_torrent_files_using_file_multicall()
        {
            var (proxy, httpClient) = CreateProxy();

            httpClient.Responses.Enqueue(request => XmlResponse(request, @"<?xml version=""1.0""?>
<methodResponse>
  <params>
    <param>
      <value>
        <array>
          <data>
            <value>
              <array>
                <data>
                  <value><string>Author - Book/part1.m4b</string></value>
                  <value><string>/downloads/Author - Book/part1.m4b</string></value>
                  <value><i8>1</i8></value>
                </data>
              </array>
            </value>
            <value>
              <array>
                <data>
                  <value><string>Author - Book/part2.m4b</string></value>
                  <value><string>/downloads/Author - Book/part2.m4b</string></value>
                  <value><i8>0</i8></value>
                </data>
              </array>
            </value>
          </data>
        </array>
      </value>
    </param>
  </params>
</methodResponse>"));

            var files = proxy.GetTorrentFiles("ABCDEF1234", Settings());

            Assert.That(files, Has.Count.EqualTo(2));
            Assert.That(files[0].Path, Is.EqualTo("Author - Book/part1.m4b"));
            Assert.That(files[0].FrozenPath, Is.EqualTo("/downloads/Author - Book/part1.m4b"));
            Assert.That(files[0].Priority, Is.EqualTo(1));
            Assert.That(files[1].Path, Is.EqualTo("Author - Book/part2.m4b"));
            Assert.That(files[1].FrozenPath, Is.EqualTo("/downloads/Author - Book/part2.m4b"));
            Assert.That(files[1].Priority, Is.Zero);

            var requestBody = Encoding.UTF8.GetString(httpClient.Requests.Single().ContentData);
            Assert.That(requestBody, Does.Contain("<methodName>f.multicall</methodName>"));
            Assert.That(requestBody, Does.Contain("<string>ABCDEF1234</string>"));
            Assert.That(requestBody, Does.Contain("<string>f.path=</string>"));
            Assert.That(requestBody, Does.Contain("<string>f.frozen_path=</string>"));
            Assert.That(requestBody, Does.Contain("<string>f.priority=</string>"));
        }

        [Test]
        public void should_capture_authoritative_selected_file_list()
        {
            var proxy = new TestProxy
            {
                Files = new List<RTorrentFile>
                {
                    new() { Path = "part1.m4b", FrozenPath = "/remote/downloads/Author - Book/part1.m4b", Priority = 1 },
                    new() { Path = "part2.m4b", FrozenPath = "/remote/downloads/Author - Book/part2.m4b", Priority = 2 },
                    new() { Path = "sample.mp3", FrozenPath = "/remote/downloads/Author - Book/sample.mp3", Priority = 0 }
                }
            };

            var client = CreateClient(proxy, new PrefixRemotePathMappingService("/remote/downloads", "/local/downloads"));

            var importItem = client.GetImportItem(new DownloadClientItem
            {
                DownloadId = "ABCDEF1234",
                Title = "Author - Book",
                OutputPath = new OsPath("/local/downloads/Author - Book"),
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Id = 19,
                    Name = "rTorrent",
                    Protocol = DownloadProtocol.Torrent
                }
            }, null);

            Assert.That(proxy.RequestedFileHashes, Is.EqualTo(new[] { "abcdef1234" }));
            Assert.That(importItem.FilePaths, Is.EqualTo(new[]
            {
                "/local/downloads/Author - Book/part1.m4b",
                "/local/downloads/Author - Book/part2.m4b"
            }));
            Assert.That(importItem.FileListConfidence, Is.EqualTo(DownloadClientFileListConfidence.Authoritative));
        }

        [Test]
        public void should_resolve_relative_file_paths_against_existing_output_path()
        {
            var proxy = new TestProxy
            {
                Files = new List<RTorrentFile>
                {
                    new() { Path = "Disc 1/part1.m4b", Priority = 1 },
                    new() { Path = "Disc 2/part2.m4b", Priority = 1 }
                }
            };

            var client = CreateClient(proxy, new PrefixRemotePathMappingService());

            var importItem = client.GetImportItem(new DownloadClientItem
            {
                DownloadId = "ABCDEF1234",
                Title = "Author - Book",
                OutputPath = new OsPath("/local/downloads/Author - Book")
            }, null);

            Assert.That(importItem.FilePaths, Is.EqualTo(new[]
            {
                "/local/downloads/Author - Book/Disc 1/part1.m4b",
                "/local/downloads/Author - Book/Disc 2/part2.m4b"
            }));
            Assert.That(importItem.FileListConfidence, Is.EqualTo(DownloadClientFileListConfidence.Authoritative));
        }

        [Test]
        public void should_resolve_windows_output_path_with_forward_slash_torrent_member_paths()
        {
            var proxy = new TestProxy
            {
                Files = new List<RTorrentFile>
                {
                    new() { Path = "Author - Book/part1.epub", Priority = 1 }
                }
            };

            var client = CreateClient(proxy, new PrefixRemotePathMappingService());

            var importItem = client.GetImportItem(new DownloadClientItem
            {
                DownloadId = "ABCDEF1234",
                Title = "Author - Book",
                OutputPath = new OsPath(@"X:\TempBooks")
            }, null);

            Assert.That(importItem.FilePaths, Is.EqualTo(new[]
            {
                @"X:\TempBooks\Author - Book\part1.epub"
            }));
            Assert.That(importItem.FileListConfidence, Is.EqualTo(DownloadClientFileListConfidence.Authoritative));
        }

        [Test]
        public void should_keep_single_file_relative_path_at_existing_output_path()
        {
            var proxy = new TestProxy
            {
                Files = new List<RTorrentFile>
                {
                    new() { Path = "Author - Book.m4b", Priority = 1 }
                }
            };

            var client = CreateClient(proxy, new PrefixRemotePathMappingService());

            var importItem = client.GetImportItem(new DownloadClientItem
            {
                DownloadId = "ABCDEF1234",
                Title = "Author - Book",
                OutputPath = new OsPath("/local/downloads/Author - Book.m4b")
            }, null);

            Assert.That(importItem.FilePaths, Is.EqualTo(new[]
            {
                "/local/downloads/Author - Book.m4b"
            }));
            Assert.That(importItem.FileListConfidence, Is.EqualTo(DownloadClientFileListConfidence.Authoritative));
        }

        [Test]
        public void should_not_mark_authoritative_when_no_file_paths_resolve()
        {
            var proxy = new TestProxy
            {
                Files = new List<RTorrentFile>
                {
                    new()
                }
            };

            var client = CreateClient(proxy, new PrefixRemotePathMappingService());

            var importItem = client.GetImportItem(new DownloadClientItem
            {
                DownloadId = "ABCDEF1234",
                Title = "Author - Book",
                OutputPath = new OsPath("/local/downloads/Author - Book")
            }, null);

            Assert.That(importItem.FilePaths, Is.Null);
            Assert.That(importItem.FileListConfidence, Is.Null);
        }

        [Test]
        public void should_fall_back_to_output_path_when_file_list_request_fails()
        {
            var proxy = new TestProxy
            {
                FilesException = new DownloadClientException("boom")
            };

            var client = CreateClient(proxy, new PrefixRemotePathMappingService());

            var importItem = client.GetImportItem(new DownloadClientItem
            {
                DownloadId = "ABCDEF1234",
                Title = "Author - Book",
                OutputPath = new OsPath("/local/downloads/Author - Book")
            }, null);

            Assert.That(importItem.OutputPath, Is.EqualTo(new OsPath("/local/downloads/Author - Book")));
            Assert.That(importItem.FilePaths, Is.Null);
            Assert.That(importItem.FileListConfidence, Is.Null);
        }

        [Test]
        public void should_return_clone_without_file_paths_when_file_list_request_fails_without_output_path()
        {
            var proxy = new TestProxy
            {
                FilesException = new DownloadClientException("boom")
            };

            var client = CreateClient(proxy, new PrefixRemotePathMappingService());

            var importItem = client.GetImportItem(new DownloadClientItem
            {
                DownloadId = "ABCDEF1234",
                Title = "Author - Book"
            }, null);

            Assert.That(importItem.FilePaths, Is.Null);
            Assert.That(importItem.FileListConfidence, Is.Null);
        }
    }
}
