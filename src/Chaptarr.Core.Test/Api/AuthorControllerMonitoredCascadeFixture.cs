using Chaptarr.Api.V1.Author;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class AuthorControllerMonitoredCascadeFixture
    {
        private static readonly string AudiobookRoot = "/audiobooks";
        private static readonly string EbookRoot = "/ebooks";

        [Test]
        public void should_persist_explicit_unmonitor_when_put_echoes_unchanged_format_gates()
        {
            var author = CreateAuthor(audiobookMonitored: true, ebookMonitored: true);
            var resource = new AuthorResource
            {
                Id = author.Id,
                AuthorName = author.Name,
                Monitored = false,
                AudiobookRootFolderPath = AudiobookRoot,
                EbookRootFolderPath = EbookRoot,
                AudiobookMonitored = true,
                EbookMonitored = true
            };
            var stored = AuthorController.StoredAuthorMonitoringState.Capture(author);

            var updated = resource.ToModel(author);
            AuthorController.CascadeExplicitMonitoredIntoMediaTypeSettings(resource, updated, null, stored);

            Assert.Multiple(() =>
            {
                Assert.That(updated.AudiobookMonitored, Is.False);
                Assert.That(updated.EbookMonitored, Is.False);
                Assert.That(updated.IsMonitoredFromMediaSettings(), Is.False);
                Assert.That(updated.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
                Assert.That(updated.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            });
        }

        [Test]
        public void should_not_change_format_gates_when_monitored_is_only_echoed()
        {
            var author = CreateAuthor(audiobookMonitored: true, ebookMonitored: false);
            var resource = new AuthorResource
            {
                Id = author.Id,
                AuthorName = author.Name,
                Monitored = true,
                AudiobookRootFolderPath = AudiobookRoot,
                EbookRootFolderPath = EbookRoot,
                AudiobookMonitored = true,
                EbookMonitored = false
            };
            var stored = AuthorController.StoredAuthorMonitoringState.Capture(author);

            var updated = resource.ToModel(author);
            AuthorController.CascadeExplicitMonitoredIntoMediaTypeSettings(resource, updated, null, stored);

            Assert.Multiple(() =>
            {
                Assert.That(updated.AudiobookMonitored, Is.True);
                Assert.That(updated.EbookMonitored, Is.False);
            });
        }

        [Test]
        public void should_not_cascade_when_monitored_is_omitted()
        {
            var author = CreateAuthor(audiobookMonitored: true, ebookMonitored: false);
            var resource = new AuthorResource
            {
                Id = author.Id,
                AuthorName = author.Name,
                AudiobookRootFolderPath = AudiobookRoot,
                EbookRootFolderPath = EbookRoot
            };
            var stored = AuthorController.StoredAuthorMonitoringState.Capture(author);

            var updated = resource.ToModel(author);
            AuthorController.CascadeExplicitMonitoredIntoMediaTypeSettings(resource, updated, null, stored);

            Assert.Multiple(() =>
            {
                Assert.That(updated.AudiobookMonitored, Is.True);
                Assert.That(updated.EbookMonitored, Is.False);
            });
        }

        [Test]
        public void should_clear_a_stale_enabled_gate_for_a_format_without_a_root_folder()
        {
            var author = CreateAuthor(audiobookMonitored: true, ebookMonitored: true, includeEbookRoot: false);
            var resource = new AuthorResource
            {
                Id = author.Id,
                AuthorName = author.Name,
                Monitored = false,
                AudiobookRootFolderPath = AudiobookRoot,
                AudiobookMonitored = true,
                EbookMonitored = true
            };
            var stored = AuthorController.StoredAuthorMonitoringState.Capture(author);

            var updated = resource.ToModel(author);
            AuthorController.CascadeExplicitMonitoredIntoMediaTypeSettings(resource, updated, null, stored);

            Assert.That(updated.IsMonitoredFromMediaSettings(), Is.False);
        }

        [Test]
        public void should_apply_explicit_unmonitor_when_a_format_gets_its_first_root_folder()
        {
            var author = CreateAuthor(audiobookMonitored: false, ebookMonitored: null, includeEbookRoot: false);
            var resource = new AuthorResource
            {
                Id = author.Id,
                AuthorName = author.Name,
                Monitored = false,
                AudiobookRootFolderPath = AudiobookRoot,
                EbookRootFolderPath = EbookRoot,
                AudiobookMonitored = false
            };
            var stored = AuthorController.StoredAuthorMonitoringState.Capture(author);

            var updated = resource.ToModel(author);
            AuthorController.CascadeExplicitMonitoredIntoMediaTypeSettings(resource, updated, null, stored);

            Assert.That(updated.EbookMonitored, Is.False);
        }

        private static NzbDrone.Core.Books.Author CreateAuthor(bool audiobookMonitored, bool? ebookMonitored, bool includeEbookRoot = true)
        {
            return new NzbDrone.Core.Books.Author
            {
                Id = 12,
                Name = "Terry Pratchett",
                AudiobookRootFolderPath = AudiobookRoot,
                EbookRootFolderPath = includeEbookRoot ? EbookRoot : null,
                AudiobookMonitored = audiobookMonitored,
                AudiobookMonitorNewItems = NewItemMonitorTypes.New,
                EbookMonitored = ebookMonitored,
                EbookMonitorNewItems = NewItemMonitorTypes.None
            };
        }
    }
}
