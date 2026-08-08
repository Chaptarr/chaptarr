using System;
using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Update;

namespace Chaptarr.Core.Test.Update
{
    [TestFixture]
    public class UpdatePackageProviderSupplementFixture
    {
        private static readonly Version InstalledVersion = new Version("0.9.600");

        private static List<UpdatePackage> Enhancements(string marker = "enhancement")
        {
            return new List<UpdatePackage>
            {
                new UpdatePackage
                {
                    Version = InstalledVersion,
                    Branch = "chaptarr",
                    Changes = new UpdateChanges
                    {
                        New = new List<string> { $"{marker} new" },
                        Fixed = new List<string> { $"{marker} fixed" }
                    }
                }
            };
        }

        private static UpdatePackage ServerRow(string version, UpdateChanges changes)
        {
            return new UpdatePackage
            {
                Version = new Version(version),
                Branch = "develop",
                Changes = changes
            };
        }

        [Test]
        public void should_return_enhancements_when_server_has_no_rows()
        {
            var enhancements = Enhancements();

            Assert.That(UpdatePackageProvider.SupplementWithChaptarrEnhancements(null, enhancements, InstalledVersion), Is.SameAs(enhancements));
            Assert.That(UpdatePackageProvider.SupplementWithChaptarrEnhancements(new List<UpdatePackage>(), enhancements, InstalledVersion), Is.SameAs(enhancements));
        }

        [Test]
        public void should_not_supplement_when_server_knows_installed_version_with_content()
        {
            var serverChanges = new UpdateChanges
            {
                New = new List<string> { "server new" },
                Fixed = new List<string> { "server fixed" }
            };
            var rows = new List<UpdatePackage>
            {
                ServerRow("0.9.600", serverChanges),
                ServerRow("0.9.500", new UpdateChanges { New = new List<string> { "older release" } })
            };

            var result = UpdatePackageProvider.SupplementWithChaptarrEnhancements(rows, Enhancements(), InstalledVersion);

            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result[0].Changes.New, Is.EqualTo(new[] { "server new" }), "server row is canonical; enhancements must not be appended");
            Assert.That(result[0].Changes.Fixed, Is.EqualTo(new[] { "server fixed" }));
            Assert.That(result[1].Changes.New, Is.EqualTo(new[] { "older release" }), "older rows must never be touched");
        }

        [Test]
        public void should_fill_changes_when_server_row_for_installed_version_has_none()
        {
            foreach (var emptyChanges in new[] { null, new UpdateChanges() })
            {
                var rows = new List<UpdatePackage> { ServerRow("0.9.600", emptyChanges) };
                var enhancements = Enhancements();

                var result = UpdatePackageProvider.SupplementWithChaptarrEnhancements(rows, enhancements, InstalledVersion);

                Assert.That(result, Has.Count.EqualTo(1));
                Assert.That(result[0].Changes, Is.SameAs(enhancements[0].Changes), "empty server row must be filled from the binary's own notes");
            }
        }

        [Test]
        public void should_add_synthetic_row_when_server_lags_behind_installed_version()
        {
            var rows = new List<UpdatePackage>
            {
                ServerRow("0.9.578", new UpdateChanges { New = new List<string> { "0.9.578 note" } }),
                ServerRow("0.9.577", new UpdateChanges { New = new List<string> { "0.9.577 note" } })
            };
            var enhancements = Enhancements();

            var result = UpdatePackageProvider.SupplementWithChaptarrEnhancements(rows, enhancements, InstalledVersion);

            Assert.That(result, Has.Count.EqualTo(3), "running version unknown to the server must be added, not grafted onto an older row");
            Assert.That(result, Does.Contain(enhancements[0]));
            Assert.That(result[0].Changes.New, Is.EqualTo(new[] { "0.9.578 note" }), "existing rows keep their own notes");
            Assert.That(result[1].Changes.New, Is.EqualTo(new[] { "0.9.577 note" }));
        }

        [Test]
        public void should_match_installed_version_across_3_part_and_4_part_shapes()
        {
            // BuildInfo.Version is 4-part (0.9.600.0); manifest rows may be 3-part (0.9.600).
            var rows = new List<UpdatePackage>
            {
                ServerRow("0.9.600", new UpdateChanges { New = new List<string> { "server new" } })
            };

            var result = UpdatePackageProvider.SupplementWithChaptarrEnhancements(rows, Enhancements(), new Version("0.9.600.0"));

            Assert.That(result, Has.Count.EqualTo(1), "3-part server row must be recognized as the running 4-part version, not duplicated");
            Assert.That(result[0].Changes.New, Is.EqualTo(new[] { "server new" }));
        }

        [Test]
        public void should_leave_newer_rows_alone_when_update_is_available()
        {
            var rows = new List<UpdatePackage>
            {
                ServerRow("0.9.700", new UpdateChanges { New = new List<string> { "future release" } }),
                ServerRow("0.9.600", new UpdateChanges { New = new List<string> { "installed release" } })
            };

            var result = UpdatePackageProvider.SupplementWithChaptarrEnhancements(rows, Enhancements(), InstalledVersion);

            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result[0].Changes.New, Is.EqualTo(new[] { "future release" }), "available-update row must never receive the running version's notes");
            Assert.That(result[1].Changes.New, Is.EqualTo(new[] { "installed release" }));
        }
    }
}
