using System;
using System.Collections.Generic;
using NLog;
using Newtonsoft.Json;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.Deluge;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.TorrentInfo;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class DelugeDuplicateHandlingFixture
    {
        private class TestTorrentFileInfoReader : ITorrentFileInfoReader
        {
            public string GetHashFromTorrentFile(byte[] fileContents)
            {
                return "hash";
            }
        }

        private class TestProxy : IDelugeProxy
        {
            public int LabelOptionsCalls { get; set; }

            public List<(string Hash, string Label)> Labels { get; } = new();
            public List<string> AddedMagnets { get; } = new();
            public List<string> AddedTorrentFiles { get; } = new();
            public List<string> SeedConfigurationHashes { get; } = new();
            public DelugeTorrentDetails TorrentDetails { get; set; }
            public Exception TorrentDetailsException { get; set; }
            public Exception AddException { get; set; }

            public string GetVersion(DelugeSettings settings) => throw new NotImplementedException();
            public Dictionary<string, object> GetConfig(DelugeSettings settings) => new()
            {
                { "move_completed", false },
                { "download_location", null }
            };
            public DelugeTorrent[] GetTorrents(DelugeSettings settings) => throw new NotImplementedException();
            public DelugeTorrent[] GetTorrentsByLabel(string label, DelugeSettings settings) => throw new NotImplementedException();
            public DelugeTorrentDetails GetTorrentDetails(string hash, DelugeSettings settings)
            {
                if (TorrentDetailsException != null)
                {
                    throw TorrentDetailsException;
                }

                return TorrentDetails;
            }
            public string[] GetAvailablePlugins(DelugeSettings settings) => throw new NotImplementedException();
            public string[] GetEnabledPlugins(DelugeSettings settings) => throw new NotImplementedException();
            public string[] GetAvailableLabels(DelugeSettings settings) => throw new NotImplementedException();
            public DelugeLabel GetLabelOptions(DelugeSettings settings)
            {
                LabelOptionsCalls++;
                throw new DelugeException("label.get_options should not be called for an empty label", 4);
            }
            public void SetTorrentLabel(string hash, string label, DelugeSettings settings) => Labels.Add((hash, label));
            public void SetTorrentConfiguration(string hash, string key, object value, DelugeSettings settings) => throw new NotImplementedException();
            public void SetTorrentSeedingConfiguration(string hash, TorrentSeedConfiguration seedConfiguration, DelugeSettings settings) => SeedConfigurationHashes.Add(hash);
            public void AddLabel(string label, DelugeSettings settings) => throw new NotImplementedException();

            public string AddTorrentFromMagnet(string magnetLink, DelugeSettings settings)
            {
                AddedMagnets.Add(magnetLink);

                if (AddException != null)
                {
                    throw AddException;
                }

                return "ABCDEF1234";
            }

            public string AddTorrentFromFile(string filename, byte[] fileContent, DelugeSettings settings)
            {
                AddedTorrentFiles.Add(filename);

                if (AddException != null)
                {
                    throw AddException;
                }

                return "ABCDEF1234";
            }

            public bool RemoveTorrent(string hash, bool removeData, DelugeSettings settings) => throw new NotImplementedException();
            public void MoveTorrentToTopInQueue(string hash, DelugeSettings settings) => throw new NotImplementedException();
        }

        private class PassthroughRemotePathMappingService : IRemotePathMappingService
        {
            public List<RemotePathMapping> All() => throw new NotImplementedException();
            public RemotePathMapping Add(RemotePathMapping mapping) => throw new NotImplementedException();
            public void Remove(int id) => throw new NotImplementedException();
            public RemotePathMapping Get(int id) => throw new NotImplementedException();
            public RemotePathMapping Update(RemotePathMapping mapping) => throw new NotImplementedException();
            public OsPath RemapRemoteToLocal(string host, OsPath remotePath) => remotePath;
            public OsPath RemapLocalToRemote(string host, OsPath localPath) => localPath;
            public OsPath RemapRemoteToLocal(int downloadClientId, string host, OsPath remotePath) => remotePath;
            public OsPath RemapLocalToRemote(int downloadClientId, string host, OsPath localPath) => localPath;
            public RemotePathMappingTestResult Test(RemotePathMapping mapping) => throw new NotImplementedException();
        }

        private class TestDeluge : Deluge
        {
            public TestDeluge(IDelugeProxy proxy, Logger logger)
                : base(proxy,
                    new TestTorrentFileInfoReader(),
                    httpClient: null,
                    configService: null,
                    diskProvider: null,
                    remotePathMappingService: new PassthroughRemotePathMappingService(),
                    blocklistService: null,
                    logger: logger)
            {
            }

            public string AddTorrentFile(RemoteBook remoteBook, string hash)
            {
                return base.AddFromTorrentFile(remoteBook, hash, "file.torrent", Array.Empty<byte>());
            }

            public string AddMagnet(RemoteBook remoteBook, string hash)
            {
                return base.AddFromMagnetLink(remoteBook, hash, "magnet:?xt=urn:btih:HASH&tr=https://tracker.example/");
            }
        }

        [Test]
        public void should_not_request_label_options_when_legacy_category_is_empty()
        {
            var proxy = new TestProxy();
            var client = new TestDeluge(proxy, LogManager.GetCurrentClassLogger())
            {
                Definition = new DownloadClientDefinition
                {
                    Name = "Deluge",
                    Settings = new DelugeSettings
                    {
                        MusicCategory = string.Empty,
                        EbookCategory = string.Empty,
                        AudiobookCategory = string.Empty
                    }
                }
            };

            Assert.DoesNotThrow(() => client.GetStatus());
            Assert.That(proxy.LabelOptionsCalls, Is.EqualTo(0));
        }

        [Test]
        public void should_reject_torrent_file_duplicates_without_adopting_the_existing_torrent()
        {
            var proxy = new TestProxy { AddException = new DelugeException("Torrent already in session", 4) };
            var client = new TestDeluge(proxy, LogManager.GetCurrentClassLogger())
            {
                Definition = new DownloadClientDefinition
                {
                    Name = "Deluge",
                    Settings = new DelugeSettings
                    {
                        EbookCategory = "ebooks",
                        AudiobookCategory = "audiobooks"
                    }
                }
            };

            var remoteBook = new RemoteBook
            {
                Books = new List<Book> { new Book { MediaType = BookMediaType.Ebook } },
                Release = new TorrentInfo(),
                SeedConfiguration = new TorrentSeedConfiguration()
            };

            var hash = "ABCDEF1234";
            var exception = Assert.Throws<DownloadClientRejectedReleaseException>(() => client.AddTorrentFile(remoteBook, hash));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Message, Is.EqualTo("Deluge rejected the torrent because it is already in the session"));
                Assert.That(proxy.AddedTorrentFiles, Has.Count.EqualTo(1));
                Assert.That(proxy.SeedConfigurationHashes, Is.Empty);
                Assert.That(proxy.Labels, Is.Empty);
            });
        }

        [Test]
        public void should_reject_magnet_duplicates_without_adopting_the_existing_torrent()
        {
            var proxy = new TestProxy { AddException = new DelugeException("Torrent already in session", 4) };
            var client = new TestDeluge(proxy, LogManager.GetCurrentClassLogger())
            {
                Definition = new DownloadClientDefinition
                {
                    Name = "Deluge",
                    Settings = new DelugeSettings
                    {
                        EbookCategory = "ebooks",
                        AudiobookCategory = "audiobooks"
                    }
                }
            };

            var remoteBook = new RemoteBook
            {
                Books = new List<Book> { new Book { MediaType = BookMediaType.Ebook } },
                Release = new TorrentInfo(),
                SeedConfiguration = new TorrentSeedConfiguration()
            };

            var hash = "ABCDEF1234";
            var exception = Assert.Throws<DownloadClientRejectedReleaseException>(() => client.AddMagnet(remoteBook, hash));

            Assert.Multiple(() =>
            {
                Assert.That(exception.Message, Is.EqualTo("Deluge rejected the torrent because it is already in the session"));
                Assert.That(proxy.AddedMagnets, Has.Count.EqualTo(1));
                Assert.That(proxy.SeedConfigurationHashes, Is.Empty);
                Assert.That(proxy.Labels, Is.Empty);
            });
        }

        [Test]
        public void should_surface_non_duplicate_errors_unchanged()
        {
            var original = new DelugeException("Permission denied", 2);
            var proxy = new TestProxy { AddException = original };
            var client = new TestDeluge(proxy, LogManager.GetCurrentClassLogger())
            {
                Definition = new DownloadClientDefinition
                {
                    Name = "Deluge",
                    Settings = new DelugeSettings
                    {
                        EbookCategory = "ebooks",
                        AudiobookCategory = "audiobooks"
                    }
                }
            };

            var remoteBook = new RemoteBook
            {
                Books = new List<Book> { new Book { MediaType = BookMediaType.Ebook } }
            };

            var exception = Assert.Throws<DelugeException>(() => client.AddTorrentFile(remoteBook, "ABCDEF1234"));

            Assert.Multiple(() =>
            {
                Assert.That(exception, Is.SameAs(original));
                Assert.That(proxy.SeedConfigurationHashes, Is.Empty);
                Assert.That(proxy.Labels, Is.Empty);
            });
        }

        [Test]
        public void should_capture_authoritative_file_list_for_import_item()
        {
            var proxy = new TestProxy
            {
                TorrentDetails = new DelugeTorrentDetails
                {
                    DownloadPath = "/downloads",
                    Files = new List<DelugeTorrentFile>
                    {
                        new() { Path = "Author - Book/part1.m4b" },
                        new() { Path = "Author - Book/part2.m4b" },
                        new() { Path = "Author - Book/sample.nfo" }
                    },
                    FilePriorities = new List<int>
                    {
                        4,
                        4,
                        0
                    }
                }
            };
            var client = new TestDeluge(proxy, LogManager.GetCurrentClassLogger())
            {
                Definition = new DownloadClientDefinition
                {
                    Id = 9,
                    Name = "Deluge",
                    Settings = new DelugeSettings { Host = "localhost" }
                }
            };

            var importItem = client.GetImportItem(new DownloadClientItem
            {
                DownloadId = "ABCDEF1234",
                Title = "Author - Book",
                OutputPath = new OsPath("/downloads/Author - Book"),
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Id = 9,
                    Name = "Deluge",
                    Protocol = DownloadProtocol.Torrent
                }
            }, null);

            Assert.That(importItem.FilePaths, Is.EqualTo(new[]
            {
                "/downloads/Author - Book/part1.m4b",
                "/downloads/Author - Book/part2.m4b"
            }));
            Assert.That(importItem.FileListConfidence, Is.EqualTo(DownloadClientFileListConfidence.Authoritative));
        }

        [Test]
        public void should_resolve_windows_save_path_with_forward_slash_torrent_member_paths()
        {
            var proxy = new TestProxy
            {
                TorrentDetails = new DelugeTorrentDetails
                {
                    DownloadPath = @"X:\TempBooks",
                    Files = new List<DelugeTorrentFile>
                    {
                        new() { Path = "Author - Book/part1.epub" }
                    },
                    FilePriorities = new List<int> { 4 }
                }
            };
            var client = new TestDeluge(proxy, LogManager.GetCurrentClassLogger())
            {
                Definition = new DownloadClientDefinition
                {
                    Id = 9,
                    Name = "Deluge",
                    Settings = new DelugeSettings { Host = "localhost" }
                }
            };

            var importItem = client.GetImportItem(new DownloadClientItem
            {
                DownloadId = "ABCDEF1234",
                Title = "Author - Book",
                OutputPath = new OsPath(@"X:\TempBooks\Author - Book"),
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Id = 9,
                    Name = "Deluge",
                    Protocol = DownloadProtocol.Torrent
                }
            }, null);

            Assert.That(importItem.FilePaths, Is.EqualTo(new[]
            {
                @"X:\TempBooks\Author - Book\part1.epub"
            }));
            Assert.That(importItem.FileListConfidence, Is.EqualTo(DownloadClientFileListConfidence.Authoritative));
        }

        [Test]
        public void should_deserialize_deluge_torrent_status_file_list_response()
        {
            const string json = @"{
                ""save_path"": ""/downloads"",
                ""file_priorities"": [4, 0],
                ""files"": [
                    { ""path"": ""Author - Book/part1.m4b"", ""size"": 12345 },
                    { ""path"": ""Author - Book/part2.m4b"", ""size"": 67890 }
                ]
            }";

            var result = JsonConvert.DeserializeObject<DelugeTorrentDetails>(json);

            Assert.That(result.DownloadPath, Is.EqualTo("/downloads"));
            Assert.That(result.FilePriorities, Is.EqualTo(new[] { 4, 0 }));
            Assert.That(result.Files, Has.Count.EqualTo(2));
            Assert.That(result.Files[0].Path, Is.EqualTo("Author - Book/part1.m4b"));
            Assert.That(result.Files[1].Path, Is.EqualTo("Author - Book/part2.m4b"));
        }

        [Test]
        public void should_return_clone_without_file_paths_when_file_list_fetch_fails_without_output_path()
        {
            var client = new TestDeluge(new TestProxy { TorrentDetailsException = new DelugeException("deluge unavailable", 4) }, LogManager.GetCurrentClassLogger())
            {
                Definition = new DownloadClientDefinition
                {
                    Id = 9,
                    Name = "Deluge",
                    Settings = new DelugeSettings { Host = "localhost" }
                }
            };

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
