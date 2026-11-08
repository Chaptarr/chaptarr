using System;
using NzbDrone.Core.ThingiProvider;
using FluentValidation;
using FluentValidation.Results;
using NzbDrone.Core.Books;
using Chaptarr.Http;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Api.V1.ImportLists
{
    [V1ApiController]
    public class ImportListController : ProviderControllerBase<ImportListResource, ImportListBulkResource, IImportList, ImportListDefinition>
    {
        public static readonly ImportListResourceMapper ResourceMapper = new();
        private readonly Lazy<IQualityProfileService> _qualityProfileService;
        private readonly Lazy<IMetadataProfileService> _metadataProfileService;

        public ImportListController(IImportListFactory importListFactory,
            Lazy<IQualityProfileService> qualityProfileService,
            Lazy<IMetadataProfileService> metadataProfileService,
            Lazy<IRootFolderService> rootFolderService)
            : base(importListFactory, "importlist", ResourceMapper, new ImportListBulkResourceMapper(qualityProfileService, metadataProfileService, rootFolderService))
        {
            _qualityProfileService = qualityProfileService;
            _metadataProfileService = metadataProfileService;
            SharedValidator.RuleFor(resource => resource.ShouldMonitor)
                .Must(policy => Enum.IsDefined(typeof(ImportListMonitorType), policy))
                .WithMessage("Monitoring must be None, SpecificBook, or EntireAuthor");
            // Disabled providers skip Test, but their saved destinations must still be valid.
            SharedValidator.RuleFor(resource => resource).Custom((resource, context) =>
            {
                if (resource.EnableAutomaticAdd || ProviderConfigTypeCache.Find(resource.ConfigContract) == null) return;
                var definition = ResourceMapper.ToModel(resource);
                if (definition.Id > 0)
                {
                    definition.PreserveMonitoringOptions(importListFactory.Get(definition.Id));
                    resource.MonitoringOptions = definition.MonitoringOptions;
                }
                foreach (var failure in DualMediaImportListActions.ValidateCreationSettings(definition))
                    context.AddFailure(failure);
                foreach (var failure in DualMediaImportListActions.ValidateCreationProfiles(definition, qualityProfileService, metadataProfileService))
                    context.AddFailure(failure);
            });
            SharedValidator.When(resource => resource.MonitoringOptions != null, () =>
            {
                SharedValidator.RuleFor(resource => resource.MonitoringOptions.AudiobookMonitor)
                    .Must(IsSupportedInitialPolicy).WithMessage("Initial monitoring must be All, Missing, Existing, None, SpecificBook, or null");
                SharedValidator.RuleFor(resource => resource.MonitoringOptions.EbookMonitor)
                    .Must(IsSupportedInitialPolicy).WithMessage("Initial monitoring must be All, Missing, Existing, None, SpecificBook, or null");
                SharedValidator.RuleFor(resource => resource.MonitoringOptions.AudiobookMonitorNewItems)
                    .Must(policy => !policy.HasValue || Enum.IsDefined(typeof(NewItemMonitorTypes), policy.Value));
                SharedValidator.RuleFor(resource => resource.MonitoringOptions.EbookMonitorNewItems)
                    .Must(policy => !policy.HasValue || Enum.IsDefined(typeof(NewItemMonitorTypes), policy.Value));
            });
        }

        private static bool IsSupportedInitialPolicy(MonitorTypes? policy) => !policy.HasValue ||
            policy.Value is MonitorTypes.All or MonitorTypes.Missing or MonitorTypes.Existing or MonitorTypes.None or MonitorTypes.SpecificBook;

        protected override void Validate(ImportListDefinition definition, bool includeWarnings, ImportListDefinition existing, bool willRunProviderTest)
        {
            definition.PreserveMonitoringOptions(existing);
            if (!willRunProviderTest)
            {
                VerifyValidationResult(new ValidationResult(DualMediaImportListActions.ValidateCreationSettings(definition)), includeWarnings);
                VerifyValidationResult(new ValidationResult(DualMediaImportListActions.ValidateCreationProfiles(definition, _qualityProfileService, _metadataProfileService)), includeWarnings);
            }
            base.Validate(definition, includeWarnings, existing, willRunProviderTest);
        }
    }
}
