using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.IO.Compression;
using System.Linq;
using System.Security;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class EbookTagServiceIsbnTagFixture
    {
        [TestCase("urn:isbn:9780306406157", null, "9780306406157")]
        [TestCase("URN:ISBN:0-306-40615-2", null, "0306406152")]
        [TestCase("urn:isbn:0-8044-2957-x", null, "080442957X")]
        [TestCase("978-0-306-40615-7", null, "9780306406157")]
        [TestCase("urn:uuid:9780306406157", null, null)]
        [TestCase("urn:isbn:9780306406158", null, null)]
        [TestCase("9780306406157", "DOI", null)]
        [TestCase("9780306406157", "ISBN", "9780306406157")]
        public void epub_reader_should_recognize_isbn_identifiers_without_legacy_scheme_attributes(string identifier, string scheme, string expected)
        {
            var folder = Path.Combine(Path.GetTempPath(), "chaptarr-epub-isbn-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "isbn.epub");
            try
            {
                using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    var version = scheme == null ? "3.0" : "2.0";
                    var schemeAttribute = scheme == null ? string.Empty : " opf:scheme=\"" + scheme + "\"";
                    var entries = new Dictionary<string, string>
                    {
                        ["mimetype"] = "application/epub+zip",
                        ["META-INF/container.xml"] = "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"package.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>",
                        ["package.opf"] = "<package xmlns=\"http://www.idpf.org/2007/opf\" xmlns:opf=\"http://www.idpf.org/2007/opf\" version=\"" + version + "\" unique-identifier=\"bookid\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"bookid\"" + schemeAttribute + ">" + SecurityElement.Escape(identifier) + "</dc:identifier><dc:title>ISBN fixture</dc:title><dc:language>en</dc:language></metadata><manifest><item id=\"text\" href=\"text.xhtml\" media-type=\"application/xhtml+xml\"/></manifest><spine><itemref idref=\"text\"/></spine></package>",
                        ["text.xhtml"] = "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>ISBN fixture</title></head><body><p>Original test content.</p></body></html>"
                    };
                    foreach (var entry in entries)
                    {
                        using var writer = new StreamWriter(archive.CreateEntry(entry.Key).Open());
                        writer.Write(entry.Value);
                    }
                }

                var service = new EBookTagService(null, null, null, null, null, null, null, LogManager.GetCurrentClassLogger());
                var tags = service.ReadAllTags(new FileSystem().FileInfo.New(path));

                Assert.That(tags.GetValueOrDefault("isbn")?.Single(), Is.EqualTo(expected));
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        [TestCase("not an isbn")]
        [TestCase("1234567890")]
        [TestCase("   ")]
        [TestCase("")]
        [TestCase(null)]
        public void should_not_store_an_isbn_tag_when_the_isbn_cannot_be_parsed(string raw)
        {
            var tags = new Dictionary<string, string>();

            EBookTagService.AddIsbnTag(tags, raw);

            Assert.That(tags.ContainsKey("isbn"), Is.False);
            Assert.That(tags.Values.Any(v => v == null), Is.False);
        }

        [TestCase("978-0-306-40615-7", "9780306406157")]
        [TestCase("0-306-40615-2", "0306406152")]
        public void should_store_the_stripped_isbn_when_it_is_valid(string raw, string expected)
        {
            var tags = new Dictionary<string, string>();

            EBookTagService.AddIsbnTag(tags, raw);

            Assert.That(tags["isbn"], Is.EqualTo(expected));
        }

        [Test]
        public void should_prefer_valid_978_then_979_identifiers_for_epub_metadata()
        {
            var preferred978 = EBookTagService.SelectPreferredIsbn(new[]
            {
                "9790000000001",
                "0306406152",
                "9780306406157"
            });
            var preferred979 = EBookTagService.SelectPreferredIsbn(new[]
            {
                "0306406152",
                "9790000000001"
            });

            Assert.That(preferred978, Is.EqualTo("9780306406157"));
            Assert.That(preferred979, Is.EqualTo("9790000000001"));
        }
    }
}
