using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Net.Http;
using System.Web;
using System.Xml.Linq;
using System.Xml.XPath;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.OAuth;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MetadataSource.Goodreads;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.ImportLists.Goodreads
{
    public abstract class GoodreadsImportListBase<TSettings> : ImportListBase<TSettings>
        where TSettings : GoodreadsSettingsBase<TSettings>, new()
    {
        protected readonly IHttpClient _httpClient;

        protected GoodreadsImportListBase(IImportListStatusService importListStatusService,
                                          IConfigService configService,
                                          IParsingService parsingService,
                                          IHttpClient httpClient,
                                          Logger logger)
        : base(importListStatusService, configService, parsingService, logger)
        {
            _httpClient = httpClient;
        }

        public override ImportListType ListType => ImportListType.Goodreads;

        public string AccessToken => Settings.AccessToken;

        protected bool HasOAuthSettings => !Settings.AccessToken.IsNullOrWhiteSpace() || !Settings.AccessTokenSecret.IsNullOrWhiteSpace();

        protected bool HasOAuthCredentials => !Settings.AccessToken.IsNullOrWhiteSpace() && !Settings.AccessTokenSecret.IsNullOrWhiteSpace();

        protected HttpRequestBuilder RequestBuilder() => new HttpRequestBuilder("https://www.goodreads.com/{route}")
            .AddQueryParam("_nc", "1")
            .KeepAlive();

        protected override void Test(List<ValidationFailure> failures)
        {
            failures.AddIfNotNull(TestConnection());
        }

        protected ValidationFailure TestConnection()
        {
            try
            {
                GetUser(Settings.UserId);
                return null;
            }
            catch (Common.Http.HttpException ex)
            {
                _logger.Warn(ex, "Goodreads Authentication Error");
                return new ValidationFailure(string.Empty, $"Goodreads authentication error: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to connect to Goodreads");

                return new ValidationFailure(string.Empty, "Unable to connect to import list, check the log for more details");
            }
        }

        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            if (action == "startOAuth")
            {
                if (query == null || !query.TryGetValue("callbackUrl", out var callbackUrl) || callbackUrl.IsNullOrWhiteSpace())
                {
                    throw new BadRequestException("QueryParam callbackUrl invalid.");
                }

                var oAuthRequest = OAuthRequest.ForRequestToken(null, null, callbackUrl);
                oAuthRequest.RequestUrl = Settings.OAuthRequestTokenUrl;
                var qscoll = OAuthQuery(oAuthRequest);

                var url = $"{Settings.OAuthUrl}?oauth_token={Uri.EscapeDataString(qscoll["oauth_token"] ?? string.Empty)}&oauth_callback={Uri.EscapeDataString(callbackUrl)}";

                return new
                {
                    OauthUrl = url,
                    RequestTokenSecret = qscoll["oauth_token_secret"]
                };
            }
            else if (action == "getOAuthToken")
            {
                if (query == null || !query.TryGetValue("oauth_token", out var oauthToken) || oauthToken.IsNullOrWhiteSpace())
                {
                    throw new BadRequestException("QueryParam oauth_token invalid.");
                }

                if (!query.TryGetValue("requestTokenSecret", out var requestTokenSecret) || requestTokenSecret.IsNullOrWhiteSpace())
                {
                    throw new BadRequestException("Missing requestTokenSecret.");
                }

                var oAuthRequest = OAuthRequest.ForAccessToken(null, null, oauthToken, requestTokenSecret, "");
                oAuthRequest.RequestUrl = Settings.OAuthAccessTokenUrl;
                var qscoll = OAuthQuery(oAuthRequest);

                Settings.AccessToken = qscoll["oauth_token"];
                Settings.AccessTokenSecret = qscoll["oauth_token_secret"];

                var user = GetUser(Settings.UserId);

                return new
                {
                    Settings.AccessToken,
                    Settings.AccessTokenSecret,
                    RequestTokenSecret = "",
                    UserId = user.Item1,
                    UserName = user.Item2
                };
            }

            return new { };
        }

        protected Common.Http.HttpResponse OAuthGet(HttpRequestBuilder builder)
        {
            var auth = OAuthRequest.ForProtectedResource(builder.Method.ToString(), null, null, Settings.AccessToken, Settings.AccessTokenSecret);

            var request = builder.Build();
            request.LogHttpError = false;
            request.LogResponseContent = false;
            request.AllowAutoRedirect = false;

            // we need the url without the query to sign
            auth.RequestUrl = request.Url.SetQuery(null).FullUri;
            auth.Parameters = builder.QueryParams.ToDictionary(x => x.Key, x => x.Value);

            var header = GetAuthorizationHeader(auth);
            request.Headers.Add("Authorization", header);

            return _httpClient.Get(request);
        }

        private string GetAuthorizationHeader(OAuthRequest oAuthRequest)
        {
            var request = new Common.Http.HttpRequest(Settings.SigningUrl)
            {
                Method = HttpMethod.Post,
            };
            request.Headers.Set("Content-Type", "application/json");

            request.SetContent(oAuthRequest.ToJson());
            request.LogHttpError = false;
            request.LogResponseContent = false;

            var response = _httpClient.Post<AuthorizationHeader>(request).Resource;

            return response.Authorization;
        }

        private NameValueCollection OAuthQuery(OAuthRequest oAuthRequest)
        {
            var auth = GetAuthorizationHeader(oAuthRequest);
            var request = new Common.Http.HttpRequest(oAuthRequest.RequestUrl);
            request.LogHttpError = false;
            request.LogResponseContent = false;
            request.AllowAutoRedirect = false;
            request.Headers.Add("Authorization", auth);
            var response = _httpClient.Get(request);

            return HttpUtility.ParseQueryString(response.Content);
        }

        protected Tuple<string, string> GetUser(string id)
        {
            var builder = RequestBuilder();

            if (id.IsNullOrWhiteSpace())
            {
                builder.SetSegment("route", "api/auth_user");
            }
            else if (GoodreadsUserIdParser.TryParse(id, out var normalizedUserId))
            {
                builder.SetSegment("route", $"user/show/{normalizedUserId}.xml");
            }
            else
            {
                throw new BadRequestException("Goodreads user ID must be numeric or a Goodreads profile URL containing a numeric ID.");
            }

            var httpResponse = OAuthGet(builder);

            string userId = null;
            string userName = null;

            var content = httpResponse.Content;

            if (!string.IsNullOrWhiteSpace(content))
            {
                var user = XDocument.Parse(content).XPathSelectElement("GoodreadsResponse/user");
                userId = user?.AttributeAsString("id");
                userName = user?.ElementAsString("name");
            }

            if (userId.IsNullOrWhiteSpace())
            {
                throw new BadRequestException("Goodreads did not return an account ID for the authenticated user.");
            }

            return Tuple.Create(userId, userName);
        }
    }

}
