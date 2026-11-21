using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class BookSearchTriggerContractFixture
    {
        [Test]
        public void literal_manual_book_searches_are_limited_to_explicit_search_endpoints()
        {
            var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "src", "Chaptarr.Core.Test", "Chaptarr.Core.Test.csproj"))) root = root.Parent;
            Assert.That(root, Is.Not.Null);
            var found = new List<string>();
            foreach (var directory in new[] { "NzbDrone.Core", "Chaptarr.Api.V1" })
            {
                foreach (var file in Directory.EnumerateFiles(Path.Combine(root.FullName, "src", directory), "*.cs", SearchOption.AllDirectories))
                {
                    var source = File.ReadAllText(file);
                    foreach (var statement in source.Split(';').Where(statement => statement.Contains("BookSearchCommand") &&
                        statement.Contains("CommandTrigger.Manual") && statement.Contains(".Push(")))
                    {
                        found.Add(Path.GetRelativePath(root.FullName, file).Replace('\\', '/'));
                        Assert.That(source.LastIndexOf("AddWantedEdition(", source.IndexOf(statement, StringComparison.Ordinal), StringComparison.Ordinal),
                            Is.GreaterThanOrEqualTo(0), "Only the explicit narrator-selection endpoint grants Manual here");
                    }
                }
            }
            Assert.That(found, Is.EqualTo(new[] { "src/Chaptarr.Api.V1/Books/BookController.cs" }));
            var commands = File.ReadAllText(Path.Combine(root.FullName, "src", "Chaptarr.Api.V1", "Commands", "CommandController.cs"));
            Assert.That(commands, Does.Contain("_commandQueueManager.Push(command, priority, CommandTrigger.Manual)"));
        }
    }
}
