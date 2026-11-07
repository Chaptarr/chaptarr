using System.Collections.Generic;
using System.Linq;
using FluentValidation.Results;

namespace NzbDrone.Core.Notifications.BookOrbit
{
    public class BookOrbit : NotificationBase<BookOrbitSettings>
    {
        private readonly IBookOrbitProxy _bookOrbitProxy;

        public BookOrbit(IBookOrbitProxy bookOrbitProxy)
        {
            _bookOrbitProxy = bookOrbitProxy;
        }

        public override string Link => "https://bookorbit.app/";

        public override void OnReleaseImport(BookDownloadMessage message)
        {
            _bookOrbitProxy.UploadFiles(Settings, message.BookFiles?.Select(bookFile => bookFile.Path) ?? Enumerable.Empty<string>());
        }

        public override string Name => "BookOrbit";

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
                var failure = _bookOrbitProxy.Test(Settings);
                if (failure != null)
                {
                    failures.Add(failure);
                }
            }

            return new ValidationResult(failures);
        }
    }
}
