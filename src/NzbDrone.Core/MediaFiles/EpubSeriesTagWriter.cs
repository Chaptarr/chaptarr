using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using NLog;

namespace NzbDrone.Core.MediaFiles
{
    public interface IEpubSeriesTagWriter
    {
        bool WriteSeriesTags(string epubPath, string seriesName, double? seriesIndex);
    }

    /// <summary>
    /// Writes Calibre-style series metadata directly into an epub's OPF package document.
    ///
    /// Chaptarr's other tag writer talks to a Calibre content server, which most users do not
    /// run. Readers such as Kavita group books into series purely from the calibre:series and
    /// calibre:series_index meta elements, so without this the series Chaptarr already knows
    /// never reaches the reader.
    /// </summary>
    public class EpubSeriesTagWriter : IEpubSeriesTagWriter
    {
        private const string SeriesMetaName = "calibre:series";
        private const string SeriesIndexMetaName = "calibre:series_index";
        private const string MimetypeEntryName = "mimetype";

        private static readonly XNamespace ContainerNs = "urn:oasis:names:tc:opendocument:xmlns:container";

        private readonly Logger _logger;

        public EpubSeriesTagWriter(Logger logger)
        {
            _logger = logger;
        }

        public bool WriteSeriesTags(string epubPath, string seriesName, double? seriesIndex)
        {
            string opfPath;
            XmlDocument opf;

            using (var archive = ZipFile.OpenRead(epubPath))
            {
                opfPath = GetOpfPath(archive);

                if (opfPath == null)
                {
                    _logger.Debug("No OPF package document found in {0}, not writing series tags", epubPath);
                    return false;
                }

                using var opfStream = archive.GetEntry(opfPath).Open();

                // PreserveWhitespace keeps the reader's own formatting, and XmlDocument writes
                // every node back with the prefix it was read with. XDocument re-derives
                // prefixes, which silently rewrites <metadata> as <opf:metadata> whenever the
                // package binds a prefix to the namespace it already uses by default.
                opf = new XmlDocument { PreserveWhitespace = true };
                opf.Load(opfStream);
            }

            if (!ApplySeriesTags(opf, seriesName, seriesIndex))
            {
                return false;
            }

            RewriteArchive(epubPath, opfPath, opf);

            return true;
        }

        private static string GetOpfPath(ZipArchive archive)
        {
            var containerEntry = archive.GetEntry("META-INF/container.xml");

            if (containerEntry == null)
            {
                return null;
            }

            using var containerStream = containerEntry.Open();

            return XDocument.Load(containerStream)
                .Descendants(ContainerNs + "rootfile")
                .Select(x => (string)x.Attribute("full-path"))
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        }

        private static bool ApplySeriesTags(XmlDocument opf, string seriesName, double? seriesIndex)
        {
            var package = opf.DocumentElement;

            if (package == null)
            {
                return false;
            }

            var metadata = package.ChildNodes.OfType<XmlElement>()
                .FirstOrDefault(x => x.LocalName == "metadata" && x.NamespaceURI == package.NamespaceURI);

            if (metadata == null)
            {
                return false;
            }

            var changed = UpsertMeta(metadata, SeriesMetaName, seriesName);

            // Calibre stores the index as a plain decimal number; anything else makes readers
            // fall back to treating the book as unpositioned.
            var index = seriesIndex?.ToString("0.##", CultureInfo.InvariantCulture);

            return UpsertMeta(metadata, SeriesIndexMetaName, index) || changed;
        }

        private static bool UpsertMeta(XmlElement metadata, string name, string content)
        {
            var existing = metadata.ChildNodes.OfType<XmlElement>()
                .FirstOrDefault(x => x.LocalName == "meta" &&
                                     x.NamespaceURI == metadata.NamespaceURI &&
                                     x.GetAttribute("name") == name);

            // A null value means "we have nothing trustworthy to say about this field", so any
            // stale value is cleared rather than left to contradict the rest of the metadata.
            if (content == null)
            {
                if (existing == null)
                {
                    return false;
                }

                RemoveWithLeadingWhitespace(metadata, existing);
                return true;
            }

            if (existing != null)
            {
                if (existing.GetAttribute("content") == content)
                {
                    return false;
                }

                existing.SetAttribute("content", content);
                return true;
            }

            AppendMeta(metadata, name, content);
            return true;
        }

        /// <summary>
        /// Adds a meta element carrying the same prefix as the metadata element around it, so the
        /// addition is indistinguishable in style from what the book already contained.
        /// </summary>
        private static void AppendMeta(XmlElement metadata, string name, string content)
        {
            var element = metadata.OwnerDocument.CreateElement(metadata.Prefix, "meta", metadata.NamespaceURI);
            element.SetAttribute("name", name);
            element.SetAttribute("content", content);

            var lastElement = metadata.ChildNodes.OfType<XmlElement>().LastOrDefault();

            if (lastElement == null)
            {
                metadata.AppendChild(element);
                return;
            }

            // Sit directly after the last entry, borrowing its indentation, so the whitespace
            // that already closes the element is left exactly as the reader wrote it.
            metadata.InsertAfter(element, lastElement);

            if (IsWhitespace(lastElement.PreviousSibling))
            {
                metadata.InsertBefore(metadata.OwnerDocument.CreateWhitespace(lastElement.PreviousSibling.Value), element);
            }
        }

        // With PreserveWhitespace on, whitespace-only nodes are XmlWhitespace rather than
        // XmlText, and the two are siblings in the type hierarchy rather than one deriving
        // from the other.
        private static bool IsWhitespace(XmlNode node)
        {
            return node is XmlCharacterData data && string.IsNullOrWhiteSpace(data.Value);
        }

        private static void RemoveWithLeadingWhitespace(XmlElement metadata, XmlElement element)
        {
            if (IsWhitespace(element.PreviousSibling))
            {
                metadata.RemoveChild(element.PreviousSibling);
            }

            metadata.RemoveChild(element);
        }

        /// <summary>
        /// Rewrites the archive to a sibling temporary file and moves it into place, so a failure
        /// part-way through can never leave the user with a truncated book.
        /// </summary>
        private void RewriteArchive(string epubPath, string opfPath, XmlDocument opf)
        {
            var tempPath = epubPath + ".chaptarr-tmp";

            try
            {
                using (var source = ZipFile.OpenRead(epubPath))
                using (var destStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                using (var dest = new ZipArchive(destStream, ZipArchiveMode.Create))
                {
                    foreach (var entry in OrderEntries(source))
                    {
                        // The spec requires mimetype to be the first entry and stored uncompressed.
                        var level = entry.FullName == MimetypeEntryName
                            ? CompressionLevel.NoCompression
                            : CompressionLevel.Optimal;

                        var destEntry = dest.CreateEntry(entry.FullName, level);
                        destEntry.LastWriteTime = entry.LastWriteTime;

                        using var destEntryStream = destEntry.Open();

                        if (entry.FullName == opfPath)
                        {
                            // No BOM and no re-indenting: the only intended difference between
                            // the old package document and the new one is the series metadata.
                            var settings = new XmlWriterSettings
                            {
                                Encoding = new UTF8Encoding(false),
                                Indent = false,
                                CloseOutput = false
                            };

                            using var xmlWriter = XmlWriter.Create(destEntryStream, settings);
                            opf.Save(xmlWriter);
                        }
                        else
                        {
                            using var sourceEntryStream = entry.Open();
                            sourceEntryStream.CopyTo(destEntryStream);
                        }
                    }
                }

                File.Move(tempPath, epubPath, true);
            }
            catch
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                throw;
            }
        }

        private static IEnumerable<ZipArchiveEntry> OrderEntries(ZipArchive archive)
        {
            return archive.Entries
                .OrderByDescending(x => x.FullName == MimetypeEntryName)
                .ToList();
        }
    }
}
