using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Datastore
{
    internal sealed class BookshelfBookMediaState
    {
        public HashSet<int> MediaTypes { get; } = new();
        public Dictionary<int, string> MonitoredEditionIdsByMediaType { get; } = new();
        public List<BookshelfEditionMediaState> Editions { get; } = new();
    }

    internal sealed class BookshelfEditionMediaState
    {
        public int Id { get; init; }
        public int SourceBookId { get; init; }
        public string ForeignEditionId { get; init; }
        public bool Monitored { get; set; }
        public bool IsAmbiguous { get; set; }
        public int? PreferredMediaType { get; set; }
        public HashSet<int> MediaTypes { get; } = new();
        public HashSet<int> ProviderMediaTypes { get; } = new();
        public Dictionary<int, int> ReadingFormatIdsByMediaType { get; } = new();
    }

    internal sealed class BookshelfMediaTypeInference
    {
        private static readonly HashSet<string> AudiobookEditionFormats = new(StringComparer.OrdinalIgnoreCase)
        {
            "Audiobook", "Audio CD", "Audio Cassette", "Audible Audio", "CD-ROM", "MP3 CD"
        };

        private static readonly HashSet<string> EbookEditionFormats = new(StringComparer.OrdinalIgnoreCase)
        {
            "Kindle Edition", "Nook", "ebook", "Digital"
        };

        private static readonly string[] AudiobookFormatMarkers =
        {
            "audio", "audible", "audiobook", "audioble", "eaudio", "mp3", "cassette", "playaway",
            "graphicaudio", "soundbooth", "libro.fm", "dramat", "overdrive listen", "radio drama",
            "performance", "podcast", "compact disc", "cd-audio", "cartridge", "downpour", "unabridged"
        };

        private static readonly string[] EbookFormatMarkers =
        {
            "ebook", "e-book", "kindle", "nook", "kobo", "epub", "mobi", "azw", "pdf",
            "online publication", "adobe digital", "libby ebook"
        };

        private static readonly string[] PhysicalFormatMarkers =
        {
            "paper", "hardcover", "hardback", "binding", "bound", "cloth", "leather", "physical",
            "print", "comic", "magazine", "textbook", "microform", "microfiche", "spiral",
            "pocket", "loose leaf", "board book", "turtleback", "unbound", "chapbook"
        };

        private readonly Dictionary<int, BookshelfEditionMediaState> _editions = new();

        private BookshelfMediaTypeInference()
        {
        }

        public Dictionary<int, BookshelfBookMediaState> Books { get; } = new();

        public IReadOnlyDictionary<int, BookshelfEditionMediaState> Editions => _editions;

        public static BookshelfMediaTypeInference Read(DbConnection source)
        {
            var result = new BookshelfMediaTypeInference();
            using var command = source.CreateCommand();
            command.CommandText = @"
                SELECT e.""Id"",
                       e.""BookId"",
                       e.""IsEbook"",
                       e.""Format"",
                       e.""Narrator"",
                       e.""Monitored"",
                       e.""ForeignEditionId"",
                       bf.""Path""
                  FROM ""Editions"" e
                  LEFT JOIN ""BookFiles"" bf ON bf.""EditionId"" = e.""Id""
                 ORDER BY e.""Id"", bf.""Id"";";

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var editionId = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                    if (!result._editions.TryGetValue(editionId, out var edition))
                    {
                        var sourceBookId = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                        edition = new BookshelfEditionMediaState
                        {
                            Id = editionId,
                            SourceBookId = sourceBookId,
                            ForeignEditionId = reader.IsDBNull(6) ? null : Convert.ToString(reader.GetValue(6), CultureInfo.InvariantCulture),
                            Monitored = !reader.IsDBNull(5) && Convert.ToBoolean(reader.GetValue(5), CultureInfo.InvariantCulture)
                        };
                        result._editions.Add(editionId, edition);
                        if (!result.Books.TryGetValue(sourceBookId, out var book))
                        {
                            book = new BookshelfBookMediaState();
                            result.Books.Add(sourceBookId, book);
                        }

                        book.Editions.Add(edition);

                        var isEbook = !reader.IsDBNull(2) && Convert.ToBoolean(reader.GetValue(2), CultureInfo.InvariantCulture);
                        var format = reader.IsDBNull(3) ? null : Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture)?.Trim();
                        var narrator = reader.IsDBNull(4) ? null : Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture)?.Trim();
                        if (isEbook)
                        {
                            edition.PreferredMediaType = 1;
                            AddSourceMediaType(edition, 1, 3);
                        }
                        else
                        {
                            var hasAudioFormat = !string.IsNullOrWhiteSpace(narrator) || IsFormat(format, AudiobookEditionFormats, AudiobookFormatMarkers);
                            var hasEbookFormat = IsFormat(format, EbookEditionFormats, EbookFormatMarkers);
                            var hasPhysicalFormat = IsPhysicalFormat(format);

                            if (hasAudioFormat)
                            {
                                AddSourceMediaType(edition, 0, 2);
                            }

                            if (hasEbookFormat)
                            {
                                AddSourceMediaType(edition, 1, 3);
                            }
                            else if (hasPhysicalFormat)
                            {
                                AddSourceMediaType(edition, 1, 1);
                            }

                            if (edition.MediaTypes.Count == 1)
                            {
                                edition.PreferredMediaType = edition.MediaTypes.Single();
                            }
                            else if (edition.MediaTypes.Count > 1)
                            {
                                edition.PreferredMediaType = hasPhysicalFormat ? 1 : 0;
                            }
                        }
                    }

                    if (!reader.IsDBNull(7) && TryGetMediaTypeFromPath(Convert.ToString(reader.GetValue(7), CultureInfo.InvariantCulture), out var fileMediaType))
                    {
                        AddMediaType(edition, fileMediaType, fileMediaType == 1 ? 3 : 2);
                        edition.PreferredMediaType ??= fileMediaType;
                    }
                }
            }

            foreach (var edition in result._editions.Values.Where(x => x.MediaTypes.Count == 0))
            {
                // BookshelfNG uses IsEbook=false for both audio and print editions. Keep both
                // candidates when neither metadata nor a linked file identifies the format;
                // the per-book map can narrow this safely before the target rows are written.
                AddSourceMediaType(edition, 0, 2);
                AddMediaType(edition, 1, 1);
                edition.PreferredMediaType = 0;
                edition.IsAmbiguous = true;
            }

            result.RebuildBookMediaStates();
            return result;
        }

        public void ApplyMediaChoices(IReadOnlyDictionary<int, BookshelfMediaChoice> mediaChoices)
        {
            foreach (var edition in _editions.Values.Where(x => x.IsAmbiguous))
            {
                if (!mediaChoices.TryGetValue(edition.SourceBookId, out var choice))
                {
                    throw new InvalidDataException(
                        $"BookshelfNG edition {edition.Id} has an unresolved media type. The source database was not changed.");
                }

                var selectedMediaTypes = choice switch
                {
                    BookshelfMediaChoice.Audiobook => new[] { 0 },
                    BookshelfMediaChoice.Ebook => new[] { 1 },
                    BookshelfMediaChoice.Both or BookshelfMediaChoice.BothUnmonitored => new[] { 0, 1 },
                    _ => throw new InvalidDataException($"BookshelfNG edition {edition.Id} has an invalid media choice.")
                };

                edition.MediaTypes.Clear();
                edition.ReadingFormatIdsByMediaType.Clear();
                edition.ProviderMediaTypes.Clear();
                foreach (var mediaType in selectedMediaTypes)
                {
                    AddMediaType(edition, mediaType, mediaType == 0 ? 2 : 1);
                }

                // Keep a single provider identity on the selected row. For a both-format
                // choice, preserve the legacy IsEbook=false identity on the audiobook row.
                var providerMediaType = choice == BookshelfMediaChoice.Ebook ? 1 : 0;
                edition.ProviderMediaTypes.Add(providerMediaType);
                edition.PreferredMediaType = selectedMediaTypes.Contains(providerMediaType)
                    ? providerMediaType
                    : selectedMediaTypes[0];
                edition.Monitored = choice != BookshelfMediaChoice.BothUnmonitored && edition.Monitored;
                edition.IsAmbiguous = false;
            }

            RebuildBookMediaStates();
        }

        internal static bool TryGetMediaTypeFromPath(string path, out int mediaType)
        {
            var extension = Path.GetExtension(path ?? string.Empty);
            if (MediaFileExtensions.AudioExtensions.Contains(extension))
            {
                mediaType = 0;
                return true;
            }

            if (MediaFileExtensions.TextExtensions.Contains(extension))
            {
                mediaType = 1;
                return true;
            }

            mediaType = default;
            return false;
        }

        private void RebuildBookMediaStates()
        {
            foreach (var book in Books.Values)
            {
                book.MediaTypes.Clear();
                book.MonitoredEditionIdsByMediaType.Clear();
                foreach (var edition in book.Editions)
                {
                    book.MediaTypes.UnionWith(edition.MediaTypes);
                    if (!edition.Monitored)
                    {
                        continue;
                    }

                    foreach (var mediaType in edition.MediaTypes)
                    {
                        var providerIdentityApplies = edition.ProviderMediaTypes.Contains(mediaType) ||
                                                      (edition.ProviderMediaTypes.Count == 0 && edition.MediaTypes.Count == 1);
                        var providerId = providerIdentityApplies ? edition.ForeignEditionId : null;
                        if (!book.MonitoredEditionIdsByMediaType.TryAdd(mediaType, providerId))
                        {
                            var format = mediaType == 1 ? "ebook" : "audiobook";
                            throw new InvalidDataException(
                                $"BookshelfNG book {edition.SourceBookId} has more than one monitored {format} edition; conversion stopped before promotion.");
                        }
                    }
                }
            }
        }

        private static void AddMediaType(BookshelfEditionMediaState edition, int mediaType, int readingFormatId)
        {
            edition.MediaTypes.Add(mediaType);
            edition.ReadingFormatIdsByMediaType.TryAdd(mediaType, readingFormatId);
        }

        private static void AddSourceMediaType(BookshelfEditionMediaState edition, int mediaType, int readingFormatId)
        {
            AddMediaType(edition, mediaType, readingFormatId);
            edition.ProviderMediaTypes.Add(mediaType);
        }

        private static bool IsFormat(string format, ISet<string> exactMatches, IEnumerable<string> markers)
        {
            if (string.IsNullOrWhiteSpace(format))
            {
                return false;
            }

            return exactMatches.Contains(format) || markers.Any(marker => format.Contains(marker, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsPhysicalFormat(string format)
        {
            return !string.IsNullOrWhiteSpace(format) &&
                   PhysicalFormatMarkers.Any(marker => format.Contains(marker, StringComparison.OrdinalIgnoreCase));
        }
    }
}
