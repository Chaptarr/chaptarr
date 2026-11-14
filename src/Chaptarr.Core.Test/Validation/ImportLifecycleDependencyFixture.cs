using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Validation
{
    [TestFixture]
    public class ImportLifecycleDependencyFixture
    {
        private static readonly Regex DatabaseWithThrowingRepository = new(
            @"mainDatabase:\s*(?!null\b)[\w.]+\s*,\s*(?:bookRepository:[^;]*?,\s*)?authorRepository:\s*(?:global::)?[\w.]*NoPendingAuthorSetup\(\)");

        [Test]
        public void database_backed_refresh_constructors_use_a_working_author_repository()
        {
            var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (root != null && !Directory.Exists(Path.Combine(root.FullName, "src", "Chaptarr.Core.Test"))) root = root.Parent;
            Assert.That(root, Is.Not.Null, "Repository source is required for the dependency gate");
            var source = Path.Combine(root.FullName, "src", "Chaptarr.Core.Test");
            var failures = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path) != nameof(ImportLifecycleDependencyFixture) + ".cs")
                .Where(path => DatabaseWithThrowingRepository.IsMatch(File.ReadAllText(path)))
                .Select(path => Path.GetRelativePath(source, path)).ToList();
            Assert.That(failures, Is.Empty, "A real database must not be paired with the throwing no-setup repository stub");
        }

        [TestCase("mainDatabase: database, authorRepository: global::Chaptarr.Core.Test.Common.ImportLifecycleStubs.NoPendingAuthorSetup()", true)]
        [TestCase("mainDatabase: database, bookRepository: new BookRepository(database, events), authorRepository: ImportLifecycleStubs.NoPendingAuthorSetup()", true)]
        [TestCase("mainDatabase: null, authorRepository: ImportLifecycleStubs.NoPendingAuthorSetup()", false)]
        [TestCase("mainDatabase: database, authorRepository: new AuthorRepository(database, events, logger)", false)]
        public void dependency_gate_detects_the_mixed_constructor(string source, bool expected)
        {
            Assert.That(DatabaseWithThrowingRepository.IsMatch(source), Is.EqualTo(expected));
        }
    }
}
