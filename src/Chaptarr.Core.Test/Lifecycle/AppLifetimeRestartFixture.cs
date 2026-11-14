using NUnit.Framework;
using NzbDrone.Host;

namespace Chaptarr.Core.Test.Lifecycle
{
    [TestFixture]
    public class AppLifetimeRestartFixture
    {
        [Test]
        public void restart_under_the_dotnet_host_should_pass_the_entry_assembly_first()
        {
            var (path, args) = AppLifetime.BuildRestartCommand(
                "/usr/share/dotnet/dotnet",
                "/app/Chaptarr.dll",
                "/data=/config /nobrowser /restart");

            Assert.That(path, Is.EqualTo("/usr/share/dotnet/dotnet"));
            Assert.That(args, Is.EqualTo("\"/app/Chaptarr.dll\" /data=/config /nobrowser /restart"));
        }

        [TestCase("/usr/lib/dotnet/dotnet")]
        [TestCase("/usr/bin/DOTNET")]
        public void dotnet_host_detection_should_ignore_case_and_install_location(string host)
        {
            var (_, args) = AppLifetime.BuildRestartCommand(host, "/app/Chaptarr.dll", "/restart");

            Assert.That(args, Is.EqualTo("\"/app/Chaptarr.dll\" /restart"));
        }

        [TestCase("/opt/chaptarr/Chaptarr")]
        [TestCase(@"C:\ProgramData\Chaptarr\bin\Chaptarr.exe")]
        public void restart_of_a_native_app_host_should_keep_the_arguments_unchanged(string host)
        {
            var (path, args) = AppLifetime.BuildRestartCommand(host, "/opt/chaptarr/Chaptarr.dll", "/data=/config /nobrowser /restart");

            Assert.That(path, Is.EqualTo(host));
            Assert.That(args, Is.EqualTo("/data=/config /nobrowser /restart"));
        }

        [Test]
        public void restart_without_a_known_entry_assembly_should_keep_the_arguments_unchanged()
        {
            var (_, args) = AppLifetime.BuildRestartCommand("/usr/share/dotnet/dotnet", null, "/restart");

            Assert.That(args, Is.EqualTo("/restart"));
        }

        [TestCase(true, 4242, true)]
        [TestCase(false, 1, true)]
        [TestCase(true, 1, true)]
        [TestCase(false, 4242, false)]
        public void restart_should_be_left_to_the_container_when_in_docker_or_running_as_pid_1(bool isDocker, int processId, bool expected)
        {
            Assert.That(AppLifetime.ShouldLeaveRestartToContainer(isDocker, processId), Is.EqualTo(expected));
        }
    }
}
