using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.RootFolders;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.HealthCheck.Checks
{
    [TestFixture]
    public class AuthorQualityProfileCheckFixture
    {
        [Test]
        public void reports_incompatible_saved_profiles_with_four_batch_reads_and_clears_when_corrected()
        {
            var author = new Author { Name = "Frank Herbert", EbookQualityProfileId = 1 };
            var root = new RootFolder { Path = "/ebooks", FolderType = FolderType.Ebook };
            root.SetEbookSettings(new MediaTypeSettings { QualityProfileId = 1 });
            var profile = new QualityProfile { Id = 1, Name = "Audio only", ProfileType = ProfileType.Audiobook };
            var reads = new List<string>();
            var check = new AuthorQualityProfileCheck(
                Proxy<IAuthorService>((method, _) => { Assert.That(method.Name, Is.EqualTo(nameof(IAuthorService.GetAllAuthors))); reads.Add("authors"); return new List<Author> { author }; }),
                Proxy<IRootFolderService>((method, _) => { Assert.That(method.Name, Is.EqualTo(nameof(IRootFolderService.All))); reads.Add("roots"); return new List<RootFolder> { root }; }),
                Proxy<IQualityProfileService>((method, _) => { Assert.That(method.Name, Is.EqualTo(nameof(IQualityProfileService.All))); reads.Add("profiles"); return new List<QualityProfile> { profile }; }),
                Proxy<ILocalizationService>((_, _) => "Incompatible: {0}"), LogManager.GetCurrentClassLogger(),
                Proxy<IMetadataProfileService>((method, _) => { Assert.That(method.Name, Is.EqualTo(nameof(IMetadataProfileService.All))); reads.Add("metadata"); return new List<MetadataProfile>(); }));
            var runner = new HealthCheckService(new[] { check },
                Proxy<IServerSideNotificationService>((_, _) => new List<NzbDrone.Core.HealthCheck.HealthCheck>()), null,
                Proxy<IEventAggregator>((_, _) => null), new CacheManager(),
                Proxy<IRuntimeInfo>((_, _) => DateTime.UtcNow), LogManager.GetCurrentClassLogger());
            runner.HandleAsync(new ApplicationStartedEvent());
            var warning = runner.Results().Single();
            Assert.That(warning.Type, Is.EqualTo(HealthCheckResult.Warning));
            Assert.That(warning.Message, Does.Contain("Frank Herbert (Ebook: Audio only)").And.Contain("/ebooks"));
            Assert.That(reads, Is.EquivalentTo(new[] { "authors", "roots", "profiles", "metadata" }));
            for (var index = 0; index < 100; index++) runner.HandleAsync(new AuthorEditedEvent(author, author));
            Assert.That(reads.Count, Is.EqualTo(4), "Ordinary refreshes cannot repeat the whole-library scan, even while a warning is active");
            var oldAuthor = new Author { EbookQualityProfileId = author.EbookQualityProfileId };
            author.EbookQualityProfileId = profile.Id = 2;
            profile.ProfileType = ProfileType.Ebook;
            root.SetEbookSettings(new MediaTypeSettings { QualityProfileId = 2 });
            runner.HandleAsync(new AuthorEditedEvent(author, oldAuthor));
            Assert.That(runner.Results(), Is.Empty);
            Assert.That(reads.Count, Is.EqualTo(8));
            for (var index = 0; index < 100; index++) runner.HandleAsync(new AuthorEditedEvent(author, oldAuthor));
            Assert.That(reads.Count, Is.EqualTo(8), "Healthy author edits do not repeat the check");
            runner.Execute(new CheckHealthCommand());
            Assert.That(reads.Count, Is.EqualTo(12), "Scheduled checks remain enabled");
        }
        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void metadata_profile_type_warning_clears_for_the_right_side_or_general(BookMediaType side)
        {
            var audio = side == BookMediaType.Audiobook;
            var author = new Author { Name = "Ursula Le Guin", AudiobookMetadataProfileId = audio ? 1 : null, EbookMetadataProfileId = audio ? null : 1 };
            var root = new RootFolder { Path = "/books", FolderType = audio ? FolderType.Audiobook : FolderType.Ebook };
            var settings = new MediaTypeSettings { MetadataProfileId = 1 };
            if (audio) root.SetAudiobookSettings(settings); else root.SetEbookSettings(settings);
            var profile = new MetadataProfile { Id = 1, Name = "Wrong side", ProfileType = audio ? MetadataProfileType.Ebook : MetadataProfileType.Audiobook };
            var check = new AuthorQualityProfileCheck(
                Proxy<IAuthorService>((_, _) => new List<Author> { author }),
                Proxy<IRootFolderService>((_, _) => new List<RootFolder> { root }),
                Proxy<IQualityProfileService>((_, _) => new List<QualityProfile>()),
                Proxy<ILocalizationService>((_, _) => "Incompatible: {0}"), LogManager.GetCurrentClassLogger(),
                Proxy<IMetadataProfileService>((_, _) => new List<MetadataProfile> { profile }));
            var warning = check.Check();
            Assert.That(warning.Type, Is.EqualTo(HealthCheckResult.Warning));
            Assert.That(warning.Message, Does.Contain(author.Name).And.Contain(root.Path).And.Contain(profile.Name));
            profile.ProfileType = audio ? MetadataProfileType.Audiobook : MetadataProfileType.Ebook;
            Assert.That(check.Check().Type, Is.EqualTo(HealthCheckResult.Ok));
            profile.ProfileType = MetadataProfileType.General;
            Assert.That(check.Check().Type, Is.EqualTo(HealthCheckResult.Ok));
            Assert.That(check.ShouldCheckOnEvent(new AuthorEditedEvent(author, new Author())), Is.True);
        }
    }
}
