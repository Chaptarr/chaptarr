using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.HealthCheck.Checks
{
    [CheckOn(typeof(AuthorAddedEvent), CheckOnCondition.FailedOnly)]
    [CheckOn(typeof(AuthorEditedEvent), CheckOnCondition.FailedOnly)]
    [CheckOn(typeof(AuthorDeletedEvent))]
    [CheckOn(typeof(ModelEvent<RootFolder>))]
    public class AuthorQualityProfileCheck : HealthCheckBase, ICheckOnCondition<AuthorEditedEvent>
    {
        private readonly IAuthorService _authors;
        private readonly IRootFolderService _roots;
        private readonly IQualityProfileService _profiles;
        private readonly Logger _logger;
        private readonly IMetadataProfileService _metadataProfiles;

        public AuthorQualityProfileCheck(IAuthorService authors, IRootFolderService roots,
            IQualityProfileService profiles, ILocalizationService localizationService, Logger logger, IMetadataProfileService metadataProfiles) : base(localizationService)
        {
            _authors = authors;
            _roots = roots;
            _profiles = profiles;
            _metadataProfiles = metadataProfiles;
            _logger = logger;
        }

        public bool ShouldCheckOnEvent(AuthorEditedEvent message) =>
            message.OldAuthor == null || message.Author.AudiobookQualityProfileId != message.OldAuthor.AudiobookQualityProfileId ||
            message.Author.EbookQualityProfileId != message.OldAuthor.EbookQualityProfileId ||
            message.Author.AudiobookMetadataProfileId != message.OldAuthor.AudiobookMetadataProfileId ||
            message.Author.EbookMetadataProfileId != message.OldAuthor.EbookMetadataProfileId;

        public override HealthCheck Check()
        {
            var timer = Stopwatch.StartNew();
            var profiles = _profiles.All().ToDictionary(profile => profile.Id);
            var metadataProfiles = _metadataProfiles.All().ToDictionary(profile => profile.Id);
            var authors = _authors.GetAllAuthors();
            var roots = _roots.All();
            var incompatible = new List<string>();
            void Check(string name, int? id, ProfileType side)
            {
                if (id > 0 && profiles.TryGetValue(id.Value, out var profile) && profile.ProfileType != side)
                    incompatible.Add($"{name} ({side}: {profile.Name})");
            }
            void CheckMetadata(string name, int? id, MetadataProfileType side)
            {
                if (id > 0 && metadataProfiles.TryGetValue(id.Value, out var profile) &&
                    profile.ProfileType != MetadataProfileType.General && profile.ProfileType != side)
                    incompatible.Add($"{name} ({side}: {profile.Name})");
            }
            foreach (var author in authors)
            {
                Check(author.Name, author.AudiobookQualityProfileId, ProfileType.Audiobook);
                Check(author.Name, author.EbookQualityProfileId, ProfileType.Ebook);
                CheckMetadata(author.Name, author.AudiobookMetadataProfileId, MetadataProfileType.Audiobook);
                CheckMetadata(author.Name, author.EbookMetadataProfileId, MetadataProfileType.Ebook);
            }
            foreach (var root in roots)
            {
                Check(root.Path, root.GetAudiobookSettings()?.QualityProfileId, ProfileType.Audiobook);
                Check(root.Path, root.GetEbookSettings()?.QualityProfileId, ProfileType.Ebook);
                CheckMetadata(root.Path, root.GetAudiobookSettings()?.MetadataProfileId, MetadataProfileType.Audiobook);
                CheckMetadata(root.Path, root.GetEbookSettings()?.MetadataProfileId, MetadataProfileType.Ebook);
            }
            _logger.Debug("Checked saved quality and metadata profiles for {0} authors and {1} roots: {2} incompatible selections in {3} ms",
                authors.Count, roots.Count, incompatible.Count, timer.ElapsedMilliseconds);
            return incompatible.Count == 0 ? new HealthCheck(GetType()) :
                new HealthCheck(GetType(), HealthCheckResult.Warning,
                    string.Format(_localizationService.GetLocalizedString("AuthorProfileIncompatible"), string.Join("; ", incompatible)));
        }
    }
}
