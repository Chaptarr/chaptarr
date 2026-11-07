using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Consumers;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.Extras.Metadata
{
    [TestFixture]
    public class LegacyOpfMetadataFixture
    {
        [Test]
        public void should_discover_legacy_opf_sidecars_with_the_current_consumer_identity()
        {
            var subject = new LegacyAudiobookshelfMetadata();
            var authorPath = Path.Combine(Path.GetTempPath(), "bookshelfng-opf-fixture", "Fixture Author");
            var author = new Author { Id = 7, Path = authorPath };

            var result = subject.FindMetadataFile(author, Path.Combine(authorPath, "Fixture Book", "metadata.OPF"));

            Assert.Multiple(() =>
            {
                Assert.That(result.AuthorId, Is.EqualTo(7));
                Assert.That(result.Consumer, Is.EqualTo("LegacyAudiobookshelfMetadata"));
                Assert.That(result.RelativePath, Is.EqualTo(Path.Combine("Fixture Book", "metadata.OPF")));
                Assert.That(result.Extension, Is.EqualTo(".OPF"));
                Assert.That(result.Type, Is.EqualTo(MetadataType.BookMetadata));
                Assert.That(subject.FindMetadataFile(author, Path.Combine(authorPath, "cover.jpg")), Is.Null);
            });
        }

        [Test]
        public void should_write_legacy_opf_metadata_for_the_first_audio_part()
        {
            var subject = new LegacyAudiobookshelfMetadata();
            var authorPath = Path.Combine(Path.GetTempPath(), "bookshelfng-opf-fixture", "Fixture Author");
            var author = new Author { Id = 7, Name = "Fixture Author", Path = authorPath };
            var book = new Book
            {
                Id = 10,
                Title = "Fixture Book",
                BaseBookId = "hc:fixture-book",
                Genres = new List<string> { "Fantasy" },
                ReleaseDate = new DateTime(2024, 5, 6),
                SeriesLinks = new List<SeriesBookLink>
                {
                    new SeriesBookLink
                    {
                        Series = new Series { Id = 13, Title = "Fixture Series" },
                        SeriesPosition = 3
                    }
                }
            };
            var edition = new Edition
            {
                Id = 11,
                BookId = book.Id,
                Book = book,
                Title = "Fixture Edition",
                Isbn13 = "9780000000002",
                Asin = "B0FIXTURE",
                Overview = "Fixture overview",
                Publisher = "Fixture Press",
                Language = "eng",
                ReleaseDate = new DateTime(2024, 5, 6)
            };
            var bookFile = new BookFile
            {
                Id = 12,
                EditionId = edition.Id,
                Edition = edition,
                MediaType = "audiobook",
                Path = Path.Combine(authorPath, "Fixture Book", "book.m4b"),
                Part = 1
            };

            var result = subject.BookMetadata(author, bookFile);
            var document = XDocument.Parse(result.Contents);
            XNamespace opf = "http://www.idpf.org/2007/opf";
            XNamespace dc = "http://purl.org/dc/elements/1.1/";

            Assert.Multiple(() =>
            {
                Assert.That(result.RelativePath, Is.EqualTo(Path.Combine("Fixture Book", "book.opf")));
                Assert.That((string)document.Root.Attribute("version"), Is.EqualTo("2.0"));
                Assert.That((string)document.Root.Element(opf + "metadata").Element(dc + "title"), Is.EqualTo("Fixture Edition"));
                Assert.That((string)document.Root.Element(opf + "metadata").Element(dc + "identifier"), Is.EqualTo("9780000000002"));
                Assert.That((string)document.Root.Element(opf + "metadata").Element(dc + "description"), Is.EqualTo("Fixture overview"));
                Assert.That((string)document.Root.Element(opf + "metadata").Element(dc + "publisher"), Is.EqualTo("Fixture Press"));
                Assert.That(document.Root.Element(opf + "metadata").Element(dc + "subject"), Is.Not.Null);
                Assert.That(document.Root.Element(opf + "metadata").Elements(opf + "meta")
                    .Single(element => (string)element.Attribute("name") == "calibre:series").Attribute("content").Value, Is.EqualTo("Fixture Series"));
                Assert.That(document.Root.Element(opf + "metadata").Elements(opf + "meta")
                    .Single(element => (string)element.Attribute("name") == "calibre:series_index").Attribute("content").Value, Is.EqualTo("3"));
            });

            edition.Isbn13 = null;
            edition.Asin = null;
            var fallbackDocument = XDocument.Parse(subject.BookMetadata(author, bookFile).Contents);
            Assert.That(
                (string)fallbackDocument.Root.Element(opf + "metadata").Element(dc + "identifier"),
                Is.EqualTo("urn:bookshelfng:book:hc:fixture-book"));

            bookFile.Part = 2;
            Assert.That(subject.BookMetadata(author, bookFile), Is.Null);
        }
    }
}
