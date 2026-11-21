using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Housekeeping.Housekeepers;

namespace Chaptarr.Core.Test.Housekeeping
{
    [TestFixture]
    public class DeleteBadMediaCoversFixture
    {
        private class AuthorServiceProxy : DispatchProxy
        {
            public Author Author { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod.Name == nameof(IAuthorService.AllAuthorPaths)
                    ? new List<KeyValuePair<int, string>>
                    {
                        new(Author.Id, Author.AudiobookPath),
                        new(Author.Id, Author.EbookPath)
                    }
                    : throw new NotImplementedException(targetMethod.Name);
            }
        }

        private class MetadataServiceProxy : DispatchProxy
        {
            public List<MetadataFile> Files { get; set; }
            public List<int> DeletedIds { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(IMetadataFileService.GetFilesByAuthor))
                {
                    return Files;
                }

                if (targetMethod.Name == nameof(IMetadataFileService.Delete))
                {
                    DeletedIds.Add((int)args[0]);
                    return null;
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            public List<string> DeletedPaths { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                var path = args?.Length > 0 ? args[0] as string : null;
                return targetMethod.Name switch
                {
                    nameof(IDiskProvider.FileExistsCanonical) => path != null && File.Exists(path),
                    nameof(IDiskProvider.OpenReadStream) => File.OpenRead(path),
                    nameof(IDiskProvider.DeleteFile) => Delete(path),
                    _ => throw new NotImplementedException(targetMethod.Name)
                };
            }

            private object Delete(string path)
            {
                DeletedPaths.Add(path);
                return null;
            }
        }

        private class ConfigServiceProxy : DispatchProxy
        {
            public bool CleanupMetadataImages { get; private set; } = true;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == "get_CleanupMetadataImages")
                {
                    return CleanupMetadataImages;
                }

                if (targetMethod.Name == "set_CleanupMetadataImages")
                {
                    CleanupMetadataImages = (bool)args[0];
                    return null;
                }

                throw new NotImplementedException(targetMethod.Name);
            }
        }

        [Test]
        public void should_preserve_ambiguous_frank_herbert_dune_cover()
        {
            var result = Run("<html>bad cover</html>", "<html>bad cover</html>");

            Assert.That(result.DeletedIds, Is.Empty);
            Assert.That(result.DeletedPaths, Is.Empty);
            Assert.That(result.Logs, Has.Some.Contains("Skipping ambiguous metadata image"));
            Assert.That(result.Logs, Has.Some.Contains("2 base paths"));
        }

        [Test]
        public void should_preserve_absent_frank_herbert_dune_cover()
        {
            var result = Run(null, null);

            Assert.That(result.DeletedIds, Is.Empty);
            Assert.That(result.DeletedPaths, Is.Empty);
        }

        [Test]
        public void should_delete_unique_bad_ebook_frank_herbert_dune_cover()
        {
            var result = Run(null, "<html>bad cover</html>");

            Assert.That(result.DeletedIds, Is.EqualTo(new[] { 41 }));
            Assert.That(result.DeletedPaths, Has.Count.EqualTo(1));
            Assert.That(result.DeletedPaths[0], Does.EndWith(Path.Combine("ebook", "Frank Herbert", "Dune", "cover.jpg")));
        }

        [Test]
        public void should_keep_unique_valid_ebook_frank_herbert_dune_cover()
        {
            var result = Run(null, "valid image bytes");

            Assert.That(result.DeletedIds, Is.Empty);
            Assert.That(result.DeletedPaths, Is.Empty);
        }

        private static (List<int> DeletedIds, List<string> DeletedPaths, List<string> Logs) Run(string audioContents, string ebookContents)
        {
            var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"bad_cover_{Guid.NewGuid():N}").AsOsAgnostic();
            var audioPath = Path.Combine(root, "audio", "Frank Herbert");
            var ebookPath = Path.Combine(root, "ebook", "Frank Herbert");
            var coverRelativePath = Path.Combine("Dune", "cover.jpg");
            try
            {
                WriteCover(audioPath, coverRelativePath, audioContents);
                WriteCover(ebookPath, coverRelativePath, ebookContents);

                var author = new Author
                {
                    Id = 7,
                    Name = "Frank Herbert",
                    AudiobookPath = audioPath,
                    EbookPath = ebookPath
                };
                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                ((AuthorServiceProxy)(object)authorService).Author = author;
                var metadataService = DispatchProxy.Create<IMetadataFileService, MetadataServiceProxy>();
                ((MetadataServiceProxy)(object)metadataService).Files = new List<MetadataFile>
                {
                    new() { Id = 41, AuthorId = author.Id, RelativePath = coverRelativePath, LastUpdated = DateTime.UtcNow }
                };
                var diskProvider = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
                var configService = DispatchProxy.Create<IConfigService, ConfigServiceProxy>();

                var target = new MemoryTarget { Layout = "${message}" };
                using var factory = new LogFactory();
                var logging = new LoggingConfiguration(factory);
                logging.AddRule(LogLevel.Debug, LogLevel.Fatal, target);
                factory.Configuration = logging;
                var subject = new DeleteBadMediaCovers(
                    metadataService,
                    authorService,
                    diskProvider,
                    configService,
                    factory.GetLogger(nameof(DeleteBadMediaCoversFixture)));

                subject.Clean();

                return (((MetadataServiceProxy)(object)metadataService).DeletedIds,
                    ((DiskProviderProxy)(object)diskProvider).DeletedPaths, target.Logs.ToList());
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        private static void WriteCover(string authorPath, string relativePath, string contents)
        {
            if (contents == null)
            {
                return;
            }

            var path = Path.Combine(authorPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, contents);
        }
    }
}
