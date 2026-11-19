using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Organizer
{
    public interface IFilenameSampleService
    {
        SampleResult GetStandardTrackSample(NamingConfig nameSpec, string mediaType = "audiobook");
        SampleResult GetMultiDiscTrackSample(NamingConfig nameSpec, string mediaType = "audiobook");
        string GetAuthorFolderSample(NamingConfig nameSpec, string mediaType = "audiobook");
    }

    public class FileNameSampleService : IFilenameSampleService
    {
        private readonly IBuildFileNames _buildFileNames;

        public FileNameSampleService(IBuildFileNames buildFileNames)
        {
            _buildFileNames = buildFileNames;
        }

        public SampleResult GetStandardTrackSample(NamingConfig nameSpec, string mediaType = "audiobook")
        {
            return GetSample(nameSpec, mediaType, 1);
        }

        public SampleResult GetMultiDiscTrackSample(NamingConfig nameSpec, string mediaType = "audiobook")
        {
            var firstPart = GetSample(nameSpec, mediaType, 12, 1);
            var secondPart = GetSample(nameSpec, mediaType, 12, 2);
            if (string.Equals(firstPart.FileName, secondPart.FileName, DiskProviderBase.PathStringComparison))
            {
                firstPart.FileName = string.Empty;
            }

            return firstPart;
        }

        public string GetAuthorFolderSample(NamingConfig nameSpec, string mediaType = "audiobook")
        {
            return _buildFileNames.GetAuthorFolder(CreateAuthor(), nameSpec, mediaType);
        }

        private static Author CreateAuthor() => new Author
        {
            Name = "Jim Butcher",
            Disambiguation = "US Author",
            NameLastFirst = "Butcher, Jim"
        };

        private SampleResult GetSample(NamingConfig nameSpec, string mediaType, int partCount, int partNumber = 1)
        {
            // Samples own their format-specific metadata: concurrent ebook/audio requests must
            // neither share mutable files nor show MP3/narrator data for an ebook pattern.
            var isEbook = string.Equals(mediaType, "ebook", StringComparison.OrdinalIgnoreCase);
            var author = CreateAuthor();
            var book = new Book
            {
                Title = "Grave Peril",
                ReleaseDate = DateTime.Today,
                Author = author,
                MediaType = isEbook ? BookMediaType.Ebook : BookMediaType.Audiobook,
                SeriesLinks = new List<SeriesBookLink>
                {
                    new SeriesBookLink { Position = "3", Series = new Series { Title = "The Dresden Files", GoodreadsSeriesId = "gr:1",
                        MediaType = isEbook ? BookMediaType.Ebook : BookMediaType.Audiobook } }
                }
            };
            var edition = new Edition
            {
                Title = book.Title,
                Book = book,
                NarratorNames = isEbook ? new List<string>() : new List<string> { "James Marsters" }
            };
            var originalName = book.Title;
            // Preserve source identity per part so Original Title patterns can distinguish input files.
            if (partCount > 1)
            {
                originalName += " - Part " + partNumber.ToString(CultureInfo.InvariantCulture);
            }

            var file = new BookFile
            {
                Quality = new QualityModel(isEbook ? Quality.EPUB : Quality.MP3, new Revision(2)),
                MediaType = mediaType,
                Path = Path.Combine(Path.DirectorySeparatorChar.ToString(), "books", originalName + (isEbook ? ".epub" : ".mp3")),
                SceneName = originalName,
                ReleaseGroup = "RlsGrp",
                MediaInfo = isEbook ? null : new MediaInfoModel
                {
                    AudioFormat = "Flac Audio",
                    AudioChannels = 2,
                    AudioBitrate = 875,
                    AudioBits = 24,
                    AudioSampleRate = 44100
                },
                Edition = edition,
                Part = partNumber,
                PartCount = partCount
            };
            var customFormats = isEbook ? new List<CustomFormat>() : new List<CustomFormat>
            {
                new CustomFormat { Name = "Surround Sound", IncludeCustomFormatWhenRenaming = true },
                new CustomFormat { Name = "x264", IncludeCustomFormatWhenRenaming = true }
            };
            var result = new SampleResult { Author = author, Book = book, BookFile = file };
            try
            {
                result.FileName = _buildFileNames.BuildBookFileName(author, edition, file, nameSpec, customFormats);
            }
            catch (NamingFormatException)
            {
                result.FileName = string.Empty;
            }

            return result;
        }
    }
}
