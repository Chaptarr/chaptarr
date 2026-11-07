using System.Collections.Generic;
using System.Linq;
using FluentValidation.Results;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Notifications.BookLore
{
    public class BookLore : NotificationBase<BookLoreSettings>
    {
        private readonly IBookLoreProxy _bookLoreProxy;

        public BookLore(IBookLoreProxy bookLoreProxy)
        {
            _bookLoreProxy = bookLoreProxy;
        }

        public override string Link => "https://booklore.org/";

        public override void OnReleaseImport(BookDownloadMessage message)
        {
            _bookLoreProxy.UploadFiles(Settings, message.BookFiles?.Select(bookFile => bookFile.Path) ?? Enumerable.Empty<string>());
        }

        public override string Name => "BookLore";

        public override ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();
            var settingsValidation = Settings.Validate();

            if (!settingsValidation.IsValid)
            {
                failures.AddRange(settingsValidation.Errors);
            }
            else
            {
                failures.AddIfNotNull(_bookLoreProxy.Test(Settings));
            }

            return new ValidationResult(failures);
        }
    }
}
