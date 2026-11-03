using System;
using System.IO;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class ProviderChapterSidecarInputsFixture
    {
        private string _folder;
        private string _source;
        private ImportApprovedBooks _subject;
        private ConversionTagOptions _tags;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"provider-chapter-copy-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_folder);
            _source = Path.Combine(_folder, "source.mp3");
            File.WriteAllText(_source, "source audio");
            _subject = new ImportApprovedBooks(
                null, null, null, null, null, null, null, null, null,
                null, null, null, null, null, null, null, null,
                LogManager.GetLogger(nameof(ProviderChapterSidecarInputsFixture)));
            _tags = new ConversionTagOptions
            {
                ProviderChapterCount = 2,
                ChaptersTxtContent = "## total-length 00:20:00.000\n00:00:00.000 Opening\n00:05:00.000 Chapter 1"
            };
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_folder, recursive: true);
        }

        [TestCase(typeof(IOException))]
        [TestCase(typeof(UnauthorizedAccessException))]
        [TestCase(typeof(PlatformNotSupportedException))]
        public void should_copy_inputs_and_preserve_provider_chapters_when_symlinks_are_unavailable(Type failureType)
        {
            var secondSource = Path.Combine(_folder, "second.mp3");
            File.WriteAllText(secondSource, "second audio");
            var chapters = _tags.ChaptersTxtContent;

            var inputs = _subject.CreateProviderChapterSidecarInputs(
                _folder,
                new[] { _source, secondSource },
                _tags,
                new Book { Title = "Test book" },
                (_, _) => throw (Exception)Activator.CreateInstance(failureType));

            Assert.That(inputs, Is.EqualTo(new[]
            {
                Path.Combine(_folder, "provider-chapters", "0001.mp3"),
                Path.Combine(_folder, "provider-chapters", "0002.mp3")
            }));
            Assert.That(File.ReadAllText(inputs[0]), Is.EqualTo("source audio"));
            Assert.That(File.ReadAllText(inputs[1]), Is.EqualTo("second audio"));
            Assert.That(new FileInfo(inputs[0]).LinkTarget, Is.Null);
            Assert.That(new FileInfo(inputs[1]).LinkTarget, Is.Null);
            Assert.That(File.ReadAllText(_source), Is.EqualTo("source audio"));
            Assert.That(File.ReadAllText(secondSource), Is.EqualTo("second audio"));
            Assert.That(File.ReadAllText(Path.Combine(_folder, "provider-chapters", "chapters.txt")), Is.EqualTo(chapters));
            Assert.That(_tags.ChaptersTxtContent, Is.EqualTo(chapters));
            Assert.That(_tags.ProviderChapterCount, Is.EqualTo(2));
        }

        [Test]
        public void should_report_staging_failure_without_discarding_chapters_or_overwriting_files()
        {
            var stageFolder = Path.Combine(_folder, "provider-chapters");
            Directory.CreateDirectory(stageFolder);
            var occupiedPath = Path.Combine(stageFolder, "0001.mp3");
            File.WriteAllText(occupiedPath, "existing audio");
            var chapters = _tags.ChaptersTxtContent;

            var error = Assert.Throws<IOException>(() => _subject.CreateProviderChapterSidecarInputs(
                _folder,
                new[] { _source },
                _tags,
                new Book { Title = "Test book" },
                (_, _) => throw new IOException("Operation not supported")));

            Assert.That(error.Message, Does.Contain("Unable to stage provider chapters for 'Test book'"));
            Assert.That(error.InnerException, Is.InstanceOf<IOException>());
            Assert.That(File.ReadAllText(occupiedPath), Is.EqualTo("existing audio"));
            Assert.That(File.ReadAllText(_source), Is.EqualTo("source audio"));
            Assert.That(_tags.ChaptersTxtContent, Is.EqualTo(chapters));
            Assert.That(_tags.ProviderChapterCount, Is.EqualTo(2));
        }
    }
}
