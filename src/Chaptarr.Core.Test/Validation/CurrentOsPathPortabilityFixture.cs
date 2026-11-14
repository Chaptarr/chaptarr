using System.IO;
using NUnit.Framework;

namespace Chaptarr.Core.Test.Validation
{
    [TestFixture]
    public class CurrentOsPathPortabilityFixture
    {
        [Test]
        public void fixture_paths_are_absolute_and_use_native_separators()
        {
            var root = @"C:\Library".AsOsAgnostic();
            var relative = @"Author\Book\part.mp3".AsOsAgnostic();
            Assert.That(Path.IsPathFullyQualified(root), Is.True);
            Assert.That(Path.Combine(root, relative), Is.EqualTo(Path.Combine(root, "Author", "Book", "part.mp3")));
        }
    }
}
