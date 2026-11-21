using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using Newtonsoft.Json;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.IndexerSearch;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    public partial class PersistedAliasPendingLifecycleFixture
    {
        private static int EnqueueReuse(Harness h, MonitoringConfig config, PendingAuthorImportService service = null)
        {
            return (service ?? h.Pending).EnqueueAsync("hc:9001", config, config.RequestedBy).GetAwaiter().GetResult();
        }

        private static PendingAuthorImport FailReuse(Harness h, int id)
        {
            var item = h.Requests.Get(id);
            Assert.That(h.Pending.UpdateStatus(item, PendingImportStatus.Failed, "Declared request refusal", item.Version), Is.True);
            return h.Requests.Get(id);
        }

        [TestCase(false, "pin")]
        [TestCase(true, "pin")]
        [TestCase(false, "catalog")]
        [TestCase(true, "catalog")]
        public void failed_edition_request_should_reuse_its_row_and_reevaluate_repairs(bool postgres, string repair)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var book = h.AddBook(BookMediaType.Ebook, "hc:1001", "hc:edition:201");
                var originalEdition = h.EditionService.GetEditionsByBook(book.Id).Single();
                Edition requested = repair == "catalog" ? null : AddPendingEdition(h, book, "hc:edition:202");
                if (repair == "pin")
                {
                    originalEdition.ManualAdd = true;
                    h.Editions.Update(originalEdition);
                    book.AnyEditionOk = false;
                    h.Books.Update(book);
                }
                var incoming = TypedPendingConfig(BookMediaType.Ebook, "hc:1001", "hc:edition:202");
                var id = EnqueueReuse(h, incoming);
                var createdAt = h.Requests.Get(id).CreatedAt;
                var timer = Stopwatch.StartNew();
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    h.Run();
                    var failed = h.Requests.Get(id);
                    Assert.That(failed.OverallStatus, Is.EqualTo(PendingImportStatus.Failed), failed.LastError);
                    Assert.That(h.Pending.GetDueForProcessing(), Is.Empty);
                    var version = failed.Version;
                    var queued = EnqueueReuse(h, TypedPendingConfig(BookMediaType.Ebook, "hc:1001", "hc:edition:202"));
                    Assert.Multiple(() =>
                    {
                        Assert.That(queued, Is.EqualTo(id), "The same refused request must not create another history row.");
                        Assert.That(h.Requests.All(), Has.Count.EqualTo(1));
                        Assert.That(h.Requests.Get(id).CreatedAt, Is.EqualTo(createdAt));
                        Assert.That(h.Requests.Get(id).Version, Is.GreaterThan(version));
                        Assert.That(h.Requests.Get(id).IsActive(), Is.True);
                        Assert.That(h.Requests.Get(id).LastError, Is.Null);
                        Assert.That(h.Requests.Get(id).AttemptCount, Is.Zero);
                    });
                }
                h.Run();
                Assert.That(h.Requests.Get(id).OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
                if (repair == "pin")
                {
                    originalEdition.ManualAdd = false;
                    h.Editions.Update(originalEdition);
                    book.AnyEditionOk = true;
                    h.Books.Update(book);
                }
                if (repair == "catalog") requested = AddPendingEdition(h, book, "hc:edition:202");
                var repaired = TypedPendingConfig(BookMediaType.Ebook, "hc:1001", "hc:edition:202");
                repaired.EbookMetadataProfileId = 2;
                repaired.EbookQualityProfileId = 2;
                repaired.EbookRootFolderPath = System.IO.Path.Combine(TestContext.CurrentContext.WorkDirectory, "repaired-root");
                repaired.EbookTags = new HashSet<int> { 9 };
                Assert.That(EnqueueReuse(h, repaired), Is.EqualTo(id));
                var refreshed = h.Requests.Get(id);
                Assert.Multiple(() =>
                {
                    Assert.That(refreshed.EbookMetadataProfileId, Is.EqualTo(2));
                    Assert.That(refreshed.EbookQualityProfileId, Is.EqualTo(2));
                    Assert.That(refreshed.EbookRootFolderPath, Is.EqualTo(repaired.EbookRootFolderPath));
                    Assert.That(JsonConvert.DeserializeObject<List<int>>(refreshed.EbookTags), Is.EqualTo(new[] { 9 }));
                });
                h.Run();
                timer.Stop();
                Assert.Multiple(() =>
                {
                    Assert.That(h.Requests.All(), Is.Empty);
                    Assert.That(h.AuthorFetches, Is.EqualTo(5), "Every fresh attempt still reevaluates the catalog.");
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(h.BookService.GetBook(book.Id)).Id, Is.EqualTo(requested.Id));
                    Assert.That(h.Editions.Get(requested.Id).ManualAdd, Is.True);
                    Assert.That(h.Events.OfType<PendingAuthorImportQueuedEvent>().Count(), Is.EqualTo(5));
                    Assert.That(h.Events.OfType<PendingAuthorImportFailedEvent>().Count(), Is.EqualTo(4));
                    Assert.That(h.Events.OfType<PendingAuthorImportSucceededEvent>().Count(), Is.EqualTo(1));
                });
                TestContext.WriteLine($"Failed request reuse {repair}/{(postgres ? "postgres" : "sqlite")}: 5 evaluations, one reused row, {timer.Elapsed.TotalMilliseconds:F3} ms");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void failed_request_reuse_should_find_the_latest_matching_intent_in_author_history(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var first = TypedPendingConfig(BookMediaType.Ebook, "hc:1001", "hc:edition:202");
                var id = EnqueueReuse(h, first);
                var failed = FailReuse(h, id);
                var different = EnqueueReuse(h, TypedPendingConfig(BookMediaType.Ebook, "hc:1001", "hc:edition:203"));
                Assert.That(different, Is.Not.EqualTo(id));
                FailReuse(h, different);
                var narration = JsonConvert.DeserializeObject<List<RequestedNarration>>(failed.AudiobookNarrations).Single();
                narration.NarratorNames = new List<string> { "Metadata supplied cast" };
                failed.AudiobookNarrations = JsonConvert.SerializeObject(new[] { narration });
                h.Requests.Update(failed);
                first.EbookBooksToMonitor = new List<string> { "HC:1001", "hc:1001" };
                first.AudiobookNarrations[0].EditionProviderId = "HC:EDITION:202";
                Assert.That(EnqueueReuse(h, first), Is.EqualTo(id));
                Assert.That(h.Requests.All(), Has.Count.EqualTo(2));
                Assert.That(h.Pending.GetByProviderId("hc:9001").Id, Is.EqualTo(id), "Provider lookup must report the reactivated request rather than newer failed history.");
                Assert.That(h.Requests.Get(different).OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
            });
        }

        [TestCase(false, "media")]
        [TestCase(true, "media")]
        [TestCase(false, "work")]
        [TestCase(true, "work")]
        [TestCase(false, "search")]
        [TestCase(true, "search")]
        [TestCase(false, "cast")]
        [TestCase(true, "cast")]
        [TestCase(false, "origin")]
        [TestCase(true, "origin")]
        [TestCase(false, "cancelled")]
        [TestCase(true, "cancelled")]
        [TestCase(false, "partial")]
        [TestCase(true, "partial")]
        [TestCase(false, "succeeded")]
        [TestCase(true, "succeeded")]
        public void failed_request_reuse_should_preserve_distinct_demand_and_terminal_boundaries(bool postgres, string difference)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var original = TypedPendingConfig(BookMediaType.Ebook, "hc:1001", "hc:edition:202");
                original.AudiobookNarrations[0].NarratorNames = new List<string> { "First", "Second" };
                var id = EnqueueReuse(h, original);
                if (difference == "cancelled") h.Pending.Cancel(id);
                else FailReuse(h, id);
                var incoming = TypedPendingConfig(BookMediaType.Ebook, "hc:1001", "hc:edition:202");
                incoming.AudiobookNarrations[0].NarratorNames = new List<string> { "Second", "First" };
                switch (difference)
                {
                    case "media": incoming = TypedPendingConfig(BookMediaType.Audiobook, "hc:1001", "hc:edition:202"); break;
                    case "work": incoming.EbookBooksToMonitor.Add("hc:1002"); break;
                    case "search": incoming.AudiobookNarrations[0].Search = false; incoming.EbookBooksToSearch = null; break;
                    case "cast": incoming.AudiobookNarrations[0].NarratorNames.Remove("First"); break;
                    case "origin": incoming.RequestedBy = "UserInterface"; break;
                    case "cancelled": break;
                    case "partial": var partial = h.Requests.Get(id); partial.EbookStatus = PendingImportStatus.PartialSuccess; partial.UpdateOverallStatus(); h.Requests.Update(partial); break;
                    case "succeeded": var succeeded = h.Requests.Get(id); succeeded.EbookStatus = PendingImportStatus.Succeeded; succeeded.UpdateOverallStatus(); h.Requests.Update(succeeded); break;
                }
                var old = JsonConvert.SerializeObject(h.Requests.Get(id));
                Assert.That(EnqueueReuse(h, incoming), Is.Not.EqualTo(id));
                Assert.That(JsonConvert.SerializeObject(h.Requests.Get(id)), Is.EqualTo(old));
                Assert.That(h.Requests.All(), Has.Count.EqualTo(2));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void reactivated_failed_request_should_be_the_current_provider_request(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var olderId = EnqueueReuse(h, TypedPendingConfig(BookMediaType.Ebook, "hc:1001", "hc:edition:202"));
                var older = FailReuse(h, olderId);
                older.CreatedAt = DateTime.UtcNow.AddDays(-2);
                older.UpdatedAt = older.CreatedAt;
                h.Requests.Update(older);
                var newerId = EnqueueReuse(h, TypedPendingConfig(BookMediaType.Ebook, "hc:1001", "hc:edition:203"));
                var newer = FailReuse(h, newerId);
                newer.CreatedAt = DateTime.UtcNow.AddDays(-1);
                newer.UpdatedAt = newer.CreatedAt;
                h.Requests.Update(newer);
                older.EbookStatus = PendingImportStatus.Pending;
                older.UpdateOverallStatus();
                older.UpdatedAt = DateTime.UtcNow;
                older.NextAttemptAt = DateTime.UtcNow;
                Assert.That(h.Requests.TryUpdateRequest(older, older.Version), Is.True);
                Assert.Multiple(() =>
                {
                    Assert.That(h.Requests.GetByProviderId("hc:9001").Id, Is.EqualTo(olderId));
                    Assert.That(h.Pending.GetByProviderId("hc:9001").IsActive(), Is.True);
                    Assert.That(h.Requests.Get(olderId).CreatedAt, Is.LessThan(h.Requests.Get(newerId).CreatedAt));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void failed_request_reuse_should_allow_metadata_enriched_audio_requirement(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var book = h.AddBook(work: "hc:1001", edition: "hc:edition:201");
                var requested = AddPendingEdition(h, book, "hc:edition:202");
                var incoming = TypedPendingConfig(BookMediaType.Audiobook, "hc:1001", "hc:edition:202");
                incoming.AudiobookNarrations[0].RequiresEdition = false;
                var id = EnqueueReuse(h, incoming);
                AuthorLibraryService.PrepareRequestedNarrations(new Author { Books = h.BookService.GetBooksByAuthor(h.Author.Id) }, incoming);
                var retry = h.Requests.Get(id);
                retry.AudiobookNarrations = JsonConvert.SerializeObject(incoming.AudiobookNarrations);
                h.Pending.ScheduleRetry(retry, PendingAuthorImportRetryReason.AuthorNotYetAvailable);
                h.Pending.RetryNow(id);
                h.Author.AudiobookRootFolderPath = null;
                h.Authors.Update(h.Author);
                h.Run();
                var failed = h.Requests.Get(id);
                Assert.That(failed.OverallStatus, Is.EqualTo(PendingImportStatus.Failed), failed.LastError);
                var enriched = JsonConvert.DeserializeObject<List<RequestedNarration>>(failed.AudiobookNarrations).Single();
                Assert.That(enriched.RequiresEdition, Is.True);
                Assert.That(enriched.NarratorNames, Is.Not.Empty);
                incoming = TypedPendingConfig(BookMediaType.Audiobook, "hc:1001", "hc:edition:202");
                incoming.AudiobookNarrations[0].RequiresEdition = false;
                Assert.That(EnqueueReuse(h, incoming), Is.EqualTo(id));
                Assert.That(h.Requests.All(), Has.Count.EqualTo(1));
                var reactivated = JsonConvert.DeserializeObject<List<RequestedNarration>>(h.Requests.Get(id).AudiobookNarrations).Single();
                Assert.That(reactivated.NarratorNames, Is.EquivalentTo(enriched.NarratorNames));
                h.Author.AudiobookRootFolderPath = incoming.AudiobookRootFolderPath;
                h.Authors.Update(h.Author);
                h.Run();
                Assert.Multiple(() =>
                {
                    Assert.That(h.Requests.All(), Is.Empty);
                    Assert.That(BookEditionIdentity.GetMonitoredEdition(h.BookService.GetBook(book.Id)).Id, Is.EqualTo(requested.Id));
                    Assert.That(h.Editions.Get(requested.Id).ManualAdd, Is.True);
                    Assert.That(h.AuthorFetches, Is.EqualTo(2));
                });
            });
        }

        [TestCase(false, "absent")]
        [TestCase(true, "absent")]
        [TestCase(false, "empty")]
        [TestCase(true, "empty")]
        [TestCase(false, "explicit")]
        [TestCase(true, "explicit")]
        public void failed_request_reuse_should_retain_prepared_cast_without_overriding_explicit_names(bool postgres, string incomingCast)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var book = h.AddBook(work: "hc:1001", edition: "hc:edition:201");
                AddPendingEdition(h, book, "hc:edition:202");
                var prepared = TypedPendingConfig(BookMediaType.Audiobook, "hc:1001", "hc:edition:202");
                AuthorLibraryService.PrepareRequestedNarrations(new Author { Books = h.BookService.GetBooksByAuthor(h.Author.Id) }, prepared);
                var savedCast = prepared.AudiobookNarrations.Single().NarratorNames.ToList();
                Assert.That(savedCast, Is.Not.Empty);
                var id = EnqueueReuse(h, prepared);
                FailReuse(h, id);
                var incoming = TypedPendingConfig(BookMediaType.Audiobook, "hc:1001", "hc:edition:202");
                incoming.AudiobookNarrations.Single().NarratorNames = incomingCast switch
                {
                    "empty" => new List<string>(),
                    "explicit" => savedCast.Select(name => name.ToUpperInvariant()).ToList(),
                    _ => null
                };
                var expectedCast = incomingCast == "explicit" ? incoming.AudiobookNarrations.Single().NarratorNames : savedCast;
                Assert.That(EnqueueReuse(h, incoming), Is.EqualTo(id));
                var reactivated = h.Requests.Get(id);
                Assert.Multiple(() =>
                {
                    Assert.That(reactivated.IsActive(), Is.True);
                    Assert.That(h.Requests.All(), Has.Count.EqualTo(1));
                    Assert.That(JsonConvert.DeserializeObject<List<RequestedNarration>>(reactivated.AudiobookNarrations).Single().NarratorNames,
                        Is.EqualTo(expectedCast));
                });
            });
        }

        [TestCase(false, "null-entry")]
        [TestCase(true, "null-entry")]
        [TestCase(false, "mixed-entry")]
        [TestCase(true, "mixed-entry")]
        [TestCase(false, "invalid-json")]
        [TestCase(true, "invalid-json")]
        public void failed_request_reuse_should_skip_malformed_saved_narrations_and_reactivate_healthy_history(bool postgres, string malformed)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var id = EnqueueReuse(h, TypedPendingConfig(BookMediaType.Ebook, "hc:1001", "hc:edition:202"));
                FailReuse(h, id);
                var other = EnqueueReuse(h, TypedPendingConfig(BookMediaType.Ebook, "hc:1001", "hc:edition:203"));
                var failed = FailReuse(h, other);
                failed.AudiobookNarrations = malformed switch
                {
                    "null-entry" => "[null]",
                    "mixed-entry" => JsonConvert.SerializeObject(new RequestedNarration[] { null, TypedPendingEdition(BookMediaType.Ebook, "hc:1001", "hc:edition:203") }),
                    _ => "{invalid-json"
                };
                h.Requests.Update(failed);
                var saved = JsonConvert.SerializeObject(h.Requests.Get(other));
                Assert.That(EnqueueReuse(h, TypedPendingConfig(BookMediaType.Ebook, "hc:1001", "hc:edition:202")), Is.EqualTo(id));
                Assert.Multiple(() =>
                {
                    Assert.That(h.Requests.Get(id).IsActive(), Is.True);
                    Assert.That(h.Requests.All(), Has.Count.EqualTo(2));
                    Assert.That(JsonConvert.SerializeObject(h.Requests.Get(other)), Is.EqualTo(saved));
                });
            });
        }

        [TestCase(false, false, false)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, true, false)]
        [TestCase(false, false, true)]
        [TestCase(true, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, true, true)]
        public void terminal_worker_should_persist_prepared_cast_and_search_progress_only_for_unchanged_request(bool postgres, bool healthyEbook, bool concurrentMerge)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var audio = h.AddBook(work: "hc:1001", edition: "hc:edition:201");
                var requested = AddPendingEdition(h, audio, "hc:edition:202");
                var config = TypedPendingConfig(BookMediaType.Audiobook, "hc:1001", "hc:edition:202");
                config.AudiobookNarrations.Single().RequiresEdition = false;
                Book ebook = null;
                if (healthyEbook)
                {
                    ebook = h.AddBook(BookMediaType.Ebook, "hc:1002", "hc:edition:301");
                    config.CreateEbook = true;
                    config.EbookBooksToMonitor = new List<string> { "hc:1002" };
                    config.EbookBooksToSearch = new List<string> { "hc:1002" };
                    config.AudiobookNarrations.Add(TypedPendingEdition(BookMediaType.Ebook, "hc:1002", "hc:edition:301"));
                }

                var id = EnqueueReuse(h, config);
                h.Author.AudiobookRootFolderPath = null;
                h.Authors.Update(h.Author);
                string mergedSnapshot = null;
                if (concurrentMerge)
                {
                    h.Configure = _ =>
                    {
                        var newer = TypedPendingConfig(BookMediaType.Audiobook, "hc:1003", "hc:edition:303");
                        newer.AudiobookNarrations.Single().NarratorNames = new List<string> { "New requested cast" };
                        Assert.That(EnqueueReuse(h, newer), Is.EqualTo(id));
                        mergedSnapshot = JsonConvert.SerializeObject(h.Requests.Get(id));
                    };
                }

                h.Run();
                var saved = h.Requests.Get(id);
                if (concurrentMerge)
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(saved.IsActive(), Is.True);
                        if (!healthyEbook) Assert.That(JsonConvert.SerializeObject(saved), Is.EqualTo(mergedSnapshot));
                        else
                        {
                            var merged = JsonConvert.DeserializeObject<PendingAuthorImport>(mergedSnapshot);
                            Assert.That(saved.Version, Is.GreaterThan(merged.Version));
                            Assert.That(saved.AudiobookBooksToMonitor, Is.EqualTo(merged.AudiobookBooksToMonitor));
                            Assert.That(saved.AudiobookBooksToSearch, Is.EqualTo(merged.AudiobookBooksToSearch));
                            Assert.That(saved.EbookBooksToMonitor, Is.EqualTo("[]"));
                            Assert.That(saved.EbookBooksToSearch, Is.EqualTo("[]"));
                            Assert.That(saved.LastError, Is.EqualTo(merged.LastError));
                            Assert.That(saved.AttemptCount, Is.EqualTo(merged.AttemptCount));
                            var remaining = JsonConvert.DeserializeObject<List<RequestedNarration>>(saved.AudiobookNarrations);
                            Assert.That(remaining.Select(request => request.WorkProviderId), Is.EquivalentTo(new[] { "hc:1001", "hc:1003" }));
                            Assert.That(remaining.Single(request => request.WorkProviderId == "hc:1003").NarratorNames, Is.EqualTo(new[] { "New requested cast" }));
                            Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(search => search.BookIds), Is.EqualTo(new[] { ebook.Id }));
                        }
                    });
                    return;
                }

                var narrations = JsonConvert.DeserializeObject<List<RequestedNarration>>(saved.AudiobookNarrations);
                Assert.Multiple(() =>
                {
                    Assert.That(saved.OverallStatus, Is.EqualTo(healthyEbook ? PendingImportStatus.PartialSuccess : PendingImportStatus.Failed), saved.LastError);
                    Assert.That(saved.AttemptCount, Is.Zero, "This path must persist its first terminal result without a prior retry.");
                    Assert.That(narrations.Single(request => request.MediaType == BookMediaType.Audiobook).NarratorNames,
                        Is.EquivalentTo(BookEditionIdentity.GetNarratorNames(requested)));
                    Assert.That(narrations.Single(request => request.MediaType == BookMediaType.Audiobook).Search, Is.True);
                    if (healthyEbook)
                    {
                        Assert.That(narrations.Any(request => request.MediaType == BookMediaType.Ebook), Is.False);
                        Assert.That(string.IsNullOrWhiteSpace(saved.EbookBooksToSearch) ||
                            JsonConvert.DeserializeObject<List<string>>(saved.EbookBooksToSearch).Count == 0, Is.True);
                        Assert.That(h.Commands.OfType<BookSearchCommand>().SelectMany(search => search.BookIds), Is.EqualTo(new[] { ebook.Id }));
                    }
                });
            });
        }

        private static object InvokeReuseRepository(MethodInfo method, object[] args, PendingAuthorImportRepository repository)
        {
            try { return method.Invoke(repository, args); }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void concurrent_failed_request_reuse_should_keep_one_active_row_and_merge_new_demand(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var first = TypedPendingConfig(BookMediaType.Ebook, "hc:1001", "hc:edition:202");
                var id = EnqueueReuse(h, first);
                FailReuse(h, id);
                using var barrier = new Barrier(2);
                var activeReads = 0;
                var repository = Proxy<IPendingAuthorImportRepository>((method, args) =>
                {
                    var result = InvokeReuseRepository(method, args, h.Requests);
                    if (method.Name == nameof(IPendingAuthorImportRepository.GetActiveByProviderId) && Interlocked.Increment(ref activeReads) <= 2)
                        Assert.That(barrier.SignalAndWait(TimeSpan.FromSeconds(10)), Is.True);
                    return result;
                });
                var services = Enumerable.Range(0, 2).Select(_ => new PendingAuthorImportService(repository, h.AuthorService,
                    Proxy<NzbDrone.Core.Messaging.Events.IEventAggregator>((_, _) => null), LogManager.GetCurrentClassLogger())).ToArray();
                var second = TypedPendingConfig(BookMediaType.Ebook, "hc:1002", "hc:edition:203");
                var tasks = new[] { Task.Run(() => EnqueueReuse(h, first, services[0])), Task.Run(() => EnqueueReuse(h, second, services[1])) };
                Assert.That(Task.WaitAll(tasks, TimeSpan.FromSeconds(20)), Is.True);
                var active = h.Requests.GetActiveByProviderId("hc:9001");
                Assert.Multiple(() =>
                {
                    Assert.That(tasks.Select(task => task.Result).Distinct().Count(), Is.EqualTo(1));
                    Assert.That(h.Requests.All().Count(row => row.IsActive()), Is.EqualTo(1));
                    Assert.That(JsonConvert.DeserializeObject<List<string>>(active.EbookBooksToMonitor), Is.EquivalentTo(new[] { "hc:1001", "hc:1002" }));
                    Assert.That(JsonConvert.DeserializeObject<List<RequestedNarration>>(active.AudiobookNarrations).Select(request => request.EditionProviderId), Is.EquivalentTo(new[] { "hc:edition:202", "hc:edition:203" }));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void failed_request_reuse_should_reload_after_a_version_conflict_without_overwriting_new_intent(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var config = TypedPendingConfig(BookMediaType.Ebook, "hc:1001", "hc:edition:202");
                var id = EnqueueReuse(h, config);
                FailReuse(h, id);
                var updates = 0;
                var repository = Proxy<IPendingAuthorImportRepository>((method, args) =>
                {
                    if (method.Name == nameof(IPendingAuthorImportRepository.TryUpdateRequest) && ++updates == 1)
                    {
                        var newer = h.Requests.Get(id);
                        newer.EbookBooksToMonitor = "[\"hc:1001\",\"hc:1002\"]";
                        Assert.That(h.Requests.TryUpdateRequest(newer, newer.Version), Is.True);
                    }
                    return InvokeReuseRepository(method, args, h.Requests);
                });
                var service = new PendingAuthorImportService(repository, h.AuthorService, h.EventAggregator, LogManager.GetCurrentClassLogger());
                var queued = EnqueueReuse(h, config, service);
                Assert.Multiple(() =>
                {
                    Assert.That(updates, Is.EqualTo(1));
                    Assert.That(queued, Is.Not.EqualTo(id));
                    Assert.That(h.Requests.Get(id).OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
                    Assert.That(JsonConvert.DeserializeObject<List<string>>(h.Requests.Get(id).EbookBooksToMonitor), Is.EquivalentTo(new[] { "hc:1001", "hc:1002" }));
                    Assert.That(h.Requests.Get(queued).IsActive(), Is.True);
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void failed_request_reuse_should_add_one_author_scoped_read_only_on_the_inactive_path(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var reads = new Dictionary<string, int>();
                var repository = Proxy<IPendingAuthorImportRepository>((method, args) =>
                {
                    reads[method.Name] = reads.GetValueOrDefault(method.Name) + 1;
                    return InvokeReuseRepository(method, args, h.Requests);
                });
                var service = new PendingAuthorImportService(repository, h.AuthorService, h.EventAggregator, LogManager.GetCurrentClassLogger());
                var config = TypedPendingConfig(BookMediaType.Ebook, "hc:1001", "hc:edition:202");
                var id = EnqueueReuse(h, config, service);
                FailReuse(h, id);
                reads.Clear();
                var timer = Stopwatch.StartNew();
                Assert.That(EnqueueReuse(h, config, service), Is.EqualTo(id));
                timer.Stop();
                Assert.That(reads, Is.EquivalentTo(new Dictionary<string, int>
                {
                    [nameof(IPendingAuthorImportRepository.GetActiveByProviderId)] = 1,
                    ["GetFailedByProviderId"] = 1,
                    [nameof(IPendingAuthorImportRepository.TryUpdateRequest)] = 1
                }));
                var reactivated = h.Requests.Get(id);
                var requestedEdition = JsonConvert.DeserializeObject<List<RequestedNarration>>(reactivated.AudiobookNarrations).Single();
                Assert.Multiple(() =>
                {
                    Assert.That(reactivated.EbookStatus, Is.EqualTo(PendingImportStatus.Pending));
                    Assert.That(JsonConvert.DeserializeObject<List<string>>(reactivated.EbookBooksToMonitor), Is.EqualTo(new[] { "hc:1001" }));
                    Assert.That(JsonConvert.DeserializeObject<List<string>>(reactivated.EbookBooksToSearch), Is.EqualTo(new[] { "hc:1001" }));
                    Assert.That(requestedEdition.MediaType, Is.EqualTo(BookMediaType.Ebook));
                    Assert.That(requestedEdition.WorkProviderId, Is.EqualTo("hc:1001"));
                    Assert.That(requestedEdition.EditionProviderId, Is.EqualTo("hc:edition:202"));
                    Assert.That(requestedEdition.NarratorNames, Is.Empty);
                    Assert.That(requestedEdition.RequiresEdition, Is.True);
                    Assert.That(requestedEdition.Search, Is.True);
                });
                var persistedSnapshot = JsonConvert.SerializeObject(reactivated);
                reads.Clear();
                Assert.That(EnqueueReuse(h, config, service), Is.EqualTo(id));
                Assert.That(reads, Is.EquivalentTo(new Dictionary<string, int>
                {
                    [nameof(IPendingAuthorImportRepository.GetActiveByProviderId)] = 1
                }));
                Assert.That(JsonConvert.SerializeObject(h.Requests.Get(id)), Is.EqualTo(persistedSnapshot));
                reads.Clear();
                Assert.That(EnqueueReuse(h, config, service), Is.EqualTo(id));
                Assert.That(reads, Is.EquivalentTo(new Dictionary<string, int> { [nameof(IPendingAuthorImportRepository.GetActiveByProviderId)] = 1 }));
                Assert.That(JsonConvert.SerializeObject(h.Requests.Get(id)), Is.EqualTo(persistedSnapshot));
                TestContext.WriteLine($"Reactivation {(postgres ? "postgres" : "sqlite")}: 2 reads, 1 fenced update, {timer.Elapsed.TotalMilliseconds:F3} ms; identical active repeats: 1 read, 0 writes");
            });
        }
        private static MonitoringConfig NamedPendingConfig(string name) => new()
        {
            CreateAudiobook = true, CreateEbook = false, RequestedBy = "UserInterface", AuthorName = name,
            AudiobookBooksToMonitor = new() { "hc:7000" }, AudiobookBooksToSearch = new() { "hc:7000" }
        };

        [TestCase(false)]
        [TestCase(true)]
        public void pending_request_name_enrichment_preserves_wait_schedule_and_request_identity(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var id = EnqueueReuse(h, NamedPendingConfig(null));
                var waiting = h.Requests.Get(id);
                waiting.NextAttemptAt = DateTime.UtcNow.AddHours(1);
                Assert.That(h.Requests.TryUpdateRequest(waiting, waiting.Version), Is.True);
                var before = h.Requests.Get(id);
                Assert.That(EnqueueReuse(h, NamedPendingConfig(h.Author.Name)), Is.EqualTo(id));
                var saved = h.Requests.Get(id);
                Assert.Multiple(() =>
                {
                    Assert.That(saved.AuthorName, Is.EqualTo(h.Author.Name));
                    Assert.That(saved.ProviderId, Is.EqualTo(before.ProviderId));
                    Assert.That(saved.AudiobookBooksToMonitor, Is.EqualTo(before.AudiobookBooksToMonitor));
                    Assert.That(saved.AudiobookBooksToSearch, Is.EqualTo(before.AudiobookBooksToSearch));
                    Assert.That(saved.NextAttemptAt, Is.EqualTo(before.NextAttemptAt));
                    Assert.That(saved.OverallStatus, Is.EqualTo(before.OverallStatus));
                    Assert.That(h.Requests.All(), Has.Count.EqualTo(1));
                    Assert.That(Chaptarr.Api.V1.PendingImport.PendingAuthorImportResourceMapper.ToResource(saved).AuthorName, Is.EqualTo(h.Author.Name));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void pending_request_known_name_is_not_erased_or_rewritten_by_blank_or_identical_input(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var id = EnqueueReuse(h, NamedPendingConfig(h.Author.Name));
                var before = h.Requests.Get(id);
                foreach (var name in new[] { null, "", " ", h.Author.Name })
                {
                    Assert.That(EnqueueReuse(h, NamedPendingConfig(name)), Is.EqualTo(id));
                    var saved = h.Requests.Get(id);
                    Assert.That(saved.AuthorName, Is.EqualTo(before.AuthorName));
                    Assert.That(saved.Version, Is.EqualTo(before.Version));
                    Assert.That(saved.NextAttemptAt, Is.EqualTo(before.NextAttemptAt));
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void pending_failed_request_revival_keeps_new_known_name_and_existing_source_identity(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var id = EnqueueReuse(h, NamedPendingConfig(null));
                var before = FailReuse(h, id);
                Assert.That(EnqueueReuse(h, NamedPendingConfig(h.Author.Name)), Is.EqualTo(id));
                var saved = h.Requests.Get(id);
                Assert.Multiple(() =>
                {
                    Assert.That(saved.AuthorName, Is.EqualTo(h.Author.Name));
                    Assert.That(saved.ProviderId, Is.EqualTo(before.ProviderId));
                    Assert.That(saved.SourceApplication, Is.EqualTo(before.SourceApplication));
                    Assert.That(saved.CreatedAt, Is.EqualTo(before.CreatedAt));
                    Assert.That(saved.IsActive(), Is.True);
                    Assert.That(h.Requests.All(), Has.Count.EqualTo(1));
                });
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void pending_request_fresh_name_survives_stale_progress_and_status_updates(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var id = EnqueueReuse(h, NamedPendingConfig("Stored Search Name"));
                var stale = h.Requests.Get(id);
                var previous = h.Requests.Get(id);
                Assert.That(EnqueueReuse(h, NamedPendingConfig(h.Author.Name)), Is.EqualTo(id));
                stale.AudiobookBooksToSearch = "[]";
                Assert.That(h.Pending.SaveProgress(stale, previous), Is.True);
                Assert.That(h.Requests.Get(id).AuthorName, Is.EqualTo(h.Author.Name));
                Assert.That(h.Pending.UpdateStatus(stale, PendingImportStatus.Failed, "Stale terminal", stale.Version), Is.False);
                Assert.That(h.Requests.Get(id).AuthorName, Is.EqualTo(h.Author.Name));
                Assert.That(h.Requests.Get(id).IsActive(), Is.True);
            });
        }

    }
}
