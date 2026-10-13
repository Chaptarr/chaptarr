using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class ImportApprovedBooksCalibreDestinationFixture
    {
        private static readonly MethodInfo GetSettingsMethod = typeof(ImportApprovedBooks).GetMethod(
            "GetCalibreDestinationSettings",
            BindingFlags.NonPublic | BindingFlags.Instance);

        private class StubProxy<T> : DispatchProxy
            where T : class
        {
            public Dictionary<string, Func<object[], object>> Handlers { get; } = new();

            public static T Create(Dictionary<string, Func<object[], object>> handlers = null)
            {
                var proxy = DispatchProxy.Create<T, StubProxy<T>>();
                var state = (StubProxy<T>)(object)proxy;
                foreach (var handler in handlers ?? new Dictionary<string, Func<object[], object>>())
                {
                    state.Handlers[handler.Key] = handler.Value;
                }

                return proxy;
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (Handlers.TryGetValue(targetMethod.Name, out var handler))
                {
                    return handler(args);
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod.Name}");
            }
        }

        private const string CalibreRoot = "/data/calibre/library";
        private const string PlainRoot = "/data/books";

        [Test]
        public void should_route_ebook_into_calibre_library_root_through_calibre()
        {
            var settings = new CalibreSettings { Host = "calibre", Port = 8181 };

            var result = GetSettings(BuildFile("ebook"), CalibreRoot + "/Author/Book/Book.epub",
                new RootFolder { Path = CalibreRoot, IsCalibreLibrary = true, CalibreSettings = settings });

            Assert.That(result, Is.SameAs(settings));
        }

        [Test]
        public void should_use_normal_transfer_for_plain_root_folder()
        {
            var result = GetSettings(BuildFile("ebook"), PlainRoot + "/Author/Book/Book.epub",
                new RootFolder { Path = PlainRoot, IsCalibreLibrary = false });

            Assert.That(result, Is.Null);
        }

        [Test]
        public void should_use_normal_transfer_when_calibre_root_has_no_settings()
        {
            var result = GetSettings(BuildFile("ebook"), CalibreRoot + "/Author/Book/Book.epub",
                new RootFolder { Path = CalibreRoot, IsCalibreLibrary = true, CalibreSettings = null });

            Assert.That(result, Is.Null);
        }

        [Test]
        public void should_never_send_audiobooks_to_calibre()
        {
            var result = GetSettings(BuildFile("audiobook"), CalibreRoot + "/Author/Book/Book.m4b",
                new RootFolder { Path = CalibreRoot, IsCalibreLibrary = true, CalibreSettings = new CalibreSettings() });

            Assert.That(result, Is.Null);
        }

        private static BookFile BuildFile(string mediaType)
        {
            return new BookFile { Path = "/downloads/Book.epub", MediaType = mediaType };
        }

        private static CalibreSettings GetSettings(BookFile bookFile, string destination, RootFolder rootFolder)
        {
            var mover = StubProxy<IMoveBookFiles>.Create(new Dictionary<string, Func<object[], object>>
            {
                [nameof(IMoveBookFiles.GetImportDestinationPath)] = _ => destination
            });
            var rootFolders = StubProxy<IRootFolderService>.Create(new Dictionary<string, Func<object[], object>>
            {
                [nameof(IRootFolderService.GetBestRootFolder)] = args =>
                {
                    Assert.That(args[0], Is.EqualTo(destination));
                    return rootFolder;
                }
            });

            var subject = new ImportApprovedBooks(
                null, null, null, null, null, null, null, null, mover, null, null,
                null, null, null, null, null, null,
                LogManager.GetLogger(nameof(ImportApprovedBooksCalibreDestinationFixture)),
                rootFolderService: rootFolders,
                calibre: StubProxy<ICalibreProxy>.Create());

            return (CalibreSettings)GetSettingsMethod.Invoke(subject, new object[] { bookFile, new LocalBook { Path = bookFile.Path } });
        }
    }
}
