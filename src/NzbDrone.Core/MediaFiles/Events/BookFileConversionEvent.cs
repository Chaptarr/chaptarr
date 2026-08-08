using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles.Events
{
    public class BookFileConvertedEvent : IEvent
    {
        public Author Author { get; }
        public Book Book { get; }
        public Edition Edition { get; }
        public QualityModel SourceQuality { get; }
        public QualityModel TargetQuality { get; }
        public string SourceTitle { get; }
        public List<string> SourcePaths { get; }
        public string ConvertedPath { get; }
        public string ImportedPath { get; }
        public long? OutputSize { get; }
        public string Message { get; }
        public string TagMode { get; }
        public string TagManifestJson { get; }
        public DownloadClientItemClientInfo DownloadClientInfo { get; }
        public string DownloadId { get; }

        public BookFileConvertedEvent(LocalBook bookInfo, BookFile importedBook, Author author, Book book, DownloadClientItem downloadClientItem, string message = null)
        {
            Author = author ?? bookInfo?.Author ?? importedBook?.Author;
            Book = book ?? bookInfo?.Book ?? importedBook?.Edition?.Book;
            Edition = importedBook?.Edition ?? bookInfo?.Edition;
            SourceQuality = bookInfo?.GeneratedConversionSourceQuality ?? bookInfo?.Quality;
            TargetQuality = importedBook?.Quality ?? bookInfo?.Quality;
            SourcePaths = bookInfo?.GeneratedConversionSourcePaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? new List<string>();
            ConvertedPath = bookInfo?.GeneratedConversionOutputPath ?? bookInfo?.Path;
            ImportedPath = importedBook?.Path;
            OutputSize = importedBook?.Size > 0 ? importedBook.Size : bookInfo?.GeneratedConversionOutputSize;
            Message = message;
            TagMode = bookInfo?.GeneratedConversionTagMode;
            TagManifestJson = bookInfo?.GeneratedConversionTagManifestJson;
            SourceTitle = bookInfo?.SceneName ?? Path.GetFileNameWithoutExtension(SourcePaths.FirstOrDefault() ?? ConvertedPath ?? importedBook?.Path);

            if (downloadClientItem != null)
            {
                DownloadClientInfo = downloadClientItem.DownloadClientInfo;
                DownloadId = downloadClientItem.DownloadId;
            }
        }
    }

    public class BookFileConversionFailedEvent : IEvent
    {
        public Author Author { get; }
        public Book Book { get; }
        public Edition Edition { get; }
        public QualityModel SourceQuality { get; }
        public QualityModel TargetQuality { get; }
        public string SourceTitle { get; }
        public List<string> SourcePaths { get; }
        public string ConvertedPath { get; }
        public string Message { get; }
        public DownloadClientItemClientInfo DownloadClientInfo { get; }
        public string DownloadId { get; }

        public BookFileConversionFailedEvent(LocalBook bookInfo, IEnumerable<string> sourcePaths, Book book, Author author, QualityModel targetQuality, string convertedPath, string message, DownloadClientItem downloadClientItem)
        {
            Author = author ?? bookInfo?.Author;
            Book = book ?? bookInfo?.Book;
            Edition = bookInfo?.Edition;
            SourceQuality = bookInfo?.Quality;
            TargetQuality = targetQuality;
            SourcePaths = sourcePaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? new List<string>();
            ConvertedPath = convertedPath;
            Message = message;
            SourceTitle = bookInfo?.SceneName ?? Path.GetFileNameWithoutExtension(SourcePaths.FirstOrDefault() ?? bookInfo?.Path ?? convertedPath);

            if (downloadClientItem != null)
            {
                DownloadClientInfo = downloadClientItem.DownloadClientInfo;
                DownloadId = downloadClientItem.DownloadId;
            }
        }
    }
}
