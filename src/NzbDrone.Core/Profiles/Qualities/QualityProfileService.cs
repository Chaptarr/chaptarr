using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.CustomFormats.Events;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Profiles.Qualities
{
    public interface IQualityProfileService
    {
        QualityProfile Add(QualityProfile profile);
        void Update(QualityProfile profile);
        void Delete(int id);
        List<QualityProfile> All();
        List<QualityProfile> GetByType(ProfileType type);
        QualityProfile Get(int id);
        bool Exists(int id);
        QualityProfile GetDefaultProfile(string name, Quality cutoff = null, params Quality[] allowed);
    }

    public class QualityProfileService : IQualityProfileService,
                                         IHandle<ApplicationStartedEvent>,
                                         IHandle<CustomFormatAddedEvent>,
                                         IHandle<CustomFormatUpdatedEvent>,
                                         IHandle<CustomFormatDeletedEvent>
    {
        private readonly IProfileRepository _profileRepository;
        private readonly IAuthorService _authorService;
        private readonly IImportListFactory _importListFactory;
        private readonly ICustomFormatService _formatService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IQualityDefinitionService _qualityDefinitionService;
        private readonly Logger _logger;

        public QualityProfileService(IProfileRepository profileRepository,
                                     IAuthorService authorService,
                                     IImportListFactory importListFactory,
                                     ICustomFormatService formatService,
                                     IRootFolderService rootFolderService,
                                     IQualityDefinitionService qualityDefinitionService,
                                     Logger logger)
        {
            _profileRepository = profileRepository;
            _authorService = authorService;
            _importListFactory = importListFactory;
            _rootFolderService = rootFolderService;
            _formatService = formatService;
            _qualityDefinitionService = qualityDefinitionService;
            _logger = logger;
        }

        public QualityProfile Add(QualityProfile profile)
        {
            return _profileRepository.Insert(profile);
        }

        public void Update(QualityProfile profile)
        {
            _profileRepository.Update(profile);
        }

        public void Delete(int id)
        {
            var authorInUse = _authorService
                .GetAllAuthors()
                .Any(a => a.AudiobookQualityProfileId == id || a.EbookQualityProfileId == id);

            var importListInUse = _importListFactory
                .All()
                .Any(l => l.QualityProfileId == id);

            var rootFolderInUse = _rootFolderService
                .All()
                .Any(rf =>
                {
                    var ab = rf.GetAudiobookSettings();
                    var eb = rf.GetEbookSettings();
                    return (ab?.QualityProfileId == id) || (eb?.QualityProfileId == id);
                });

            if (authorInUse || importListInUse || rootFolderInUse)
            {
                var profile = _profileRepository.Get(id);
                throw new QualityProfileInUseException(profile.Name);
            }

            _profileRepository.Delete(id);
        }

        public List<QualityProfile> All()
        {
            return _profileRepository.All().ToList();
        }

        public List<QualityProfile> GetByType(ProfileType type)
        {
            return _profileRepository.All().Where(p => p.ProfileType == type).ToList();
        }

        public QualityProfile Get(int id)
        {
            return _profileRepository.Get(id);
        }

        public bool Exists(int id)
        {
            return _profileRepository.Exists(id);
        }

        public void Handle(ApplicationStartedEvent message)
        {
            var formats = _formatService.All();
            var profiles = All();

            RetireGeneratedCustomFormats(formats, profiles);

            formats = _formatService.All();
            profiles = All();

            if (profiles.Any())
            {
                foreach (var profile in profiles)
                {
                    if (ReconcileCustomFormatMembership(profile, formats))
                    {
                        Update(profile);
                    }
                }

                return;
            }

            _logger.Info("Setting up default quality profiles");

            // E-Book profile (Readarr-aligned defaults)
            AddDefaultProfile("eBook",
                ProfileType.Ebook,
                Quality.MOBI,
                Quality.MOBI,
                Quality.EPUB,
                Quality.AZW3);

            // Audiobook profile (generic audio formats only)
            var audiobookProfile = AddDefaultProfile("Spoken",
                              ProfileType.Audiobook,
                              Quality.M4B,
                              // Storage order: worst -> best
                              Quality.UnknownAudio,
                              Quality.FLAC,
                              Quality.MP3,
                              Quality.M4B);

            // Keep conversion opt-in
            audiobookProfile.ConvertMp3ToM4b = false;
        }

        private void RetireGeneratedCustomFormats(List<CustomFormat> formats, List<QualityProfile> profiles)
        {
            foreach (var format in formats.Where(BuiltInCustomFormats.IsRetiredBuiltIn).ToList())
            {
                try
                {
                    if (BuiltInCustomFormats.IsUntouchedRetiredBuiltIn(format) && HasDefaultRetiredScores(format, profiles))
                    {
                        _logger.Info("Removing untouched retired built-in Custom Format '{0}'", format.Name);
                        _formatService.Delete(format.Id);
                        continue;
                    }

                    _logger.Info("Preserving customized retired built-in Custom Format '{0}' as a user Custom Format", format.Name);
                    format.BuiltInKey = null;
                    _formatService.Update(format);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to retire built-in Custom Format '{0}'; it will be retried on the next startup", format.Name);
                }
            }
        }

        private static bool HasDefaultRetiredScores(CustomFormat format, List<QualityProfile> profiles)
        {
            if (profiles.Count == 0)
            {
                return true;
            }

            foreach (var profile in profiles)
            {
                var items = (profile.FormatItems ?? new List<ProfileFormatItem>())
                    .Where(item => item?.Format?.Id == format.Id)
                    .ToList();

                if (items.Count != 1)
                {
                    return false;
                }

                var expectedScore = profile.ProfileType == ProfileType.Audiobook &&
                                    (string.Equals(format.BuiltInKey, BuiltInCustomFormats.PreferredNarratorMajorityKey, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(format.BuiltInKey, BuiltInCustomFormats.CompletePreferredCastKey, StringComparison.OrdinalIgnoreCase))
                    ? BuiltInCustomFormats.RetiredNarratorTierDefaultAudiobookScore
                    : 0;

                if (items[0].Score != expectedScore)
                {
                    return false;
                }
            }

            return true;
        }

        public void Handle(CustomFormatAddedEvent message)
        {
            var all = All();
            foreach (var profile in all)
            {
                if (!message.CustomFormat.AppliesToProfile(profile.ProfileType))
                {
                    continue;
                }

                profile.FormatItems ??= new List<ProfileFormatItem>();
                profile.FormatItems.Insert(0, new ProfileFormatItem
                {
                    Score = profile.ProfileType == ProfileType.Audiobook ? message.AudiobookProfileScore ?? 0 : 0,
                    Format = message.CustomFormat
                });

                Update(profile);
            }
        }

        public void Handle(CustomFormatUpdatedEvent message)
        {
            if (message.PreviousAppliesTo == message.CustomFormat.AppliesTo)
            {
                return;
            }

            var formats = _formatService.All();
            foreach (var profile in All())
            {
                if (ReconcileCustomFormatMembership(profile, formats))
                {
                    Update(profile);
                }
            }
        }

        public void Handle(CustomFormatDeletedEvent message)
        {
            var all = All();
            foreach (var profile in all)
            {
                profile.FormatItems = (profile.FormatItems ?? new List<ProfileFormatItem>()).Where(c => c.Format.Id != message.CustomFormat.Id).ToList();

                if (profile.FormatItems.Empty())
                {
                    profile.MinFormatScore = 0;
                    profile.CutoffFormatScore = 0;
                }

                Update(profile);
            }
        }

        public QualityProfile GetDefaultProfile(string name, Quality cutoff = null, params Quality[] allowed)
        {
            var groupedQualites = Quality.DefaultQualityDefinitions
                .OrderBy(q => q.GroupWeight)
                .GroupBy(q => q.GroupWeight);
            var items = new List<QualityProfileQualityItem>();
            var groupId = 1000;
            var profileCutoff = cutoff == null ? Quality.Unknown.Id : cutoff.Id;

            foreach (var group in groupedQualites)
            {
                if (group.Count() == 1)
                {
                    var quality = group.First().Quality;
                    items.Add(new QualityProfileQualityItem { Quality = quality, Allowed = allowed.Contains(quality) });
                    continue;
                }

                var groupAllowed = group.Any(g => allowed.Contains(g.Quality));

                items.Add(new QualityProfileQualityItem
                {
                    Id = groupId,
                    Name = group.First().GroupName,
                    Items = group.OrderBy(g => g.Weight).Select(g => new QualityProfileQualityItem
                    {
                        Quality = g.Quality,
                        Allowed = groupAllowed
                    }).ToList(),
                    Allowed = groupAllowed
                });

                if (group.Any(s => s.Quality.Id == profileCutoff))
                {
                    profileCutoff = groupId;
                }

                groupId++;
            }

            var formatItems = _formatService.All().Select(format => new ProfileFormatItem
            {
                Score = 0,
                Format = format
            }).ToList();

            var qualityProfile = new QualityProfile
            {
                Name = name,
                Cutoff = profileCutoff,
                Items = items,
                MinFormatScore = 0,
                CutoffFormatScore = 0,
                FormatItems = formatItems
            };

            return qualityProfile;
        }

        public static void ApplyNewProfileCustomFormatDefaults(QualityProfile profile)
        {
            if (profile == null)
            {
                return;
            }

            // Audiobook editions (narrator, production style, and similar traits) are
            // commonly more important than their source container. Existing profiles
            // keep the migration default (false); this applies only to newly created ones.
            profile.PreferCustomFormatsOverQuality = profile.ProfileType == ProfileType.Audiobook;

            if (profile.FormatItems == null)
            {
                return;
            }

            profile.FormatItems = profile.FormatItems
                .Where(item => item?.Format?.AppliesToProfile(profile.ProfileType) == true)
                .ToList();

            foreach (var item in profile.FormatItems)
            {
                item.Score = profile.ProfileType == ProfileType.Audiobook
                    ? BuiltInCustomFormats.GetDefaultAudiobookProfileScore(item.Format) ?? 0
                    : 0;
            }
        }

        private QualityProfile AddDefaultProfile(string name, ProfileType profileType, Quality cutoff, params Quality[] allowed)
        {
            var profile = GetDefaultProfile(name, cutoff, allowed);
            profile.ProfileType = profileType;
            ApplyNewProfileCustomFormatDefaults(profile);

            return Add(profile);
        }

        public static bool ReconcileCustomFormatMembership(
            QualityProfile profile,
            IEnumerable<CustomFormat> formats,
            Func<CustomFormat, int> scoreForNewFormat = null)
        {
            if (profile == null)
            {
                return false;
            }

            var applicableFormats = (formats ?? Enumerable.Empty<CustomFormat>())
                .Where(format => format?.AppliesToProfile(profile.ProfileType) == true)
                .GroupBy(format => format.Id)
                .Select(group => group.First())
                .ToList();
            var applicableById = applicableFormats.ToDictionary(format => format.Id);
            var existingItems = profile.FormatItems ?? new List<ProfileFormatItem>();
            var previousIds = existingItems.Select(item => item?.Format?.Id ?? -1).ToList();
            var reconciledItems = new List<ProfileFormatItem>();
            var seenIds = new HashSet<int>();

            foreach (var item in existingItems)
            {
                if (item?.Format == null ||
                    !applicableById.TryGetValue(item.Format.Id, out var currentFormat) ||
                    !seenIds.Add(item.Format.Id))
                {
                    continue;
                }

                reconciledItems.Add(new ProfileFormatItem
                {
                    Format = currentFormat,
                    Score = item.Score
                });
            }

            foreach (var format in applicableFormats)
            {
                if (!seenIds.Add(format.Id))
                {
                    continue;
                }

                reconciledItems.Add(new ProfileFormatItem
                {
                    Format = format,
                    Score = scoreForNewFormat?.Invoke(format) ?? 0
                });
            }

            var nextIds = reconciledItems.Select(item => item.Format.Id).ToList();
            var changed = !previousIds.SequenceEqual(nextIds);
            profile.FormatItems = reconciledItems;
            return changed;
        }

        private QualityProfile CreateCleanSpokenProfile(QualityProfile existingProfile)
        {
            var audioQualities = new[]
            {
                Quality.UnknownAudio,
                Quality.MP3,
                Quality.M4B,
                Quality.FLAC
            };

            return GetDefaultProfile("Spoken", null, audioQualities);
        }

        private QualityProfile CreateCleanEBookProfile(QualityProfile existingProfile)
        {
            var textQualities = new[]
            {
                Quality.Unknown,
                Quality.PDF,
                Quality.MOBI,
                Quality.EPUB,
                Quality.AZW3
            };

            return GetDefaultProfile("eBook", null, textQualities);
        }

        private static IEnumerable<int> GetAllQualityIds(QualityProfileQualityItem item)
        {
            if (item.Quality != null)
            {
                yield return item.Quality.Id;
            }

            if (item.Items != null)
            {
                foreach (var subItem in item.Items)
                {
                    foreach (var id in GetAllQualityIds(subItem))
                    {
                        yield return id;
                    }
                }
            }
        }
    }
}
