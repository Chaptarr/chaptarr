using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Update;

namespace Chaptarr.Core.Test.Update
{
    [TestFixture]
    public class UpdateCheckServiceFixture
    {
        private sealed class RecentUpdates : IRecentUpdateProvider
        {
            public List<UpdatePackage> Items { get; } = new();
            public int Calls { get; private set; }

            public List<UpdatePackage> GetRecentUpdatePackages()
            {
                Calls++;
                return Items;
            }
        }

        private sealed class Packages : IUpdatePackageProvider
        {
            public UpdatePackage Available { get; set; }

            public UpdatePackage GetLatestUpdate(string branch, Version currentVersion)
            {
                Assert.That(branch, Is.EqualTo("develop"));
                Assert.That(currentVersion, Is.EqualTo(BuildInfo.Version));
                return Available;
            }

            public List<UpdatePackage> GetRecentUpdates(string branch, Version currentVersion, Version previousVersion = null)
            {
                throw new InvalidOperationException("Recent releases must use the existing recent-update provider.");
            }
        }

        private class ConfigProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod.Name == "get_Branch" ? "develop" : throw new NotImplementedException();
            }
        }

        [Test]
        public void should_find_newer_notes_only_release_without_an_installable_package()
        {
            var recent = new RecentUpdates();
            recent.Items.Add(new UpdatePackage { Version = BuildInfo.Version });
            recent.Items.Add(new UpdatePackage { Version = new Version(BuildInfo.Version.Major + 1, 0, 0, 0) });
            var subject = CreateSubject(new Packages(), recent);

            Assert.Multiple(() =>
            {
                Assert.That(subject.HasNewerRelease(), Is.True);
                Assert.That(subject.AvailableUpdate(), Is.Null);
            });
        }

        [TestCase("empty")]
        [TestCase("current")]
        [TestCase("older")]
        [TestCase("threePartCurrent")]
        public void should_not_warn_when_feed_has_no_newer_release(string feed)
        {
            var recent = new RecentUpdates();
            if (feed != "empty")
            {
                var version = feed switch
                {
                    "older" => new Version(0, 0, 0, 0),
                    "threePartCurrent" => new Version(BuildInfo.Version.Major, BuildInfo.Version.Minor, BuildInfo.Version.Build),
                    _ => BuildInfo.Version
                };
                recent.Items.Add(new UpdatePackage { Version = version });
            }

            Assert.That(CreateSubject(new Packages(), recent).HasNewerRelease(), Is.False);
        }

        [Test]
        public void installer_should_use_only_the_verified_package_path()
        {
            var package = new UpdatePackage { Version = new Version(BuildInfo.Version.Major + 1, 0, 0, 0) };
            var recent = new RecentUpdates();
            var subject = CreateSubject(new Packages { Available = package }, recent);

            Assert.Multiple(() =>
            {
                Assert.That(subject.AvailableUpdate(), Is.SameAs(package));
                Assert.That(recent.Calls, Is.Zero);
            });
        }

        private static CheckUpdateService CreateSubject(Packages packages, RecentUpdates recent)
        {
            return new CheckUpdateService(packages, DispatchProxy.Create<IConfigFileProvider, ConfigProxy>(), recent);
        }
    }
}
