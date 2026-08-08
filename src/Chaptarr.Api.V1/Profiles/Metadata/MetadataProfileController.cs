using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Profiles.Metadata
{
    [V1ApiController]
    public class MetadataProfileController : RestController<MetadataProfileResource>
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private readonly IMetadataProfileService _profileService;
        private readonly IAuthorService _authorService;
        private readonly IManageCommandQueue _commandQueueManager;

        public MetadataProfileController(IMetadataProfileService profileService,
                                       IAuthorService authorService,
                                       IManageCommandQueue commandQueueManager)
        {
            _profileService = profileService;
            _authorService = authorService;
            _commandQueueManager = commandQueueManager;

            SharedValidator.RuleFor(c => c.Name)
                .NotEqual("None").WithMessage("'None' is a reserved profile name")
                .NotEmpty();
            SharedValidator.RuleFor(c => c.ProfileType)
                .Must(x => Enum.IsDefined(typeof(MetadataProfileType), x))
                .WithMessage("Profile type must be General, Audiobook, or Ebook");
            SharedValidator.RuleFor(c => c.MinPopularity).GreaterThanOrEqualTo(0);
            SharedValidator.RuleFor(c => c.MinPages).GreaterThanOrEqualTo(0);
            SharedValidator.RuleFor(c => c.AllowedLanguages)
                .Must(x =>
                {
                    if (string.IsNullOrWhiteSpace(x))
                    {
                        return true;
                    }

                    var languages = x.Trim(',')
                        .Split(',')
                        .Select(y => y.Trim())
                        .Where(y => !string.IsNullOrWhiteSpace(y))
                        .ToList();

                    return languages.All(y =>
                        y.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                        y.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
                        y.CanonicalizeLanguage().IsNotNullOrWhiteSpace());
                })
                .When(x => x.AllowedLanguages.IsNotNullOrWhiteSpace())
                .WithMessage("Unknown languages");
        }

        [RestPostById]
        public ActionResult<MetadataProfileResource> Create([FromBody] MetadataProfileResource resource)
        {
            var model = resource.ToModel();
            model = _profileService.Add(model);
            return Created(model.Id);
        }

        [RestDeleteById]
        public void DeleteProfile(int id)
        {
            _profileService.Delete(id);
        }

        [RestPutById]
        public ActionResult<MetadataProfileResource> Update([FromBody] MetadataProfileResource resource)
        {
            var logger = NLog.LogManager.GetCurrentClassLogger();

            MetadataProfile previousProfile = null;
            try
            {
                if (resource?.Id > 0)
                {
                    previousProfile = _profileService.Get(resource.Id);
                }
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "Failed to load existing metadata profile {0} for change detection", resource?.Id ?? 0);
            }

            var model = resource.ToModel();
            var shouldRefreshAuthors = previousProfile != null && ShouldRefreshAuthorsForProfileFilterChange(previousProfile, model);
            _profileService.Update(model);
            
            // Refresh only when filter-affecting fields change; avoid expensive refreshes for name-only edits.
            // When refreshing, include both per-media profile references and the legacy MetadataProfileId.
            if (shouldRefreshAuthors)
            {
                var authorsUsingProfile = _authorService.GetAuthorIdsByMetadataProfileId(model.Id)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToList();

                if (authorsUsingProfile.Any())
                {
                    logger.Info("Metadata profile '{0}' updated; refreshing {1} authors to apply new filters", model.Name, authorsUsingProfile.Count);

                    var command = new BulkRefreshAuthorCommand(authorsUsingProfile, refreshMetadata: true, rescanFolders: false, trigger: CommandTrigger.Manual, forceRefresh: true);
                    _commandQueueManager.Push(command, trigger: CommandTrigger.Manual);
                }
            }
            
            return Accepted(model.Id);
        }

        protected override MetadataProfileResource GetResourceById(int id)
        {
            return _profileService.Get(id).ToResource();
        }

        [HttpGet]
        public List<MetadataProfileResource> GetAll([FromQuery] string mediaType = null)
        {
            var profiles = _profileService.All();

            var requestedMediaType = MetadataProfileMediaTypeParser.ParseOrNull(mediaType);
            if (requestedMediaType.HasValue)
            {
                var profileType = requestedMediaType.Value.ToProfileType();
                profiles = profiles
                    .Where(p => p.ProfileType == MetadataProfileType.General || p.ProfileType == profileType)
                    .ToList();
            }
            
            return profiles.ToResource();
        }

        [HttpGet("languages")]
        public ActionResult GetAvailableLanguages()
        {
            // Return common languages that users might want to configure
            var commonLanguages = new[]
            {
                new { Name = "English", Code = "eng" },
                new { Name = "Spanish", Code = "spa" },
                new { Name = "French", Code = "fra" },
                new { Name = "German", Code = "deu" },
                new { Name = "Italian", Code = "ita" },
                new { Name = "Portuguese", Code = "por" },
                new { Name = "Russian", Code = "rus" },
                new { Name = "Japanese", Code = "jpn" },
                new { Name = "Chinese", Code = "zho" },
                new { Name = "Korean", Code = "kor" },
                new { Name = "Dutch", Code = "nld" },
                new { Name = "Polish", Code = "pol" },
                new { Name = "Czech", Code = "ces" },
                new { Name = "Swedish", Code = "swe" },
                new { Name = "Norwegian", Code = "nor" },
                new { Name = "Danish", Code = "dan" },
                new { Name = "Finnish", Code = "fin" },
                new { Name = "Hungarian", Code = "hun" },
                new { Name = "Greek", Code = "ell" },
                new { Name = "Turkish", Code = "tur" },
                new { Name = "Arabic", Code = "ara" },
                new { Name = "Hebrew", Code = "heb" },
                new { Name = "Hindi", Code = "hin" },
                new { Name = "Thai", Code = "tha" },
                new { Name = "Vietnamese", Code = "vie" }
            };

            return Ok(new
            {
                languages = commonLanguages,
                note = "You can type language names (e.g., 'English') or ISO codes (e.g., 'eng'). Values are interpreted using Calibre language canonicalization.",
                specialValues = new[]
                {
                    new { Name = "Unknown/No Language", Code = "null" },
                    new { Name = "Unknown/No Language", Code = "unknown" }
                }
            });
        }

        private static bool ShouldRefreshAuthorsForProfileFilterChange(MetadataProfile previous, MetadataProfile current)
        {
            if (previous == null || current == null)
            {
                return true;
            }

            // Name-only edits should not trigger a bulk refresh.
            if (previous.MinPopularity != current.MinPopularity) return true;
            if (previous.MinPages != current.MinPages) return true;

            if (previous.SkipMissingDate != current.SkipMissingDate) return true;
            if (previous.SkipMissingIsbn != current.SkipMissingIsbn) return true;
            if (previous.SkipPartsAndSets != current.SkipPartsAndSets) return true;
            if (previous.SkipSeriesSecondary != current.SkipSeriesSecondary) return true;
            if (previous.SkipMissingIdentifierOmnibus != current.SkipMissingIdentifierOmnibus) return true;
            if (previous.SkipOmnibus != current.SkipOmnibus) return true;
            if (previous.SkipMissingAsin != current.SkipMissingAsin) return true;

            if (!NormalizedAllowedLanguagesEquals(previous.AllowedLanguages, current.AllowedLanguages)) return true;
            if (!NormalizedTokenSetEquals(previous.Ignored, current.Ignored)) return true;

            return false;
        }

        private static bool NormalizedAllowedLanguagesEquals(string a, string b)
        {
            static string Normalize(string raw)
            {
                EditionMetadataProfileFilter.ParseAllowedLanguages(
                    raw,
                    out var languages,
                    out var allowUnknownLanguage,
                    out var configured,
                    out _);

                if (!configured)
                {
                    return string.Empty;
                }

                var tokens = languages
                    .OrderBy(language => language, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (allowUnknownLanguage)
                {
                    tokens.Add("null");
                }

                return string.Join(",", tokens);
            }

            return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
        }

        private static bool NormalizedTokenSetEquals(string a, string b)
        {
            static HashSet<string> Normalize(string raw)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                return raw.Split(',')
                    .Select(x => (x ?? string.Empty).Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            return Normalize(a).SetEquals(Normalize(b));
        }

        private static bool NormalizedTokenSetEquals(List<string> a, List<string> b)
        {
            static HashSet<string> Normalize(List<string> raw)
            {
                if (raw == null || raw.Count == 0)
                {
                    return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                return raw
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            return Normalize(a).SetEquals(Normalize(b));
        }
    }
}
