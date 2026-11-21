using System;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Authors;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class AuthorFolderMatchingServiceFixture
    {
        [TestCase("Frank Herberts", "Frank Herbert")]
        [TestCase("Nora Robert", "Nora Roberts")]
        public void folder_lookup_should_use_the_same_surname_compatibility(string folder, string author)
        {
            Assert.That(new AuthorFolderMatchingService(null, LogManager.GetCurrentClassLogger())
                .ValidateFolderMatchesAuthor(folder, author), Is.True);
        }

        [TestCase("Stephen King", "Stephen King", true)]
        [TestCase("Stephen King", "StephenKing", true)]
        [TestCase("Stephen King", "Stephen Kingsley", false)]
        [TestCase("Stephen King", "StephenKingsley", false)]
        [TestCase("Stephen King", "Stephen Kingfisher - A Series", false)]
        [TestCase("Stephen King", "King", false)]
        [TestCase("Stephen King", "Stephen King - A Series", true)]
        [TestCase("Jennifer L. Armentrout", "Jennifer Armentrout", true)]
        [TestCase("Jennifer L. Armentrout", "Jennifer Q. Armentrout", false)]
        [TestCase("Robert Louis Stevenson", "Stevenson, Robert", true)]
        [TestCase("Robert Louis Stevenson", "Robert Andrew Stevenson", false)]
        [TestCase("C S Lewis", "CS Lewis", true)]
        [TestCase("Clive Staples Lewis", "CS Lewis", false)]
        [TestCase("Le Guin, Ursula K.", "Ursula Le Guin", true)]
        [TestCase("Le Guin, Ursula K.", "Ursula Guin", false)]
        [TestCase("Gabriel Garcia Marquez", "Gabriel Jose de la Concordia Garcia Marquez", true)]
        [TestCase("Лев Николаевич Толстой", "Лев Толстой", true)]
        [TestCase("Лев Николаевич Толстой", "Лев Александрович Толстой", false)]
        [TestCase("がとう たろう", "かとう たろう", false)]
        [TestCase("José García", "Jose Garcia", true)]
        [TestCase("Ann Smith", "Ann & Pat Smith", true)]
        [TestCase("Ann Smith", "Ann; Pat Smith", false)]
        [TestCase("Ann Smith", "Ann and Pat Smith", true)]
        [TestCase("Ann Smith", "Ann und Pat Smith", true)]
        [TestCase("Ann and Pat Smith", "Ann und Pat Smith", false)]
        [TestCase("Ann & Pat Smith", "Ann & Pat Smith", true)]
        [TestCase("Ann & Pat Smith", "Ann ＆ Pat Smith", true)]
        [TestCase("Ann & Pat Smith", "Ann & Jane Smith", false)]
        [TestCase("Ann & Pat Smith", "Pat Smith", true)]
        [TestCase("Jane Smith", "Pat Smith, Jane Q", false)]
        [TestCase("Ann ＆ Pat Smith", "Ann & Pat Smith", true)]
        [TestCase("Ann Smith", "Ann ＆ Pat Smith", true)]
        public void folder_validation_should_use_the_shared_complete_name_rule(string authorName, string folderName, bool matches)
        {
            var service = new AuthorFolderMatchingService(null, LogManager.GetCurrentClassLogger());
            var path = Path.Combine(@"C:\books".AsOsAgnostic(), folderName);

            Assert.Multiple(() =>
            {
                Assert.That(service.ValidateFolderMatchesAuthor(path, authorName), Is.EqualTo(matches));
                Assert.That(service.ValidateFolderMatchesAuthorWithDetails(path, authorName).isValid, Is.EqualTo(matches));
            });
        }

        [TestCase("Stephen King", "Stephen Kingsley", "King, Stephen")]
        [TestCase("Ann & Pat Smith", "Ann & Jane Smith", "Ann & Pat Smith")]
        public void scan_and_path_walk_should_reject_a_conflicting_name_and_read_directories_once(string canonicalName, string wrongName, string rightName)
        {
            var root = @"C:\books".AsOsAgnostic();
            var wrong = Path.Combine(root, wrongName);
            var right = Path.Combine(root, rightName);
            var directoryReads = 0;
            var disk = InterfaceProxy.Proxy<IDiskProvider>((method, _) => method.Name switch
            {
                nameof(IDiskProvider.FolderExists) => true,
                nameof(IDiskProvider.GetDirectories) => ReadDirectories(),
                _ => throw new NotSupportedException(method.Name)
            });
            var service = new AuthorFolderMatchingService(disk, LogManager.GetCurrentClassLogger());
            var author = new Author { Name = canonicalName };

            Assert.Multiple(() =>
            {
                Assert.That(service.FindAuthorFolders(root, author).Select(match => match.Path), Is.EqualTo(new[] { right }));
                Assert.That(directoryReads, Is.EqualTo(1));
                Assert.That(service.FindAuthorFolderByWalkingUp(Path.Combine(wrong, "A Book", "book.epub"), root, author), Is.Null);
                Assert.That(service.FindAuthorFolderByWalkingUp(Path.Combine(right, "A Book", "book.epub"), root, author), Is.EqualTo(right));
            });

            string[] ReadDirectories()
            {
                directoryReads++;
                return new[] { wrong, right };
            }
        }

        [Test]
        public void redundant_sort_spelling_should_not_erase_a_canonical_conflicting_part()
        {
            var root = @"C:\books".AsOsAgnostic();
            var conflicting = Path.Combine(root, "Jennifer Q. Armentrout");
            var disk = InterfaceProxy.Proxy<IDiskProvider>((method, _) => method.Name switch
            {
                nameof(IDiskProvider.FolderExists) => true,
                nameof(IDiskProvider.GetDirectories) => new[] { conflicting },
                _ => throw new NotSupportedException(method.Name)
            });
            var service = new AuthorFolderMatchingService(disk, LogManager.GetCurrentClassLogger());
            var author = new Author { Name = "Jennifer L. Armentrout", SortName = "Armentrout, Jennifer", CleanName = "jenniferarmentrout" };

            Assert.That(service.FindAuthorFolders(root, author), Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void only_explicit_pseudonym_should_supply_an_alternate_persona(bool sortName)
        {
            var root = @"C:\books".AsOsAgnostic();
            var alternate = Path.Combine(root, "Richard Bachman");
            var disk = InterfaceProxy.Proxy<IDiskProvider>((method, _) => method.Name switch
            {
                nameof(IDiskProvider.FolderExists) => true,
                nameof(IDiskProvider.GetDirectories) => new[] { alternate },
                _ => throw new NotSupportedException(method.Name)
            });
            var service = new AuthorFolderMatchingService(disk, LogManager.GetCurrentClassLogger());
            var author = new Author { Name = "Stephen King", SortName = sortName ? "Richard Bachman" : null };
            if (!sortName)
            {
                author.Pseudonyms.Add("Richard Bachman");
            }

            Assert.That(service.FindAuthorFolders(root, author).Select(match => match.Path),
                Is.EqualTo(sortName ? Array.Empty<string>() : new[] { alternate }));
        }
    }
}
