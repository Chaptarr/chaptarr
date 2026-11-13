using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Books;
using Chaptarr.Core.Test.Common;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Http;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class CalibreHardlinkFixture
    {
        [Test]
        public void linked_ebook_preview_marks_only_the_linked_format()
        {
            var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(directory);
            try
            {
                var target = Path.Combine(directory, "download.epub");
                File.WriteAllText(target, "original");
                var path = Path.Combine(directory, "linked.epub");
                SymbolicLinkTest.CreateFile(path, target);

                var author = new Author { Id = 1, Name = "Author" };
                var book = new Book { Id = 2, Author = author, MediaType = BookMediaType.Ebook };
                var edition = new Edition { Id = 3, Book = book, Title = "New title", Language = "en" };
                var file = new BookFile { Id = 4, Path = path, Author = author, Edition = edition, CalibreId = 5 };
                var files = new List<BookFile> { file };
                var calibreBooks = new List<CalibreBook>
                {
                    new CalibreBook
                    {
                        Id = file.CalibreId,
                        Title = "Old title",
                        Authors = new List<string> { author.Name },
                        Languages = new List<string> { "en" },
                        Tags = new List<string>(),
                        Identifiers = new Dictionary<string, string>()
                    }
                };
                var logger = LogManager.GetCurrentClassLogger();
                var tags = new EBookTagService(Proxy<IMediaFileService>((_, _) => files),
                    Proxy<IRootFolderService>((method, _) => method.Name == nameof(IRootFolderService.All) ? new List<RootFolder> { new RootFolder { Path = directory, CalibreSettings = new CalibreSettings { Host = "localhost" } } } : new RootFolder { Path = directory, CalibreSettings = new CalibreSettings { Host = "localhost" } }),
                    null, Proxy<ICalibreProxy>((_, _) => calibreBooks), new CalibreLibraryGate(new HttpServiceHealth(logger)), logger);
                var service = TagServiceTestFactory.Metadata(null, tags, logger, null);
                var preview = new RetagBookController(service).GetBooks(null, book.Id, "ebook").Single();
                Assert.That(preview.BookFileId, Is.EqualTo(file.Id));
                Assert.That(preview.IsFileSymlink, Is.True);
                Assert.That(preview.IsCalibreFormat, Is.True);
                Assert.That(preview.Changes.Select(change => change.Field), Does.Contain("Title"));

                file.Path = target;
                preview = new RetagBookController(service).GetBooks(null, book.Id, "ebook").Single();
                Assert.That(preview.IsFileSymlink, Is.False);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [TestCase(false, "normal")]
        [TestCase(true, "normal")]
        [TestCase(false, "missing-older")]
        [TestCase(true, "missing-older")]
        [TestCase(true, "failed-separation")]
        [TestCase(true, "file-symlink-single")]
        [TestCase(false, "file-symlink-single")]
        [TestCase(true, "file-symlink-hardlinked")]
        [TestCase(true, "file-symlink-embed-transport-failure")]
        [TestCase(true, "file-symlink-tracked-timeout")]
        [TestCase(true, "file-symlink-tracked-error")]
        [TestCase(true, "all-missing")]
        [TestCase(true, "missing-tracked")]
        [TestCase(false, "missing-tracked")]
        [TestCase(true, "embed-error")]
        [TestCase(true, "embed-invalid-response")]
        [TestCase(true, "embed-unchanged")]
        [TestCase(true, "tracked-format-absent")]
        [TestCase(false, "tracked-format-absent")]
        [TestCase(true, "unexpected-preparation-error")]
        [TestCase(true, "null-format-map")]
        [TestCase(false, "null-format-map")]
        public void only_embedding_should_separate_all_formats_after_calibre_renames_and_remote_mapping(bool embed, string scenario)
        {
            var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(directory);
            try
            {
                using var logs = new LogFactory();
                var warnings = new MemoryTarget { Layout = "${message}" };
                var allLogs = new MemoryTarget { Layout = "${level}: ${message}${exception:format=ToString}" };
                var logging = new LoggingConfiguration();
                logging.AddRule(LogLevel.Debug, LogLevel.Fatal, allLogs);
                logging.AddRule(LogLevel.Warn, LogLevel.Fatal, warnings);
                logs.Configuration = logging;
                var logger = logs.GetLogger("calibre-fixture");
                var disk = RealDiskProvider.Create(logger);
                var formats = scenario == "tracked-format-absent" ? new[] { "fb2", "cbz" } : new[] { "epub", "azw3" };
                foreach (var format in formats)
                {
                    File.WriteAllText(Path.Combine(directory, "download." + format), "original");
                    if (!disk.TryCreateHardLink(Path.Combine(directory, "download." + format), Path.Combine(directory, "old." + format)))
                    {
                        Assert.Ignore("This test filesystem does not support hardlinks.");
                    }
                }

                if (scenario.StartsWith("file-symlink"))
                {
                    var linkedFormat = scenario is "file-symlink-tracked-timeout" or "file-symlink-tracked-error" ? "epub" : "azw3";
                    var oldFormat = Path.Combine(directory, "old." + linkedFormat);
                    var target = Path.Combine(directory, "download." + linkedFormat);
                    File.Delete(oldFormat);
                    if (scenario == "file-symlink-hardlinked")
                    {
                        Assert.That(disk.TryCreateHardLink(target, Path.Combine(directory, "seed.azw3")), Is.True);
                    }

                    SymbolicLinkTest.CreateFile(oldFormat, target);
                }

                var author = new Author { Name = "Author" };
                var book = new Book { Author = author, MediaType = BookMediaType.Ebook };
                var file = new BookFile
                {
                    Path = Path.Combine(directory, "old.epub"), CalibreId = 7, Author = author,
                    Edition = new Edition { Book = book, Title = "Renamed", Language = "en" }
                };
                var settings = new CalibreSettings { Host = "calibre.invalid", Library = "test" };
                var config = Proxy<IConfigService>((method, _) => method.Name switch
                {
                    "get_WriteBookTags" => WriteBookTagsType.Sync,
                    "get_UpdateCovers" => false,
                    "get_EmbedMetadata" => embed,
                    "get_SetPermissionsLinux" => false,
                    _ => throw new NotImplementedException(method.Name)
                });
                var calls = new List<string>();
                var watcherPaths = new List<string>();
                var http = Proxy<IHttpClient>((method, args) =>
                {
                    var request = (HttpRequest)args[0];
                    if (method.Name == "Get")
                    {
                        if (request.Url.ToString().Contains("ajax/books/"))
                        {
                            return new HttpResponse<Dictionary<int, CalibreBook>>(new HttpResponse(request, new HttpHeader(),
                                new Dictionary<int, CalibreBook> { [file.CalibreId] = new CalibreBook { Id = file.CalibreId, Formats = new() } }.ToJson()));
                        }
                        calls.Add("get");
                        var response = new CalibreBook
                        {
                            Formats = formats.ToDictionary(format => format, format => new CalibreBookFormat
                            {
                                Path = "/remote/renamed." + format,
                                LastModified = new DateTime(2020, 1, scenario == "missing-older" ? (format == "epub" ? 2 : 1) : (format == "epub" ? 1 : 2))
                            })
                        };
                        if (scenario == "null-format-map")
                        {
                            response.Formats = null;
                        }
                        return new HttpResponse<CalibreBook>(new HttpResponse(request, new HttpHeader(), response.ToJson()));
                    }

                    Assert.That(method.Name, Is.EqualTo("Execute"));
                    if (request.Url.ToString().Contains("set-fields"))
                    {
                        calls.Add("set");
                        foreach (var format in formats)
                        {
                            if (scenario == "all-missing" || (scenario == "missing-older" && format == "azw3") || (scenario == "missing-tracked" && format == "epub"))
                            {
                                File.Delete(Path.Combine(directory, "old." + format));
                            }
                            else
                            {
                                File.Move(Path.Combine(directory, "old." + format), Path.Combine(directory, "renamed." + format));
                            }
                        }
                    }
                    else
                    {
                        calls.Add("embed");
                        Assert.That(request.Url.ToString(), Does.Contain("embed_metadata"));
                        if (scenario == "embed-unchanged")
                        {
                            return new HttpResponse(request, new HttpHeader(), "{\"result\":null}");
                        }
                        if (scenario == "embed-error")
                        {
                            return new HttpResponse(request, new HttpHeader(), "{\"err\":\"fixture embed failure\"}");
                        }
                        var selected = JArray.Parse(Encoding.UTF8.GetString(request.ContentData))[1].ToObject<string[]>();
                        Assert.That(selected, Is.EquivalentTo(scenario is "normal" or "tracked-format-absent" or "embed-invalid-response" ? formats : scenario is "missing-tracked" or "file-symlink-tracked-timeout" or "file-symlink-tracked-error" ? new[] { "azw3" } : new[] { "epub" }));
                        if (scenario == "file-symlink-tracked-error")
                        {
                            throw RetagMediaScopeFixture.ResponseError(500);
                        }
                        if (scenario == "file-symlink-tracked-timeout")
                        {
                            throw new WebException("fixture embed timeout", new TaskCanceledException(), WebExceptionStatus.Timeout, null);
                        }
                        if (scenario == "file-symlink-embed-transport-failure")
                        {
                            throw new HttpRequestException(HttpRequestError.ConnectionError, "Fixture embed transport failure");
                        }

                        foreach (var format in selected)
                        {
                            var path = Path.Combine(directory, "renamed." + format);
                            Assert.That(watcherPaths, Does.Contain(path));
                            Assert.That(disk.GetFileLinkCount(path), Is.EqualTo(1), "Every format must be independent before Calibre writes any of them.");
                            File.WriteAllText(path, "embedded");
                            File.SetLastWriteTimeUtc(path, scenario is "missing-tracked" or "tracked-format-absent" ? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) : file.Modified.AddSeconds(10));
                        }
                    }

                    return new HttpResponse(request, new HttpHeader(), request.Url.ToString().Contains("set-fields") ? "{\"7\":{}}" : scenario == "embed-invalid-response" ? "{}" : "{\"result\":null}");
                });
                var safety = new FileMutationSafetyService(disk, new DiskTransferService(disk, logger), new MediaFileAttributeService(config, disk, logger), logger);
                var calibre = new CalibreProxy(http, null,
                    Proxy<IRemotePathMappingService>((_, args) => new OsPath(Path.Combine(directory, Path.GetFileName(((OsPath)args[1]).FullPath)))),
                    Proxy<IRootFolderWatchingService>((_, args) => { watcherPaths.AddRange((string[])args[0]); return null; }),
                    null, config, new CacheManager(), Proxy<IFileMutationSafetyService>((_, args) =>
                    {
                        var path = (string)args[0];
                        if (scenario == "unexpected-preparation-error")
                        {
                            throw new InvalidOperationException("fixture unexpected failure");
                        }
                        if (scenario == "failed-separation" && Path.GetExtension(path) == ".azw3")
                        {
                            throw new IOException("simulated copy failure");
                        }
                        return safety.EnsureMutableFile(path);
                    }), new CalibreLibraryGate(new HttpServiceHealth(logger)), logger);
                var outcomes = new List<CalibreTagUpdateResult>();
                var observedCalibre = Proxy<ICalibreProxy>((method, args) =>
                {
                    if (method.Name == nameof(ICalibreProxy.GetBooks))
                    {
                        return calibre.GetBooks((List<int>)args[0], (CalibreSettings)args[1]);
                    }
                    var outcome = calibre.SetFields((BookFile)args[0], (CalibreSettings)args[1], (bool)args[2], (bool)args[3], (Action<string>)args[4]);
                    outcomes.Add(outcome);
                    return outcome;
                });
                var tagHealth = new HttpServiceHealth(logger);
                var tags = new EBookTagService(Proxy<IMediaFileService>((_, _) => new List<BookFile> { file }),
                    Proxy<IRootFolderService>((method, _) => method.Name == nameof(IRootFolderService.All)
                        ? new List<RootFolder> { new RootFolder { Path = directory, CalibreSettings = settings } }
                        : new RootFolder { Path = directory, CalibreSettings = settings }), config, observedCalibre, new CalibreLibraryGate(tagHealth), logger);

                if (scenario == "embed-invalid-response")
                {
                    var batch = tags.RetagFiles(new RetagFilesCommand { EmbedMetadata = true }, new List<BookFile> { file }, new TagBatch());
                    Assert.That((batch.Written, batch.Failed, batch.Incomplete), Is.EqualTo((0, 0, 1)));
                    Assert.That(tagHealth.CanBeginRequest(CalibreLibraryIdentity.GetKey(settings), out _), Is.False);
                    Assert.That(calls, Is.EqualTo(new[] { "set", "get", "embed" }));
                    Assert.That(allLogs.Logs, Has.Some.Contains("Last error: Calibre did not confirm embedding"));
                    Assert.That(File.ReadAllText(Path.Combine(directory, "download.epub")), Is.EqualTo("original"));
                    return;
                }

                if (scenario == "null-format-map")
                {
                    var batch = tags.RetagFiles(new RetagFilesCommand { EmbedMetadata = embed }, new List<BookFile> { file }, new TagBatch());
                    Assert.That((batch.Written, batch.Failed, batch.Incomplete), Is.EqualTo((0, 0, 1)));
                    Assert.That(outcomes, Is.EqualTo(new[] { CalibreTagUpdateResult.Incomplete }));
                    Assert.That(calls, Is.EqualTo(new[] { "set", "get" }));
                    Assert.That(embed ? batch.CalibreIncomplete : batch.CalibreDetailsIncomplete, Is.EqualTo(1));
                    foreach (var format in formats)
                    {
                        Assert.That(File.ReadAllText(Path.Combine(directory, "download." + format)), Is.EqualTo("original"));
                        Assert.That(disk.GetFileLinkCount(Path.Combine(directory, "renamed." + format)), Is.EqualTo(2));
                    }
                    return;
                }

                if (scenario == "unexpected-preparation-error")
                {
                    var batch = tags.RetagFiles(new RetagFilesCommand { EmbedMetadata = embed }, new List<BookFile> { file }, new TagBatch());
                    Assert.That((batch.Written, batch.Failed, batch.Incomplete), Is.EqualTo((0, 0, 1)));
                    Assert.That(allLogs.Logs, Has.Some.Contains("fixture unexpected failure"));
                    return;
                }

                if (scenario == "file-symlink-tracked-error")
                {
                    var batch = tags.RetagFiles(new RetagFilesCommand { EmbedMetadata = true }, new List<BookFile> { file }, new TagBatch());
                    Assert.That((batch.SkippedSymlinks, batch.Failed, batch.Incomplete, batch.Written), Is.EqualTo((1, 0, 0, 0)));
                    Assert.That(allLogs.Logs, Has.Some.StartsWith("Warn: Calibre metadata follow-up failed for protected symlink"));
                    Assert.That(allLogs.Logs, Has.None.StartsWith("Error:"));
                    Assert.That(File.ReadAllText(Path.Combine(directory, "download.epub")), Is.EqualTo("original"));
                    return;
                }

                var timer = Stopwatch.StartNew();
                if (scenario is "file-symlink-single" or "file-symlink-embed-transport-failure" or "file-symlink-tracked-timeout")
                {
                    var localization = Proxy<ILocalizationService>((_, args) =>
                    {
                        if ((string)args[0] != "RetagSkippedSymlinkWrites")
                        {
                            return "Localized summary";
                        }
                        Assert.That(args[0], Is.EqualTo("RetagSkippedSymlinkWrites"));
                        Assert.That(((Dictionary<string, object>)args[1])["count"], Is.EqualTo(1));
                        return "Localized message for one skipped write";
                    });
                    var audioCalls = 0;
                    var audio = Proxy<IAudioTagService>((_, _) => { audioCalls++; return new TagBatch(); });
                    TagBatch batch = null;
                    var observedTags = Proxy<IEBookTagService>((_, args) => batch = tags.RetagFiles((RetagFilesCommand)args[0], (List<BookFile>)args[1], (TagBatch)args[2]));
                    TagServiceTestFactory.Metadata(audio, observedTags, logger, localization, media: Proxy<IMediaFileService>((_, _) => new List<BookFile> { file })).Execute(new RetagFilesCommand { Files = new List<int> { file.Id }, EmbedMetadata = embed });
                    Assert.That(audioCalls, Is.EqualTo(1));
                    Assert.That(allLogs.Logs.Count(line => line.Contains("Localized message for one skipped write")), Is.EqualTo(embed ? 1 : 0));
                    Assert.That(batch.Written, Is.EqualTo(scenario == "file-symlink-single" ? 1 : 0));
                    Assert.That(batch.Incomplete, Is.EqualTo(scenario == "file-symlink-embed-transport-failure" ? 1 : 0));
                    Assert.That(batch.SkippedSymlinks, Is.EqualTo(embed ? 1 : 0));
                    Assert.That(batch.Failed, Is.Zero);
                }
                else
                {
                    tags.WriteTags(file, false);
                }

                if (scenario is not "file-symlink-embed-transport-failure" and not "file-symlink-tracked-timeout")
                {
                    var expected = scenario is "missing-tracked" or "tracked-format-absent" ? CalibreTagUpdateResult.Incomplete
                        : !embed || scenario is "normal" or "missing-older" or "failed-separation" || scenario.StartsWith("file-symlink") ? CalibreTagUpdateResult.Completed
                        : CalibreTagUpdateResult.Incomplete;
                    Assert.That(outcomes, Is.EqualTo(new[] { expected }), "Requested work must not be reported completed when formats were skipped or embedding failed");
                }

                if (scenario == "embed-error")
                {
                    Assert.That(warnings.Logs, Has.Some.Contains("fixture embed failure"));
                    Assert.That(timer.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), "An error response must not enter the 20-second success polling loop");
                }

                if (scenario is "missing-tracked" or "tracked-format-absent")
                {
                    Assert.That(warnings.Logs, Has.Some.Contains("old.epub"));
                }
                if (scenario.StartsWith("file-symlink"))
                {
                    var linkedPath = Path.Combine(directory, scenario == "file-symlink-tracked-timeout" ? "renamed.epub" : "renamed.azw3");
                    if (scenario is "file-symlink-embed-transport-failure" or "file-symlink-tracked-timeout")
                    {
                        Assert.That(warnings.Logs, Has.Some.Contains("is unavailable after"));
                        Assert.That(allLogs.Logs, Has.Some.Contains(scenario == "file-symlink-tracked-timeout" ? "fixture embed timeout" : "Fixture embed transport failure"));
                        Assert.That(allLogs.Logs, Has.Some.Contains(scenario == "file-symlink-tracked-timeout" ? "Info: Localized message for one skipped write" : "Warn: Localized summary"));
                    }
                    else
                    {
                        Assert.That(warnings.Logs, Is.Empty);
                    }

                    Assert.That(warnings.Logs, Has.None.Contains("Skipping tag writes through file symlink"));
                    Assert.That(allLogs.Logs.Contains($"Debug: Skipping tag writes through file symlink '{linkedPath}'."), Is.EqualTo(embed));
                    Assert.That(allLogs.Logs, Has.None.Contains($"Separated hardlinked file {linkedPath}"));
                    Assert.That(new FileInfo(linkedPath).LinkTarget, Is.EqualTo(Path.Combine(directory, "download" + Path.GetExtension(linkedPath))));
                }

                Assert.That(calls, Is.EqualTo(scenario == "file-symlink-embed-transport-failure" ? new[] { "set", "get", "embed", "embed" } : embed && scenario != "all-missing" ? new[] { "set", "get", "embed" } : new[] { "set", "get" }));
                Assert.That(file.Path, Is.EqualTo(Path.Combine(directory, scenario is "all-missing" or "missing-tracked" or "tracked-format-absent" ? "old.epub" : "renamed.epub")));
                foreach (var format in formats)
                {
                    Assert.That(File.ReadAllText(Path.Combine(directory, "download." + format)), Is.EqualTo("original"));
                    if (scenario != "all-missing" && !(scenario == "missing-older" && format == "azw3") && !(scenario == "missing-tracked" && format == "epub"))
                    {
                        var expectedLinks = scenario.StartsWith("file-symlink") && format == "azw3" ? (scenario == "file-symlink-hardlinked" ? 2 : 1) : embed && !(scenario == "failed-separation" && format == "azw3") ? 1 : 2;
                        Assert.That(disk.GetFileLinkCount(Path.Combine(directory, "renamed." + format)), Is.EqualTo(expectedLinks));
                    }
                }
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
