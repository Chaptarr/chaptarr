using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Goodreads;
using NzbDrone.Core.Localization;
using NzbDrone.Core.ThingiProvider.Events;

namespace Chaptarr.Core.Test.ImportLists
{
    [TestFixture]
    public class ImportListIdentityHealthFixture
    {
        [Test]
        public void enabled_formatless_list_warns_and_correcting_settings_clears_warning()
        {
            var list = new ImportListDefinition { Name = "My list", EnableAutomaticAdd = true,
                Settings = new GoodreadsBookshelfImportListSettings { MonitorAudiobooks = false, MonitorEbooks = false } };
            var factory = InterfaceProxy.Proxy<IImportListFactory>((method, args) => new List<ImportListDefinition> { list });
            var disk = InterfaceProxy.Proxy<IDiskProvider>((method, args) => true);
            var localization = InterfaceProxy.Proxy<ILocalizationService>((method, args) => "List '{0}' has no format enabled and will import nothing.");
            var check = new ImportListRootFolderCheck(factory, disk, localization);
            Assert.That(check.Check().Type, Is.EqualTo(HealthCheckResult.Warning));
            list.EnableAutomaticAdd = false;
            Assert.That(check.Check().Type, Is.EqualTo(HealthCheckResult.Ok));
            list.EnableAutomaticAdd = true;
            ((DualMediaImportListSettings)list.Settings).MonitorAudiobooks = true;
            ((DualMediaImportListSettings)list.Settings).AudiobookRootFolderPath = "/audio";
            Assert.That(check.Check().Type, Is.EqualTo(HealthCheckResult.Ok));
        }
    }
}
