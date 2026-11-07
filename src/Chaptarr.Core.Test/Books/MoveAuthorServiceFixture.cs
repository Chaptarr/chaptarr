using System.IO;
using Moq;
using NUnit.Framework;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class MoveAuthorServiceFixture
    {
        private Mock<IAuthorService> _authorService;
        private Mock<IBuildFileNames> _filenameBuilder;
        private Mock<IDiskProvider> _diskProvider;
        private Mock<IRootFolderWatchingService> _rootFolderWatchingService;
        private Mock<IDiskTransferService> _diskTransferService;
        private Mock<IEventAggregator> _eventAggregator;
        private MoveAuthorService _service;
        private Author _author;
        private string _sourcePath;
        private string _destinationPath;

        [SetUp]
        public void Setup()
        {
            _sourcePath = Path.Combine(Path.GetTempPath(), "bookshelf-author-source");
            _destinationPath = Path.Combine(Path.GetTempPath(), "chaptarr-author-destination");
            _author = new Author
            {
                Id = 7,
                Name = "Example Author",
                // The edit request stores the new primary path before the queued move runs.
                Path = _destinationPath,
                EbookPath = Path.Combine(_sourcePath, "ebooks"),
                AudiobookPath = _sourcePath
            };

            _authorService = new Mock<IAuthorService>();
            _authorService.Setup(service => service.GetAuthor(_author.Id)).Returns(_author);

            _filenameBuilder = new Mock<IBuildFileNames>();
            _diskProvider = new Mock<IDiskProvider>();
            _diskProvider.Setup(provider => provider.FolderExists(_sourcePath)).Returns(true);
            _rootFolderWatchingService = new Mock<IRootFolderWatchingService>();
            _diskTransferService = new Mock<IDiskTransferService>();
            _diskTransferService
                .Setup(service => service.TransferFolder(_sourcePath, _destinationPath, TransferMode.Move))
                .Returns(TransferMode.Move);
            _eventAggregator = new Mock<IEventAggregator>();

            _service = new MoveAuthorService(
                _authorService.Object,
                _filenameBuilder.Object,
                _diskProvider.Object,
                _rootFolderWatchingService.Object,
                _diskTransferService.Object,
                _eventAggregator.Object,
                LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void should_update_format_paths_after_a_single_author_move()
        {
            _service.Execute(new MoveAuthorCommand
            {
                AuthorId = _author.Id,
                SourcePath = _sourcePath,
                DestinationPath = _destinationPath
            });

            Assert.Multiple(() =>
            {
                Assert.That(_author.Path, Is.EqualTo(_destinationPath));
                Assert.That(_author.EbookPath, Is.EqualTo(Path.Combine(_destinationPath, "ebooks")));
                Assert.That(_author.AudiobookPath, Is.EqualTo(_destinationPath));
            });

            _authorService.Verify(service => service.UpdateAuthor(_author), Times.Once);
        }

        [Test]
        public void should_preserve_format_paths_outside_the_moved_folder()
        {
            var separateEbookPath = Path.Combine(Path.GetTempPath(), "separate-ebook-library", "author");
            _author.EbookPath = separateEbookPath;
            _author.AudiobookPath = Path.Combine(_sourcePath, "audiobooks");

            _service.Execute(new MoveAuthorCommand
            {
                AuthorId = _author.Id,
                SourcePath = _sourcePath,
                DestinationPath = _destinationPath
            });

            Assert.Multiple(() =>
            {
                Assert.That(_author.EbookPath, Is.EqualTo(separateEbookPath));
                Assert.That(_author.AudiobookPath, Is.EqualTo(Path.Combine(_destinationPath, "audiobooks")));
            });
        }

        [Test]
        public void should_revert_primary_path_and_keep_format_paths_when_move_fails()
        {
            _diskTransferService
                .Setup(service => service.TransferFolder(_sourcePath, _destinationPath, TransferMode.Move))
                .Throws(new IOException("move failed"));

            _service.Execute(new MoveAuthorCommand
            {
                AuthorId = _author.Id,
                SourcePath = _sourcePath,
                DestinationPath = _destinationPath
            });

            Assert.Multiple(() =>
            {
                Assert.That(_author.Path, Is.EqualTo(_sourcePath));
                Assert.That(_author.EbookPath, Is.EqualTo(Path.Combine(_sourcePath, "ebooks")));
                Assert.That(_author.AudiobookPath, Is.EqualTo(_sourcePath));
            });

            _authorService.Verify(service => service.UpdateAuthor(_author), Times.Once);
        }
    }
}
