using System.Collections.Generic;
using System.Linq;
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
        private readonly ICached<object> _queueLocks;
        private readonly IPendingNotificationRepository _pending;

        public Subsonic(ISubsonicService subsonicService, ICacheManager cacheManager, IPendingNotificationRepository pending)
        {
            _subsonicService = subsonicService;
            _queueLocks = cacheManager.GetCache<object>(GetType(), "queueLocks");
            _pending = pending;
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

        public override bool HasPendingQueue => _pending.HasPending(Definition.Id);

        public override void ProcessQueue()
        {
            lock (_queueLocks.Get(Definition.Id.ToString(), () => new object()))
            {
                var updated = false;
                foreach (var batch in _pending.GetPending(Definition.Id).Chunk(PendingNotificationRepository.BatchSize))
                {
                    if (!updated && Settings.UpdateLibrary) _subsonicService.Update(Settings);
                    updated = true;
                    _pending.Complete(batch);
                }
            }
        }

        private void Update()
        {
            if (Settings.UpdateLibrary)
            {
                lock (_queueLocks.Get(Definition.Id.ToString(), () => new object()))
                {
                    _pending.Enqueue(Definition.Id, string.Empty);
                }
            }
        }
    }
}
