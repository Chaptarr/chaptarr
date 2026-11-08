using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DryIoc;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Queue;
using NzbDrone.Host;
using NUnit.Framework;
using NzbDrone.Core.Configuration;

namespace Chaptarr.Core.Test.Configuration
{
    [TestFixture]
    public class RequiredServiceDependenciesFixture
    {
        // Explicit baseline, not blanket service exclusions. New optional dependencies fail.
        private static readonly Dictionary<string, string> Exceptions = new()
        {
            ["NzbDrone.Core.MediaFiles.Events.BookFileDeletionContext.replacements"] = "Optional event payload, not an injected service.",
            ["NzbDrone.Core.MediaFiles.Events.BookFileDeletionContext.deletedFiles"] = "Optional deleted-file snapshot, not an injected service.",
            ["NzbDrone.Core.Books.BookTitleSlugIndex.incoming"] = "Optional incoming-book collection, not an injected service.",
            ["NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadRefreshedEvent.reportedDownloadIds"] = "Optional reported-download snapshot; absent means the client did not report one.",
            ["NzbDrone.Core.Backup.MakeDatabaseBackup.connectionStringFactory"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.Backup.BackupService.logDatabase"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.Backup.BackupService.cacheDatabase"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.Books.Commands.ProcessPendingImportsCommandHandler.providerAliases"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.Books.Events.AuthorDeletedEvent.retainedBookFileIds"] = "Optional event/scope payload, not an injected service.",
            ["NzbDrone.Core.Books.Events.AuthorDeletedEvent.retainedBookFileEditionIds"] = "Optional event/scope payload, not an injected service.",
            ["NzbDrone.Core.Books.Events.LibraryIdentityChangedEvent.removedBookIds"] = "Optional event payload: omitted when books are rehomed without deletion; not an injected service.",
            ["NzbDrone.Core.Books.Events.BookDeletedEvent.deletedBooks"] = "Optional event/scope payload, not an injected service.",
            ["NzbDrone.Core.Books.Services.PendingAuthorImportService.providerAliases"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.Books.BookService.editionSelector"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.Books.BookService.providerAliasService"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.Books.Services.AuthorLibraryService.editionSelector"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.Books.Services.AuthorLibraryService.refreshAuthorService"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.Books.Services.AuthorLibraryService.configService"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.Books.RefreshAuthorService.editionSelector"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.Books.RefreshAuthorService.mainDatabase"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.Books.AuthorService.providerAliasService"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.Datastore.DatabaseRestorationService.connectionStringFactory"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.DecisionEngine.Specifications.ReleaseTitleMatchSpecification.configService"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.Download.RetryFailedImportService.conversionJobService"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.Download.TrackedDownloads.TrackedDownloadAlreadyImported.mediaFileService"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.History.HistoryService.downloadHistoryRepository"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.MediaCover.MediaCoverService.authorService"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.MediaFiles.ConversionJobService.historyService"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.MediaFiles.MetadataTagService.fileTagCache"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.MediaFiles.MediaFileService.rootFolderService"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.MediaFiles.MediaFileService.replacementFiles"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.MediaFiles.BookImport.IngestQueueScanScope.exactPaths"] = "Optional event/scope payload, not an injected service.",
            ["NzbDrone.Core.MediaFiles.BookImport.ImportApprovedBooks.conversionTrackingService"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.MediaFiles.BookImport.ImportApprovedBooks.diskProvider"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.MediaFiles.BookImport.ImportApprovedBooks.configService"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.MediaFiles.BookImport.ImportApprovedBooks.containmentValidator"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.MediaFiles.BookImport.ImportApprovedBooks.coverMapper"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.MediaFiles.BookImport.ImportApprovedBooks.customFormatCalculationService"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.MediaFiles.BookImport.ImportApprovedBooks.conversionJobService"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.MediaFiles.BookImport.Manual.ManualImportService.conversionJobService"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.MediaFiles.Events.BookFileDeletedEvent.confirmedAssociatedPaths"] = "Optional event/scope payload, not an injected service.",
            ["NzbDrone.Core.MediaFiles.Events.BookFileDeletedEvent.replacementFiles"] = "Optional event/scope payload, not an injected service.",
            ["NzbDrone.Core.Queue.QueueService.conversionTrackingService"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.Queue.QueueService.diskProvider"] = "Existing optional dependency retained until its callers are migrated.",
            ["NzbDrone.Core.Queue.QueueService.conversionJobService"] = "Existing optional dependency retained until its callers are migrated.",
        };

        [Test]
        public void downloaded_import_resolves_with_the_shared_queue_from_host_registrations()
        {
            using var container = new Container(rules => rules.WithNzbDroneRules());
            container.AutoAddServices(Bootstrap.ASSEMBLIES);
            var types = new[] { typeof(DownloadedBooksImportService), typeof(QueueService) };
            foreach (var dependency in types.SelectMany(type => type.GetConstructors().Single().GetParameters())
                .Select(parameter => parameter.ParameterType).Where(type => type.IsInterface && type != typeof(IQueueService)).Distinct())
                container.RegisterInstance(dependency, DispatchProxy.Create(dependency, typeof(UnusedDependency)), ifAlreadyRegistered: IfAlreadyRegistered.Replace);
            container.RegisterInstance(NLog.LogManager.GetCurrentClassLogger(), ifAlreadyRegistered: IfAlreadyRegistered.Replace);
            var importer = container.Resolve<IDownloadedBooksImportService>();
            Assert.That(importer, Is.TypeOf<DownloadedBooksImportService>());
            var queue = typeof(DownloadedBooksImportService).GetField("_queueService", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(importer);
            Assert.That(queue, Is.TypeOf<QueueService>());
            Assert.That(queue, Is.SameAs(container.Resolve<IQueueService>()));
        }

        public class UnusedDependency : DispatchProxy
        {
            protected override object Invoke(MethodInfo method, object[] args) => throw new AssertionException("Construction must not access external dependencies: " + method.Name);
        }

        [Test]
        public void injected_interface_dependencies_must_not_silently_default_to_null()
        {
            var optional = typeof(ConfigService).Assembly.GetTypes().Where(type => type.IsClass && !type.IsAbstract)
                .SelectMany(type => type.GetConstructors().SelectMany(ctor => ctor.GetParameters())
                    .Where(parameter => parameter.ParameterType.IsInterface && parameter.HasDefaultValue && parameter.DefaultValue == null)
                    .Select(parameter => type.FullName + "." + parameter.Name)).Distinct().ToHashSet();
            Assert.That(optional.Except(Exceptions.Keys), Is.Empty, "Required services must not default to null. Supply them or document a specific justified exception.");
            Assert.That(Exceptions.Keys.Except(optional), Is.Empty, "Remove obsolete dependency exceptions.");
            Assert.That(Exceptions.Values.All(reason => !string.IsNullOrWhiteSpace(reason)), Is.True);
        }
    }
}
