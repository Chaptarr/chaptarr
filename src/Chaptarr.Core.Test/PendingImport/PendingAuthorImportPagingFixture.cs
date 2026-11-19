using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Chaptarr.Api.V1.PendingImport;
using Chaptarr.Core.Test.Common;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.SignalR;

namespace Chaptarr.Core.Test.PendingImport
{
    [TestFixture]
    public class PendingAuthorImportPagingFixture
    {
        private sealed class ReadController : PendingAuthorImportController
        {
            public ReadController(IPendingAuthorImportService service)
                : base(InterfaceProxy.Proxy<IBroadcastSignalRMessage>((method, args) => method.ReturnType == typeof(Task) ? Task.CompletedTask : false),
                    service, null, null, null, null)
            {
            }

            public PendingAuthorImportResource Read(int id) => GetResourceById(id);
        }

        private sealed class Commands
        {
            public int Readers;
            public int Scalars;
            public int Writes;
            public readonly List<string> Sql = new();

            public void Reset()
            {
                Readers = Scalars = Writes = 0;
                Sql.Clear();
            }

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
                    if (commandMethod.Name.StartsWith("Execute", StringComparison.Ordinal)) Sql.Add(command.CommandText);
                    return commandMethod.Invoke(command, commandArgs);
                }) : result;
            });

            public void AssertReads(int readers, int scalars)
            {
                Assert.That(Readers, Is.EqualTo(readers));
                Assert.That(Scalars, Is.EqualTo(scalars));
                Assert.That(Writes, Is.Zero);
            }
        }

        private static void Run(bool postgres, Action<ReadController, PendingAuthorImportService, List<PendingAuthorImport>, Commands> test)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var commands = new Commands();
                var repository = new PendingAuthorImportRepository(commands.Wrap(database), InterfaceProxy.Proxy<IEventAggregator>((method, args) => null));
                var createdAt = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);
                var rows = Enumerable.Range(1, 1000).Select(index => new PendingAuthorImport
                {
                    ProviderId = $"gr:{index:D4}",
                    ProviderPrefix = "gr",
                    AuthorName = $"Fixture Author {index:D4}",
                    AudiobookStatus = PendingImportStatus.Pending,
                    EbookStatus = PendingImportStatus.NotRequested,
                    OverallStatus = PendingImportStatus.Pending,
                    CreatedAt = createdAt.AddSeconds(-index),
                    NextAttemptAt = createdAt,
                    SourceApplication = "NativePagingFixture",
                    RequestedBy = "NativePagingFixture",
                    LastError = "Stored diagnostic: not a UI reason"
                }).ToList();
                repository.InsertMany(rows);
                var authors = InterfaceProxy.Proxy<IAuthorService>((method, _) =>
                    method.Name == nameof(IAuthorService.FindByProviderId) ? null : throw new NotSupportedException(method.Name));
                var service = new PendingAuthorImportService(repository, authors, InterfaceProxy.Proxy<IEventAggregator>((method, args) => null), LogManager.GetCurrentClassLogger());
                commands.Reset();
                test(new ReadController(service), service, rows, commands);
            });
        }

        private static PagingResource<PendingAuthorImportResource> Page(ReadController controller, PagingRequestResource request)
        {
            var method = typeof(PendingAuthorImportController).GetMethod("GetPaged");
            Assert.That(method, Is.Not.Null, "The separate paged route must preserve the legacy list endpoint");
            Assert.That(method.GetCustomAttribute<HttpGetAttribute>()?.Template, Is.EqualTo("paged"));
            try
            {
                return (PagingResource<PendingAuthorImportResource>)method.Invoke(controller, new object[] { request });
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void page_should_return_twenty_of_one_thousand_rows_with_two_queries(bool postgres)
        {
            Run(postgres, (controller, service, rows, commands) =>
            {
                var elapsed = Stopwatch.StartNew();
                var page = Page(controller, new PagingRequestResource { Page = 2, PageSize = 20 });
                elapsed.Stop();
                Assert.That(page.TotalRecords, Is.EqualTo(1000));
                Assert.That(page.Records.Select(row => row.Id), Is.EqualTo(rows.AsEnumerable().Reverse().Skip(20).Take(20).Select(row => row.Id)));
                Assert.That(page.Page, Is.EqualTo(2));
                Assert.That(page.PageSize, Is.EqualTo(20));
                Assert.That(page.SortDirection, Is.EqualTo(SortDirection.Descending));
                commands.AssertReads(1, 1);
                Assert.That(commands.Sql[0], Does.Contain("LIMIT 20 OFFSET 20"));
                TestContext.WriteLine($"PAGE provider={(postgres ? "PostgreSQL" : "SQLite")} rows=1000 returned=20 readers={commands.Readers} scalars={commands.Scalars} writes={commands.Writes} elapsedMs={elapsed.Elapsed.TotalMilliseconds:F3}");
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void all_pages_should_preserve_unique_id_order_without_missing_rows(bool postgres)
        {
            Run(postgres, (controller, service, rows, commands) =>
            {
                var ids = Enumerable.Range(1, 50).SelectMany(page => Page(controller, new PagingRequestResource { Page = page, PageSize = 20 }).Records.Select(row => row.Id)).ToList();
                Assert.That(ids, Is.EqualTo(rows.AsEnumerable().Reverse().Select(row => row.Id)));
                Assert.That(ids.Distinct().Count(), Is.EqualTo(1000));
                commands.AssertReads(50, 50);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ui_sort_keys_should_use_existing_repository_ordering(bool postgres)
        {
            Run(postgres, (controller, service, rows, commands) =>
            {
                foreach (var key in new[] { "providerId", "authorName", "createdAt" })
                {
                    foreach (var direction in new[] { SortDirection.Ascending, SortDirection.Descending })
                    {
                        commands.Reset();
                        var page = Page(controller, new PagingRequestResource { Page = 2, PageSize = 20, SortKey = key, SortDirection = direction });
                        var forward = key != "createdAt";
                        if (direction == SortDirection.Descending) forward = !forward;
                        var expected = forward ? rows.AsEnumerable() : rows.AsEnumerable().Reverse();
                        Assert.That(page.Records.Select(row => row.Id), Is.EqualTo(expected.Skip(20).Take(20).Select(row => row.Id)), key + " " + direction);
                        commands.AssertReads(1, 1);
                    }
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void generic_defaults_validation_and_unknown_sort_fallback_should_be_preserved(bool postgres)
        {
            Run(postgres, (controller, service, rows, commands) =>
            {
                var page = Page(controller, new PagingRequestResource());
                Assert.That(page.Page, Is.EqualTo(1));
                Assert.That(page.PageSize, Is.EqualTo(10));
                Assert.That(page.Records.Select(row => row.Id), Is.EqualTo(rows.AsEnumerable().Reverse().Take(10).Select(row => row.Id)));
                commands.Reset();
                Assert.Throws<BadRequestException>(() => Page(controller, new PagingRequestResource { Page = 0 }));
                Assert.Throws<BadRequestException>(() => Page(controller, new PagingRequestResource { PageSize = 0 }));
                commands.AssertReads(0, 0);
                page = Page(controller, new PagingRequestResource { PageSize = 20, SortKey = "notAColumn" });
                Assert.That(page.Records.Select(row => row.Id), Is.EqualTo(rows.AsEnumerable().Reverse().Take(20).Select(row => row.Id)));
                commands.AssertReads(1, 1);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void legacy_list_should_remain_an_array_with_existing_order_and_fields(bool postgres)
        {
            Run(postgres, (controller, service, rows, commands) =>
            {
                var list = controller.GetAll();
                Assert.That(list.Select(row => row.Id), Is.EqualTo(rows.Select(row => row.Id)));
                Assert.That(list[0].ProviderId, Is.EqualTo(rows[0].ProviderId));
                Assert.That(list[0].OverallStatus, Is.EqualTo("Pending"));
                Assert.That(list[0].LastError, Is.EqualTo(rows[0].LastError));
                Assert.That(JToken.Parse(list.ToJson()), Is.TypeOf<JArray>());
                commands.AssertReads(1, 0);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void by_id_should_query_only_the_selected_row_and_preserve_missing_null(bool postgres)
        {
            Run(postgres, (controller, service, rows, commands) =>
            {
                var row = controller.Read(rows[986].Id);
                Assert.That(row.ProviderId, Is.EqualTo("gr:0987"));
                commands.AssertReads(1, 0);
                Assert.That(commands.Sql.Single(), Does.Contain("WHERE").And.Contain("\"Id\""));
                commands.Reset();
                Assert.That(controller.Read(rows.Last().Id + 1), Is.Null);
                commands.AssertReads(1, 0);
                Assert.That(commands.Sql.Single(), Does.Contain("WHERE"));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void existing_cancel_retry_and_terminal_states_should_round_trip_without_new_labels(bool postgres)
        {
            Run(postgres, (controller, service, rows, commands) =>
            {
                controller.Cancel(rows[0].Id);
                Assert.That(controller.Read(rows[0].Id).OverallStatus, Is.EqualTo("Failed"));
                Assert.That(controller.Read(rows[0].Id).LastError, Is.EqualTo("Cancelled by user"));
                Assert.That(service.UpdateStatus(rows[1], PendingImportStatus.Failed, "Synthetic stored provider diagnostic"), Is.True);
                Assert.That(service.UpdateStatus(rows[2], PendingImportStatus.Succeeded, null), Is.True);
                var cancelled = controller.Read(rows[0].Id);
                var failed = controller.Read(rows[1].Id);
                var succeeded = controller.Read(rows[2].Id);
                Assert.That(failed.OverallStatus, Is.EqualTo("Failed"));
                Assert.That(succeeded.OverallStatus, Is.EqualTo("Succeeded"));
                var directory = Environment.GetEnvironmentVariable("CHAPTARR_PAGING_JSON_DIRECTORY");
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                    var provider = postgres ? "postgres" : "sqlite";
                    File.WriteAllText(Path.Combine(directory, provider + "-states.json"), new { Seed = "Controlled synthetic 1000-row native fixture; cancel and status updates use existing canonical service paths", Cancelled = cancelled, Failed = failed, Succeeded = succeeded }.ToJson());
                    if (typeof(PendingAuthorImportController).GetMethod("GetPaged") != null)
                    {
                        File.WriteAllText(Path.Combine(directory, provider + "-page20.json"), Page(controller, new PagingRequestResource { PageSize = 20 }).ToJson());
                    }
                }

                controller.RetryNow(rows[0].Id);
                var retried = controller.Read(rows[0].Id);
                Assert.That(retried.OverallStatus, Is.EqualTo("Retrying"));
                Assert.That(retried.MaxAttempts, Is.Zero);
                Assert.That(typeof(PendingAuthorImportController).GetMethod("Cancel").GetCustomAttribute<HttpDeleteAttribute>()?.Template, Is.EqualTo("{id}"));
                Assert.That(typeof(PendingAuthorImportController).GetMethod("RetryNow").GetCustomAttribute<HttpPostAttribute>()?.Template, Is.EqualTo("{id}/retry"));
            });
        }
    }
}
