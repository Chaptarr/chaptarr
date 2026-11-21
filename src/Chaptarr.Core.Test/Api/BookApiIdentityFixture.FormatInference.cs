using System.Collections.Generic;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.Books;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using Chaptarr.Core.Test.Common;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Api
{
    public partial class BookApiIdentityFixture
    {
        [Test]
        public void classic_book_add_infers_mixed_root_scope_from_persisted_profile(
            [Values(false, true)] bool postgres,
            [Values(ProfileType.Audiobook, ProfileType.Ebook)] ProfileType type,
            [Values(false, true)] bool contradictRoot)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { KeepExistingAuthorSettings = true };
                var profiles = new QualityProfileService(new QualityProfileRepository(database, h.Events,
                    Proxy<ICustomFormatService>((_, _) => new List<CustomFormat>())), null, null, null, null, null, LogManager.GetCurrentClassLogger());
                var profile = profiles.Add(new QualityProfile { Name = "Mobile app profile", ProfileType = type,
                    Items = new List<QualityProfileQualityItem> { new QualityProfileQualityItem { Allowed = true,
                        Quality = type == ProfileType.Audiobook ? NzbDrone.Core.Qualities.Quality.M4B : NzbDrone.Core.Qualities.Quality.EPUB } } });
                var root = new RootFolder { Path = @"C:\Mixed".AsOsAgnostic(), FolderType = contradictRoot
                    ? type == ProfileType.Audiobook ? FolderType.Ebook : FolderType.Audiobook : FolderType.Mixed };
                var otherProfile = profiles.Add(new QualityProfile { Name = "Other format", ProfileType = type == ProfileType.Audiobook ? ProfileType.Ebook : ProfileType.Audiobook,
                    Items = new List<QualityProfileQualityItem> { new QualityProfileQualityItem { Allowed = true,
                        Quality = type == ProfileType.Audiobook ? NzbDrone.Core.Qualities.Quality.EPUB : NzbDrone.Core.Qualities.Quality.M4B } } });
                root.SetAudiobookSettings(new MediaTypeSettings { QualityProfileId = type == ProfileType.Audiobook ? profile.Id : otherProfile.Id, MetadataProfileId = 1 });
                root.SetEbookSettings(new MediaTypeSettings { QualityProfileId = type == ProfileType.Ebook ? profile.Id : otherProfile.Id, MetadataProfileId = 1 });
                var roots = Proxy<IRootFolderService>((method, _) => method.Name == nameof(IRootFolderService.All) ? new List<RootFolder> { root } : root);
                var side = type == ProfileType.Audiobook ? BookMediaType.Audiobook : BookMediaType.Ebook;
                var book = h.AddBook(side);
                var counterpart = h.AddBook(side == BookMediaType.Audiobook ? BookMediaType.Ebook : BookMediaType.Audiobook);
                var controller = h.ImportController(roots: roots, profiles: profiles);
                var resource = new BookResource
                {
                    Title = book.Title, HardcoverBookId = book.HardcoverBookId, Monitored = true,
                    Author = new AuthorResource
                    {
                        Id = h.Author.Id, AuthorName = h.Author.Name, RootFolderPath = root.Path,
                        QualityProfileId = profile.Id, MetadataProfileId = 1
                    }
                };
                if (contradictRoot)
                {
                    Assert.That(Assert.Throws<BadRequestException>(() => controller.AddBook(null, resource).GetAwaiter().GetResult()).Message,
                        Does.Contain("does not match"));
                    Assert.That(h.Books.Get(book.Id).IsMonitored(), Is.False);
                    return;
                }

                var result = controller.AddBook(null, resource).GetAwaiter().GetResult();
                Assert.That(result.Result, Is.TypeOf<CreatedAtActionResult>());
                Assert.That(resource.MediaType, Is.EqualTo(side == BookMediaType.Audiobook ? "audiobook" : "ebook"));
                Assert.That(h.Books.Get(book.Id).IsMonitored(), Is.True);
                Assert.That(h.Books.Get(counterpart.Id).IsMonitored(), Is.False);
            });
        }
    }
}
