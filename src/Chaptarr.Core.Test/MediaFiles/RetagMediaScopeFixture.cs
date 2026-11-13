using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Chaptarr.Api.V1.Books;
using Chaptarr.Core.Test.Http;
using Chaptarr.Http.REST;
using DryIoc;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Http;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public partial class RetagMediaScopeFixture
    {
        private List<BookFile> _files;
        private List<Author> _authors;
        private List<RootFolder> _roots;
        private IMediaFileService _media;
        private IAuthorService _authorService;
        private ILocalizationService _localizer;
        private EBookTagService _ebooks;
        private HttpServiceHealth _health;
        private CalibreLibraryGate _gate;
        private LogFactory _logs;
        private MemoryTarget _messages;
        private Logger _logger;
        private List<CommandResult> _results;
        private Action<BookFile> _write;
        private Action _afterRead;
        private Func<BookFile, CalibreTagUpdateResult> _outcome;
        private Func<List<int>, CalibreSettings, List<CalibreBook>> _preview;
        private Action<int> _authorRead;
        private List<int> _attempts;
        private int _selections;
        private int _authorSelections;
        private static readonly string Root = @"C:\ebooks".AsOsAgnostic();
        private static readonly string Other = @"C:\other".AsOsAgnostic();

        [SetUp]
        public void SetUp()
        {
            _files = new(); _authors = new(); _attempts = new(); _results = new();
            _selections = 0; _authorSelections = 0;
            _write = _ => { }; _authorRead = _ => { }; _afterRead = () => { };
            _outcome = _ => CalibreTagUpdateResult.Completed;
            _preview = (ids, settings) => ids.Select(id => new CalibreBook
            {
                Id = id,
                Title = settings.Library,
                Authors = new() { "Old author" },
                Languages = new() { "en" },
                Tags = new(),
                Identifiers = new()
            }).ToList();
            _logs = new LogFactory();
            _messages = new MemoryTarget { Layout = "${level}: ${message}" };
            var config = new LoggingConfiguration(); config.AddRule(LogLevel.Trace, LogLevel.Fatal, _messages); _logs.Configuration = config;
            _logger = _logs.GetLogger("retag-test");
            _health = new HttpServiceHealth(_logger); _gate = new CalibreLibraryGate(_health);
            _roots = new()
            {
                new RootFolder { Path = Root, CalibreSettings = new CalibreSettings { Host = "localhost", Library = "first" } },
                new RootFolder { Path = Other, CalibreSettings = new CalibreSettings { Host = "localhost", Library = "other" } }
            };
            _media = Proxy<IMediaFileService>((method, args) =>
            {
                if (method.Name == nameof(IMediaFileService.GetExisting))
                {
                    _selections++;
                    return _files.Where(file => ((IEnumerable<int>)args[0]).Contains(file.Id)).ToList();
                }
                if (method.Name == nameof(IMediaFileService.GetFilesByAuthor))
                {
                    _authorRead((int)args[0]);
                    return _files.Where(file => file.Author?.Id == (int)args[0]).ToList();
                }
                if (method.Name == nameof(IMediaFileService.GetFilesByBook))
                {
                    return _files;
                }
                throw new AssertionException("Unexpected file query: " + method.Name);
            });
            _authorService = Proxy<IAuthorService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IAuthorService.GetExistingAuthors)));
                _authorSelections++;
                return _authors.Where(author => ((IEnumerable<int>)args[0]).Contains(author.Id)).ToList();
            });
            var roots = Proxy<IRootFolderService>((method, args) => method.Name == nameof(IRootFolderService.All)
                ? _roots : _roots.SingleOrDefault(root => ((string)args[0]).StartsWith(root.Path + Path.DirectorySeparatorChar, StringComparison.Ordinal)));
            var calibre = Proxy<ICalibreProxy>((method, args) =>
            {
                if (method.Name == nameof(ICalibreProxy.GetBooks))
                {
                    return _preview((List<int>)args[0], (CalibreSettings)args[1]);
                }
                Assert.That(method.Name, Is.EqualTo(nameof(ICalibreProxy.SetFields)));
                var file = (BookFile)args[0]; _attempts.Add(file.Id); _write(file); return _outcome(file);
            });
            _ebooks = new EBookTagService(_media, roots, Proxy<IConfigService>((method, _) =>
                method.Name == "get_WriteBookTags" ? WriteBookTagsType.Sync : (object)false), calibre, Proxy<ICalibreLibraryGate>((method, args) =>
                {
                    var result = method.Invoke(_gate, args);
                    if (method.Name == nameof(ICalibreLibraryGate.ReportSuccess)) _afterRead();
                    return result;
                }), _logger);
            // Use the shipped English resource and real token replacement, not a key-printing fake.
            _localizer = new LocalizationService(Proxy<IConfigService>((_, _) => 1),
                Proxy<IAppFolderInfo>((_, _) => TestContext.CurrentContext.TestDirectory), new CacheManager(), _logger);
        }

        [TearDown]
        public void TearDown() => _logs.Dispose();

        private BookFile Add(int id, BookMediaType side = BookMediaType.Ebook, int authorId = 1, string root = null)
        {
            var author = _authors.SingleOrDefault(a => a.Id == authorId);
            if (author == null) { author = new Author { Id = authorId, Name = "Author " + authorId }; _authors.Add(author); }
            var book = new Book { Id = id, Author = author, MediaType = side };
            var file = new BookFile
            {
                Id = id,
                Path = Path.Combine(root ?? Root, id + ".epub"),
                Author = author,
                CalibreId = id,
                EditionId = id,
                MediaType = side == BookMediaType.Ebook ? "audiobook" : "ebook",
                Edition = new Edition { Id = id, BookId = id, Title = "New title", Language = "en", Book = book }
            };
            _files.Add(file); return file;
        }

        private MetadataTagService Service(Action<BookFile> audioWrite = null, IEBookTagService ebooks = null, int restoredTotals = 0)
        {
            var audio = Proxy<IAudioTagService>((method, args) =>
            {
                if (method.Name.StartsWith("GetRetagPreviews", StringComparison.Ordinal))
                {
                    return new List<RetagBookFilePreview>();
                }
                var batch = (TagBatch)args[2];
                batch.RestoredTotals = restoredTotals;
                void Write(List<BookFile> files)
                {
                    foreach (var file in RetagMediaScope.Select(files, BookMediaType.Audiobook, batch)) { audioWrite?.Invoke(file); batch.Written++; }
                }
                if (method.Name == nameof(IAudioTagService.RetagFiles))
                {
                    Write((List<BookFile>)args[1]);
                }
                else
                {
                    RetagMediaScope.ForEachAuthor((IReadOnlyList<Author>)args[1], _media, BookMediaType.Audiobook, batch, Write);
                }
                return batch;
            });
            return new MetadataTagService(audio, ebooks ?? _ebooks, _logger, _localizer,
                Proxy<ICommandResultReporter>((_, args) => { _results.Add((CommandResult)args[0]); return null; }), _media, _authorService);
        }

        private RetagFilesCommand RunFiles()
        {
            var command = new RetagFilesCommand(1, _files.Select(file => file.Id).ToList()); Service().Execute(command); return command;
        }

        [TestCase(0)]
        [TestCase(2)]
        public void command_reports_preserved_totals_only_when_present(int count)
        {
            var file = Add(1, BookMediaType.Audiobook);
            var command = new RetagFilesCommand(1, new List<int> { file.Id });
            Service(restoredTotals: count).Execute(command);
            Assert.That(Regex.Replace(command.CompletionMessage, @" \(\d+ ms\)\.$", string.Empty), Is.EqualTo(count > 0
                ? "Audio: written: 1, stored totals preserved: 2"
                : "Audio: written: 1"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void zero_window_rate_limit_allows_immediate_rerun(bool preview)
        {
            Add(1);
            var request = new HttpRequest("http://localhost/");
            var error = new TooManyRequestsException(request, new HttpResponse(request, new HttpHeader { ["Retry-After"] = "0" }, "limited", HttpStatusCode.TooManyRequests));
            var read = _preview;
            if (preview)
            {
                _preview = (_, _) => throw error;
            }
            else
            {
                _write = _ => throw error;
            }

            RunFiles();
            Assert.That(_health.GetStatus(_gate.GetKey(_roots[0].CalibreSettings)).ProbeInProgress, Is.False);
            _write = _ => { };
            _preview = read;
            var batch = _ebooks.RetagFiles(new RetagFilesCommand(), _files, new TagBatch());
            Assert.That((batch.Written, batch.Unavailable), Is.EqualTo((1, 0)));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void failed_page_isolated_to_preview_while_retag_attempts_each_book(bool preview)
        {
            Add(1); Add(2);
            _preview = (_, _) => throw ResponseError(500);
            _write = file =>
            {
                if (file.Id == 1)
                {
                    throw ResponseError(500);
                }
            };
            if (preview)
            {
                Assert.That(_ebooks.GetRetagPreviewsByBook(1).Select(row => row.SkipReason),
                    Is.EqualTo(new[] { RetagSkipReason.LibraryPreviewFailed, RetagSkipReason.LibraryPreviewFailed }));
                Assert.That(_attempts, Is.Empty);
            }
            else
            {
                var batch = _ebooks.RetagFiles(new RetagFilesCommand(), _files, new TagBatch());
                Assert.That(_attempts, Is.EqualTo(new[] { 1, 2 }));
                Assert.That((batch.Written, batch.Failed), Is.EqualTo((1, 1)));
            }
        }

        [Test]
        public void previous_sibling_symlink_skip_cannot_hide_a_rejected_write()
        {
            var file = Add(1);
            var batch = new TagBatch();
            batch.SkippedSymlinkPaths.Add(file.Path);
            _write = _ => throw ResponseError(500);
            _ebooks.RetagFiles(new RetagFilesCommand(), _files, batch);
            Assert.That(batch.Failed, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void never_connected_write_is_not_attempted_not_failed(bool proxyAuthentication)
        {
            Add(1); Add(2);
            _write = _ => throw (proxyAuthentication
                ? new HttpException(new HttpResponse(new HttpRequest("http://localhost/"), new HttpHeader(), "", HttpStatusCode.ProxyAuthenticationRequired))
                : new HttpRequestException(HttpRequestError.ConnectionError, "refused", new SocketException((int)SocketError.ConnectionRefused)));
            var batch = _ebooks.RetagFiles(new RetagFilesCommand(), _files, new TagBatch());
            Assert.That((batch.Unavailable, batch.Failed, batch.Incomplete), Is.EqualTo((2, 0, 0)));
            Assert.That(_attempts, Is.EqualTo(new[] { 1 }));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void automatic_writes_share_the_library_backoff(bool sync, bool preflight)
        {
            Add(1); Add(2);
            var reads = 0;
            var read = _preview;
            var timeout = new WebException("timeout", new TaskCanceledException(), WebExceptionStatus.Timeout, null);
            _preview = (ids, settings) =>
            {
                reads++;
                if (preflight)
                {
                    throw timeout;
                }
                return read(ids, settings);
            };
            _write = _ => throw timeout;
            foreach (var file in _files)
            {
                if (sync)
                {
                    file.Edition.BookFiles = new List<BookFile> { file };
                    _ebooks.SyncTags(new List<Edition> { file.Edition });
                }
                else
                {
                    _ebooks.WriteTags(file, true);
                }
            }

            Assert.That(reads, Is.EqualTo(1));
            Assert.That(_attempts, Is.EqualTo(preflight ? Array.Empty<int>() : new[] { 1 }));
            Assert.That(_messages.Logs.Count(line => line.StartsWith("Warn:") && line.Contains("is unavailable after")), Is.EqualTo(1));
        }

        [TestCase(400)]
        [TestCase(407)]
        [TestCase(511)]
        [TestCase(501)]
        [TestCase(0)]
        public void non_server_page_failures_do_not_fan_out_into_writes(int status)
        {
            foreach (var id in Enumerable.Range(1, 1600))
            {
                Add(id);
            }
            _preview = (_, _) => throw (status == 0 ? new CalibreException("Local page preparation failed") : ResponseError(status));
            var batch = _ebooks.RetagFiles(new RetagFilesCommand(), _files, new TagBatch());
            Assert.That((batch.Failed, batch.Written, batch.CalibrePages, batch.CalibreIds), Is.EqualTo((status == 0 ? 1600 : 0, 0, 0, 0)));
            Assert.That(_attempts, Is.Empty);
            var warnings = _messages.Logs.Where(line => line.StartsWith("Warn: Unable to read a page")).ToList();
            Assert.That(warnings, Has.Count.EqualTo(status == 0 ? 3 : 0), "Only inconclusive local failures warn per page");
            Assert.That(batch.Unavailable, Is.EqualTo(status == 0 ? 0 : 1600));
            Assert.That(warnings, Has.All.Contains("Local page preparation failed"));
            Assert.That(_messages.Logs, Has.Some.Contains("failed 750 ids").And.None.Contains("read 750 ids")); // absent-literal: The page size is formatted into the log at runtime; failed reads must not be counted as successful.
        }

        [TestCase("missing-root", "1 missing roots")]
        [TestCase("missing-book", "1 missing Calibre books")]
        [TestCase("unlinked-root", "1 roots not linked")]
        [TestCase("invalid-settings", "1 invalid settings")]
        public void automatic_structural_skips_are_visible(string failure, string expected)
        {
            var file = Add(1);
            switch (failure)
            {
                case "missing-root": _roots.Clear(); break;
                case "missing-book": _preview = (_, _) => new List<CalibreBook>(); break;
                case "unlinked-root": _roots[0].CalibreSettings = null; break;
                case "invalid-settings": _roots[0].CalibreSettings.Host = ""; break;
            }
            _ebooks.WriteTags(file, true);
            Assert.That(_attempts, Is.Empty);
            Assert.That(_messages.Logs, Has.Exactly(1).StartsWith("Warn: Calibre tags:").And.Contains(expected));
        }

        [Test]
        public void automatic_success_is_quiet_with_the_calibre_label_from_first_use()
        {
            _ebooks.WriteTags(Add(1), true);
            Assert.That(_messages.Logs, Has.None.Contains("Calibre tags:"));
            Assert.That(_messages.Logs, Has.Some.StartsWith("Debug: Calibre http://localhost:8080/ [first] reported success"));
        }

        [TestCase(false, "HTTP 504 GatewayTimeout")]
        [TestCase(true, "Http request timed out")]
        public void calibre_health_keeps_the_short_reason_instead_of_the_exception_type(bool transport, string expected)
        {
            Add(1);
            _preview = (_, _) => throw (transport ? new WebException("Http request timed out", WebExceptionStatus.Timeout) : ResponseError(504));
            RunFiles();
            var key = _gate.GetKey(_roots[0].CalibreSettings);
            Assert.That(_health.GetStatus(key).LastErrorMessage, Is.EqualTo(expected));
        }

        [Test]
        public void new_page_failure_does_not_blame_the_file_from_an_earlier_run()
        {
            Add(1);
            var key = _gate.GetKey(_roots[0].CalibreSettings);
            _gate.ReportException(key, ResponseError(504), "old.epub");
            _health.GetStatus(key).RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);
            _preview = (_, _) => throw ResponseError(504);
            var command = RunFiles();
            Assert.That(command.CompletionMessage, Does.Not.Contain("Triggered by").And.Not.Contains("old.epub")); // absent-literal: Test filename from a previous run must not be attributed to this failure.
        }

        [TestCase(false)]
        [TestCase(true)]
        public void cancellation_releases_the_recovery_probe_without_an_outage(bool write)
        {
            Add(1);
            var key = _gate.GetKey(_roots[0].CalibreSettings);
            void OpenExpiredBackoff()
            {
                _gate.ReportException(key, ResponseError(504));
                _health.GetStatus(key).RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);
            }
            if (write)
            {
                // A concurrent request fails after the successful read. Production
                // TryBegin must reserve the expired probe before the write runs.
                _afterRead = OpenExpiredBackoff;
                _write = file =>
                {
                    Assert.That(_health.GetStatus(key).ProbeInProgress, Is.True);
                    throw new TaskCanceledException();
                };
            }
            else
            {
                OpenExpiredBackoff();
                _preview = (_, _) => throw new OperationCanceledException();
            }
            Assert.Catch<OperationCanceledException>(() => _ebooks.RetagFiles(new RetagFilesCommand(), _files, new TagBatch()));
            Assert.That(_health.GetStatus(key).ProbeInProgress, Is.False);
            Assert.That(_health.GetStatus(key).ConsecutiveFailures, Is.EqualTo(1));
            Assert.That(_gate.CanBegin(key, out _), Is.True);
        }

        [Test]
        public void zero_window_rate_limit_reports_retry_now()
        {
            Add(1);
            var request = new HttpRequest("http://localhost/");
            _write = _ => throw new TooManyRequestsException(request, new HttpResponse(request, new HttpHeader { ["Retry-After"] = "0" }, "limited", HttpStatusCode.TooManyRequests));
            var command = RunFiles();
            Assert.That(command.CompletionMessage, Does.Contain("unusable Calibre response: 1. Retry now.").And.Not.Contains("retrying after").And.Not.Contains("run Retag again").And.Not.Contains("root folder"));
        }

        [Test]
        public void bare_cancellation_is_not_a_page_failure_or_outage()
        {
            Add(1);
            _preview = (_, _) => throw new OperationCanceledException();
            Assert.Throws<OperationCanceledException>(() => _ebooks.GetRetagPreviewsByBook(1));
            Assert.That(_health.GetStatus(_gate.GetKey(_roots[0].CalibreSettings)).ConsecutiveFailures, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void missing_library_settings_are_structural_skips_for_preview_and_command(bool emptyHost)
        {
            Add(1);
            _roots[0].CalibreSettings = emptyHost ? new CalibreSettings { Host = "" } : null;
            Assert.That(_ebooks.GetRetagPreviewsByBook(1).Single().SkipReason, Is.EqualTo(emptyHost ? RetagSkipReason.InvalidCalibreSettings : RetagSkipReason.RootNotLinkedToCalibre));
            var preview = _ebooks.GetRetagPreviewsByBook(1).Single();
            Assert.That(preview.RootFolderPath, Is.EqualTo(Root));
            Assert.That(preview.CalibreLibrary, Is.Null);
            var command = RunFiles();
            Assert.That(_attempts, Is.Empty);
            Assert.That(_results, Is.Empty);
            Assert.That(_messages.Logs, Has.None.StartsWith("Error:"));
            Assert.That(_messages.Logs, Has.None.Contains("Calibre tags:"));
            Assert.That(command.CompletionMessage, Is.EqualTo(emptyHost ? $"Calibre settings for {Root} are incomplete. Affected files: 1." :
                "Ebooks in a root folder not linked to a Calibre library: 1."));
        }

        [Test]
        public void missing_calibre_book_does_not_remove_other_previews()
        {
            Add(1); Add(2); var read = _preview;
            _preview = (ids, settings) => read(ids.Where(id => id != 1).ToList(), settings);
            var rows = _ebooks.GetRetagPreviewsByBook(1);
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows.Single(x => x.BookFileId == 1).SkipReason, Is.EqualTo(RetagSkipReason.MissingCalibreBook));
            Assert.That(rows.Single(x => x.BookFileId == 2).SkipReason, Is.Null);
        }

        [Test]
        public void accepted_metadata_then_refused_embed_is_incomplete_not_failed()
        {
            Add(1); Add(2);
            _write = _ => throw new CalibreException("saved metadata", new HttpRequestException(HttpRequestError.ConnectionError, "refused", new SocketException((int)SocketError.ConnectionRefused))) { WriteAccepted = true };
            var batch = _ebooks.RetagFiles(new RetagFilesCommand(), _files, new TagBatch());
            Assert.That(batch.Incomplete, Is.EqualTo(1)); Assert.That(batch.Failed, Is.Zero);
            Assert.That(batch.Unavailable, Is.EqualTo(1));
        }

        [Test]
        public void calibre_rate_limit_honors_retry_after_without_counting_server_outage()
        {
            Add(1); Add(2);
            var request = new HttpRequest("http://localhost/");
            var response = new HttpResponse(request, new HttpHeader { ["Retry-After"] = "180" }, "limited", HttpStatusCode.TooManyRequests);
            _write = _ => throw new CalibreException("wrapped", new TooManyRequestsException(request, response));
            var command = RunFiles();
            Assert.That(command.CompletionMessage, Does.Not.Contain("incomplete").And.Contains("unusable Calibre response: 2"));
            var state = _health.GetStatus(_gate.GetKey(_roots[0].CalibreSettings));
            Assert.That(state.IsRateLimited, Is.True); Assert.That(state.ConsecutiveFailures, Is.Zero);
            Assert.That((state.RateLimitedUntil.Value - DateTime.UtcNow).TotalSeconds, Is.InRange(175, 180));
            Assert.That(_attempts, Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void ebook_timer_stops_before_audio_pass()
        {
            Add(1); Add(2, BookMediaType.Audiobook); TagBatch ebook = null;
            var writer = Proxy<IEBookTagService>((_, args) => { ebook = (TagBatch)args[2]; ebook.Written++; return ebook; });
            Service(_ => Assert.That(ebook.Timer.IsRunning, Is.False), writer).Execute(new RetagFilesCommand(1, new() { 1, 2 }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void combined_preview_has_one_title_order(bool author)
        {
            var audio = Proxy<IAudioTagService>((_, _) => new List<RetagBookFilePreview> { new() { Title = "Zulu", Path = "z", BookFileId = 1 }, new() { Title = "Beta", Path = "b", BookFileId = 2 } });
            var ebook = Proxy<IEBookTagService>((_, _) => new List<RetagBookFilePreview> { new() { Title = "Alpha", Path = "a", BookFileId = 3 } });
            var service = new MetadataTagService(audio, ebook, _logger, _localizer, Proxy<ICommandResultReporter>((_, _) => null), _media, _authorService);
            Assert.That((author ? service.GetRetagPreviewsByAuthor(1) : service.GetRetagPreviewsByBook(1)).Select(row => row.BookFileId), Is.EqualTo(new[] { 3, 2, 1 }));
        }

        [Test]
        public void file_pass_failure_never_invents_unattempted_author_counts()
        {
            Add(1); Add(2, BookMediaType.Audiobook);
            var ebook = Proxy<IEBookTagService>((_, _) => throw new IOException("file selection failed"));
            var command = new RetagFilesCommand(1, new() { 1, 2 }); Service(ebooks: ebook).Execute(command);
            var normalized = Regex.Replace(command.CompletionMessage, @"\(\d+ ms\)", "(elapsed)");
            Assert.That(normalized, Is.EqualTo("Audio: written: 1 (elapsed). Ebooks stopped while processing selected files. Counts include work completed before the failure."));
        }

        [Test]
        public void all_missing_files_never_claim_remaining_files_were_processed()
        {
            var command = new RetagFilesCommand(1, new() { 99 }); Service().Execute(command);
            Assert.That(command.CompletionMessage, Is.EqualTo("Selected files no longer in the library: 1. They were not retagged."));
        }

        [Test]
        public void skipped_sibling_symlink_is_counted_once_without_hiding_written_files()
        {
            Add(1); Add(2).CalibreId = 1;
            var proxy = Proxy<ICalibreProxy>((method, args) =>
            {
                if (method.Name == nameof(ICalibreProxy.GetBooks))
                {
                    return _preview((List<int>)args[0], (CalibreSettings)args[1]);
                }
                var skip = (Action<string>)args[4]; skip(Path.Combine(Root, "sibling.azw3"));
                return CalibreTagUpdateResult.Completed;
            });
            var roots = Proxy<IRootFolderService>((method, _) => method.Name == nameof(IRootFolderService.All) ? (object)_roots : _roots[0]);
            var writer = new EBookTagService(_media, roots, null, proxy, _gate, _logger);
            var batch = writer.RetagFiles(new RetagFilesCommand(), _files, new TagBatch());
            Assert.That((batch.Written, batch.SkippedSymlinks, batch.Skipped, batch.Failed), Is.EqualTo((2, 1, 0, 0)));
        }

        [Test]
        public void unavailable_calibre_ids_are_skipped_before_writes()
        {
            Add(1); Add(2); var read = _preview;
            _preview = (ids, settings) => read(ids.Where(id => id != 1).ToList(), settings);
            var command = RunFiles();
            Assert.That(_attempts, Is.EqualTo(new[] { 2 }));
            Assert.That(_results, Is.Empty);
            Assert.That(Regex.Replace(command.CompletionMessage, @"\(\d+ ms\)", "(elapsed)"), Is.EqualTo(
                $"Ebooks: written: 1 (elapsed). Files no longer available in Calibre library {_gate.GetKey(_roots[0].CalibreSettings)}: 1."));
            Assert.That(_messages.Logs, Has.None.StartsWith("Error:"));
        }

        [Test]
        public void missing_root_is_a_structural_skip_in_preview_and_retag()
        {
            Add(1); _roots.Clear();
            Assert.That(_ebooks.GetRetagPreviewsByBook(1).Single().SkipReason, Is.EqualTo(RetagSkipReason.MissingRootFolder));
            Assert.That(RunFiles().CompletionMessage, Is.EqualTo("Files not in any root folder: 1. They were not retagged."));
            Assert.That(_attempts, Is.Empty); Assert.That(_results, Is.Empty);
            Assert.That(_messages.Logs, Has.None.StartsWith("Error:"));
            Assert.That(_messages.Logs, Has.None.Contains("Calibre tags:"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void successful_pages_survive_a_later_timeout(bool retag)
        {
            foreach (var id in Enumerable.Range(1, 1600)) Add(id);
            var read = _preview; var pages = new List<List<int>>();
            _preview = (ids, settings) =>
            {
                pages.Add(ids);
                if (pages.Count == 2)
                {
                    throw new WebException("fixture timeout", new TaskCanceledException(), WebExceptionStatus.Timeout, null);
                }
                return read(ids, settings);
            };
            if (retag)
            {
                var batch = _ebooks.RetagFiles(new RetagFilesCommand(), _files, new TagBatch());
                Assert.That((batch.Written, batch.Unavailable, batch.Incomplete, batch.Failed), Is.EqualTo((750, 850, 0, 0)));
                Assert.That(_attempts, Is.EqualTo(Enumerable.Range(1, 750)));
            }
            else
            {
                var rows = _ebooks.GetRetagPreviewsByAuthor(1);
                Assert.That(rows.Count(x => x.SkipReason == null), Is.EqualTo(750));
                Assert.That(rows.Count(x => x.SkipReason == RetagSkipReason.LibraryUnavailable), Is.EqualTo(850));
            }
            Assert.That(pages.Select(page => page.Count), Is.EqualTo(new[] { 750, 750 }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void backoff_summary_has_a_trigger_only_when_known(bool known)
        {
            Add(1);
            var key = _gate.GetKey(_roots[0].CalibreSettings);
            _gate.ReportException(key, new TimeoutException("fixture outage"), known ? "trigger.epub" : null);
            var deadline = _health.GetStatus(key).RateLimitedUntil.Value.ToString("u");
            Assert.That(RunFiles().CompletionMessage, Is.EqualTo(
                $"Files not attempted while Calibre library {key} is paused after a connection or server error, access refusal, rate limit, or an unusable Calibre response: 1 (retrying after {deadline}). " +
                (known ? "Triggered by trigger.epub. " : "") + "Once the pause ends, run Retag again for the files not attempted."));
        }

        [Test]
        public void summary_has_localized_multiple_outcomes_and_no_remaining_files_claim()
        {
            Add(1); Add(2);
            _write = file =>
            {
                if (file.Id == 2)
                {
                    throw new IOException("fixture failure");
                }
            };
            var command = new RetagFilesCommand(1, new() { 1, 2, 99 }); Service().Execute(command);
            var normalized = Regex.Replace(command.CompletionMessage, @"\(\d+ ms\)", "(elapsed)");
            Assert.That(normalized, Is.EqualTo("Ebooks: written: 1, failed: 1 (elapsed). Selected files no longer in the library: 1. They were not retagged."));
        }

        [Test]
        public void accepted_write_then_rate_limit_is_still_partial_work()
        {
            Add(1);
            var request = new HttpRequest("http://localhost/");
            var response = new HttpResponse(request, new HttpHeader(), "limited", HttpStatusCode.TooManyRequests);
            _write = _ => throw new CalibreException("accepted", new TooManyRequestsException(request, response)) { WriteAccepted = true };
            var batch = _ebooks.RetagFiles(new RetagFilesCommand(), _files, new TagBatch());
            Assert.That((batch.CalibreDetailsIncomplete, batch.Unavailable), Is.EqualTo((1, 0)));
        }

        [TestCase("audiobook", 0, 1)]
        [TestCase("ebook", 1, 0)]
        [TestCase("all", 1, 1)]
        [TestCase(null, 1, 1)]
        [TestCase(" EBOOK ", 1, 0)]
        public void author_command_selects_enum_before_work(string side, int ebooks, int audio)
        {
            Add(1); Add(2, BookMediaType.Audiobook); var audioCalls = 0;
            var command = new RetagAuthorCommand { AuthorIds = new() { 1 }, MediaType = side };
            Service(_ => audioCalls++).Execute(command);
            Assert.That(_attempts.Count, Is.EqualTo(ebooks)); Assert.That(audioCalls, Is.EqualTo(audio));
            Assert.That(_authorSelections, Is.EqualTo(1));
            var normalized = Regex.Replace(command.CompletionMessage, @"\(\d+ ms\)", "(elapsed)");
            Assert.That(normalized, Is.EqualTo(string.Join(" ", new[] {
                ebooks > 0 ? "Ebooks: written: 1 (elapsed)." : null,
                audio > 0 ? "Audio: written: 1 (elapsed)." : null }.Where(x => x != null))));
        }

        [TestCase("audiobook", 0)]
        [TestCase("ebook", 1)]
        [TestCase(" ALL ", 1)]
        public void preview_selects_side_before_calibre(string side, int queries)
        {
            Add(1); var calls = 0; var original = _preview; _preview = (ids, settings) => { calls++; return original(ids, settings); };
            var controller = new RetagBookController(Service());
            controller.GetBooks(1, null, side); controller.GetBooks(null, 1, side);
            Assert.That(calls, Is.EqualTo(queries * 2));
        }

        [Test]
        public void invalid_scope_fails_before_lookup_or_work()
        {
            Assert.Throws<ArgumentException>(() => Service().Execute(new RetagAuthorCommand { MediaType = "audio" }));
            Assert.Throws<BadRequestException>(() => new RetagBookController(Service()).GetBooks(1, null, "audio"));
            Assert.That(_authorSelections, Is.Zero); Assert.That(_attempts, Is.Empty);
        }

        [Test]
        public void selected_files_are_loaded_once_missing_counted_once_and_other_side_runs()
        {
            Add(1); Add(2, BookMediaType.Audiobook); Add(3); var audio = new List<int>();
            var command = new RetagFilesCommand(1, new() { 1, 2, 99, 99 }); Service(file => audio.Add(file.Id)).Execute(command);
            Assert.That(_selections, Is.EqualTo(1)); Assert.That(_attempts, Is.EqualTo(new[] { 1 })); Assert.That(audio, Is.EqualTo(new[] { 2 }));
            Assert.That(command.CompletionMessage, Does.Contain("Selected files no longer in the library: 1")); Assert.That(_results, Is.EqualTo(new[] { CommandResult.Unsuccessful }));
        }

        [Test]
        public void missing_authors_do_not_stop_remaining_authors()
        {
            Add(1, authorId: 1); Add(2, authorId: 3);
            var command = new RetagAuthorCommand { AuthorIds = new() { 1, 2, 3, 2 }, MediaType = "ebook" }; Service().Execute(command);
            Assert.That(_attempts, Is.EqualTo(new[] { 1, 2 })); Assert.That(_authorSelections, Is.EqualTo(1));
            Assert.That(command.CompletionMessage, Does.Contain("Selected authors no longer present: 1"));
        }

        [Test]
        public void partial_author_failure_keeps_writes_and_reports_later_authors_not_unknown_files()
        {
            Add(1, authorId: 1); Add(2, authorId: 2); Add(3, authorId: 3);
            _authorRead = id =>
            {
                if (id == 2)
                {
                    throw new IOException("fixture lookup failed");
                }
            };
            var command = new RetagAuthorCommand { AuthorIds = new() { 1, 2, 3 }, MediaType = "ebook" }; Service().Execute(command);
            Assert.That(command.CompletionMessage, Does.Contain("Ebooks: written: 1"));
            Assert.That(command.CompletionMessage, Does.Contain("Author 2.").And.Contain("Later authors not attempted: 1"));
            Assert.That(_results, Does.Contain(CommandResult.Unsuccessful));
        }

        [Test]
        public void all_not_in_calibre_is_explained_in_preview_and_final_localized_message()
        {
            Add(1).CalibreId = 0;
            var row = _ebooks.GetRetagPreviewsByBook(1).Single(); Assert.That(row.SkipReason, Is.EqualTo(RetagSkipReason.NotInCalibre));
            var command = RunFiles(); Assert.That(_attempts, Is.Empty);
            Assert.That(command.CompletionMessage, Does.Contain("Ebooks not registered in Calibre: 1. This feature uses Calibre to write ebook metadata."));
            Assert.That(command.CompletionMessage, Does.Not.Contain("Audio:")); // absent-literal: Localized pass name and colon are joined at runtime; an ebook-only run must omit it.
            Assert.That(_results, Is.Empty);
            Assert.That(_messages.Logs.Last(), Is.EqualTo("Info: " + command.CompletionMessage));
        }

        [Test]
        public void unlinked_file_is_counted_once_instead_of_silently_lost()
        {
            Add(1).Edition = null; var command = RunFiles();
            Assert.That(command.CompletionMessage, Does.Contain("Files without a linked book or edition: 1"));
            Assert.That(_attempts, Is.Empty);
        }

        [TestCase(CalibreTagUpdateResult.Incomplete, "incomplete: 1")]
        public void calibre_outcome_is_not_reported_as_completed(CalibreTagUpdateResult outcome, string expected)
        {
            Add(1); _outcome = _ => outcome; var command = RunFiles();
            Assert.That(command.CompletionMessage, Does.Contain(expected));
            Assert.That(_results.Count, Is.EqualTo(outcome == CalibreTagUpdateResult.Incomplete ? 1 : 0));
        }

        [TestCase(408)]
        [TestCase(502)]
        [TestCase(503)]
        [TestCase(504)]
        [TestCase(520)]
        [TestCase(521)]
        [TestCase(522)]
        [TestCase(523)]
        [TestCase(524)]
        [TestCase(525)]
        [TestCase(526)]
        public void gateway_opens_shared_backoff_across_commands_without_requeueing(int status)
        {
            Add(1); Add(2); Add(3, root: Other); Add(4, BookMediaType.Audiobook);
            _write = file =>
            {
                if (file.Id == 1)
                {
                    throw new CalibreException("wrapped", ResponseError(status));
                }
            };
            var first = RunFiles(); var second = RunFiles();
            Assert.That(_attempts, Is.EqualTo(new[] { 1, 3, 3 }));
            Assert.That(first.CompletionMessage, Does.Contain("incomplete: 1"));
            Assert.That(first.CompletionMessage, Does.Contain("unusable Calibre response: 1 (retrying after"));
            Assert.That(second.CompletionMessage, Does.Contain("unusable Calibre response: 2 (retrying after"));
            Assert.That(second.CompletionMessage, Does.Contain("Audio: written: 1"));
            Assert.That(second.CompletionMessage, Does.EndWith("Once the pause ends, run Retag again for the files not attempted."));
            Assert.That(_messages.Logs, Has.Some.StartsWith("Warn:").And.Contains("is unavailable after"));
            Assert.That(first.CompletionMessage, Does.Contain("Triggered by " + _files[0].Path));
        }

        [TestCase(404)]
        [TestCase(500)]
        public void ordinary_http_error_is_per_book_not_library(int status)
        {
            Add(1); Add(2); _write = _ => throw ResponseError(status); var command = RunFiles();
            Assert.That(_attempts, Is.EqualTo(new[] { 1, 2 })); Assert.That(command.CompletionMessage, Does.Contain("failed: 2"));
            Assert.That(_health.GetStatus(_gate.GetKey(_roots[0].CalibreSettings)).IsHealthy, Is.True);
        }

        [TestCase("closed-port")]
        [TestCase("dns")]
        [TestCase("timeout")]
        public async Task actual_transport_failure_backs_off_only_affected_library(string failure)
        {
            var error = await HttpTransportErrorsFixture.CaptureTransportFailure(failure);
            Add(1); Add(2); Add(3, root: Other); Add(4, BookMediaType.Audiobook);
            _write = file =>
            {
                if (file.Id == 1)
                {
                    throw error;
                }
            };
            var command = RunFiles();
            Assert.That(_attempts, Is.EqualTo(new[] { 1, 3 }));
            Assert.That(command.CompletionMessage, Does.Contain(failure == "timeout" ? "incomplete: 1" : "unusable Calibre response: 2"));
            Assert.That(command.CompletionMessage, Does.Contain("Audio: written: 1"));
        }

        [Test]
        public void expired_backoff_allows_remaining_books_and_logs_recovery_without_retrying_skipped_books()
        {
            Add(1, authorId: 1); Add(2, root: Other, authorId: 2); Add(3, authorId: 3);
            var settings = _roots[0].CalibreSettings; _gate.ReportException(_gate.GetKey(settings), ResponseError(504));
            _write = file =>
            {
                if (file.Id == 2)
                {
                    _health.GetStatus(_gate.GetKey(settings)).RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);
                }
            };
            var command = new RetagAuthorCommand { AuthorIds = new() { 1, 2, 3 }, MediaType = "ebook" };
            Service().Execute(command);
            Assert.That(_attempts, Is.EqualTo(new[] { 2, 3 })); Assert.That(command.CompletionMessage, Does.Contain("unusable Calibre response: 1 (retrying after"));
            Assert.That(_messages.Logs, Has.Some.Contains("available again"));
        }

        [TestCase("same", 1)]
        [TestCase("library", 2)]
        [TestCase("base-path", 2)]
        [TestCase("scheme", 2)]
        public void library_key_uses_server_uri_and_library_not_root_path(string difference, int attempts)
        {
            _roots[0].CalibreSettings.UrlBase = "/calibre";
            _roots[1].CalibreSettings = new CalibreSettings
            {
                Host = "LOCALHOST",
                Library = difference == "library" ? "other" : "first",
                UrlBase = difference == "base-path" ? "/other" : "/calibre/",
                UseSsl = difference == "scheme"
            };
            Add(1); Add(2, root: Other); _write = _ => throw ResponseError(504); RunFiles();
            Assert.That(_attempts.Count, Is.EqualTo(attempts));
        }

        [Test]
        public void preview_collision_uses_correct_book_in_each_library_and_reports_not_registered()
        {
            Add(1); var second = Add(2, root: Other); second.CalibreId = 1; second.Edition.Id = 202; Add(3).CalibreId = 0;
            var rows = _ebooks.GetRetagPreviewsByBook(1);
            Assert.That(rows.Single(row => row.BookFileId == 1).Changes["Title"].Item1, Is.EqualTo("first"));
            Assert.That(rows.Single(row => row.BookFileId == 2).Changes["Title"].Item1, Is.EqualTo("other"));
            Assert.That(rows.Single(row => row.BookFileId == 3).SkipReason, Is.EqualTo(RetagSkipReason.NotInCalibre));
            Assert.That(rows.Single(row => row.BookFileId == 2).BookId, Is.EqualTo(2));
        }

        [Test]
        public void preview_failure_preserves_healthy_library_and_next_preview_uses_same_backoff()
        {
            Add(1); Add(2, root: Other); var original = _preview; var requests = new List<string>();
            _preview = (ids, settings) =>
            {
                requests.Add(settings.Library);
                if (settings.Library == "first")
                {
                    throw ResponseError(504);
                }
                return original(ids, settings);
            };
            var first = _ebooks.GetRetagPreviewsByBook(1); var second = _ebooks.GetRetagPreviewsByBook(1);
            Assert.That(requests, Is.EqualTo(new[] { "first", "other", "other" }));
            Assert.That(first.Single(row => row.BookFileId == 2).SkipReason, Is.Null);
            Assert.That(second.Single(row => row.BookFileId == 1).SkipReason, Is.EqualTo(RetagSkipReason.LibraryUnavailable));
            Assert.That(second.Single(row => row.BookFileId == 1).RetryAfter, Is.GreaterThan(DateTime.UtcNow));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void real_wrong_side_audio_file_is_never_read(bool preview)
        {
            var directory = Path.Combine(Path.GetTempPath(), "retag-scope-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            try
            {
                var file = Add(1); file.Path = Path.Combine(directory, "wrong.mp3");
                File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "Audio", "silence.mp3"), file.Path);
                var audio = new AudioTagService(null, _media, null, null, null, null, null, _logger, null);
                if (preview)
                {
                    Assert.That(audio.GetRetagPreviewsByBook(1), Is.Empty);
                }
                else
                {
                    audio.RetagFiles(new RetagFilesCommand(1, new() { 1 }), _files, new TagBatch());
                }
                Assert.That(_messages.Logs, Has.None.Contains("Audio tags ").And.None.StartsWith("Warn:").And.None.StartsWith("Error:")); // absent-literal: The test logger formats level prefixes; a successful run must emit neither warning nor error.
            }
            finally { Directory.Delete(directory, true); }
        }

        [Test]
        public void task_cancelled_http_timeout_is_incomplete_and_backs_off()
        {
            Add(1); Add(2); _write = _ => throw new WebException("HTTP deadline", new TaskCanceledException(), WebExceptionStatus.Timeout, null);
            var command = RunFiles();
            Assert.That(command.CompletionMessage, Does.Contain("incomplete: 1"));
            Assert.That(command.CompletionMessage, Does.Contain("unusable Calibre response: 1 (retrying after"));
            Assert.That(_attempts, Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void unsupported_audio_is_counted_and_debug_logged_without_opening_the_file()
        {
            var file = Add(1, BookMediaType.Audiobook); file.Path = Path.Combine(Root, "book.mka");
            var audio = new AudioTagService(null, _media, null, null, null, null, null, _logger, null);
            var batch = audio.RetagFiles(new RetagFilesCommand(1, new() { 1 }), _files, new TagBatch());
            Assert.That(batch.Unsupported, Is.EqualTo(1)); Assert.That(batch.Skipped, Is.Zero);
            Assert.That(batch.Unsuccessful, Is.False);
            Assert.That(_messages.Logs, Has.Some.EqualTo("Debug: Audio tag writing is unsupported for " + file.Path + "; skipping file"));
        }

        [Test]
        public void host_registration_shares_one_health_service_and_keeps_library_outages_separate()
        {
            using var container = new Container(rules => rules.WithNzbDroneRules());
            container.AutoAddServices(new List<string> { "Chaptarr.Core" });
            container.RegisterInstance(_logger, ifAlreadyRegistered: IfAlreadyRegistered.Replace);
            var gate = container.Resolve<ICalibreLibraryGate>();
            var health = container.Resolve<IHttpServiceHealth>();
            var settings = _roots[0].CalibreSettings;
            gate.ReportException(gate.GetKey(settings), ResponseError(504));
            Assert.That(health.GetStatus(gate.GetKey(settings)).IsHealthy, Is.False);
            Assert.That(gate.TryBegin(gate.GetKey(_roots[1].CalibreSettings), out _), Is.True);
            var status = health.GetStatus(gate.GetKey(settings)); status.RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);
            Assert.That(gate.TryBegin(gate.GetKey(settings), out _), Is.True);
            Assert.That(container.Resolve<ICalibreLibraryGate>().TryBegin(gate.GetKey(settings), out _), Is.False, "Only one half-open probe");
            gate.ReportSuccess(gate.GetKey(settings));
            Assert.That(gate.TryBegin(gate.GetKey(settings), out _), Is.True);
        }

        internal static Exception ResponseError(int status)
        {
            var request = new HttpRequest("http://localhost/calibre");
            return new HttpException(new HttpResponse(request,
                new HttpHeader(), "fixture error", (HttpStatusCode)status));
        }
    }
}
