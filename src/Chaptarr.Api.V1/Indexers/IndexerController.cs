using Chaptarr.Http;
using FluentValidation;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Validation;

namespace Chaptarr.Api.V1.Indexers
{
    [V1ApiController]
    public class IndexerController : ProviderControllerBase<IndexerResource, IndexerBulkResource, IIndexer, IndexerDefinition>
    {
        public static readonly IndexerResourceMapper ResourceMapper = new();
        public static readonly IndexerBulkResourceMapper BulkResourceMapper = new();

        private readonly ICached<MamTestResult> _mamTests;

        public IndexerController(IIndexerFactory indexerFactory, DownloadClientExistsValidator downloadClientExistsValidator, ICacheManager cacheManager)
            : base(indexerFactory, "indexer", ResourceMapper, BulkResourceMapper)
        {
            _mamTests = cacheManager.GetCache<MamTestResult>(typeof(IndexerController));
            SharedValidator.RuleFor(c => c.Priority).InclusiveBetween(1, 50);
            SharedValidator.RuleFor(c => c.DownloadClientId).SetValidator(downloadClientExistsValidator);
        }

        protected override void Test(IndexerDefinition definition, bool includeWarnings)
        {
            if (definition.Settings is not MyAnonaMouseSettings settings)
            {
                base.Test(definition, includeWarnings);
                return;
            }

            var connection = MamConnection.For(definition);
            var key = definition.Id.ToString();
            _mamTests.Remove(key);
            base.Test(definition, includeWarnings);

            _mamTests.Set(key, new MamTestResult(connection, settings));
        }

        protected override void PrepareForSave(IndexerDefinition definition, IndexerDefinition existing)
        {
            if (definition.Settings is not MyAnonaMouseSettings settings)
            {
                return;
            }

            // Quickstart reads the hidden class field, but a form opened before a refresh can post its old value.
            if (existing?.Settings is MyAnonaMouseSettings stored)
            {
                settings.UserClass = stored.UserClass;
            }

            var tested = _mamTests.Find(definition.Id.ToString());
            if (tested?.Covers(MamConnection.For(definition)) == true &&
                tested.Settings.UnsatisfiedStatusRefreshedUtc is { } testedAt &&
                (settings.UnsatisfiedStatusRefreshedUtc is not { } storedAt || testedAt > storedAt))
            {
                settings.CopyAccountStatusFrom(tested.Settings);
            }
        }

        protected override void TestOnSave(IndexerDefinition definition, IndexerDefinition existing, bool includeWarnings)
        {
            var connection = MamConnection.For(definition);
            if (connection == null)
            {
                base.TestOnSave(definition, existing, includeWarnings);
                return;
            }

            if (connection == MamConnection.For(existing) || _mamTests.Find(definition.Id.ToString())?.Covers(connection) == true)
            {
                return;
            }

            base.Test(definition, includeWarnings);
        }

        private sealed record MamTestResult(MamConnection Connection, MyAnonaMouseSettings Settings)
        {
            public bool Covers(MamConnection connection) =>
                connection == Connection || connection == Connection with { MamId = Settings.MamId };
        }
    }
}
