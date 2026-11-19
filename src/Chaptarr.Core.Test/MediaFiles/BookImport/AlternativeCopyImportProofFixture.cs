using System;
using System.Collections.Generic;
using System.Linq;
using System.Data;
using System.Diagnostics;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.History;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Common.Serializer;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using Chaptarr.Core.Test.Common;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class AlternativeCopyImportProofFixture
    {
        private sealed class Candidates : StagedEditionFtsStub
        {
            public List<EditionFtsMatch> Rows;
            public override bool FtsTableExists() => true;
            public override void RebuildIndex() {}
            protected override List<EditionFtsMatch> SelectEditions(int? authorId, IEnumerable<string> terms, BookMediaType media, int limit) => Rows;
        }

        private static IEnumerable<TestCaseData> Cases()
        {
            foreach (var postgres in new[] { false, true })
            foreach (var media in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            foreach (var scenario in new[] { "identifier-b", "neither", "both-identifiers", "captured-pin", "cloned-other-edition", "unknown-loser", "ambiguous-unit", "opposite-media", "batch-25", "companion-media", "same-media-follow", "twenty-parts", "conflicting-book-tags", "conflicting-author-tags", "excluded-field-follow", "ordinary-follow", "ordinary-untracked", "ordinary-multi", "ordinary-conflicting-book", "ordinary-conflicting-author" })
                yield return new TestCaseData(postgres, media, scenario);
            foreach (var postgres in new[] { false, true })
            foreach (var scenario in new[] { "cast-a", "both-casts", "partial-cast", "conflicting-parts", "equivalent-cast", "raw-equivalent-cast", "unknown-cast", "known-square-note", "known-fullwidth-note", "conflicting-narrator-tags", "chapter-only-follow" })
                yield return new TestCaseData(postgres, BookMediaType.Audiobook, scenario);
        }

        [TestCaseSource(nameof(Cases))]
        public void captured_copy_proof_should_precede_selection_and_preserve_units(bool postgres, BookMediaType media, string scenario)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var logger = LogManager.GetCurrentClassLogger();
                var events = Proxy<IEventAggregator>((method, args) => null);
                var authorRepository = new AuthorRepository(database, events, logger);
                var author = authorRepository.Insert(new Author { Name = "Test Writer", CleanName = "testwriter", SortName = "testwriter", TitleSlug = "test-writer", Path = "/test-writer", Added = DateTime.UtcNow });
                var books = new BookRepository(database, events);
                var editions = new EditionRepository(database, events);
                var aliases = new ProviderAliasSeed(database, events);
                Book Seed(string slug, string asin, string cast)
                {
                    var book = books.Insert(new Book { AuthorId = author.Id, Title = "Paper Lanterns", CleanTitle = "paperlanterns", TitleSlug = slug,
                        MediaType = media, Added = DateTime.UtcNow, HardcoverBookId = "hc:7000", BaseBookId = "hc:7000", AnyEditionOk = false });
                    aliases.SeedAliases("Book", book.Id, "work", new[] { "hc:7000" });
                    var edition = editions.Insert(new Edition { BookId = book.Id, Title = book.Title, ForeignEditionId = "am:" + asin, Asin = asin,
                        Narrator = cast, NarratorNames = string.IsNullOrWhiteSpace(cast) ? new List<string>() : cast.Split(';').Select(name => name.Trim()).ToList(),
                        Format = media == BookMediaType.Audiobook ? "audiobook" : "ebook", ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 3, Monitored = true });
                    book.Editions = new List<Edition> { edition }; return book;
                }
                var a = Seed("copy-a", "B000000001", "First Reader");
                var b = Seed("copy-b", "B000000002", scenario == "unknown-loser" ? null : "Second Reader");
                if (scenario == "unknown-loser" && media == BookMediaType.Ebook) { b.Editions[0].Asin=null; b.Editions[0].ForeignEditionId="local:unresolved"; editions.Update(b.Editions[0]); }
                if (scenario == "raw-equivalent-cast")
                {
                    a.Editions[0].Narrator="First Reader; Third Reader";a.Editions[0].NarratorNames=new List<string>{"First Reader","Third Reader"};editions.Update(a.Editions[0]);
                    b.Editions[0].Narrator="First Reader; Third Reader";b.Editions[0].NarratorNames=new List<string>();editions.Update(b.Editions[0]);
                }
                if (scenario == "partial-cast") { a.Editions[0].Narrator = "First Reader; Third Reader"; a.Editions[0].NarratorNames = new List<string> { "First Reader", "Third Reader" }; editions.Update(a.Editions[0]); }
                if (scenario == "unknown-cast") { a.Editions[0].Narrator = "First Reader + 2 more narrators"; a.Editions[0].NarratorNames = new List<string> { "First Reader" }; editions.Update(a.Editions[0]); }
                if (scenario == "equivalent-cast") { b.Editions[0].Narrator = "First Reader"; b.Editions[0].NarratorNames = new List<string> { "First Reader" }; editions.Update(b.Editions[0]); }
                if (scenario is "known-square-note" or "known-fullwidth-note")
                {
                    a.Editions[0].Title = a.Title + (scenario == "known-fullwidth-note" ? "（朗読 First Reader）" :
                        " [gelesen von First Reader]");
                    editions.Update(a.Editions[0]);
                }
                var captured = new Dictionary<int, int> { [a.Id] = a.Editions[0].Id, [b.Id] = b.Editions[0].Id };
                if (scenario == "captured-pin" || scenario == "cloned-other-edition")
                {
                    b.Editions[0].Monitored = false; editions.Update(b.Editions[0]);
                    editions.Insert(new Edition { BookId = b.Id, Title = b.Title, ForeignEditionId = "am:B000000001", Asin = "B000000001", Narrator = "First Reader",
                        NarratorNames = new List<string> { "First Reader" }, Monitored = true, ReadingFormatId = media == BookMediaType.Audiobook ? 2 : 3 });
                }
                var sql = new List<string>();
                var sqlTexts = new List<string>();
                var observed = Proxy<IMainDatabase>((method,args) =>
                {
                    if (method.Name != nameof(IMainDatabase.OpenConnection)) return method.Invoke(database,args);
                    var connection = database.OpenConnection();
                    return Proxy<IDbConnection>((m,a) =>
                    {
                        var value = m.Invoke(connection,a);
                        return value is IDbCommand command ? Proxy<IDbCommand>((cm,ca) =>
                        {
                            if (cm.Name is nameof(IDbCommand.ExecuteReader) or nameof(IDbCommand.ExecuteScalar) or nameof(IDbCommand.ExecuteNonQuery)) { sql.Add(cm.Name); sqlTexts.Add(command.CommandText); }
                            return cm.Invoke(command,ca);
                        }) : value;
                    });
                });
                var readBooks = new BookRepository(observed,events);
                var readEditions = new EditionRepository(observed,events);
                var readAliases = new ProviderAliasService(new ProviderAliasRepository(observed,events));
                var authorService = new AuthorService(new AuthorRepository(observed,events,logger), events, null, null, null, new CacheManager(), readBooks, null, logger, null, null, null, observed, readAliases);
                var editionService = new EditionService(readEditions, events, logger);
                var actualBooks = new BookService(readBooks, editionService, events, authorService, null, null, new SeriesBookLinkRepository(observed, events), null, logger, providerAliasService: readAliases);
                var batches = 0;
                var bookService = Proxy<IBookService>((method, args) => { if (method.Name == nameof(IBookService.GetExistingBooks)) batches++; return method.Invoke(actualBooks, args); });
                var rows = new[] { a, b }.Select(book => new EditionFtsMatch { EditionId = book.Editions[0].Id, BookId = book.Id, AuthorId = author.Id,
                    AuthorName = author.Name, BookTitle = book.Title, EditionTitle = book.Title, NarratorNames = book.Editions[0].Narrator }).ToList();
                var logged=0;
                var matchLog=Proxy<IMatchingUploadLogger>((method,args)=>{if(method.Name==nameof(IMatchingUploadLogger.LogFinalDecision))logged++;return null;});
                var matcher = new FileMatchingService(matchLog, null, new ContainmentValidator(new TagNormalizer(), logger), null, null, null, null,
                    ConfigServiceTestProxy.Create(), authorService, events, null, new Candidates { Rows = rows }, bookService, editionService, readEditions, null, logger);
                DiscoveredFileWithMetadata File(string suffix, string identifiers, string cast, string extension = null)
                {
                    var tags = new Dictionary<string, List<string>> { ["TITLE"] = new() { a.Title }, ["AUTHOR"] = new() { author.Name } };
                    if (identifiers != null) tags["ASIN"] = new() { identifiers };
                    if (cast != null) tags["NARRATOR"] = new() { cast };
                    return new DiscoveredFileWithMetadata { Path = "/downloads/payload/" + suffix + (extension ?? (media == BookMediaType.Audiobook ? ".m4b" : ".epub")), AllTags = tags };
                }
                var files = new List<DiscoveredFileWithMetadata> { File("unit", "B000000002", null) };
                if (scenario == "neither") files = new() { File("unit", null, null) };
                if (scenario == "both-identifiers") files = new() { File("unit", "B000000001 B000000002", null) };
                if (scenario is "cast-a" or "partial-cast" or "unknown-cast") files = new() { File("unit", null, "First Reader") };
                if (scenario == "both-casts") files = new() { File("unit", null, "First Reader; Second Reader") };
                if (scenario is "unknown-loser" or "equivalent-cast" or "raw-equivalent-cast") files = new() { File("unit", "B000000001", null) };
                if (scenario is "known-square-note" or "known-fullwidth-note")
                {
                    files = new() { File("unit", "B000000001", null) };
                }
                if (scenario == "ambiguous-unit") files = new() { File("unit-a", "B000000001", null), File("unit-unknown", null, null) };
                if (scenario == "conflicting-parts") files = new() { File("part-1", "B000000001", null, ".mp3"), File("part-2", "B000000002", null, ".mp3") };
                if (scenario == "opposite-media") files = new() { File("unit", "B000000002", null, media == BookMediaType.Audiobook ? ".epub" : ".m4b") };
                if (scenario == "companion-media") files.Add(File("unit", "B000000001", "First Reader", media == BookMediaType.Audiobook ? ".epub" : ".m4b"));
                if (scenario is "same-media-follow" or "twenty-parts")
                {
                    var count = scenario == "twenty-parts" ? 20 : 2;
                    files = Enumerable.Range(0, count).Select(index => index == 0 ? File("part-0", "B000000002", null,
                        media == BookMediaType.Audiobook ? ".mp3" : ".epub") : new DiscoveredFileWithMetadata
                        { Path = "/downloads/payload/part-" + index + (media == BookMediaType.Audiobook ? ".mp3" : ".mobi"), AllTags = new() }).ToList();
                }
                if (scenario is "conflicting-book-tags" or "conflicting-author-tags" or "conflicting-narrator-tags" or "excluded-field-follow" or "chapter-only-follow")
                {
                    var following = File("following", null, null);
                    if (scenario == "conflicting-book-tags") following.AllTags["TITLE"] = new() { "An Unrelated Book" };
                    if (scenario == "conflicting-author-tags") following.AllTags["AUTHOR"] = new() { "Another Writer" };
                    if (scenario == "conflicting-narrator-tags") following.AllTags["NARRATOR"] = new() { "Unknown Reader" };
                    if (scenario == "excluded-field-follow") following.AllTags = new() { ["COMMENT"] = new() { "An Unrelated Book; Unknown Reader" } };
                    if (scenario == "chapter-only-follow") following.AllTags = new() { ["TITLE"] = new() { "Chapter 2" }, ["TRACKNUMBER"] = new() { "2" } };
                    files.Add(following);
                }
                if (scenario == "batch-25") files = Enumerable.Range(1,25).Select(index => File("unit-" + index, "B000000002", null)).ToList();
                if (scenario.StartsWith("ordinary-", StringComparison.Ordinal))
                {
                    files.Add(new DiscoveredFileWithMetadata { Path = "/downloads/payload/following" + (media == BookMediaType.Audiobook ? ".m4b" : ".mobi"), AllTags = new() });
                    if (scenario == "ordinary-conflicting-book")
                        files[1].AllTags = new() { ["TITLE"] = new() { "An Unrelated Book" }, ["AUTHOR"] = new() { author.Name } };
                    if (scenario == "ordinary-conflicting-author")
                        files[1].AllTags = new() { ["AUTHOR"] = new() { "Another Writer" } };
                }
                var context = MatchingContextPresets.ForDownloaded(false, captured.Keys);
                context.AlternativeCopies = !scenario.StartsWith("ordinary-", StringComparison.Ordinal); context.TargetEditionIds = captured;
                if (scenario.StartsWith("ordinary-", StringComparison.Ordinal)) context.TargetBookIds = scenario == "ordinary-untracked" ? null :
                    scenario == "ordinary-multi" ? captured.Keys.ToList() : new() { b.Id };
                var timer = Stopwatch.StartNew();
                FileMatchResult result = null;
                try
                {
                result = matcher.MatchFilesToLibraryAsync(files.ToArray(), author.Id, context).GetAwaiter().GetResult();
                if (scenario.StartsWith("ordinary-", StringComparison.Ordinal))
                {
                    if (scenario is "ordinary-conflicting-book" or "ordinary-conflicting-author")
                    {
                        Assert.Multiple(() =>
                        {
                            Assert.That(result.MatchedFiles.Select(match => match.File.Path), Is.EqualTo(new[] { files[0].Path }));
                            Assert.That(result.MatchedFiles.Single().BookId, Is.EqualTo(b.Id));
                            Assert.That(result.MatchedFiles.Single().EditionId, Is.EqualTo(captured[b.Id]));
                            Assert.That(result.UnmatchedFiles.Select(unmatched => unmatched.File.Path), Is.EqualTo(new[] { files[1].Path }));
                            Assert.That(result.MatchedFiles.Any(match => match.File.Path == files[1].Path), Is.False);
                        });
                    }
                    else
                    {
                        Assert.That(result.MatchedFiles, Has.Length.EqualTo(scenario == "ordinary-follow" ? 2 : 1));
                        Assert.That(result.MatchedFiles.All(match => match.BookId == b.Id && match.EditionId == captured[b.Id]), Is.True);
                        Assert.That(result.UnmatchedFiles, Has.Length.EqualTo(scenario == "ordinary-follow" ? 0 : 1));
                    }
                    Assert.That(sql.Count(command => command != nameof(IDbCommand.ExecuteReader)), Is.Zero);
                    return;
                }
                var held = scenario is "neither" or "both-identifiers" or "both-casts" or "partial-cast" or "unknown-cast" or "equivalent-cast" or "raw-equivalent-cast" or "conflicting-parts" or "opposite-media" or "conflicting-book-tags" or "conflicting-author-tags" or "conflicting-narrator-tags";
                if (held) { Assert.That(result.MatchedFiles, Is.Empty); Assert.That(result.UnmatchedFiles, Has.Length.EqualTo(files.Count)); }
                else
                {
                    Assert.That(result.UnmatchedFiles, Is.Empty); Assert.That(result.MatchedFiles, Has.Length.EqualTo(scenario == "companion-media" ? 1 : files.Count));
                    Assert.That(result.MatchedFiles.Select(match => match.BookId), Is.EquivalentTo(Enumerable.Repeat(scenario is "cast-a" or "unknown-loser" or "ambiguous-unit" or "known-square-note" or "known-fullwidth-note" ? a.Id : b.Id, result.MatchedFiles.Length)));
                    Assert.That(result.MatchedFiles.All(match => match.EditionId == captured[match.BookId]), Is.True);
                    var remote = new RemoteBook { Author = author, Books = new() { a,b }, AlternativeCopies = true, TargetEditionIds = captured, AlternativeCopyGrabDate = DateTime.UtcNow };
                    var disk = Proxy<IDiskProvider>((method,args) => method.Name == nameof(IDiskProvider.GetFileSize) ? 1L : (object)DateTime.UtcNow);
                    var importer = new DownloadedBooksImportService(disk,null,matcher,null,null,bookService,authorService,editionService,null,null,null,null,null,events,null,null,logger, queueService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.EmptyQueue(), conversionJobService: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoConversionJobs());
                    var decisionBuilder = typeof(DownloadedBooksImportService).GetMethod("CreateDecisionForMatch", BindingFlags.NonPublic|BindingFlags.Instance);
                    var tagsByPath = files.ToDictionary(file=>file.Path,file=>file.AllTags);
                    var bookCache = new Dictionary<int,Book>(); var authorCache = new Dictionary<int,Author>();
                    var decisions = result.MatchedFiles.Select(match => (ImportDecision<LocalBook>)decisionBuilder.Invoke(importer,
                        new object[] { match,tagsByPath,bookCache,authorCache })).ToList();
                    typeof(DownloadedBooksImportService).GetMethod("EnforceTrackedDownloadTargetBooks", BindingFlags.NonPublic|BindingFlags.Instance).Invoke(importer, new object[] { decisions, remote, new DownloadClientItem() });
                    Assert.That(decisions.All(decision => decision.Approved), Is.True);
                    Assert.That(decisions.All(decision => decision.Item.AlternativeCopyGrabDate == remote.AlternativeCopyGrabDate), Is.True);
                    if (scenario == "unknown-loser") Assert.That(decisions.SelectMany(decision => decision.Item.AlternativeCopyIncompatibleEditionIds), Is.Empty);
                    else Assert.That(decisions.All(decision => decision.Item.AlternativeCopyIncompatibleEditionIds.Count == 1), Is.True);
                }
                timer.Stop();
                Assert.That(batches, Is.EqualTo(1));
                Assert.That(logged, Is.EqualTo(scenario == "companion-media" ? 1 : files.Count));
                if(scenario=="raw-equivalent-cast")
                {
                    Assert.That(editions.Get(captured[b.Id]).NarratorNames,Is.Empty);
                    Assert.That(editions.Get(captured[b.Id]).Narrator,Is.EqualTo("First Reader; Third Reader"));
                }
                // Owner snapshots eliminate the two alias hydration queries.
                Assert.That(sql.Count(command=>command == nameof(IDbCommand.ExecuteReader)), Is.EqualTo(4));
                Assert.That(sql.Count(command=>command != nameof(IDbCommand.ExecuteReader)), Is.Zero);
                if (scenario == "identifier-b")
                {
                    var repository = new HistoryRepository(database,events);
                    var grabbed = DateTime.UtcNow.AddMinutes(-1);
                    var imported = repository.Insert(new EntityHistory { BookId=b.Id,AuthorId=author.Id,EditionId=captured[b.Id],DownloadId="ALT",
                        Date=DateTime.UtcNow,EventType=EntityHistoryEventType.BookFileImported,SourceTitle="Captured alternative",Quality=new QualityModel(media==BookMediaType.Audiobook?Quality.M4B:Quality.EPUB),
                        Data=new Dictionary<string,string> { ["DroppedPath"]="/downloads/converted.m4b",["SourcePaths"]=files.Select(file=>file.Path).ToList().ToJson() } });
                    var history = new HistoryService(repository,logger).FindByDownloadId("alt");
                    Assert.That(history.Single().GetDataValue("SourcePaths"), Is.Not.Null);
                    var tracked = new TrackedDownload { DownloadItem = new DownloadClientItem { DownloadId="alt",Title="Captured copies",FilePaths=files.Select(file=>file.Path).ToList() },
                        RemoteBook = new RemoteBook { Books=new List<Book>{a,b},AlternativeCopies=true,TargetEditionIds=captured,AlternativeCopyGrabDate=grabbed } };
                    var completion = new TrackedDownloadAlreadyImported(logger);
                    Assert.That(completion.IsImported(tracked,history), Is.True);
                    imported.Data["SourcePaths"]="{broken";repository.Update(imported);
                    Assert.That(completion.IsImported(tracked,new HistoryService(repository,logger).FindByDownloadId("alt")), Is.False);
                }
                }
                finally
                {
                    timer.Stop();
                    TestContext.Progress.WriteLine($"C20_ALT_PROOF database={database.DatabaseType} media={media} scenario={scenario} files={files.Count} capturedBookB={b.Id} matched={string.Join(',',result?.MatchedFiles.Select(match=>match.BookId) ?? Enumerable.Empty<int>())} bookHydrationBatches={batches} SQL={sql.Count} readers={sql.Count(command=>command==nameof(IDbCommand.ExecuteReader))} scalars={sql.Count(command=>command==nameof(IDbCommand.ExecuteScalar))} writes={sql.Count(command=>command==nameof(IDbCommand.ExecuteNonQuery))} elapsedMs={timer.Elapsed.TotalMilliseconds:F3}");
                    for(var index=0;index<sqlTexts.Count;index++) TestContext.Progress.WriteLine($"C20_ALT_SQL[{index}] {sql[index]} {sqlTexts[index].Replace('\r',' ').Replace('\n',' ')}");
                }
            });
        }
    }
}
