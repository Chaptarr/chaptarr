using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using NzbDrone.Core.Books;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.Extras.Metadata.Consumers
{
    public class LegacyAudiobookshelfMetadata : MetadataBase<NullConfig>
    {
        private const string OpfNamespace = "http://www.idpf.org/2007/opf";
        private const string DcNamespace = "http://purl.org/dc/elements/1.1/";

        public override string Name => "Audiobookshelf OPF";

        public override MetadataFile FindMetadataFile(Author author, string path)
        {
            if (!string.Equals(Path.GetExtension(path), ".opf", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new MetadataFile
            {
                AuthorId = author.Id,
                Consumer = GetType().Name,
                Type = MetadataType.BookMetadata,
                RelativePath = Path.GetRelativePath(author.Path, path),
                Extension = Path.GetExtension(path)
            };
        }

        public override MetadataFileResult AuthorMetadata(Author author)
        {
            return null;
        }

        public override MetadataFileResult BookMetadata(Author author, BookFile bookFile)
        {
            if (bookFile.Part > 1 ||
                string.IsNullOrWhiteSpace(bookFile.Path) ||
                !MediaFileExtensions.AudioExtensions.Contains(Path.GetExtension(bookFile.Path)))
            {
                return null;
            }

            var edition = bookFile.Edition;
            var book = edition?.Book;
            if (edition == null || book == null)
            {
                return null;
            }

            var authorName = bookFile.Author?.Name;
            if (string.IsNullOrWhiteSpace(authorName))
            {
                authorName = book.Author?.Name;
            }

            var relativePath = Path.GetRelativePath(author.Path, Path.ChangeExtension(bookFile.Path, ".opf"));
            return new MetadataFileResult(relativePath, CreateOpf(edition, book, authorName));
        }

        public override List<ImageFileResult> AuthorImages(Author author)
        {
            return new List<ImageFileResult>();
        }

        public override List<ImageFileResult> BookImages(Author author, BookFile bookFile)
        {
            return new List<ImageFileResult>();
        }

        private static string CreateOpf(Edition edition, Book book, string authorName)
        {
            var output = new StringBuilder();
            var settings = new XmlWriterSettings
            {
                Indent = true,
                OmitXmlDeclaration = true
            };

            using (var writer = XmlWriter.Create(output, settings))
            {
                writer.WriteStartElement("package", OpfNamespace);
                writer.WriteAttributeString("version", "2.0");
                writer.WriteAttributeString("unique-identifier", "BookId");

                writer.WriteStartElement("metadata", OpfNamespace);
                writer.WriteAttributeString("xmlns", "dc", null, DcNamespace);
                writer.WriteAttributeString("xmlns", "opf", null, OpfNamespace);

                WriteDcElement(writer, "title", edition.Title ?? book.Title);
                WriteCreator(writer, authorName, "aut");

                var isbn = edition.Isbn13;
                var asin = edition.Asin;
                var primaryScheme = !string.IsNullOrWhiteSpace(isbn)
                    ? "ISBN"
                    : (!string.IsNullOrWhiteSpace(asin) ? "ASIN" : "URI");
                var bookId = book.BaseBookId ??
                    book.HardcoverBookId ??
                    book.GoodreadsWorkId ??
                    book.OpenLibraryWorkId ??
                    book.Id.ToString(CultureInfo.InvariantCulture);
                var primaryIdentifier = !string.IsNullOrWhiteSpace(isbn)
                    ? isbn
                    : (!string.IsNullOrWhiteSpace(asin)
                        ? asin
                        : $"urn:bookshelfng:book:{bookId}");

                WriteIdentifier(writer, primaryIdentifier, primaryScheme, true);
                if (!string.IsNullOrWhiteSpace(isbn) && !string.IsNullOrWhiteSpace(asin))
                {
                    WriteIdentifier(writer, asin, "ASIN", false);
                }

                WriteDcElement(writer, "description", edition.Overview);
                WriteDcElement(writer, "publisher", edition.Publisher);
                WriteDcElement(writer, "language", edition.Language);

                var releaseDate = edition.ReleaseDate ?? book.ReleaseDate;
                if (releaseDate.HasValue)
                {
                    WriteDcElement(writer, "date", releaseDate.Value.ToString("yyyy", CultureInfo.InvariantCulture));
                }

                foreach (var genre in book.Genres ?? new List<string>())
                {
                    WriteDcElement(writer, "subject", genre);
                }

                WriteSeries(writer, book);

                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            return output.ToString();
        }

        private static void WriteDcElement(XmlWriter writer, string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            writer.WriteElementString("dc", name, DcNamespace, value);
        }

        private static void WriteCreator(XmlWriter writer, string name, string role)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            writer.WriteStartElement("dc", "creator", DcNamespace);
            writer.WriteAttributeString("opf", "role", OpfNamespace, role);
            writer.WriteString(name);
            writer.WriteEndElement();
        }

        private static void WriteIdentifier(XmlWriter writer, string value, string scheme, bool isPrimary)
        {
            writer.WriteStartElement("dc", "identifier", DcNamespace);
            if (isPrimary)
            {
                writer.WriteAttributeString("id", "BookId");
            }

            writer.WriteAttributeString("opf", "scheme", OpfNamespace, scheme);
            writer.WriteString(value);
            writer.WriteEndElement();
        }

        private static void WriteSeries(XmlWriter writer, Book book)
        {
            var preferredSeries = book.Author?.GetMetadataProfileFor(book.MediaType)?.PreferredSeries;
            var seriesLink = BookSeriesLabel.SelectDisplayLink(book.SeriesLinks, preferredSeries);

            var seriesTitle = seriesLink?.Series?.Value?.Title;
            if (string.IsNullOrWhiteSpace(seriesTitle))
            {
                return;
            }

            writer.WriteStartElement("meta", OpfNamespace);
            writer.WriteAttributeString("name", "calibre:series");
            writer.WriteAttributeString("content", seriesTitle);
            writer.WriteEndElement();

            var seriesIndex = seriesLink.Position;
            if (string.IsNullOrWhiteSpace(seriesIndex) && seriesLink.SeriesPosition > 0)
            {
                seriesIndex = seriesLink.SeriesPosition.ToString(CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrWhiteSpace(seriesIndex))
            {
                writer.WriteStartElement("meta", OpfNamespace);
                writer.WriteAttributeString("name", "calibre:series_index");
                writer.WriteAttributeString("content", seriesIndex);
                writer.WriteEndElement();
            }
        }
    }
}
