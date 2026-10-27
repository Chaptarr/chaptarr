using System.Collections.Generic;
using FluentValidation.Results;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Notifications.Subsonic
{
    public class Subsonic : NotificationBase<SubsonicSettings>
    {
        private readonly ISubsonicService _subsonicService;
        private readonly ICached<bool> _pendingUpdates;

        public Subsonic(ISubsonicService subsonicService, ICacheManager cacheManager)
        {
            _subsonicService = subsonicService;
            _pendingUpdates = cacheManager.GetCache<bool>(GetType(), "pendingUpdates");
        }

        public override string Link => "http://subsonic.org/";

        public override void OnGrab(GrabMessage grabMessage)
        {
            const string header = "Chaptarr - Grabbed";

            Notify(header, grabMessage.Message);
        }

        public override void OnReleaseImport(BookDownloadMessage message)
        {
            const string header = "Chaptarr - Downloaded";

            Notify(header, message.Message);
            Update();
        }

        public override void OnRename(Author author, List<RenamedBookFile> renamedFiles)
        {
            Update();
        }

        public override void OnAuthorAdded(Author author)
        {
            Notify(AUTHOR_ADDED_TITLE_BRANDED, author.Name);
        }

        public override void OnBookAdded(Book book)
        {
            const string header = "Chaptarr - Book Added";

            Notify(header, $"{book.Author.Name} - {book.Title}");
        }

        public override void OnAuthorDelete(AuthorDeleteMessage deleteMessage)
        {
            const string header = "Chaptarr - Author Deleted";

            Notify(header, deleteMessage.Message);

            if (deleteMessage.DeletedFiles)
            {
                Update();
            }
        }

        public override void OnBookDelete(BookDeleteMessage deleteMessage)
        {
            const string header = "Chaptarr - Book Deleted";

            Notify(header, deleteMessage.Message);

            if (deleteMessage.DeletedFiles)
            {
                Update();
            }
        }

        public override void OnBookFileDelete(BookFileDeleteMessage deleteMessage)
        {
            const string header = "Chaptarr - Book File Deleted";

            Notify(header, deleteMessage.Message);
            Update();
        }

        public override void OnHealthIssue(HealthCheck.HealthCheck healthCheck)
        {
            Notify(HEALTH_ISSUE_TITLE_BRANDED, healthCheck.Message);
        }

        public override void OnBookRetag(BookRetagMessage message)
        {
            Notify(BOOK_RETAGGED_TITLE_BRANDED, message.Message);
        }

        public override string Name => "Subsonic";

        public override ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();

            failures.AddIfNotNull(_subsonicService.Test(Settings, "Success! Subsonic has been successfully configured!"));

            return new ValidationResult(failures);
        }

        private void Notify(string header, string message)
        {
            if (Settings.Notify)
            {
                _subsonicService.Notify(Settings, $"{header} - {message}");
            }
        }

        public override bool HasPendingQueue => _pendingUpdates.Find(Definition.Id.ToString());

        public override void ProcessQueue()
        {
            lock (_pendingUpdates)
            {
                if (!HasPendingQueue)
                {
                    return;
                }

                if (Settings.UpdateLibrary)
                {
                    _subsonicService.Update(Settings);
                }

                _pendingUpdates.Remove(Definition.Id.ToString());
            }
        }

        private void Update()
        {
            if (Settings.UpdateLibrary)
            {
                lock (_pendingUpdates)
                {
                    _pendingUpdates.Set(Definition.Id.ToString(), true);
                }
            }
        }
    }
}
