using System;
using System.IO;
using Chaptarr.Api.V1.PendingImport;
using Chaptarr.Core.Test.Common;
using Chaptarr.Http.ErrorManagement;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    [NonParallelizable]
    public class PendingAuthorImportRetryConflictFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void retry_of_old_request_should_report_conflict_and_preserve_new_active_request(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = InterfaceProxy.Proxy<IEventAggregator>((method, args) => null);
                var repository = new PendingAuthorImportRepository(database, events);
                var old = repository.Insert(Row(PendingImportStatus.Failed));
                var active = repository.Insert(Row(PendingImportStatus.Pending));
                var service = new PendingAuthorImportService(repository, NoLocalAuthors(), events, LogManager.GetCurrentClassLogger());
                var controller = new PendingAuthorImportController(null, service, null, null, null, null);
                var failure = Assert.Throws<ModelConflictException>(() => controller.RetryNow(old.Id));
                var context = new DefaultHttpContext();
                context.Request.Method = "POST";
                context.Request.Path = $"/api/v1/pendingauthorimport/{old.Id}/retry";
                context.Response.Body = new MemoryStream();
                context.Features.Set<IExceptionHandlerPathFeature>(new ExceptionHandlerFeature { Error = failure, Path = context.Request.Path });
                new ChaptarrErrorPipeline(LogManager.GetCurrentClassLogger()).HandleException(context).GetAwaiter().GetResult();
                Assert.That(context.Response.StatusCode, Is.EqualTo(409));
                Assert.That(repository.Find(old.Id).OverallStatus, Is.EqualTo(PendingImportStatus.Failed));
                Assert.That(repository.Find(active.Id).OverallStatus, Is.EqualTo(PendingImportStatus.Pending));
                Assert.That(repository.Find(old.Id).Version, Is.EqualTo(old.Version));
                Assert.That(repository.Find(active.Id).Version, Is.EqualTo(active.Version));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void retry_without_competing_active_request_should_keep_existing_behavior(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = InterfaceProxy.Proxy<IEventAggregator>((method, args) => null);
                var repository = new PendingAuthorImportRepository(database, events);
                var old = repository.Insert(Row(PendingImportStatus.Failed));
                new PendingAuthorImportService(repository, NoLocalAuthors(), events, LogManager.GetCurrentClassLogger()).RetryNow(old.Id);
                var saved = repository.Find(old.Id);
                Assert.That(saved.OverallStatus, Is.EqualTo(PendingImportStatus.Retrying));
                Assert.That(saved.AudiobookStatus, Is.EqualTo(PendingImportStatus.Failed));
                Assert.That(saved.Version, Is.EqualTo(old.Version + 1));
                Assert.That(saved.MaxAttempts, Is.Zero);
            });
        }

        [Test]
        public void unrelated_repository_failure_should_not_be_mapped_to_active_provider_conflict()
        {
            var failure = new InvalidOperationException("unrelated failure");
            var repository = InterfaceProxy.Proxy<IPendingAuthorImportRepository>((method, args) =>
                method.Name == nameof(IPendingAuthorImportRepository.Find) ? Row(PendingImportStatus.Failed) : throw failure);
            var service = new PendingAuthorImportService(repository, NoLocalAuthors(), null, LogManager.GetCurrentClassLogger());
            Assert.That(Assert.Throws<InvalidOperationException>(() => service.RetryNow(1)), Is.SameAs(failure));
        }

        private static IAuthorService NoLocalAuthors() => InterfaceProxy.Proxy<IAuthorService>((method, _) =>
            method.Name == nameof(IAuthorService.FindByProviderId) ? null : throw new NotSupportedException(method.Name));

        private static PendingAuthorImport Row(PendingImportStatus status) => new()
        {
            ProviderId = "gr:9001", ProviderPrefix = "gr", AuthorName = "Controlled Retry Fixture",
            OverallStatus = status, AudiobookStatus = status, EbookStatus = PendingImportStatus.NotRequested,
            CreatedAt = DateTime.UtcNow, NextAttemptAt = DateTime.UtcNow, MaxAttempts = 5
        };
    }
}
