using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Validation
{
    [TestFixture]
    public class CurrentOsPathPortabilityFixture
    {
        private static readonly Regex RootedUnixPathAssignment = new Regex(
            "\\b(?:Path|(?:Audio|Ebook)?RootFolderPath|(?:Audio|Ebook)?RootPath|FilePath|FolderPath|LibraryPath|DownloadPath|SourcePath|DestinationPath|TargetPath|root)\\s*=\\s*@?\"/",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        [Test]
        public void current_os_validation_tests_should_adapt_rooted_file_system_paths()
        {
            var testSourceRoot = Path.Combine(FindRepositoryRoot(), "src", "Chaptarr.Core.Test");
            var failures = new List<string>();

            foreach (var file in Directory.EnumerateFiles(testSourceRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file) == nameof(CurrentOsPathPortabilityFixture) + ".cs")
                {
                    continue;
                }

                var source = File.ReadAllText(file);
                if (!source.Contains(".OnActionExecuting(", StringComparison.Ordinal) &&
                    !source.Contains("PathValidationType.CurrentOs", StringComparison.Ordinal))
                {
                    continue;
                }

                var lines = File.ReadAllLines(file);
                for (var index = 0; index < lines.Length; index++)
                {
                    if (RootedUnixPathAssignment.IsMatch(lines[index]) &&
                        !lines[index].Contains(".AsOsAgnostic()", StringComparison.Ordinal))
                    {
                        failures.Add($"{Path.GetRelativePath(testSourceRoot, file)}:{index + 1}");
                    }
                }
            }

            Assert.That(
                failures,
                Is.Empty,
                "Tests that execute current-OS path validation must author local paths with AsOsAgnostic().");
        }

        [TestCase("Path = \"/ebooks\",", true)]
        [TestCase("RootFolderPath = @\"C:\\ebooks\".AsOsAgnostic(),", false)]
        [TestCase("routePath = \"/readarr/ebook\",", false)]
        public void rooted_path_assignment_detection_should_only_match_unix_file_system_literals(string source, bool expected)
        {
            Assert.That(
                RootedUnixPathAssignment.IsMatch(source) && !source.Contains(".AsOsAgnostic()", StringComparison.Ordinal),
                Is.EqualTo(expected));
        }

        private static string FindRepositoryRoot()
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

            throw new InvalidOperationException("Could not locate the repository root for the path portability check.");
        }
    }
}
