using System;
using System.IO;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Common
{
    internal static class SymbolicLinkTest
    {
        public static void CreateFile(string path, string target) => Create(() => File.CreateSymbolicLink(path, target));

        public static void CreateDirectory(string path, string target) => Create(() => Directory.CreateSymbolicLink(path, target));

        private static void Create(Action create)
        {
            try
            {
                create();
            }
            catch (UnauthorizedAccessException)
            {
                Assert.Ignore("This test requires permission to create symbolic links.");
            }
            catch (PlatformNotSupportedException)
            {
                Assert.Ignore("This platform does not support creating symbolic links.");
            }
            catch (IOException ex) when (OperatingSystem.IsWindows() && (ex.HResult & 0xffff) is 50 or 1314)
            {
                Assert.Ignore("This test requires filesystem symbolic link support and privileges.");
            }
        }
    }
}
