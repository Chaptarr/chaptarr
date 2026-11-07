using System;
using System.Collections.Generic;
using FluentValidation.Results;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Notifications.Slack.Payloads;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Slack
{
    public class Slack : NotificationBase<SlackSettings>
    {
        private readonly ISlackProxy _proxy;

        public Slack(ISlackProxy proxy)
        {
            _proxy = proxy;
        }

        public override string Name => "Slack";
        public override string Link => "https://my.slack.com/services/new/incoming-webhook/";

        public override void OnGrab(GrabMessage message)
        {
            var attachments = new List<Attachment>
                              {
                                  new Attachment
                                  {
                                      Fallback = message.Message,
                                      Title = message.Author.Name,
                                      Text = message.Message,
                                      Color = "warning"
                                  }
                              };
            var payload = CreatePayload(Localize("notification.prefix.grabbed", "Grabbed: {message}", new Dictionary<string, object> { ["message"] = message.Message }), attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnReleaseImport(BookDownloadMessage message)
        {
            var attachments = new List<Attachment>
            {
                new Attachment
                {
                    Fallback = message.Message,
                    Title = message.Author.Name,
                    Text = message.Message,
                    Color = "good"
                }
            };
            var payload = CreatePayload(Localize("notification.prefix.imported", "Imported: {message}", new Dictionary<string, object> { ["message"] = message.Message }), attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnRename(Author author, List<RenamedBookFile> renamedFiles)
        {
            var attachments = new List<Attachment>
                              {
                                  new Attachment
                                  {
                                      Title = author.Name,
                                  }
                              };

            var payload = CreatePayload(Localize("notification.heading.renamed", "Renamed"), attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnAuthorAdded(Author author)
        {
            var attachments = new List<Attachment>
                             {
                                 new Attachment
                                 {
                                     Title = author.Name,
                                 }
                             };

            var payload = CreatePayload(Localize("notification.heading.authorAdded", "Author Added"), attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnBookAdded(Book book)
        {
            var attachments = new List<Attachment>
                             {
                                 new Attachment
                                 {
                                     Title = $"{book.Author.Name} - {book.Title}",
                                 }
                             };

            var payload = CreatePayload(Localize("notification.heading.bookAdded", "Book Added"), attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnAuthorDelete(AuthorDeleteMessage deleteMessage)
        {
            var attachments = new List<Attachment>
                             {
                                 new Attachment
                                 {
                                     Title = deleteMessage.Author.Name,
                                     Text = deleteMessage.DeletedFilesMessage
                                 }
                             };

            var payload = CreatePayload(Localize("notification.heading.authorDeleted", "Author Deleted"), attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnBookDelete(BookDeleteMessage deleteMessage)
        {
            var attachments = new List<Attachment>
                             {
                                 new Attachment
                                 {
                                     Title =  $"{deleteMessage.Book.Author.Name} - {deleteMessage.Book.Title}",
                                     Text = deleteMessage.DeletedFilesMessage
                                 }
                             };

            var payload = CreatePayload(Localize("notification.heading.bookDeleted", "Book Deleted"), attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnBookFileDelete(BookFileDeleteMessage deleteMessage)
        {
            var attachments = new List<Attachment>
                             {
                                 new Attachment
                                 {
                                     Title = Localize("notification.body.fileDeleted", "{author} - {book} - file deleted",
                        new Dictionary<string, object> { ["author"] = deleteMessage.Book.Author.Name, ["book"] = deleteMessage.Book.Title }),
                                     Text = deleteMessage.BookFile.Path
                                 }
                             };

            var payload = CreatePayload(Localize("notification.heading.bookFileDeleted", "Book File Deleted"), attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnHealthIssue(HealthCheck.HealthCheck healthCheck)
        {
            var attachments = new List<Attachment>
                              {
                                  new Attachment
                                  {
                                      Title = healthCheck.Source.Name,
                                      Text = healthCheck.Message,
                                      Color = healthCheck.Type == HealthCheck.HealthCheckResult.Warning ? "warning" : "danger"
                                  }
                              };

            var payload = CreatePayload(Localize("notification.heading.healthIssue", "Health Check Failure"), attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnBookRetag(BookRetagMessage message)
        {
            var attachments = new List<Attachment>
                              {
                                  new Attachment
                                  {
                                      Title = BOOK_RETAGGED_TITLE,
                                      Text = message.Message
                                  }
                              };

            var payload = CreatePayload(BOOK_RETAGGED_TITLE, attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnDownloadFailure(DownloadFailedMessage message)
        {
            var attachments = new List<Attachment>
            {
                new Attachment
                {
                    Fallback = message.Message,
                    Title = message.SourceTitle,
                    Text = message.Message,
                    Color = "danger"
                }
            };
            var payload = CreatePayload(Localize("notification.prefix.downloadFailed", "Download failed: {message}", new Dictionary<string, object> { ["message"] = message.Message }), attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnImportFailure(BookDownloadMessage message)
        {
            var attachments = new List<Attachment>
            {
                new Attachment
                {
                    Fallback = message.Message,
                    Text = message.Message,
                    Color = "warning"
                }
            };
            var payload = CreatePayload(Localize("notification.prefix.importFailed", "Import failed: {message}", new Dictionary<string, object> { ["message"] = message.Message }), attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnApplicationUpdate(ApplicationUpdateMessage updateMessage)
        {
            var attachments = new List<Attachment>
                              {
                                  new Attachment
                                  {
                                      Title = Environment.MachineName,
                                      Text = updateMessage.Message,
                                      Color = "good"
                                  }
                              };

            var payload = CreatePayload(Localize("notification.heading.applicationUpdate", "Application Updated"), attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();

            failures.AddIfNotNull(TestMessage());

            return new ValidationResult(failures);
        }

        public ValidationFailure TestMessage()
        {
            try
            {
                var message = Localize("notification.test.timestampedBody", "Test message from Chaptarr posted at {timestamp}",
                    new Dictionary<string, object> { ["timestamp"] = DateTime.Now });
                var payload = CreatePayload(message);

                _proxy.SendPayload(payload, Settings);
            }
            catch (SlackExeption ex)
            {
                return new NzbDroneValidationFailure("Unable to post", ex.Message);
            }

            return null;
        }

        private SlackPayload CreatePayload(string message, List<Attachment> attachments = null)
        {
            var icon = Settings.Icon;
            var channel = Settings.Channel;

            var payload = new SlackPayload
            {
                Username = Settings.Username,
                Text = message,
                Attachments = attachments
            };

            if (icon.IsNotNullOrWhiteSpace())
            {
                if (icon.StartsWith(":") && icon.EndsWith(":"))
                {
                    payload.IconEmoji = icon;
                }
                else
                {
                    payload.IconUrl = icon;
                }
            }

            if (channel.IsNotNullOrWhiteSpace())
            {
                payload.Channel = channel;
            }

            return payload;
        }
    }
}
