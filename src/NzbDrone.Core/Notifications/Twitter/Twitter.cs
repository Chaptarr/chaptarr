using System.Collections.Generic;
using FluentValidation.Results;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Twitter
{
    public class Twitter : NotificationBase<TwitterSettings>, IResolveProviderPendingSecrets
    {
        private readonly ITwitterService _twitterService;
        private readonly IPendingProviderSecretService _pendingProviderSecretService;

        public Twitter(ITwitterService twitterService, IPendingProviderSecretService pendingProviderSecretService)
        {
            _twitterService = twitterService;
            _pendingProviderSecretService = pendingProviderSecretService;
        }

        public override string Name => "Twitter";
        public override string Link => "https://twitter.com/";

        public override void OnGrab(GrabMessage message)
        {
            _twitterService.SendNotification(Localize("notification.prefix.grabbed", "Grabbed: {message}", new Dictionary<string, object> { ["message"] = message.Message }), Settings);
        }

        public override void OnReleaseImport(BookDownloadMessage message)
        {
            _twitterService.SendNotification(Localize("notification.prefix.imported", "Imported: {message}", new Dictionary<string, object> { ["message"] = message.Message }), Settings);
        }

        public override void OnAuthorAdded(Author author)
        {
            _twitterService.SendNotification(Localize("notification.prefix.authorAdded", "Author added: {message}", new Dictionary<string, object> { ["message"] = author.Name }), Settings);
        }

        public override void OnBookAdded(Book book)
        {
            _twitterService.SendNotification(Localize("notification.prefix.bookAdded", "Book added: {message}", new Dictionary<string, object> { ["message"] = $"{book.Author.Name} - {book.Title}" }), Settings);
        }

        public override void OnAuthorDelete(AuthorDeleteMessage deleteMessage)
        {
            _twitterService.SendNotification(Localize("notification.prefix.deleted", "Deleted: {message}", new Dictionary<string, object> { ["message"] = deleteMessage.Message }), Settings);
        }

        public override void OnBookDelete(BookDeleteMessage deleteMessage)
        {
            _twitterService.SendNotification(Localize("notification.prefix.deleted", "Deleted: {message}", new Dictionary<string, object> { ["message"] = deleteMessage.Message }), Settings);
        }

        public override void OnBookFileDelete(BookFileDeleteMessage deleteMessage)
        {
            _twitterService.SendNotification(Localize("notification.prefix.deleted", "Deleted: {message}", new Dictionary<string, object> { ["message"] = deleteMessage.Message }), Settings);
        }

        public override void OnHealthIssue(HealthCheck.HealthCheck healthCheck)
        {
            _twitterService.SendNotification(Localize("notification.prefix.healthIssue", "Health issue: {message}", new Dictionary<string, object> { ["message"] = healthCheck.Message }), Settings);
        }

        public override void OnDownloadFailure(DownloadFailedMessage message)
        {
            _twitterService.SendNotification(Localize("notification.prefix.downloadFailed", "Download failed: {message}", new Dictionary<string, object> { ["message"] = message.Message }), Settings);
        }

        public override void OnImportFailure(BookDownloadMessage message)
        {
            _twitterService.SendNotification(Localize("notification.prefix.importFailed", "Import failed: {message}", new Dictionary<string, object> { ["message"] = message.Message }), Settings);
        }

        public override void OnApplicationUpdate(ApplicationUpdateMessage updateMessage)
        {
            _twitterService.SendNotification(Localize("notification.prefix.applicationUpdated", "Application updated: {message}", new Dictionary<string, object> { ["message"] = updateMessage.Message }), Settings);
        }

        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            if (action == "startOAuth")
            {
                Settings.Validate().Filter("ConsumerKey", "ConsumerSecret").ThrowOnError();

                if (query["callbackUrl"].IsNullOrWhiteSpace())
                {
                    throw new BadRequestException("QueryParam callbackUrl invalid.");
                }

                var oauthRedirectUrl = _twitterService.GetOAuthRedirect(Settings.ConsumerKey, Settings.ConsumerSecret, query["callbackUrl"]);
                return new
                {
                    oauthUrl = oauthRedirectUrl
                };
            }
            else if (action == "getOAuthToken")
            {
                Settings.Validate().Filter("ConsumerKey", "ConsumerSecret").ThrowOnError();

                if (query["oauth_token"].IsNullOrWhiteSpace())
                {
                    throw new BadRequestException("QueryParam oauth_token invalid.");
                }

                if (query["oauth_verifier"].IsNullOrWhiteSpace())
                {
                    throw new BadRequestException("QueryParam oauth_verifier invalid.");
                }

                var oauthToken = _twitterService.GetOAuthToken(Settings.ConsumerKey, Settings.ConsumerSecret, query["oauth_token"], query["oauth_verifier"]);
                return new
                {
                    accessToken = _pendingProviderSecretService.Create(oauthToken.AccessToken),
                    accessTokenSecret = _pendingProviderSecretService.Create(oauthToken.AccessTokenSecret)
                };
            }

            return new { };
        }

        public override ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();

            failures.AddIfNotNull(_twitterService.Test(Settings));

            return new ValidationResult(failures);
        }

        public void ResolveProviderPendingSecrets(bool consume)
        {
            Settings.AccessToken = _pendingProviderSecretService.Resolve(Settings.AccessToken, consume);
            Settings.AccessTokenSecret = _pendingProviderSecretService.Resolve(Settings.AccessTokenSecret, consume);
        }
    }
}
