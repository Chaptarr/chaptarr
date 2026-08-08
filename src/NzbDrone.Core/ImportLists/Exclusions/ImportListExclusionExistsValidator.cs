using System;
using FluentValidation.Validators;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.ImportLists.Exclusions
{
    public class ImportListExclusionProviderIdValidator : PropertyValidator
    {
        protected override string GetDefaultMessageTemplate() => $"Invalid provider ID. Expected {ProviderIdValidator.ValidPrefixesDisplay}:id.";

        protected override bool IsValid(PropertyValidatorContext context)
        {
            return TryNormalizeProviderId(context.PropertyValue?.ToString(), out _);
        }

        internal static bool TryNormalizeProviderId(string value, out string normalizedProviderId)
        {
            normalizedProviderId = null;

            if (value.IsNullOrWhiteSpace())
            {
                return false;
            }

            normalizedProviderId = ImportListProviderIdHelper.Normalize(value, null);
            return ProviderIdValidator.TryNormalize(normalizedProviderId, out normalizedProviderId, out _, out _, out _);
        }
    }

    public class ImportListExclusionExistsValidator : PropertyValidator
    {
        private readonly IImportListExclusionService _importListExclusionService;

        public ImportListExclusionExistsValidator(IImportListExclusionService importListExclusionService)
        {
            _importListExclusionService = importListExclusionService;
        }

        protected override string GetDefaultMessageTemplate() => "This exclusion has already been added.";

        protected override bool IsValid(PropertyValidatorContext context)
        {
            if (context.PropertyValue == null)
            {
                return true;
            }

            return !Exists(context.PropertyValue.ToString(), 0, null);
        }

        public bool Exists(string foreignId, int currentId, BookMediaType? mediaType)
        {
            if (!ImportListExclusionProviderIdValidator.TryNormalizeProviderId(foreignId, out var normalizedForeignId))
            {
                return false;
            }

            return _importListExclusionService.All().Exists(existing =>
                existing.Id != currentId &&
                SameProviderId(existing.ForeignId, normalizedForeignId) &&
                MediaScopesOverlap(existing.MediaType, mediaType));
        }

        private static bool SameProviderId(string existingForeignId, string normalizedForeignId)
        {
            if (existingForeignId.IsNullOrWhiteSpace() || normalizedForeignId.IsNullOrWhiteSpace())
            {
                return false;
            }

            return ImportListExclusionProviderIdValidator.TryNormalizeProviderId(existingForeignId, out var normalizedExistingForeignId) &&
                   string.Equals(normalizedExistingForeignId, normalizedForeignId, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MediaScopesOverlap(BookMediaType? existingMediaType, BookMediaType? mediaType)
        {
            return !existingMediaType.HasValue || !mediaType.HasValue || existingMediaType == mediaType;
        }
    }
}
