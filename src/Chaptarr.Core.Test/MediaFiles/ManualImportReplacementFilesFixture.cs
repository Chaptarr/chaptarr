using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class ManualImportReplacementFilesFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void preview_describes_the_existing_recording_and_configured_disposal(bool recycleBinConfigured)
        {
            var subject = new ManualImportReplacementFiles(InterfaceProxy.Proxy<IDiskProvider>((_, _) => false),
                InterfaceProxy.Proxy<IExtraFileRepository<MetadataFile>>((_, _) => new List<MetadataFile>()),
                InterfaceProxy.Proxy<IExtraFileRepository<OtherExtraFile>>((_, _) => new List<OtherExtraFile>()),
                InterfaceProxy.Proxy<IAuthorRepository>((_, _) => new List<Author>()),
                InterfaceProxy.Proxy<IBookRepository>((_, _) => new List<Book>()),
                InterfaceProxy.Proxy<IConfigService>((_, _) => recycleBinConfigured ? "/recycle" : string.Empty));
            var edition = new Edition { Id = 42, BookId = 7, Title = "Dark Age (Dramatized Adaptation)", Narrator = "Full Cast" };
            var result = subject.Describe(new[] { new BookFile { Id = 9, EditionId = edition.Id, Edition = edition,
                Path = @"C:\library\Dark Age.m4b".AsOsAgnostic(), MediaType = "audiobook", IsGraphicAudio = true, Quality = new QualityModel(Quality.M4B),
                AudioProductionType = AudioProductionConstants.DetectedDramatizedFullCastType } }).Single();

            Assert.Multiple(() =>
            {
                Assert.That(result.EditionId, Is.EqualTo(42));
                Assert.That(result.BookId, Is.EqualTo(7));
                Assert.That(result.EditionTitle, Is.EqualTo(edition.Title));
                Assert.That(result.Narrator, Is.EqualTo("Full Cast"));
                Assert.That(result.MediaType, Is.EqualTo("audiobook"));
                Assert.That(result.IsGraphicAudio, Is.True);
                Assert.That(result.Quality, Does.Contain("M4B"));
                Assert.That(result.AudioProductionType, Is.EqualTo("Full Cast"));
                Assert.That(result.IsPermanentDelete, Is.EqualTo(!recycleBinConfigured));
            });
        }

        [Test]
        public void deletion_preview_allows_an_unmapped_file_and_its_managed_copy()
        {
            var subject = new ManualImportReplacementFiles(InterfaceProxy.Proxy<IDiskProvider>((_, _) => true),
                InterfaceProxy.Proxy<IExtraFileRepository<MetadataFile>>((_, _) => new List<MetadataFile>()),
                InterfaceProxy.Proxy<IExtraFileRepository<OtherExtraFile>>((_, _) => new List<OtherExtraFile>()),
                InterfaceProxy.Proxy<IAuthorRepository>((_, _) => new List<Author>()),
                InterfaceProxy.Proxy<IBookRepository>((_, _) => new List<Book>()),
                InterfaceProxy.Proxy<IConfigService>((_, _) => string.Empty));
            var file = new BookFile { Id = 9, EditionId = 0, Path = @"C:\library\unknown.epub".AsOsAgnostic(),
                ReplicaPaths = new List<string> { @"C:\audio\unknown.epub".AsOsAgnostic(), @"C:\audio\unknown.epub".AsOsAgnostic() } };

            var result = subject.Describe(new[] { file }).Single();
            Assert.That(result.Path, Is.EqualTo(file.Path));
            Assert.That(result.BookId, Is.Zero);
            Assert.That(result.AssociatedPaths, Is.EqualTo(new[] { @"C:\audio\unknown.epub".AsOsAgnostic() }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void deletion_preview_lists_linked_sidecars_regardless_of_other_media_in_the_folder(bool shared)
        {
            var primary = @"C:\library\Frank Herbert\Dune\Dune.m4b".AsOsAgnostic();
            var sidecar = @"C:\library\Frank Herbert\Dune\cover.jpg".AsOsAgnostic();
            var replica = @"C:\copies\Dune.m4b".AsOsAgnostic();
            var disk = InterfaceProxy.Proxy<IDiskProvider>((method, args) => method.Name switch
            {
                "FileExistsCanonical" => true,
                "GetFiles" => shared ? new[] { primary, sidecar, @"C:\library\Frank Herbert\Dune\Other.m4b".AsOsAgnostic() } : new[] { primary, sidecar },
                _ => throw new System.NotSupportedException(method.Name)
            });
            var subject = new ManualImportReplacementFiles(disk,
                InterfaceProxy.Proxy<IExtraFileRepository<MetadataFile>>((_, _) => new List<MetadataFile> {
                    new MetadataFile { Id = 1, BookFileId = 9, BookId = 7, RelativePath = "Dune/cover.jpg" } }),
                InterfaceProxy.Proxy<IExtraFileRepository<OtherExtraFile>>((_, _) => new List<OtherExtraFile>()),
                InterfaceProxy.Proxy<IAuthorRepository>((_, _) => new List<Author> { new Author { Id = 1, Path = @"C:\library\Frank Herbert".AsOsAgnostic() } }),
                InterfaceProxy.Proxy<IBookRepository>((_, _) => new List<Book> { new Book { Id = 7, AuthorId = 1 } }),
                InterfaceProxy.Proxy<IConfigService>((_, _) => string.Empty));
            var file = new BookFile { Id = 9, EditionId = 42, Edition = new Edition { Id = 42, BookId = 7, Title = "Dune" },
                Path = primary, ReplicaPaths = new List<string> { replica } };

            var result = subject.Describe(new[] { file }).Single();
            // Tracked association, not another media file in the folder, determines extras.
            Assert.That(result.AssociatedPaths, Is.EquivalentTo(new[] { replica, sidecar }));
        }
    }
}
