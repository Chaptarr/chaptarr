using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Plex.PlexTv;
using NzbDrone.Core.Notifications.Plex.Server;
using NzbDrone.Core.ThingiProvider;

namespace Chaptarr.Core.Test.Notifications.Plex
{
    [TestFixture]
    public class PlexServerFixture
    {
        [Test]
        public void should_select_one_best_reachable_connection_per_plex_server()
        {
            var plexTvService = new FakePlexTvService
            {
                Resources = new List<PlexTvResourceResponse>
                {
                    new PlexTvResourceResponse
                    {
                        Name = "Example Server",
                        Provides = "server",
                        ClientIdentifier = "server-1",
                        Connections = new List<PlexTvResourceConnection>
                        {
                            new PlexTvResourceConnection
                            {
                                Protocol = "https",
                                Address = "172.18.0.1",
                                Port = 32400,
                                Local = true
                            },
                            new PlexTvResourceConnection
                            {
                                Protocol = "https",
                                Address = "172.18.0.1",
                                Port = 32400,
                                Local = true
                            },
                            new PlexTvResourceConnection
                            {
                                Protocol = "https",
                                Address = "172.19.0.1",
                                Port = 32400,
                                Local = true
                            },
                            new PlexTvResourceConnection
                            {
                                Protocol = "https",
                                Address = "192.0.2.10",
                                Port = 32400,
                                Local = true
                            },
                            new PlexTvResourceConnection
                            {
                                Protocol = "https",
                                Address = "remote.plex.direct",
                                Port = 32400,
                                Local = false
                            },
                            new PlexTvResourceConnection
                            {
                                Protocol = "https",
                                Address = "remote.plex.direct",
                                Port = 32400,
                                Local = false
                            },
                            new PlexTvResourceConnection
                            {
                                Protocol = "https",
                                Address = "relay.plex.direct",
                                Port = 443,
                                Local = false,
                                Relay = true
                            }
                        }
                    }
                }
            };

            var plexServerService = new FakePlexServerService
            {
                ReachableHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "172.18.0.1",
                    "172.19.0.1",
                    "192.0.2.10",
                    "remote.plex.direct",
                    "relay.plex.direct"
                }
            };

            var subject = CreateSubject(plexTvService, plexServerService, new PlexServerSettings
            {
                AuthToken = "token"
            });

            var result = subject.RequestAction("getPlexServers", new Dictionary<string, string>());
            var selected = GetProperty<string>(result, "selectOption");
            var options = GetProperty<IEnumerable<object>>(result, "options").ToList();

            var option = options.Single();

            Assert.That(selected, Is.EqualTo(GetProperty<string>(option, "Value")));
            Assert.That(GetProperty<string>(option, "Name"), Is.EqualTo("Example Server"));
            Assert.That(GetProperty<string>(option, "Hint"), Does.Contain("192.0.2.10:32400"));
            Assert.That(GetProperty<bool>(option, "IsDisabled"), Is.False);
            Assert.That(GetProperty<bool>(option, "IsHidden"), Is.False);

            var properties = GetProperty<Dictionary<string, object>>(option, "AdditionalProperties");
            Assert.That(properties["host"], Is.EqualTo("192.0.2.10"));
            Assert.That(properties["port"], Is.EqualTo(32400));
            Assert.That(properties["useSsl"], Is.EqualTo(true));
            Assert.That(plexServerService.ProbeAuthTokens, Has.Count.EqualTo(5));
            Assert.That(plexServerService.ProbeAuthTokens.All(string.IsNullOrEmpty), Is.True);
        }

        [Test]
        public void should_require_oauth_token_before_testing_settings()
        {
            var settings = new PlexServerSettings
            {
                Host = "plex.example.com",
                Port = 32400
            };

            var result = settings.Validate();

            Assert.That(result.Errors.Select(e => e.PropertyName), Does.Contain(nameof(PlexServerSettings.AuthToken)));
            Assert.That(result.Errors.Select(e => e.ErrorMessage), Does.Contain("Authenticate with Plex.tv first"));
        }

        [Test]
        public void should_allow_start_oauth_before_host_and_auth_token_are_set()
        {
            var signInUrlResponse = new PlexTvSignInUrlResponse
            {
                OauthUrl = "https://app.plex.tv/auth/hashBang",
                PinId = 123
            };

            var plexTvService = new FakePlexTvService
            {
                PinResponse = new PlexTvPinResponse
                {
                    Id = 123,
                    Code = "pin-code"
                },
                SignInUrlResponse = signInUrlResponse
            };

            var subject = CreateSubject(plexTvService, new FakePlexServerService(), new PlexServerSettings());

            var result = subject.RequestAction("startOAuth", new Dictionary<string, string>
            {
                { "callbackUrl", "http://tower:8789/oauth.html" }
            });

            Assert.That(result, Is.SameAs(signInUrlResponse));
        }

        [Test]
        public void should_report_oauth_poll_pending_when_pin_has_no_token()
        {
            var plexTvService = new FakePlexTvService
            {
                AuthToken = null
            };

            var subject = CreateSubject(plexTvService, new FakePlexServerService(), new PlexServerSettings());

            var result = subject.RequestAction("getOAuthToken", new Dictionary<string, string>
            {
                { "pinId", "123" }
            });

            Assert.That(GetProperty<bool>(result, "authorized"), Is.False);
            Assert.That(result.GetType().GetProperty("authToken"), Is.Null);
        }

        [Test]
        public void should_return_pending_secret_when_oauth_poll_has_token()
        {
            var plexTvService = new FakePlexTvService
            {
                AuthToken = "raw-token"
            };

            var subject = CreateSubject(plexTvService, new FakePlexServerService(), new PlexServerSettings());

            var result = (Dictionary<string, object>)subject.RequestAction("getOAuthToken", new Dictionary<string, string>
            {
                { "pinId", "123" }
            });

            Assert.That(result["authorized"], Is.True);
            Assert.That(result["authToken"], Is.TypeOf<string>());
            Assert.That(result["authToken"], Is.Not.EqualTo("raw-token"));
            Assert.That((string)result["authToken"], Does.StartWith("chaptarr-pending-secret:"));
        }

        [Test]
        public void should_update_library_when_trigger_fires_even_if_legacy_update_library_setting_is_false()
        {
            var plexServerService = new FakePlexServerService();

            var subject = CreateSubject(new FakePlexTvService(), plexServerService, new PlexServerSettings
            {
                Host = "plex.example.com",
                AuthToken = "token",
                UpdateLibrary = false
            });

            subject.OnReleaseImport(new BookDownloadMessage
            {
                Author = new Author
                {
                    Id = 1,
                    Name = "Author"
                }
            });

            subject.ProcessQueue();

            Assert.That(plexServerService.UpdatedAuthorIds, Is.EqualTo(new[] { 1 }));
        }

        private static NzbDrone.Core.Notifications.Plex.Server.PlexServer CreateSubject(
            FakePlexTvService plexTvService,
            FakePlexServerService plexServerService,
            PlexServerSettings settings)
        {
            return new NzbDrone.Core.Notifications.Plex.Server.PlexServer(
                plexServerService,
                plexTvService,
                new PendingProviderSecretService(new CacheManager()),
                new CacheManager(),
                LogManager.GetLogger("PlexServerFixture"))
            {
                Definition = new NotificationDefinition
                {
                    Settings = settings
                }
            };
        }

        private static T GetProperty<T>(object target, string name)
        {
            return (T)target.GetType().GetProperty(name).GetValue(target);
        }

        private class FakePlexTvService : IPlexTvService
        {
            public List<PlexTvResourceResponse> Resources { get; set; } = new List<PlexTvResourceResponse>();
            public PlexTvPinResponse PinResponse { get; set; }
            public PlexTvSignInUrlResponse SignInUrlResponse { get; set; }
            public string AuthToken { get; set; }

            public PlexTvPinResponse CreatePin() => PinResponse ?? throw new NotImplementedException();
            public PlexTvSignInUrlResponse GetSignInUrl(string callbackUrl, int pinId, string pinCode) => SignInUrlResponse ?? throw new NotImplementedException();
            public string GetAuthToken(int pinId) => AuthToken;
            public PlexTvUserResponse GetUser(string authToken) => throw new NotImplementedException();
            public List<PlexTvResourceResponse> GetResources(string authToken) => Resources;
            public void Ping(string authToken)
            {
            }
        }

        private class FakePlexServerService : IPlexServerService
        {
            public HashSet<string> ReachableHosts { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public ConcurrentBag<string> ProbeAuthTokens { get; } = new ConcurrentBag<string>();
            public List<int> UpdatedAuthorIds { get; } = new List<int>();

            public void UpdateLibrary(Author author, PlexServerSettings settings) => throw new NotImplementedException();
            public void UpdateLibrary(IEnumerable<Author> authors, PlexServerSettings settings)
            {
                UpdatedAuthorIds.AddRange(authors.Select(a => a.Id));
            }

            public bool CanConnect(PlexServerSettings settings, TimeSpan timeout, out string message)
            {
                ProbeAuthTokens.Add(settings.AuthToken);
                var reachable = ReachableHosts.Contains(settings.Host);
                message = reachable ? "Reachable" : "Connection refused";

                return reachable;
            }

            public FluentValidation.Results.ValidationFailure Test(PlexServerSettings settings) => throw new NotImplementedException();
            public List<PlexSection> GetSections(PlexServerSettings settings) => throw new NotImplementedException();
        }
    }
}
