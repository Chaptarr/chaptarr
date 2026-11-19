using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Model;
using NzbDrone.Common.Processes;
using NzbDrone.Host;

using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Datastore
{
    [TestFixture]
    [NonParallelizable]
    public class StartupOwnershipFixture
    {
        private const string ChildFolderVariable = "CHAPTARR_TEST_LOCK_FOLDER";
        private string _folder;
        private IAppFolderInfo _folders;
        private static readonly Logger Logger = LogManager.GetLogger(nameof(StartupOwnershipFixture));

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, "startup_ownership_" + Guid.NewGuid().ToString("N"));
            _folders = Folders(_folder);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
        }

        private static IAppFolderInfo Folders(string folder) => Proxy<IAppFolderInfo>((_, _) => folder);

        private static IProcessProvider Processes(Action<int> kill = null, bool otherInstance = true, Action onFind = null)
        {
            return Proxy<IProcessProvider>((method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(IProcessProvider.GetCurrentProcess): return new ProcessInfo { Id = 1 };
                    case nameof(IProcessProvider.FindProcessByName):
                        onFind?.Invoke();
                        return otherInstance && (string)args[0] == ProcessProvider.CHAPTARR_PROCESS_NAME
                            ? new List<ProcessInfo> { new ProcessInfo { Id = 1 }, new ProcessInfo { Id = 2 } }
                            : new List<ProcessInfo>();
                    case nameof(IProcessProvider.Kill): kill?.Invoke((int)args[0]); return null;
                    default: throw new InvalidOperationException("Unexpected process operation " + method.Name);
                }
            });
        }

        [Test]
        public void normal_second_launch_should_open_existing_ui_before_attempting_ownership()
        {
            using var owner = SingleInstancePolicy.AcquireAppData(_folders);
            var launched = 0;
            var error = Assert.Throws<TerminateApplicationException>(() => SingleInstancePolicy.AcquireAppData(
                _folders, new StartupContext(), Processes(), () =>
                {
                    Assert.Throws<TerminateApplicationException>(() => SingleInstancePolicy.AcquireAppData(_folders));
                    launched++;
                }, Logger));
            Assert.That(launched, Is.EqualTo(1));
            Assert.That(error.Message, Does.EndWith("Another instance is already running"));
        }

        [Test]
        public void terminateexisting_should_wait_for_existing_process_to_release_ownership_before_acquiring()
        {
            var owner = SingleInstancePolicy.AcquireAppData(_folders);
            try
            {
                var killed = new List<int>();
                using var next = SingleInstancePolicy.AcquireAppData(_folders, new StartupContext("/terminateexisting"), Processes(id =>
                {
                    Assert.Throws<TerminateApplicationException>(() => SingleInstancePolicy.AcquireAppData(_folders));
                    killed.Add(id);
                    owner.Dispose();
                }), () => Assert.Fail("Termination must not launch the browser"), Logger);
                Assert.That(killed, Is.EqualTo(new[] { 2 }));
                Assert.That(next.CanWrite, Is.True);
                Assert.Throws<TerminateApplicationException>(() => SingleInstancePolicy.AcquireAppData(_folders));
            }
            finally
            {
                owner.Dispose();
            }
            using var following = SingleInstancePolicy.AcquireAppData(_folders);
        }

        [Test]
        public void failed_termination_must_not_create_or_mutate_appdata()
        {
            Assert.Throws<InvalidOperationException>(() => SingleInstancePolicy.AcquireAppData(_folders,
                new StartupContext("/terminateexisting"), Processes(_ => throw new InvalidOperationException("Termination failed")),
                () => Assert.Fail("Unexpected browser launch"), Logger));
            Assert.That(Directory.Exists(_folder), Is.False);
        }

        [TestCase("/restart")]
        [TestCase("/nosingleinstancecheck")]
        public void process_check_optouts_must_still_require_exclusive_appdata(string flag)
        {
            using var owner = SingleInstancePolicy.AcquireAppData(_folders);
            Assert.Throws<TerminateApplicationException>(() => SingleInstancePolicy.AcquireAppData(_folders,
                new StartupContext(flag), Processes(onFind: () => Assert.Fail("Process policy was bypassed")),
                () => Assert.Fail("Unexpected browser launch"), Logger));
        }

        [Test]
        public void explicit_data_folder_should_warn_but_keep_the_ownership_boundary()
        {
            var finds = 0;
            using var owner = SingleInstancePolicy.AcquireAppData(_folders);
            var error = Assert.Throws<TerminateApplicationException>(() => SingleInstancePolicy.AcquireAppData(_folders,
                new StartupContext("/data=" + _folder), Processes(onFind: () => finds++),
                () => Assert.Fail("Explicit data folder only warns about processes"), Logger));
            Assert.That(finds, Is.EqualTo(2));
            Assert.That(error.Message, Does.Contain("Cannot exclusively open the data directory"));
        }

        [Test]
        public void first_launch_should_hold_ownership_until_disposal()
        {
            using (var owner = SingleInstancePolicy.AcquireAppData(_folders, new StartupContext(), Processes(otherInstance: false),
                () => Assert.Fail("No existing instance"), Logger))
            {
                Assert.That(owner.CanWrite, Is.True);
                Assert.Throws<TerminateApplicationException>(() => SingleInstancePolicy.AcquireAppData(_folders));
                using var independent = SingleInstancePolicy.AcquireAppData(Folders(Path.Combine(_folder, "independent")));
            }
            using var following = SingleInstancePolicy.AcquireAppData(_folders);
        }

        [TestCase(true, 9876, "http://localhost:9876")]
        [TestCase(false, 9876, null)]
        public void early_and_host_browser_launches_should_use_the_same_runtime_and_port_behavior(bool interactive, int port, string expected)
        {
            string opened = null;
            var process = Proxy<IProcessProvider>((method, args) =>
            {
                Assert.That(method.Name, Is.EqualTo(nameof(IProcessProvider.OpenDefaultBrowser)));
                opened = (string)args[0];
                return null;
            });
            BrowserService.LaunchWebUI(process, port, interactive, Logger);
            Assert.That(opened, Is.EqualTo(expected));
            opened = null;
            new BrowserService(process, Proxy<NzbDrone.Core.Configuration.IConfigFileProvider>((_, _) => port),
                Proxy<IRuntimeInfo>((_, _) => interactive), Logger).LaunchWebUI();
            Assert.That(opened, Is.EqualTo(expected));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void separate_process_ownership_should_be_released_by_shutdown_or_process_exit(bool terminate)
        {
            Directory.CreateDirectory(_folder);
            var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add("vstest");
            info.ArgumentList.Add(typeof(StartupOwnershipFixture).Assembly.Location);
            info.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=" + typeof(StartupOwnershipFixture).FullName + ".child_process_ownership_probe");
            info.Environment[ChildFolderVariable] = _folder;
            using var child = Process.Start(info);
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            try
            {
                Assert.That(SpinWait.SpinUntil(() => File.Exists(Path.Combine(_folder, "ready")) || child.HasExited,
                    TimeSpan.FromSeconds(30)), Is.True, "Child failed to acquire ownership");
                Assert.That(File.Exists(Path.Combine(_folder, "ready")), Is.True, child.HasExited ? output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult() : "Child not ready");
                Assert.Throws<TerminateApplicationException>(() => SingleInstancePolicy.AcquireAppData(_folders));
                if (OperatingSystem.IsWindows())
                {
                    using var reader = new FileStream(_folders.GetAppDataLockFile(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    Assert.That(reader.CanRead, Is.True);
                }
                using var ownerProcess = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(_folder, "ready"))));
                if (terminate) ownerProcess.Kill();
                else File.WriteAllText(Path.Combine(_folder, "release"), string.Empty);
                Assert.That(ownerProcess.WaitForExit(30000), Is.True, "Owner process did not exit");
                Assert.That(child.WaitForExit(30000), Is.True, "Child did not release ownership");
                if (!terminate) Assert.That(child.ExitCode, Is.Zero, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
                using var following = SingleInstancePolicy.AcquireAppData(_folders);
                Assert.That(following.CanWrite, Is.True);
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(true);
                    child.WaitForExit();
                }
            }
        }

        [Test]
        public void child_process_ownership_probe()
        {
            var folder = Environment.GetEnvironmentVariable(ChildFolderVariable);
            if (string.IsNullOrEmpty(folder)) return;
            using var owner = SingleInstancePolicy.AcquireAppData(Folders(folder));
            File.WriteAllText(Path.Combine(folder, "ready.tmp"), Environment.ProcessId.ToString());
            File.Move(Path.Combine(folder, "ready.tmp"), Path.Combine(folder, "ready"));
            Assert.That(SpinWait.SpinUntil(() => File.Exists(Path.Combine(folder, "release")), TimeSpan.FromSeconds(45)), Is.True);
        }
    }
}
