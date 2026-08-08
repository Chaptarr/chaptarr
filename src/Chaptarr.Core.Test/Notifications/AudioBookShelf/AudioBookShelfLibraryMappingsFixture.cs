using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Notifications.AudioBookShelf;

namespace Chaptarr.Core.Test.Notifications.AudioBookShelf
{
    [TestFixture]
    public class AudioBookShelfLibraryMappingsFixture
    {
        [Test]
        public void should_round_trip_library_mappings()
        {
            var settings = new AudioBookShelfSettings();
            var mappings = new List<AudioBookShelfLibraryMapping>
            {
                new AudioBookShelfLibraryMapping
                {
                    RootFolderId = 1,
                    MediaType = "audiobook",
                    LibraryId = "lib-audio",
                    LibraryFolderId = "folder-audio",
                    LibraryFolderPath = "/abs/audio"
                },
                new AudioBookShelfLibraryMapping
                {
                    RootFolderId = 2,
                    MediaType = "ebook",
                    LibraryId = "lib-book",
                    LibraryFolderId = "folder-book",
                    LibraryFolderPath = "/abs/books"
                }
            };

            settings.SetLibraryMappings(mappings);

            var roundTrippedMappings = settings.GetLibraryMappings();

            Assert.That(settings.HasConfiguredLibraryMappings(), Is.True);
            Assert.That(roundTrippedMappings, Has.Count.EqualTo(2));
            Assert.That(roundTrippedMappings[0].RootFolderId, Is.EqualTo(1));
            Assert.That(roundTrippedMappings[0].MediaType, Is.EqualTo("audiobook"));
            Assert.That(roundTrippedMappings[0].LibraryId, Is.EqualTo("lib-audio"));
            Assert.That(roundTrippedMappings[0].LibraryFolderId, Is.EqualTo("folder-audio"));
            Assert.That(roundTrippedMappings[0].LibraryFolderPath, Is.EqualTo("/abs/audio"));
            Assert.That(roundTrippedMappings[1].RootFolderId, Is.EqualTo(2));
            Assert.That(roundTrippedMappings[1].MediaType, Is.EqualTo("ebook"));
            Assert.That(roundTrippedMappings[1].LibraryId, Is.EqualTo("lib-book"));
            Assert.That(roundTrippedMappings[1].LibraryFolderId, Is.EqualTo("folder-book"));
            Assert.That(roundTrippedMappings[1].LibraryFolderPath, Is.EqualTo("/abs/books"));
        }


        [Test]
        public void should_not_treat_empty_mapping_array_as_configured()
        {
            var settings = new AudioBookShelfSettings
            {
                LibraryMappingsJson = "[]"
            };

            Assert.That(settings.HasConfiguredLibraryMappings(), Is.False);
        }

        [Test]
        public void should_ignore_unusable_mappings_when_detecting_configuration()
        {
            var settings = new AudioBookShelfSettings
            {
                LibraryMappingsJson = "[{\"rootFolderId\":0,\"mediaType\":\"audiobook\",\"libraryId\":\"\"}]"
            };

            Assert.That(settings.HasConfiguredLibraryMappings(), Is.False);
        }

        [Test]
        public void should_reject_duplicate_mapping_keys()
        {
            var mappingsJson = "[{\"rootFolderId\":1,\"mediaType\":\"audiobook\",\"libraryId\":\"a\"},{\"rootFolderId\":1,\"mediaType\":\"audiobook\",\"libraryId\":\"b\"}]";

            Assert.That(AudioBookShelfSettings.IsValidLibraryMappingsJson(mappingsJson), Is.False);
        }

        [Test]
        public void should_clear_library_mappings_and_legacy_library_selection()
        {
            var settings = new AudioBookShelfSettings
            {
                AudiobookLibraryId = "legacy-audio",
                EbookLibraryId = "legacy-ebook",
                LibraryId = "legacy-single"
            };

            settings.SetLibraryMappings(new List<AudioBookShelfLibraryMapping>
            {
                new AudioBookShelfLibraryMapping
                {
                    RootFolderId = 1,
                    MediaType = "audiobook",
                    LibraryId = "lib-audio"
                }
            });

            var cleared = settings.ClearLibraryMappings();

            Assert.That(cleared, Is.True);
            Assert.That(settings.GetLibraryMappings(), Is.Empty);
            Assert.That(settings.HasConfiguredLibraryMappings(), Is.False);
            Assert.That(settings.AudiobookLibraryId, Is.Null);
            Assert.That(settings.EbookLibraryId, Is.Null);
            Assert.That(settings.LibraryId, Is.Null);
        }

        [Test]
        public void should_not_report_clear_when_no_library_mappings_exist()
        {
            var settings = new AudioBookShelfSettings();

            var cleared = settings.ClearLibraryMappings();

            Assert.That(cleared, Is.False);
        }
    }
}
