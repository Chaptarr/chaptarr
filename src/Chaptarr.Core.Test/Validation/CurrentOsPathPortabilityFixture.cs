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
            "\\b(?:Path|(?:Audio|Ebook)?RootFolderPath|(?:Audio|Ebook)?RootPath|(?:Author|Audiobook|Ebook)(?:Path|Folder)|FilePath|FolderPath|LibraryPath|DownloadPath|OutputPath|SourcePath|DestinationPath|TargetPath|root)\\s*(?:=|:)\\s*@?\"/|\\bnew\\s+OsPath\\s*\\(\\s*@?\"/",
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
                // Remote-client paths and stored metadata can intentionally use another OS's syntax.
                // Enforce local-path spelling where the fixture invokes current-OS validation.
                if (!source.Contains(".OnActionExecuting(", StringComparison.Ordinal) &&
                    !source.Contains("PathValidationType.CurrentOs", StringComparison.Ordinal))
                {
                    continue;
                }

                var lines = File.ReadAllLines(file);
                for (var index = 0; index < lines.Length; index++)
                {
                    if (RootedUnixPathAssignment.IsMatch(lines[index]))
                    {
                        failures.Add($"{Path.GetRelativePath(testSourceRoot, file)}:{index + 1}");
                    }
                }
            }

            Assert.That(
                failures,
                Is.Empty,
                "Tests that execute current-OS path validation must author Windows-style local paths with AsOsAgnostic(), or build native paths with Path.Combine().");
        }

        [TestCase("Path = \"/ebooks\",", true)]
        [TestCase("Path = \"/ebooks\".AsOsAgnostic(),", true)]
        [TestCase("new OsPath(\"/downloads/book\")", true)]
        [TestCase("OutputPath = \"/downloads/book\",", true)]
        [TestCase("AuthorPath = \"/books/author\",", true)]
        [TestCase("AudiobookPath = \"/audio/author\",", true)]
        [TestCase("EbookPath = \"/ebooks/author\",", true)]
        [TestCase("AuthorFolder = \"/books/author\",", true)]
        [TestCase("AudiobookFolder = \"/audio/author\",", true)]
        [TestCase("EbookFolder = \"/ebooks/author\",", true)]
        [TestCase("new BookFile { Path = \"/books/book.epub\" }", true)]
        [TestCase("Import(outputPath: \"/downloads/book\")", true)]
        [TestCase("new OsPath(@\"C:\\downloads\\book\".AsOsAgnostic())", false)]
        [TestCase("Path = Path.Combine(root, \"book.epub\"),", false)]
        [TestCase("RootFolderPath = @\"C:\\ebooks\".AsOsAgnostic(),", false)]
        [TestCase("routePath = \"/readarr/ebook\",", false)]
        [TestCase("requestPath = \"/api/v1/book\",", false)]
        public void rooted_path_assignment_detection_should_only_match_unix_file_system_literals(string source, bool expected)
        {
            Assert.That(
                RootedUnixPathAssignment.IsMatch(source),
                Is.EqualTo(expected));
        }

        [Test]
        public void fixture_paths_are_absolute_and_use_native_separators()
        {
            var root = @"C:\Library".AsOsAgnostic();
            var relative = @"Author\Book\part.mp3".AsOsAgnostic();
            Assert.That(Path.IsPathFullyQualified(root), Is.True);
            Assert.That(Path.Combine(root, relative), Is.EqualTo(Path.Combine(root, "Author", "Book", "part.mp3")));
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
