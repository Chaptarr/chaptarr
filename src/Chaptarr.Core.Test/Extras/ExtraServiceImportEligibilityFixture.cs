using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras;
using NzbDrone.Core.Extras.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Extras
{
    [TestFixture]
    public class ExtraServiceImportEligibilityFixture
    {
        [Test]
        public void extra_files_setting_only_imports_configured_sidecars([Values] bool enabled, [Values] bool readOnly, [Values] bool replaceExisting)
        {
            var selected = "/downloads/book.mp3";
            var paths = MediaFileExtensions.AllExtensions.Select(extension => "/downloads/book" + extension)
                .Concat(new[] { "/downloads/book.jpg", "/downloads/book.nfo", "/downloads/book.png" }).ToArray();
            var imported = new List<string>();
            var scans = 0;
            var disk = Proxy<IDiskProvider>((method, _) =>
            {
                if (method.Name == nameof(IDiskProvider.GetParentFolder)) { return "/downloads"; }
                if (method.Name == nameof(IDiskProvider.GetFiles)) { scans++; return paths; }
                throw new NotImplementedException(method.Name);
            });
            var config = Proxy<IConfigService>((method, _) => method.Name switch
            {
                "get_ImportExtraFiles" => enabled,
                "get_ExtraFileExtensions" => "jpg,nfo," + string.Join(",", MediaFileExtensions.AllExtensions),
                _ => throw new NotImplementedException(method.Name)
            });
            var manager = Proxy<IManageExtraFiles>((method, args) =>
            {
                if (method.Name == "get_Order") { return 0; }
                Assert.That(method.Name, Is.EqualTo(nameof(IManageExtraFiles.Import)));
                Assert.That(args[4], Is.EqualTo(readOnly));
                Assert.That(args[5], Is.EqualTo(replaceExisting));
                imported.Add((string)args[2]);
                return null;
            });
            var service = new ExtraService(null, null, disk, config, new[] { manager }, LogManager.GetCurrentClassLogger());

            service.ImportExtraFiles(new LocalBook { Path = selected }, new BookFile(), readOnly, replaceExisting);

            Assert.That(imported, Is.EquivalentTo(enabled ? new[] { "/downloads/book.jpg", "/downloads/book.nfo" } : Array.Empty<string>()));
            Assert.That(scans, Is.EqualTo(enabled ? 1 : 0));
        }
    }
}
