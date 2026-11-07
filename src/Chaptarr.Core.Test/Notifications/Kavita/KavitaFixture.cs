using System.Collections.Generic;
using System.IO;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Kavita;
using NzbDrone.Core.ThingiProvider;

namespace Chaptarr.Core.Test.Notifications.Kavita
{
    [TestFixture]
    public class KavitaFixture
    {
        private FakeKavitaService _kavitaService;
        private NzbDrone.Core.Notifications.Kavita.Kavita _subject;

        [SetUp]
        public void SetUp()
        {
            _kavitaService = new FakeKavitaService();
            _subject = new NzbDrone.Core.Notifications.Kavita.Kavita(_kavitaService, LogManager.GetCurrentClassLogger())
            {
                Definition = new NotificationDefinition
                {
                    Settings = new KavitaSettings
                    {
                        Host = "kavita",
                        Port = 5000,
                        ApiKey = "test-key",
                        Notify = true
                    }
                }
            };
        }

        [Test]
        public void should_send_parent_folder_path_on_release_import()
        {
            var filePath = Path.Combine(Path.GetTempPath(), "Books", "Author", "Title", "book.epub");
            var expectedFolder = Path.GetDirectoryName(filePath);

            var message = new BookDownloadMessage
            {
                BookFiles = new List<BookFile>
                {
                    new BookFile { Path = filePath }
                }
            };

            _subject.OnReleaseImport(message);

            Assert.That(_kavitaService.NotifiedFolders, Has.Count.EqualTo(1));
            Assert.That(_kavitaService.NotifiedFolders[0], Is.EqualTo(expectedFolder));
        }

        [Test]
        public void should_not_notify_on_release_import_with_empty_book_files()
        {
            var message = new BookDownloadMessage
            {
                BookFiles = new List<BookFile>()
            };

            Assert.DoesNotThrow(() => _subject.OnReleaseImport(message));
            Assert.That(_kavitaService.NotifiedFolders, Is.Empty);
        }

        [Test]
        public void should_send_parent_folder_path_on_book_delete()
        {
            var filePath = Path.Combine(Path.GetTempPath(), "Books", "Author", "Title", "book.epub");
            var expectedFolder = Path.GetDirectoryName(filePath);

            var book = new Book
            {
                BookFiles = new List<BookFile>
                {
                    new BookFile { Path = filePath }
                }
            };
            var message = new BookDeleteMessage(book, true);

            _subject.OnBookDelete(message);

            Assert.That(_kavitaService.NotifiedFolders, Has.Count.EqualTo(1));
            Assert.That(_kavitaService.NotifiedFolders[0], Is.EqualTo(expectedFolder));
        }

        [Test]
        public void should_send_parent_folder_path_on_book_file_delete()
        {
            var filePath = Path.Combine(Path.GetTempPath(), "Books", "Author", "Title", "book.epub");
            var expectedFolder = Path.GetDirectoryName(filePath);

            var message = new BookFileDeleteMessage
            {
                BookFile = new BookFile { Path = filePath }
            };

            _subject.OnBookFileDelete(message);

            Assert.That(_kavitaService.NotifiedFolders, Has.Count.EqualTo(1));
            Assert.That(_kavitaService.NotifiedFolders[0], Is.EqualTo(expectedFolder));
        }

        [Test]
        public void should_send_parent_folder_path_on_book_retag()
        {
            var filePath = Path.Combine(Path.GetTempPath(), "Books", "Author", "Title", "book.epub");
            var expectedFolder = Path.GetDirectoryName(filePath);

            var message = new BookRetagMessage
            {
                BookFile = new BookFile { Path = filePath }
            };

            _subject.OnBookRetag(message);

            Assert.That(_kavitaService.NotifiedFolders, Has.Count.EqualTo(1));
            Assert.That(_kavitaService.NotifiedFolders[0], Is.EqualTo(expectedFolder));
        }

        [Test]
        public void should_not_notify_when_notify_setting_is_disabled()
        {
            var settings = (KavitaSettings)_subject.Definition.Settings;
            settings.Notify = false;

            var filePath = Path.Combine(Path.GetTempPath(), "Books", "Author", "Title", "book.epub");
            var message = new BookFileDeleteMessage
            {
                BookFile = new BookFile { Path = filePath }
            };

            _subject.OnBookFileDelete(message);

            Assert.That(_kavitaService.NotifiedFolders, Is.Empty);
        }

        private class FakeKavitaService : IKavitaService
        {
            public List<string> NotifiedFolders { get; } = new List<string>();

            public void Notify(KavitaSettings settings, string folderPath)
            {
                NotifiedFolders.Add(folderPath);
            }

            public ValidationFailure Test(KavitaSettings settings, string message)
            {
                return null;
            }
        }
    }
}
