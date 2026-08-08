using System.Collections.Generic;
using DryIoc;
using NzbDrone.Core.Books.Repositories;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Books.Events;

namespace NzbDrone.Core.Books.Services
{
    public static class BookServicesExtensions
    {
        public static IContainer AddFuzzyMatchingServices(this IContainer container)
        {
            // Register FTS repository
            container.Register<IEditionFtsRepository, EditionFtsRepository>(Reuse.Singleton);

            // Register core services
            container.Register<ITagNormalizer, TagNormalizer>(Reuse.Singleton);
            container.Register<IContainmentValidator, ContainmentValidator>(Reuse.Singleton);
            container.Register<IV5MatchingService, V5MatchingService>(Reuse.Singleton);

            return container;
        }

        public static IContainer AddImportServices(this IContainer container)
        {
            // Register staging database
            container.Register<IStagingDbContext, StagingDbContext>(Reuse.Singleton,
                setup: Setup.With(allowDisposableTransient: true));
            
            // Register repositories
            container.Register<IIngestQueueRepository, IngestQueueRepository>(Reuse.Singleton);
            container.Register<IFileTagCacheRepository, FileTagCacheRepository>(Reuse.Singleton);
            
            // Register import services
            container.Register<IImportOrchestrator, ImportOrchestratorV2>(Reuse.Singleton);
            container.Register<IFileMatchingService, FileMatchingService>(Reuse.Singleton);
            container.Register<IBookUnitDestinationService, BookUnitDestinationService>(Reuse.Singleton);
            container.Register<IDiscoveryWorker, NzbDrone.Core.MediaFiles.BookImport.Services.DiscoveryWorker>(Reuse.Singleton);
            container.Register<IAuthorLibraryService, AuthorLibraryService>(Reuse.Singleton);
            container.Register<INarratorLinkService, NarratorLinkService>(Reuse.Singleton);
            container.Register<IBookImportService, BookImportService>(Reuse.Singleton);
            
            // Register staging database initializer
            container.Register<StagingDbInitializer>(Reuse.Singleton);

            // Register event handler explicitly to ensure it is wired.
            // Must handle both canonical author paths and explicit discovered prefixes.
            // Registering via delegate keeps a single shared handler instance across event types.
            container.Register<IngestQueueOnAuthorReadyHandler>(Reuse.Singleton);
            container.RegisterDelegate<IngestQueueOnAuthorReadyHandler, IHandle<AuthorRefreshCompleteEvent>>(h => h, Reuse.Singleton);
            container.RegisterDelegate<IngestQueueOnAuthorReadyHandler, IHandle<PendingAuthorImportSucceededEvent>>(h => h, Reuse.Singleton);
            container.RegisterDelegate<IngestQueueOnAuthorReadyHandler, IHandle<AuthorFolderImportReadyEvent>>(h => h, Reuse.Singleton);
            
            return container;
        }
    }
}
