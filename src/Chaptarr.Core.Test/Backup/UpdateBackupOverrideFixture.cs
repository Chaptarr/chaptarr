using System;
using System.IO;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Backup;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Update;
using NzbDrone.Core.Update.Commands;

namespace Chaptarr.Core.Test.Backup
{
    [TestFixture]
    public class UpdateBackupOverrideFixture
    {
        [TestCase(CommandTrigger.Manual, false, 1)]
        [TestCase(CommandTrigger.Manual, true, 0)]
        [TestCase(CommandTrigger.Scheduled, false, 1)]
        [TestCase(CommandTrigger.Scheduled, true, 1)]
        public void only_an_explicit_manual_update_can_skip_the_backup(CommandTrigger trigger, bool skip, int expectedBackups)
        {
            var backups = 0;
            var backup = InterfaceProxy.Proxy<IBackupService>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IBackupService.Backup)));
                Assert.That(args[0], Is.EqualTo(BackupType.Update));
                backups++;
                throw new IOException("Matching PostgreSQL tools are unavailable");
            });
            var config = InterfaceProxy.Proxy<IConfigFileProvider>((method, _) => method.Name switch
            {
                "get_UpdateAutomatically" => true,
                "get_UpdateMechanism" => UpdateMechanism.BuiltIn,
                "get_Branch" => "develop",
                _ => throw new NotImplementedException(method.Name)
            });
            var folders = InterfaceProxy.Proxy<IAppFolderInfo>((method, _) => method.Name switch
            {
                "get_StartUpFolder" => @"C:\Apps\Chaptarr".AsOsAgnostic(),
                "get_AppDataFolder" => @"C:\Data\Chaptarr".AsOsAgnostic(),
                "get_TempFolder" => @"C:\Temp".AsOsAgnostic(),
                _ => throw new NotImplementedException(method.Name)
            });
            var deployment = InterfaceProxy.Proxy<IDeploymentInfoProvider>((method, _) => method.Name == "get_PackageUpdateMechanism" ? UpdateMechanism.BuiltIn : false);
            var disk = InterfaceProxy.Proxy<IDiskProvider>((method, _) => method.Name switch
            {
                "FolderWritable" => true,
                "GetTotalSize" => 2L * 1024 * 1024 * 1024,
                "FolderExists" or "FileExists" => false,
                _ => throw new NotImplementedException(method.Name)
            });
            var subject = new InstallUpdateService(
                InterfaceProxy.Proxy<ICheckUpdateService>((_, _) => new UpdatePackage { Version = BuildInfo.Version, FileName = "chaptarr.zip", Branch = "develop", Url = "https://example.invalid/update" }),
                folders, null, disk, InterfaceProxy.Proxy<IDiskTransferService>((_, _) => TransferMode.Move), InterfaceProxy.Proxy<IHttpClient>((_, _) => null),
                InterfaceProxy.Proxy<IArchiveService>((_, _) => null), null, InterfaceProxy.Proxy<IVerifyUpdates>((_, _) => true), null,
                deployment, config, null, backup, null, LogManager.GetCurrentClassLogger());
            var command = new ApplicationUpdateCommand { Trigger = trigger, SkipBackup = skip };
            if (expectedBackups == 0) Assert.DoesNotThrow(() => subject.Execute(command));
            else Assert.Throws<IOException>(() => subject.Execute(command));
            Assert.That(backups, Is.EqualTo(expectedBackups));
        }
    }
}
