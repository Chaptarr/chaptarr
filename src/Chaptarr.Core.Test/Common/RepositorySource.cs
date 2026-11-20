using System;
using System.IO;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Common
{
    internal static class RepositorySource
    {
        internal static string FindRoot()
        {
            var current = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "src", "Chaptarr.Core.Test", "Chaptarr.Core.Test.csproj")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException("Could not locate repository source for the source-contract tests.");
        }
    }
}
