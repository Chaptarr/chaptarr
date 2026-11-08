using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Chaptarr.Core.Test.Common;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Cloud;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Update.History;
using NzbDrone.Core.Update;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Update
{
    [TestFixture]
    public class UpdatePackageProviderFixture
    {
        [TestCase("available", true)]
        [TestCase("unavailable", false)]
        [TestCase("old", false)]
        [TestCase("unverified", false)]
        public void services_feed_preserves_package_checks(string scenario, bool expected)
        {
            var package = new UpdatePackage
            {
                Version = new Version(scenario == "old" ? "1.0.0" : "2.0.0"),
                FileName = "package.tar.gz", Url = "https://example.invalid/package.tar.gz",
                Hash = scenario == "unverified" ? null : new string('a', 64)
            };
            var requests = new List<HttpRequest>();
            var subject = CreateSubject(new UpdatePackageAvailable { Available = scenario != "unavailable", UpdatePackage = package }, false, requests);

            var result = subject.GetLatestUpdate("develop", new Version("1.0.0"));

            Assert.That(result != null, Is.EqualTo(expected));
            Assert.That(requests.Single().Url.FullUri, Does.StartWith("https://services.chaptarr.com/v1/update/develop?"));
            if (expected)
            {
                Assert.That(result.Version, Is.EqualTo(package.Version));
                Assert.That(result.Hash, Is.EqualTo(package.Hash));
            }
        }

        [Test]
        public void successful_empty_feed_keeps_installed_release_notes()
        {
            var requests = new List<HttpRequest>();
            var subject = CreateSubject(new List<UpdatePackage>(), false, requests);
            var current = new Version("1.2.3");

            var result = subject.GetRecentUpdates("develop", current, new Version("1.2.2"));

            Assert.That(result.Single().Version, Is.EqualTo(current));
            Assert.That(result.Single().Changes.New, Is.Not.Empty);
            Assert.That(requests.Single().Url.FullUri, Does.StartWith("https://services.chaptarr.com/v1/update/develop/changes?"));
            Assert.That(requests.Single().Url.FullUri, Does.Contain("prevVersion=1.2.2"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void feed_failure_reaches_the_caller_instead_of_claiming_no_updates(bool recent)
        {
            var requests = new List<HttpRequest>();
            var subject = CreateSubject(new List<UpdatePackage>(), true, requests);
            var config = InterfaceProxy.Proxy<IConfigFileProvider>((_, _) => "develop");
            var history = InterfaceProxy.Proxy<IUpdateHistoryService>((_, _) => new Version("1.2.2"));
            var recentUpdates = new RecentUpdateProvider(config, subject, history);
            var check = new CheckUpdateService(subject, config, recentUpdates, NLog.LogManager.GetCurrentClassLogger());

            var error = Assert.Throws<WebException>(() =>
            {
                if (recent)
                {
                    recentUpdates.GetRecentUpdatePackages();
                }
                else
                {
                    check.AvailableUpdate();
                }
            });

            Assert.That(error.Message, Is.EqualTo("Test services outage"));
            Assert.That(requests, Has.Count.EqualTo(1));
            Assert.That(requests[0].Url.FullUri, Does.StartWith("https://services.chaptarr.com/v1/update/develop"));
        }

        private static UpdatePackageProvider CreateSubject(object payload, bool offline, List<HttpRequest> requests)
        {
            var http = InterfaceProxy.Proxy<IHttpClient>((method, args) =>
            {
                var request = (HttpRequest)args[0];
                requests.Add(request);
                if (offline)
                {
                    throw new WebException("Test services outage");
                }

                return Activator.CreateInstance(method.ReturnType, new HttpResponse(request, new HttpHeader(), JsonConvert.SerializeObject(payload)));
            });
            var platform = InterfaceProxy.Proxy<IPlatformInfo>((_, _) => new Version("10.0.0"));
            var database = InterfaceProxy.Proxy<IMainDatabase>((_, _) => DatabaseType.SQLite);
            return new UpdatePackageProvider(http, new ChaptarrCloudRequestBuilder(), platform, database, LogManager.GetCurrentClassLogger());
        }
    }
}
