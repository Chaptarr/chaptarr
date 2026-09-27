using System.Collections.Generic;
using DryIoc;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorServiceDefaultRootFoldersFixture
    {
        private static readonly List<RootFolder> AudioAndEbook = new List<RootFolder>
        {
            new RootFolder { Id = 1, Path = "/audiobooks", FolderType = FolderType.Audiobook },
            new RootFolder { Id = 2, Path = "/ebooks", FolderType = FolderType.Ebook }
        };

        private static AuthorService BuildService(IConfigService configService = null)
        {
            return new AuthorService(
                authorRepository: null,
                eventAggregator: null,
                authorPathBuilder: null,
                rootFolderService: null,
                commandQueueManager: null,
                cacheManager: new CacheManager(),
                bookRepository: null,
                mediaFileService: null,
                logger: LogManager.GetCurrentClassLogger(),
                configService: configService);
        }

        [Test]
        public void should_fill_missing_ebook_root_for_audiobook_only_author()
        {
            var author = new Author { Name = "A", AudiobookRootFolderPath = "/audiobooks" };

            BuildService().ApplyDefaultRootFoldersOnAdd(author, AudioAndEbook);

            Assert.That(author.AudiobookRootFolderPath, Is.EqualTo("/audiobooks"));
            Assert.That(author.EbookRootFolderPath, Is.EqualTo("/ebooks"));
        }

        [Test]
        public void should_fill_missing_audiobook_root_for_ebook_only_author()
        {
            var author = new Author { Name = "A", EbookRootFolderPath = "/ebooks" };

            BuildService().ApplyDefaultRootFoldersOnAdd(author, AudioAndEbook);

            Assert.That(author.AudiobookRootFolderPath, Is.EqualTo("/audiobooks"));
            Assert.That(author.EbookRootFolderPath, Is.EqualTo("/ebooks"));
        }

        [Test]
        public void should_not_overwrite_explicit_roots()
        {
            var author = new Author { Name = "A", AudiobookRootFolderPath = "/custom-audio", EbookRootFolderPath = "/custom-ebook" };

            BuildService().ApplyDefaultRootFoldersOnAdd(author, AudioAndEbook);

            Assert.That(author.AudiobookRootFolderPath, Is.EqualTo("/custom-audio"));
            Assert.That(author.EbookRootFolderPath, Is.EqualTo("/custom-ebook"));
        }

        [Test]
        public void should_leave_blank_when_default_is_ambiguous()
        {
            var rootFolders = new List<RootFolder>
            {
                new RootFolder { Id = 1, Path = "/audio-a", FolderType = FolderType.Audiobook },
                new RootFolder { Id = 2, Path = "/audio-b", FolderType = FolderType.Audiobook },
                new RootFolder { Id = 3, Path = "/ebooks", FolderType = FolderType.Ebook }
            };
            var author = new Author { Name = "A", EbookRootFolderPath = "/ebooks" };

            BuildService().ApplyDefaultRootFoldersOnAdd(author, rootFolders);

            Assert.That(author.AudiobookRootFolderPath, Is.Null.Or.Empty);
        }

        [Test]
        public void should_use_configured_default_when_several_roots_exist()
        {
            var rootFolders = new List<RootFolder>
            {
                new RootFolder { Id = 1, Path = "/audio-a", FolderType = FolderType.Audiobook },
                new RootFolder { Id = 2, Path = "/audio-b", FolderType = FolderType.Audiobook },
                new RootFolder { Id = 3, Path = "/ebooks", FolderType = FolderType.Ebook }
            };
            var config = ConfigServiceTestProxy.Create();
            ((ConfigServiceTestProxy)config).DefaultAudiobookRootFolderPath = "/audio-b";
            var author = new Author { Name = "A", EbookRootFolderPath = "/ebooks" };

            BuildService(config).ApplyDefaultRootFoldersOnAdd(author, rootFolders);

            Assert.That(author.AudiobookRootFolderPath, Is.EqualTo("/audio-b"));
        }

        [Test]
        public void should_do_nothing_without_root_folders()
        {
            var author = new Author { Name = "A", EbookRootFolderPath = "/ebooks" };

            BuildService().ApplyDefaultRootFoldersOnAdd(author, new List<RootFolder>());

            Assert.That(author.AudiobookRootFolderPath, Is.Null.Or.Empty);
        }

        [Test]
        public void the_container_should_inject_the_config_service_into_the_optional_constructor_parameter()
        {
            // configService is an optional constructor parameter (= null). Guard against the container silently
            // using the default, which would make the configured default root folders never apply in production.
            var config = DispatchProxy.Create<IConfigService, ConfigProxy>();

            var container = new Container(rules => rules.WithNzbDroneRules());
            container.RegisterInstance(Stub<IAuthorRepository>());
            container.RegisterInstance(Stub<IEventAggregator>());
            container.RegisterInstance(Stub<IBuildAuthorPaths>());
            container.RegisterInstance(Stub<IRootFolderService>());
            container.RegisterInstance(Stub<IManageCommandQueue>());
            container.RegisterInstance<ICacheManager>(new CacheManager());
            container.RegisterInstance(Stub<IBookRepository>());
            container.RegisterInstance(Stub<IMediaFileService>());
            container.RegisterInstance(LogManager.GetCurrentClassLogger());
            container.RegisterInstance(config);
            container.Register<AuthorService>(Reuse.Singleton);

            var service = container.Resolve<AuthorService>();

            var author = new Author { Name = "A" };
            service.ApplyDefaultRootFoldersOnAdd(author, new List<RootFolder>
            {
                new RootFolder { Id = 1, Path = "/audiobooks-configured", FolderType = FolderType.Audiobook },
                new RootFolder { Id = 2, Path = "/audiobooks-other", FolderType = FolderType.Audiobook }
            });

            Assert.That(author.AudiobookRootFolderPath, Is.EqualTo("/audiobooks-configured"), "the configured default only applies if the container injected IConfigService");
        }

        private static T Stub<T>() where T : class => DispatchProxy.Create<T, NullProxy>();

        public class NullProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod.ReturnType != typeof(void) && targetMethod.ReturnType.IsValueType
                    ? System.Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
            }
        }

        public class ConfigProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == "get_" + nameof(IConfigService.DefaultAudiobookRootFolderPath))
                {
                    return "/audiobooks-configured";
                }

                return targetMethod.ReturnType != typeof(void) && targetMethod.ReturnType.IsValueType
                    ? System.Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
            }
        }
    }
}
