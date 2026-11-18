using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using NLog;
using NzbDrone.Core.MediaFiles;
using NUnit.Framework;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class EpubSeriesTagWriterFixture
    {
        private static readonly XNamespace OpfNs = "http://www.idpf.org/2007/opf";

        private string _workingFolder;

        [SetUp]
        public void SetUp()
        {
            _workingFolder = Path.Combine(Path.GetTempPath(), "chaptarr-epub-tests", Path.GetRandomFileName());
            Directory.CreateDirectory(_workingFolder);
        }

        [TearDown]
        public void TearDown()
        {
            if (_workingFolder != null && Directory.Exists(_workingFolder))
            {
                Directory.Delete(_workingFolder, true);
            }
        }

        private EpubSeriesTagWriter Subject => new EpubSeriesTagWriter(LogManager.GetLogger("test"));

        [Test]
        public void should_write_series_name_and_index_when_epub_has_none()
        {
            var path = GivenEpub();

            var changed = Subject.WriteSeriesTags(path, "Example Series", 3);

            Assert.That(changed, Is.True);
            Assert.That(GetMeta(path, "calibre:series"), Is.EqualTo("Example Series"));
            Assert.That(GetMeta(path, "calibre:series_index"), Is.EqualTo("3"));
        }

        [Test]
        public void should_report_unchanged_when_series_tags_already_match()
        {
            var path = GivenEpub(existingSeries: "Example Series", existingSeriesIndex: "3");
            var before = File.ReadAllBytes(path);

            var changed = Subject.WriteSeriesTags(path, "Example Series", 3);

            Assert.That(changed, Is.False);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before), "the file should not be rewritten when nothing changes");
        }

        [Test]
        public void should_update_existing_series_tags_when_they_differ()
        {
            var path = GivenEpub(existingSeries: "Stale Series", existingSeriesIndex: "9");

            var changed = Subject.WriteSeriesTags(path, "Example Series", 3);

            Assert.That(changed, Is.True);
            Assert.That(GetMeta(path, "calibre:series"), Is.EqualTo("Example Series"));
            Assert.That(GetMeta(path, "calibre:series_index"), Is.EqualTo("3"));
        }

        [Test]
        public void should_write_tags_that_the_bundled_epub_reader_can_read_back()
        {
            var path = GivenEpub();

            Subject.WriteSeriesTags(path, "Example Series", 3);

            using var book = VersOne.Epub.EpubReader.OpenBook(path);
            var metaItems = book.Schema.Package.Metadata.MetaItems;

            Assert.That(metaItems.Any(x => x.Name == "calibre:series" && x.Content == "Example Series"), Is.True);
            Assert.That(metaItems.Any(x => x.Name == "calibre:series_index" && x.Content == "3"), Is.True);
        }

        [Test]
        public void should_keep_mimetype_first_and_uncompressed()
        {
            var path = GivenEpub();

            Subject.WriteSeriesTags(path, "Example Series", 3);

            using var archive = ZipFile.OpenRead(path);
            var first = archive.Entries.First();

            Assert.That(first.FullName, Is.EqualTo("mimetype"));
            Assert.That(first.CompressedLength, Is.EqualTo(first.Length), "mimetype must be stored uncompressed");
        }

        [Test]
        public void should_write_fractional_series_index_using_invariant_decimal_separator()
        {
            var path = GivenEpub();
            var originalCulture = CultureInfo.CurrentCulture;

            try
            {
                // A culture whose decimal separator is a comma would otherwise emit "11,5",
                // which readers cannot parse as a series position.
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");

                Subject.WriteSeriesTags(path, "Example Series", 11.5);
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
            }

            Assert.That(GetMeta(path, "calibre:series_index"), Is.EqualTo("11.5"));
        }

        [Test]
        public void should_write_series_name_and_omit_index_when_position_is_unknown()
        {
            var path = GivenEpub();

            var changed = Subject.WriteSeriesTags(path, "Example Series", null);

            // Grouping is what readers key off; an unparseable position should cost the book
            // its ordering, not its series.
            Assert.That(changed, Is.True);
            Assert.That(GetMeta(path, "calibre:series"), Is.EqualTo("Example Series"));
            Assert.That(GetMeta(path, "calibre:series_index"), Is.Null);
        }

        [Test]
        public void should_remove_stale_index_when_position_becomes_unknown()
        {
            var path = GivenEpub(existingSeries: "Example Series", existingSeriesIndex: "9");

            Subject.WriteSeriesTags(path, "Example Series", null);

            Assert.That(GetMeta(path, "calibre:series_index"), Is.Null);
        }

        [Test]
        public void should_not_reprefix_elements_it_did_not_need_to_touch()
        {
            var path = GivenEpub();

            Subject.WriteSeriesTags(path, "Example Series", 3);

            // An OPF package commonly binds a prefix to the same namespace URI it already uses
            // by default. Re-prefixing every element on the way out is namespace-equivalent but
            // rewrites far more of the reader's book than asked, and trips naive OPF parsers.
            var opf = GetOpfText(path);

            Assert.That(opf, Does.Contain("<metadata"));
            Assert.That(opf, Does.Not.Contain("<opf:metadata"));
            Assert.That(opf, Does.Contain("<meta name=\"calibre:series\""));
            Assert.That(opf, Does.Not.Contain("<opf:meta "));
        }

        [Test]
        public void should_indent_added_metadata_to_match_the_surrounding_document()
        {
            var path = GivenEpub();

            Subject.WriteSeriesTags(path, "Example Series", 3);

            var opf = GetOpfText(path);

            Assert.That(opf, Does.Contain("\n    <meta name=\"calibre:series\""));
            Assert.That(opf, Does.Contain("\n    <meta name=\"calibre:series_index\""));
            Assert.That(opf, Does.Contain("\n  </metadata>"));
        }

        [Test]
        public void should_leave_unrelated_metadata_untouched()
        {
            var path = GivenEpub();

            Subject.WriteSeriesTags(path, "Example Series", 3);

            var opf = GetOpfText(path);

            Assert.That(opf, Does.Contain("<dc:creator opf:role=\"aut\">An Example Author</dc:creator>"));
            Assert.That(opf, Does.Contain("<dc:title>An Example Book</dc:title>"));
        }

        // Builds a minimal, structurally valid EPUB 2 containing only text authored for this
        // test. The mimetype entry is written first and uncompressed, as the EPUB spec requires.
        private string GivenEpub(string existingSeries = null, string existingSeriesIndex = null)
        {
            var path = Path.Combine(_workingFolder, "book.epub");

            var metaItems = new StringBuilder();
            if (existingSeries != null)
            {
                metaItems.Append($"<meta name=\"calibre:series\" content=\"{existingSeries}\"/>");
            }

            if (existingSeriesIndex != null)
            {
                metaItems.Append($"<meta name=\"calibre:series_index\" content=\"{existingSeriesIndex}\"/>");
            }

            var opf = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<package xmlns=""http://www.idpf.org/2007/opf"" unique-identifier=""bookid"" version=""2.0"">
  <metadata xmlns:dc=""http://purl.org/dc/elements/1.1/"" xmlns:opf=""http://www.idpf.org/2007/opf"">
    <dc:title>An Example Book</dc:title>
    <dc:creator opf:role=""aut"">An Example Author</dc:creator>
    <dc:identifier id=""bookid"">urn:uuid:00000000-0000-0000-0000-000000000001</dc:identifier>
    <dc:language>en</dc:language>{metaItems}
  </metadata>
  <manifest>
    <item id=""chapter1"" href=""chapter1.xhtml"" media-type=""application/xhtml+xml""/>
  </manifest>
  <spine>
    <itemref idref=""chapter1""/>
  </spine>
</package>";

            var container = @"<?xml version=""1.0"" encoding=""utf-8""?>
<container version=""1.0"" xmlns=""urn:oasis:names:tc:opendocument:xmlns:container"">
  <rootfiles>
    <rootfile full-path=""OEBPS/content.opf"" media-type=""application/oebps-package+xml""/>
  </rootfiles>
</container>";

            var chapter = @"<?xml version=""1.0"" encoding=""utf-8""?>
<html xmlns=""http://www.w3.org/1999/xhtml""><head><title>Chapter One</title></head>
<body><p>This placeholder chapter exists only so the archive is a well-formed book.</p></body></html>";

            using (var stream = new FileStream(path, FileMode.Create))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
                WriteEntry(archive, "META-INF/container.xml", container, CompressionLevel.Optimal);
                WriteEntry(archive, "OEBPS/content.opf", opf, CompressionLevel.Optimal);
                WriteEntry(archive, "OEBPS/chapter1.xhtml", chapter, CompressionLevel.Optimal);
            }

            return path;
        }

        private static void WriteEntry(ZipArchive archive, string name, string content, CompressionLevel level)
        {
            using var entryStream = archive.CreateEntry(name, level).Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            entryStream.Write(bytes, 0, bytes.Length);
        }

        private static string GetOpfText(string epubPath)
        {
            using var archive = ZipFile.OpenRead(epubPath);
            using var opfStream = archive.GetEntry("OEBPS/content.opf").Open();
            using var reader = new StreamReader(opfStream);

            return reader.ReadToEnd();
        }

        private static string GetMeta(string epubPath, string name)
        {
            using var archive = ZipFile.OpenRead(epubPath);
            using var opfStream = archive.GetEntry("OEBPS/content.opf").Open();

            return XDocument.Load(opfStream)
                .Descendants(OpfNs + "meta")
                .Where(x => (string)x.Attribute("name") == name)
                .Select(x => (string)x.Attribute("content"))
                .FirstOrDefault();
        }
    }
}
