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
        private string _workFolder;
        private string _source;
        private ImportApprovedBooks _subject;
        private ConversionTagOptions _tags;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"provider-chapter-staging-{Guid.NewGuid():N}");
            _workFolder = Path.Combine(_folder, "work");
            Directory.CreateDirectory(_workFolder);
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
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
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
                _workFolder,
                new[] { _source, secondSource },
                _tags,
                new Book { Title = "Test book" },
                (_, _) => throw (Exception)Activator.CreateInstance(failureType));

            Assert.That(inputs, Is.EqualTo(new[]
            {
                Path.Combine(_workFolder, "provider-chapters", "0001.mp3"),
                Path.Combine(_workFolder, "provider-chapters", "0002.mp3")
            }));
            Assert.That(File.ReadAllText(inputs[0]), Is.EqualTo("source audio"));
            Assert.That(File.ReadAllText(inputs[1]), Is.EqualTo("second audio"));
            Assert.That(new FileInfo(inputs[0]).LinkTarget, Is.Null);
            Assert.That(new FileInfo(inputs[1]).LinkTarget, Is.Null);
            Assert.That(File.ReadAllText(_source), Is.EqualTo("source audio"));
            Assert.That(File.ReadAllText(secondSource), Is.EqualTo("second audio"));
            Assert.That(File.ReadAllText(Path.Combine(_workFolder, "provider-chapters", "chapters.txt")), Is.EqualTo(chapters));
            Assert.That(_tags.ChaptersTxtContent, Is.EqualTo(chapters));
            Assert.That(_tags.ProviderChapterCount, Is.EqualTo(2));
        }

        [Test]
        public void should_remove_partial_staging_and_fail_without_discarding_chapters()
        {
            var missingSource = Path.Combine(_folder, "missing.mp3");
            var chapters = _tags.ChaptersTxtContent;

            var error = Assert.Throws<IOException>(() => _subject.CreateProviderChapterSidecarInputs(
                _workFolder,
                new[] { _source, missingSource },
                _tags,
                new Book { Title = "Test book" },
                (_, _) => throw new IOException("Operation not supported")));

            Assert.That(error.Message, Does.Contain("Unable to stage provider chapters for 'Test book'"));
            Assert.That(error.InnerException, Is.InstanceOf<IOException>());
            Assert.That(Directory.Exists(Path.Combine(_workFolder, "provider-chapters")), Is.False);
            Assert.That(File.ReadAllText(_source), Is.EqualTo("source audio"));
            Assert.That(_tags.ChaptersTxtContent, Is.EqualTo(chapters));
            Assert.That(_tags.ProviderChapterCount, Is.EqualTo(2));
        }
    }
}
