using System;
using System.IO;
using System.IO.Abstractions;
using System.Reflection;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Common
{
    public partial class DiskProviderBaseUnicodePathResolutionFixture
    {
        [TestCase(true)]
        [TestCase(false)]
        public void recursive_folder_deletion_rechecks_a_pending_filesystem_operation(bool recursive)
        {
            var path = Path.Combine(_tempDir, "Author");
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "Book.epub"), "explicitly selected folder");
            var disk = new FileSystem();
            var attempts = 0;
            var directory = InterfaceProxy.Proxy<IDirectory>((method, args) =>
            {
                if (method.Name == nameof(IDirectory.Delete) && ++attempts == 1)
                    throw new IOException("The network filesystem has not completed the delete yet.");
                try { return method.Invoke(disk.Directory, args); }
                catch (TargetInvocationException error) { throw error.InnerException; }
            });
            var filesystem = InterfaceProxy.Proxy<IFileSystem>((method, args) =>
                method.Name == "get_Directory" ? directory : method.Invoke(disk, args));
            var provider = new TestDiskProvider(filesystem);
            if (recursive)
            {
                provider.DeleteFolder(path, true);
                Assert.That(attempts, Is.EqualTo(2));
                Assert.That(Directory.Exists(path), Is.False);
            }
            else
            {
                Assert.Throws<IOException>(() => provider.DeleteFolder(path, false));
                Assert.That(attempts, Is.EqualTo(1));
                Assert.That(File.Exists(Path.Combine(path, "Book.epub")), Is.True);
            }
        }
    }
}
