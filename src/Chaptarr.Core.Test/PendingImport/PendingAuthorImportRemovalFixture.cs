using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Chaptarr.Api.V1.PendingImport;
using Chaptarr.Core.Test.Common;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.SignalR;

namespace Chaptarr.Core.Test.PendingImport
{
    [TestFixture]
    public class PendingAuthorImportRemovalFixture
    {
        private sealed class Commands
        {
            public int Readers;
            public int Scalars;
            public int Writes;
            public void Reset() => Readers = Scalars = Writes = 0;
            public IMainDatabase Wrap(IMainDatabase database) => InterfaceProxy.Proxy<IMainDatabase>((method, args) =>
                method.Name == nameof(IMainDatabase.OpenConnection) ? WrapConnection(database.OpenConnection()) : method.Invoke(database, args));
            private IDbConnection WrapConnection(IDbConnection connection) => InterfaceProxy.Proxy<IDbConnection>((method, args) =>
            {
                var result = method.Invoke(connection, args);
                return result is IDbCommand command ? InterfaceProxy.Proxy<IDbCommand>((commandMethod, commandArgs) =>
                {
                    if (commandMethod.Name == nameof(IDbCommand.ExecuteReader)) Readers++;
                    if (commandMethod.Name == nameof(IDbCommand.ExecuteScalar)) Scalars++;
                    if (commandMethod.Name == nameof(IDbCommand.ExecuteNonQuery)) Writes++;
                    return commandMethod.Invoke(command, commandArgs);
                }) : result;
            });
        }

        private static void Run(bool postgres, Action<IMainDatabase, PendingAuthorImportRepository, PendingAuthorImportService, PendingAuthorImportController, List<object>, Commands> test)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = new List<object>();
                var aggregator = InterfaceProxy.Proxy<IEventAggregator>((method, args) => { events.Add(args[0]); return null; });
                var commands = new Commands();
                var repository = new PendingAuthorImportRepository(commands.Wrap(database), aggregator);
                var authors = InterfaceProxy.Proxy<IAuthorService>((method, _) =>
                    method.Name == nameof(IAuthorService.FindByProviderId) ? null : throw new NotSupportedException(method.Name));
                var service = new PendingAuthorImportService(repository, authors, aggregator, LogManager.GetCurrentClassLogger());
                var controller = new PendingAuthorImportController(
                    InterfaceProxy.Proxy<IBroadcastSignalRMessage>((method, args) => method.ReturnType == typeof(Task) ? Task.CompletedTask : false),
                    service, null, null, null, null);
                test(database, repository, service, controller, events, commands);
            });
        }

        private static PendingAuthorImport Seed(PendingAuthorImportRepository repository, PendingImportStatus status = PendingImportStatus.Pending, string narrations = null, string providerId = "gr:fixture")
        {
            return repository.Insert(new PendingAuthorImport
            {
                ProviderId = providerId,
                ProviderPrefix = "gr",
                AuthorName = "Removal Fixture",
                AudiobookStatus = status,
                EbookStatus = status,
                OverallStatus = status,
                AudiobookBooksToMonitor = "[\"gr:audio\"]",
                EbookBooksToMonitor = "[\"gr:ebook\"]",
                AudiobookBooksToSearch = "[\"gr:audio\"]",
                AudiobookNarrations = narrations,
                Version = 4,
                CreatedAt = DateTime.UtcNow,
                NextAttemptAt = DateTime.UtcNow.AddMinutes(-1),
                RequestedBy = "UserInterface"
            });
        }

        private static void Remove(PendingAuthorImportController controller, int id)
        {
            var method = typeof(PendingAuthorImportController).GetMethod("Cancel");
            var args = method.GetParameters().Length == 1 ? new object[] { id } : new object[] { id, true };
            try { method.Invoke(controller, args); }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        [TestCase(false, 1)]
        [TestCase(false, 25)]
        [TestCase(true, 1)]
        [TestCase(true, 25)]
        public void cleanup_should_retain_refused_partial_and_cancelled_requests_until_manual_delete(bool postgres, int count)
        {
            Run(postgres, (database, repository, service, controller, events, commands) =>
            {
                var retained = new List<int>();
                var old = DateTime.UtcNow.AddDays(-31);
                void Age(PendingAuthorImport row, DateTime? date)
                {
                    row.UpdatedAt = date;
                    repository.SetFields(row, item => item.UpdatedAt);
                }

                for (var index = 0; index < count; index++)
                {
                    var refused = Seed(repository);
                    Assert.That(service.UpdateStatus(refused, PendingImportStatus.Failed, "Synthetic declared refusal", refused.Version), Is.True);
                    Age(refused, old);
                    retained.Add(refused.Id);
                }

                var partial = Seed(repository);
                partial.AudiobookStatus = PendingImportStatus.Succeeded;
                partial.EbookStatus = PendingImportStatus.Failed;
                Assert.That(service.UpdateStatus(partial, PendingImportStatus.PartialSuccess, "Synthetic requested-side refusal", partial.Version), Is.True);
                Age(partial, old);
                retained.Add(partial.Id);
                var cancelled = Seed(repository);
                controller.Cancel(cancelled.Id);
                Age(cancelled, old);
                retained.Add(cancelled.Id);

                PendingAuthorImport Success(DateTime? date)
                {
                    var row = Seed(repository);
                    Assert.That(service.UpdateStatus(row, PendingImportStatus.Succeeded, null, row.Version), Is.True);
                    Age(row, date);
                    return row;
                }

                var expiredSuccess = Success(old);
                var freshSuccess = Success(DateTime.UtcNow.AddDays(-29));
                var undatedSuccess = Success(null);
                var active = Seed(repository);
                Age(active, old);
                active.NextAttemptAt = DateTime.UtcNow.AddDays(1);
                repository.SetFields(active, row => row.NextAttemptAt);
                retained.AddRange(new[] { freshSuccess.Id, undatedSuccess.Id, active.Id });
                commands.Reset();
                new ProcessPendingImportsCommandHandler(service, null, null, null, null, null, LogManager.GetCurrentClassLogger())
                    .Execute(new ProcessPendingImportsCommand());
                Assert.That((commands.Readers, commands.Scalars, commands.Writes), Is.EqualTo((1, 0, 1)));
                TestContext.Progress.WriteLine($"PENDING_RETENTION_BACKGROUND {(postgres ? "PG" : "SQLite")}: refused {count}, partial 1, cancelled 1, due-reader1/scalar0/delete1");
                Assert.That(controller.GetAll().Select(row => row.Id), Is.EquivalentTo(retained));
                Assert.That(repository.Find(expiredSuccess.Id), Is.Null);
                Assert.That(repository.Find(partial.Id).EbookStatus, Is.EqualTo(PendingImportStatus.Failed));
                Assert.That(repository.Find(partial.Id).AudiobookStatus, Is.EqualTo(PendingImportStatus.Succeeded));
                Assert.That(repository.Find(cancelled.Id).AudiobookStatus, Is.EqualTo(PendingImportStatus.NotRequested));
                Assert.That(repository.Find(cancelled.Id).EbookStatus, Is.EqualTo(PendingImportStatus.NotRequested));
                controller.Cancel(partial.Id, true);
                Assert.That(repository.Find(partial.Id), Is.Null);
                Assert.That(repository.Find(retained[0]), Is.Not.Null);
                Assert.That(repository.Find(cancelled.Id), Is.Not.Null);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void cleanup_should_keep_existing_success_age_boundary_null_dates_and_active_states(bool postgres)
        {
            Run(postgres, (database, repository, service, controller, events, commands) =>
            {
                var cutoff = DateTime.UtcNow.AddDays(-30);
                var equal = Seed(repository, PendingImportStatus.Succeeded);
                equal.UpdatedAt = cutoff;
                repository.SetFields(equal, row => row.UpdatedAt);
                var expired = Seed(repository, PendingImportStatus.Succeeded);
                expired.UpdatedAt = cutoff.AddSeconds(-1);
                repository.SetFields(expired, row => row.UpdatedAt);
                var undated = Seed(repository, PendingImportStatus.Succeeded);
                var retained = new List<int> { equal.Id, undated.Id };
                foreach (var state in new[] { PendingImportStatus.Pending, PendingImportStatus.InProgress, PendingImportStatus.Retrying })
                {
                    var row = Seed(repository, state, providerId: "gr:active-" + state);
                    row.UpdatedAt = cutoff.AddDays(-1);
                    repository.SetFields(row, item => item.UpdatedAt);
                    retained.Add(row.Id);
                }

                commands.Reset();
                repository.DeleteOldCompleted(cutoff);
                Assert.That((commands.Readers, commands.Scalars, commands.Writes), Is.EqualTo((0, 0, 1)));
                Assert.That(repository.All().Select(row => row.Id), Is.EquivalentTo(retained));
                Assert.That(repository.Find(expired.Id), Is.Null);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void legacy_cancel_should_retain_failed_row_and_publish_existing_cancellation(bool postgres)
        {
            Run(postgres, (database, repository, service, controller, events, commands) =>
            {
                var item = Seed(repository);
                controller.Cancel(item.Id);
                var saved = repository.Find(item.Id);
                Assert.That(saved, Is.Not.Null);
                Assert.That(saved.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
                Assert.That(saved.AudiobookStatus, Is.EqualTo(PendingImportStatus.NotRequested));
                Assert.That(saved.EbookStatus, Is.EqualTo(PendingImportStatus.NotRequested));
                Assert.That(events.OfType<PendingAuthorImportCancelledEvent>().Count(), Is.EqualTo(1));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void explicit_delete_should_remove_each_saved_state_without_deleting_library_rows(bool postgres)
        {
            Run(postgres, (database, repository, service, controller, events, commands) =>
            {
                var authors = new BasicRepository<Author>(database, InterfaceProxy.Proxy<IEventAggregator>((method, args) => null));
                var books = new BasicRepository<Book>(database, InterfaceProxy.Proxy<IEventAggregator>((method, args) => null));
                var author = authors.Insert(new Author { Name = "Library Fixture", CleanName = "libraryfixture", Path = "/library-fixture", Added = DateTime.UtcNow });
                var audio = books.Insert(new Book { AuthorId = author.Id, Title = "Library Audio", CleanTitle = "libraryaudio", MediaType = BookMediaType.Audiobook, AudiobookMonitored = true, Added = DateTime.UtcNow });
                var ebook = books.Insert(new Book { AuthorId = author.Id, Title = "Library Ebook", CleanTitle = "libraryebook", MediaType = BookMediaType.Ebook, EbookMonitored = true, Added = DateTime.UtcNow });
                Assert.That(books.Find(audio.Id).IsMonitored(), Is.True);
                Assert.That(books.Find(ebook.Id).IsMonitored(), Is.True);
                foreach (var state in new[] { PendingImportStatus.Pending, PendingImportStatus.InProgress, PendingImportStatus.Retrying, PendingImportStatus.Failed, PendingImportStatus.PartialSuccess, PendingImportStatus.Succeeded })
                {
                    var item = Seed(repository, state);
                    var wasActive = item.IsActive();
                    events.Clear();
                    commands.Reset();
                    Remove(controller, item.Id);
                    var counts = (commands.Readers, commands.Scalars, commands.Writes);
                    Assert.That(repository.Find(item.Id), Is.Null, state.ToString());
                    Assert.That(counts, Is.EqualTo((1, 0, wasActive ? 2 : 1)));
                    TestContext.WriteLine($"REMOVE provider={(postgres ? "PostgreSQL" : "SQLite")} state={state} readers={counts.Item1} scalars={counts.Item2} writes={counts.Item3}");
                    Assert.That(service.GetDueForProcessing(), Is.Empty);
                    Assert.That(service.GetAll(), Is.Empty);
                    Assert.That(events.OfType<PendingAuthorImportCancelledEvent>().Count(), Is.EqualTo(wasActive ? 1 : 0));
                }
                Assert.That(authors.Find(author.Id).Name, Is.EqualTo(author.Name));
                Assert.That(books.Find(audio.Id).IsMonitored(), Is.True);
                Assert.That(books.Find(ebook.Id).IsMonitored(), Is.True);
                var method = typeof(PendingAuthorImportController).GetMethod("Cancel");
                Assert.That(method.GetCustomAttribute<HttpDeleteAttribute>()?.Template, Is.EqualTo("{id}"));
                Assert.That(method.GetParameters()[1].GetCustomAttribute<FromQueryAttribute>(), Is.Not.Null);
                Assert.That(method.GetParameters()[1].DefaultValue, Is.False);
                Remove(controller, int.MaxValue);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void cancel_all_should_clear_malformed_saved_narrations_without_parsing_them(bool postgres)
        {
            Run(postgres, (database, repository, service, controller, events, commands) =>
            {
                var item = Seed(repository, narrations: "{not valid json");
                controller.Cancel(item.Id);
                var saved = repository.Find(item.Id);
                Assert.That(saved.OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
                Assert.That(saved.AudiobookNarrations, Is.Null);
                Assert.That(events.OfType<PendingAuthorImportCancelledEvent>().Count(), Is.EqualTo(1));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void explicit_delete_should_also_remove_malformed_saved_narrations(bool postgres)
        {
            Run(postgres, (database, repository, service, controller, events, commands) =>
            {
                var item = Seed(repository, narrations: "{not valid json");
                Remove(controller, item.Id);
                Assert.That(repository.Find(item.Id), Is.Null);
                Assert.That(events.OfType<PendingAuthorImportCancelledEvent>().Count(), Is.EqualTo(1));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void existing_cancel_then_delete_should_fence_stale_retry_status_progress_and_completion(bool postgres)
        {
            Run(postgres, (database, repository, service, controller, events, commands) =>
            {
                var item = Seed(repository);
                var stale = repository.Find(item.Id);
                var previous = repository.Find(item.Id);
                service.Cancel(item.Id);
                service.Delete(item.Id);
                commands.Reset();
                Assert.That(service.SaveProgress(previous, previous), Is.True);
                Assert.That((commands.Readers, commands.Scalars, commands.Writes), Is.EqualTo((0, 0, 0)));
                service.ScheduleRetry(stale, "Stale worker failure");
                Assert.That(service.UpdateStatus(stale, PendingImportStatus.Failed, "Stale worker terminal", stale.Version), Is.False);
                Assert.That(service.IsActiveRequest(stale.Id, stale.Version), Is.False);
                stale.AudiobookBooksToSearch = "[]";
                Assert.That(service.SaveProgress(stale, previous), Is.False);
                Assert.That(service.TryDeleteIfUnchanged(stale), Is.False);
                Assert.That(repository.Find(item.Id), Is.Null);
                Assert.That(repository.GetAll(), Is.Empty);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void fresh_same_provider_request_should_survive_old_worker_after_removal(bool postgres)
        {
            Run(postgres, (database, repository, service, controller, events, commands) =>
            {
                var item = Seed(repository);
                var stale = repository.Find(item.Id);
                service.Cancel(item.Id);
                service.Delete(item.Id);
                var freshId = service.EnqueueAsync(item.ProviderId, new MonitoringConfig
                {
                    CreateAudiobook = true,
                    AudiobookBooksToMonitor = new List<string> { "gr:fresh" },
                    RequestedBy = "UserInterface"
                }, "UserInterface").GetAwaiter().GetResult();
                Assert.That(freshId, Is.Not.EqualTo(item.Id));
                service.ScheduleRetry(stale, "Stale worker failure");
                Assert.That(service.UpdateStatus(stale, PendingImportStatus.Failed, "Stale terminal", stale.Version), Is.False);
                Assert.That(service.TryDeleteIfUnchanged(stale), Is.False);
                var fresh = repository.Find(freshId);
                Assert.That(fresh.IsActive(), Is.True);
                Assert.That(fresh.AudiobookBooksToMonitor, Does.Contain("gr:fresh"));
                Assert.That(repository.GetAll().Select(row => row.Id), Is.EqualTo(new[] { freshId }));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void worker_finishing_after_manual_removal_should_not_recreate_row_or_publish_success(bool postgres)
        {
            Run(postgres, (database, repository, service, controller, events, commands) =>
            {
                var item = Seed(repository);
                item.AudiobookBooksToMonitor = null;
                item.AudiobookBooksToSearch = null;
                item.EbookBooksToMonitor = null;
                repository.Update(item);
                var library = InterfaceProxy.Proxy<IAuthorLibraryService>((method, args) =>
                {
                    Assert.That(method.Name, Is.EqualTo(nameof(IAuthorLibraryService.AddAuthorAsync)));
                    service.Cancel(item.Id);
                    service.Delete(item.Id);
                    return Task.FromResult(new Author { Id = 1, Name = "Already Fetched" });
                });
                var books = InterfaceProxy.Proxy<IBookService>((method, args) => method.Name == nameof(IBookService.GetBooksByAuthor) || method.Name == nameof(IBookService.GetBooks)
                    ? new List<Book>() : throw new NotImplementedException(method.Name));
                var eventsProxy = InterfaceProxy.Proxy<IEventAggregator>((method, args) => { events.Add(args[0]); return null; });
                var handler = new ProcessPendingImportsCommandHandler(service, library, books,
                    InterfaceProxy.Proxy<IProvideBookInfo>((method, args) => throw new NotImplementedException(method.Name)),
                    InterfaceProxy.Proxy<IManageCommandQueue>((method, args) => throw new NotImplementedException(method.Name)), eventsProxy, LogManager.GetCurrentClassLogger());
                handler.Execute(new ProcessPendingImportsCommand());
                Assert.That(repository.Find(item.Id), Is.Null);
                Assert.That(repository.GetAll(), Is.Empty);
                Assert.That(events.OfType<PendingAuthorImportSucceededEvent>(), Is.Empty);
                Assert.That(events.OfType<PendingAuthorImportFailedEvent>(), Is.Empty);
            });
        }
    }
}
