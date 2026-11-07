using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation.Results;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Notifications.Discord.Payloads;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Discord
{
    public class Discord : NotificationBase<DiscordSettings>
    {
        private readonly IDiscordProxy _proxy;

        public Discord(IDiscordProxy proxy)
        {
            _proxy = proxy;
        }

        public override string Name => "Discord";
        public override string Link => "https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks";

        public override void OnGrab(GrabMessage message)
        {
            var embeds = new List<Embed>
            {
                new ()
                {
                    Description = message.Message,
                    Title = message.Author.Name,
                    Text = message.Message,
                    Color = (int)DiscordColors.Warning
                }
            };
            var payload = CreatePayload(Localize("notification.prefix.grabbed", "Grabbed: {message}", new Dictionary<string, object> { ["message"] = message.Message }), embeds);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnReleaseImport(BookDownloadMessage message)
        {
            var attachments = new List<Embed>
            {
                new ()
                {
                    Description = message.Message,
                    Title = message.Author.Name,
                    Text = message.Message,
                    Color = (int)DiscordColors.Success
                }
            };
            var payload = CreatePayload(Localize("notification.prefix.imported", "Imported: {message}", new Dictionary<string, object> { ["message"] = message.Message }), attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnRename(Author author, List<RenamedBookFile> renamedFiles)
        {
            var attachments = new List<Embed>
            {
                new ()
                {
                    Title = author.Name,
                }
            };

            var payload = CreatePayload(Localize("notification.heading.renamed", "Renamed"), attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnAuthorAdded(Author author)
        {
            var attachments = new List<Embed>
            {
                new ()
                {
                    Title = author.Name,
                    Fields = new List<DiscordField>()
                    {
                        new ()
                        {
                            Name = Localize("notification.field.links", "Links"),
                            Value = string.Join(" / ", author.Links.Select(link => $"[{link.Name}]({link.Url})"))
                        }
                    },
                }
            };
            var payload = CreatePayload(Localize("notification.heading.authorAdded", "Author Added"), attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnBookAdded(Book book)
        {
            var attachments = new List<Embed>
            {
                new ()
                {
                    Title = $"{book.Author.Name} - {book.Title}",
                    Color = (int)DiscordColors.Success
                }
            };
            var payload = CreatePayload(Localize("notification.heading.bookAdded", "Book Added"), attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnAuthorDelete(AuthorDeleteMessage deleteMessage)
        {
            var attachments = new List<Embed>
            {
                new ()
                {
                    Title = deleteMessage.Author.Name,
                    Description = deleteMessage.DeletedFilesMessage
                }
            };

            var payload = CreatePayload(Localize("notification.heading.authorDeleted", "Author Deleted"), attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnBookDelete(BookDeleteMessage deleteMessage)
        {
            var attachments = new List<Embed>
            {
                new ()
                {
                    Title = $"{deleteMessage.Book.Author.Name} - {deleteMessage.Book.Title}",
                    Description = deleteMessage.DeletedFilesMessage
                }
            };

            var payload = CreatePayload(Localize("notification.heading.bookDeleted", "Book Deleted"), attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnBookFileDelete(BookFileDeleteMessage deleteMessage)
        {
            var attachments = new List<Embed>
            {
                new ()
                {
                    Title = Localize("notification.body.fileDeleted", "{author} - {book} - file deleted",
                        new Dictionary<string, object> { ["author"] = deleteMessage.Book.Author.Name, ["book"] = deleteMessage.Book.Title }),
                    Description = deleteMessage.BookFile.Path
                }
            };

            var payload = CreatePayload(Localize("notification.heading.bookFileDeleted", "Book File Deleted"), attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnHealthIssue(HealthCheck.HealthCheck healthCheck)
        {
            var attachments = new List<Embed>
            {
                new ()
                {
                    Title = healthCheck.Source.Name,
                    Text = healthCheck.Message,
                    Color = healthCheck.Type == HealthCheck.HealthCheckResult.Warning ? (int)DiscordColors.Warning : (int)DiscordColors.Danger
                }
            };

            var payload = CreatePayload(Localize("notification.heading.healthIssue", "Health Check Failure"), attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnBookRetag(BookRetagMessage message)
        {
            var attachments = new List<Embed>
            {
                new ()
                {
                    Title = BOOK_RETAGGED_TITLE,
                    Text = message.Message
                }
            };

            var payload = CreatePayload(Localize("notification.prefix.retagged", "Book file tags updated: {message}", new Dictionary<string, object> { ["message"] = message.Message }), attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnDownloadFailure(DownloadFailedMessage message)
        {
            var attachments = new List<Embed>
            {
                new ()
                {
                    Description = message.Message,
                    Title = message.SourceTitle,
                    Text = message.Message,
                    Color = (int)DiscordColors.Danger
                }
            };
            var payload = CreatePayload(Localize("notification.prefix.downloadFailed", "Download failed: {message}", new Dictionary<string, object> { ["message"] = message.Message }), attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnImportFailure(BookDownloadMessage message)
        {
            var attachments = new List<Embed>
            {
                new ()
                {
                    Description = message.Message,
                    Title = message.Book?.Title ?? message.Message,
                    Text = message.Message,
                    Color = (int)DiscordColors.Warning
                }
            };
            var payload = CreatePayload(Localize("notification.prefix.importFailed", "Import failed: {message}", new Dictionary<string, object> { ["message"] = message.Message }), attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnApplicationUpdate(ApplicationUpdateMessage updateMessage)
        {
            var attachments = new List<Embed>
            {
                new ()
                {
                    Author = new DiscordAuthor
                    {
                        Name = Settings.Author.IsNullOrWhiteSpace() ? Environment.MachineName : Settings.Author,
                        IconUrl = "https://raw.githubusercontent.com/snapetech-org/chaptarrng/main/Logo/256.png"
                    },
                    Title = APPLICATION_UPDATE_TITLE,
                    Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    Color = (int)DiscordColors.Standard,
                    Fields = new List<DiscordField>()
                    {
                        new ()
                        {
                            Name = Localize("notification.field.previousVersion", "Previous Version"),
                            Value = updateMessage.PreviousVersion.ToString()
                        },
                        new ()
                        {
                            Name = Localize("notification.field.newVersion", "New Version"),
                            Value = updateMessage.NewVersion.ToString()
                        }
                    },
                }
            };

            var payload = CreatePayload(null, attachments);

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
            catch (DiscordException ex)
            {
                return new NzbDroneValidationFailure("Unable to post", ex.Message);
            }

            return null;
        }

        private DiscordPayload CreatePayload(string message, List<Embed> embeds = null)
        {
            var avatar = Settings.Avatar;

            var payload = new DiscordPayload
            {
                Content = message,
                Embeds = embeds
            };

            if (avatar.IsNotNullOrWhiteSpace())
            {
                payload.AvatarUrl = avatar;
            }

            if (Settings.Username.IsNotNullOrWhiteSpace())
            {
                payload.Username = Settings.Username;
            }

            return payload;
        }
    }
}
