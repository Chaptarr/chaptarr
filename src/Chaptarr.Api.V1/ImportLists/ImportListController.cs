using System;
using Chaptarr.Http;
using FluentValidation;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace Chaptarr.Api.V1.ImportLists
{
    [V1ApiController]
    public class ImportListController : ProviderControllerBase<ImportListResource, ImportListBulkResource, IImportList, ImportListDefinition>
    {
        public static readonly ImportListResourceMapper ResourceMapper = new();
        public static readonly ImportListBulkResourceMapper BulkResourceMapper = new();

        private readonly IImportListFactory _importListFactory;

        public ImportListController(IImportListFactory importListFactory,
                                    QualityProfileExistsValidator qualityProfileExistsValidator,
                                    MetadataProfileExistsValidator metadataProfileExistsValidator)
            : base(importListFactory, "importlist", ResourceMapper, BulkResourceMapper)
        {
            _importListFactory = importListFactory;

            var requiresGenericAddedAuthorSettings = (ImportListResource r) =>
                r?.Implementation != "HardcoverLibraryImportList" &&
                r?.Implementation != "GoodreadsBookshelf" &&
                r?.Implementation != "GoodreadsListImportList" &&
                r?.Implementation != "GoodreadsSeriesImportList";

            SharedValidator.When(requiresGenericAddedAuthorSettings, () =>
            {
                Http.Validation.RuleBuilderExtensions.ValidId(SharedValidator.RuleFor(s => s.QualityProfileId));
                Http.Validation.RuleBuilderExtensions.ValidId(SharedValidator.RuleFor(s => s.MetadataProfileId));

                SharedValidator.RuleFor(c => c.RootFolderPath).IsValidPath();
                SharedValidator.RuleFor(c => c.QualityProfileId).SetValidator(qualityProfileExistsValidator);
                SharedValidator.RuleFor(c => c.MetadataProfileId).SetValidator(metadataProfileExistsValidator);
            });

            // MinRefreshInterval is a fixed, per-list-type constant (see each IImportList implementation)
            // and is intentionally excluded from persistence (TableMapping ignores it). Prior to this
            // check, a PUT changing it was silently accepted (202) with the change discarded - the
            // response goes on to echo the provider's value, not the one that was requested. Reject a
            // real change explicitly instead so the client gets a clear error. TimeSpan.Zero (the value
            // of an omitted field, since MinRefreshInterval is a non-nullable TimeSpan) is always allowed
            // through - no provider uses zero, so this can't mask an actual attempt to zero it out, and it
            // keeps clients that only send the fields they mean to change from getting a spurious 400.
            PutValidator.RuleFor(c => c.MinRefreshInterval)
                .Must((resource, value) => value == default || IsUnchangedOrUnknownList(resource.Id, value))
                .WithMessage(resource => $"minRefreshInterval is fixed by the list type and cannot be changed (current value: {GetProviderMinRefreshInterval(resource.Id)})");
        }

        private bool IsUnchangedOrUnknownList(int id, TimeSpan value)
        {
            var providerValue = GetProviderMinRefreshInterval(id);

            // An unknown id is left for GetDefinition/_providerFactory.Get to reject with its usual 404
            // rather than surfacing as a validation error here.
            return providerValue == null || providerValue == value;
        }

        private TimeSpan? GetProviderMinRefreshInterval(int id)
        {
            var existing = _importListFactory.Find(id);
            if (existing == null)
            {
                return null;
            }

            _importListFactory.SetProviderCharacteristics(existing);

            return existing.MinRefreshInterval;
        }
    }
}
