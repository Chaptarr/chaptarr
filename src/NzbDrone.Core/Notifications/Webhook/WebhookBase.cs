using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.Notifications.Webhook
{
    public abstract class WebhookBase<TSettings> : NotificationBase<TSettings>
        where TSettings : IProviderConfig, new()
    {
        private readonly IConfigFileProvider _configFileProvider;
        private readonly IBookshelfLegacyIdentityMapService _legacyIdentityMapService;

        protected WebhookBase(
            IConfigFileProvider configFileProvider,
            IBookshelfLegacyIdentityMapService legacyIdentityMapService)
            : base()
        {
            _configFileProvider = configFileProvider;
            _legacyIdentityMapService = legacyIdentityMapService;
        }

        private WebhookBook CreateWebhookBook(Book book, Edition edition = null)
        {
            var webhookBook = new WebhookBook(book, edition);
            var mapping = _legacyIdentityMapService.GetMappingsByTargetIds("Book", new[] { book.Id })
                .OrderBy(item => item.SourceId)
                .ThenBy(item => item.MediaType)
                .FirstOrDefault();
            if (mapping != null)
            {
                webhookBook.Id = mapping.SourceId;
            }

            return webhookBook;
        }

        private List<WebhookBook> CreateWebhookBooks(IEnumerable<Book> books)
        {
            var sourceBooks = books?.ToList() ?? new List<Book>();
            var legacyIds = _legacyIdentityMapService
                .GetMappingsByTargetIds("Book", sourceBooks.Select(book => book.Id))
                .GroupBy(mapping => mapping.TargetId)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderBy(mapping => mapping.SourceId).ThenBy(mapping => mapping.MediaType).First().SourceId);

            return sourceBooks.Select(book =>
            {
                var webhookBook = new WebhookBook(book);
                if (legacyIds.TryGetValue(book.Id, out var legacyId))
                {
                    webhookBook.Id = legacyId;
                }

                return webhookBook;
            }).ToList();
        }

        public WebhookGrabPayload BuildOnGrabPayload(GrabMessage message)
        {
            var remoteBook = message.RemoteBook;
            var quality = message.Quality;

            return new WebhookGrabPayload
            {
                EventType = WebhookEventType.Grab,
                InstanceName = _configFileProvider.InstanceName,
                Author = new WebhookAuthor(message.Author),
                Books = CreateWebhookBooks(remoteBook.Books),
                Release = new WebhookRelease(quality, remoteBook),
                DownloadClient = message.DownloadClientName,
                DownloadClientType = message.DownloadClientType,
                DownloadId = message.DownloadId
            };
        }

        public WebhookImportPayload BuildOnReleaseImportPayload(BookDownloadMessage message)
        {
            var trackFiles = message.BookFiles;
            var primaryEdition = trackFiles?.FirstOrDefault()?.Edition;

            var payload = new WebhookImportPayload
            {
                EventType = WebhookEventType.Download,
                InstanceName = _configFileProvider.InstanceName,
                Author = new WebhookAuthor(message.Author),
                Book = CreateWebhookBook(message.Book, primaryEdition),
                BookFiles = trackFiles.ConvertAll(x => new WebhookBookFile(x)),
                IsUpgrade = message.OldFiles.Any(),
                DownloadClient = message.DownloadClientInfo?.Name,
                DownloadClientType = message.DownloadClientInfo?.Type,
                DownloadId = message.DownloadId
            };

            if (message.OldFiles.Any())
            {
                payload.DeletedFiles = message.OldFiles.ConvertAll(x => new WebhookBookFile(x));
            }

            return payload;
        }

        public WebhookRenamePayload BuildOnRenamePayload(Author author, List<RenamedBookFile> renamedFiles)
        {
            return new WebhookRenamePayload
            {
                EventType = WebhookEventType.Rename,
                InstanceName = _configFileProvider.InstanceName,
                Author = new WebhookAuthor(author),
                RenamedBookFiles = renamedFiles.ConvertAll(x => new WebhookRenamedBookFile(x))
            };
        }

        public WebhookRetagPayload BuildOnBookRetagPayload(BookRetagMessage message)
        {
            return new WebhookRetagPayload
            {
                EventType = WebhookEventType.Retag,
                InstanceName = _configFileProvider.InstanceName,
                Author = new WebhookAuthor(message.Author),
                BookFile = new WebhookBookFile(message.BookFile)
            };
        }

        public WebhookBookDeletePayload BuildOnBookDelete(BookDeleteMessage deleteMessage)
        {
            return new WebhookBookDeletePayload
            {
                EventType = WebhookEventType.BookDelete,
                InstanceName = _configFileProvider.InstanceName,
                Author = new WebhookAuthor(deleteMessage.Book.Author),
                Book = CreateWebhookBook(deleteMessage.Book),
                DeletedFiles = deleteMessage.DeletedFiles
            };
        }

        public WebhookBookFileDeletePayload BuildOnBookFileDelete(BookFileDeleteMessage deleteMessage)
        {
            return new WebhookBookFileDeletePayload
            {
                EventType = WebhookEventType.BookFileDelete,
                InstanceName = _configFileProvider.InstanceName,
                Author = new WebhookAuthor(deleteMessage.Book.Author),
                Book = CreateWebhookBook(deleteMessage.Book, deleteMessage.BookFile?.Edition),
                BookFile = new WebhookBookFile(deleteMessage.BookFile)
            };
        }

        public WebhookAuthorAddedPayload BuildOnAuthorAdded(Author author)
        {
            return new WebhookAuthorAddedPayload
            {
                EventType = WebhookEventType.AuthorAdded,
                InstanceName = _configFileProvider.InstanceName,
                Author = new WebhookAuthor(author)
            };
        }

        public WebhookBookAddedPayload BuildOnBookAdded(Book book)
        {
            return new WebhookBookAddedPayload
            {
                EventType = WebhookEventType.BookAdded,
                InstanceName = _configFileProvider.InstanceName,
                Author = new WebhookAuthor(book.Author),
                Book = CreateWebhookBook(book)
            };
        }

        public WebhookAuthorDeletePayload BuildOnAuthorDelete(AuthorDeleteMessage deleteMessage)
        {
            return new WebhookAuthorDeletePayload
            {
                EventType = WebhookEventType.AuthorDelete,
                InstanceName = _configFileProvider.InstanceName,
                Author = new WebhookAuthor(deleteMessage.Author),
                DeletedFiles = deleteMessage.DeletedFiles
            };
        }

        protected WebhookHealthPayload BuildHealthPayload(HealthCheck.HealthCheck healthCheck)
        {
            return new WebhookHealthPayload
            {
                EventType = WebhookEventType.Health,
                InstanceName = _configFileProvider.InstanceName,
                Level = healthCheck.Type,
                Message = healthCheck.Message,
                Type = healthCheck.Source.Name,
                WikiUrl = healthCheck.WikiUrl?.ToString()
            };
        }

        protected WebhookApplicationUpdatePayload BuildApplicationUpdatePayload(ApplicationUpdateMessage updateMessage)
        {
            return new WebhookApplicationUpdatePayload
            {
                EventType = WebhookEventType.ApplicationUpdate,
                InstanceName = _configFileProvider.InstanceName,
                Message = updateMessage.Message,
                PreviousVersion = updateMessage.PreviousVersion.ToString(),
                NewVersion = updateMessage.NewVersion.ToString()
            };
        }

        protected WebhookPayload BuildTestPayload()
        {
            return new WebhookGrabPayload
            {
                EventType = WebhookEventType.Test,
                InstanceName = _configFileProvider.InstanceName,
                Author = new WebhookAuthor()
                {
                    Id = 1,
                    Name = "Test Name",
                    Path = "C:\\testpath",
                    GoodreadsId = "aaaaa-aaa-aaaa-aaaaaa"
                },
                Books = new List<WebhookBook>()
                    {
                            new WebhookBook()
                            {
                                Id = 123,
                                Title = "Test title"
                            }
                    }
            };
        }
    }
}
