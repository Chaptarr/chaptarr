using System;
using System.IO;
using Chaptarr.Http.Frontend.Mappers;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using System.IO.Abstractions;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class HtmlMapperUrlBaseFixture
    {
        private string _workingDirectory;
        private string _htmlPath;

        [SetUp]
        public void SetUp()
        {
            _workingDirectory = Path.Combine(Path.GetTempPath(), $"chaptarr-html-mapper-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_workingDirectory);
            _htmlPath = Path.Combine(_workingDirectory, "index.html");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_workingDirectory))
            {
                Directory.Delete(_workingDirectory, true);
            }
        }

        [Test]
        public void should_prefix_minified_unquoted_assets_and_preserve_file_extensions()
        {
            File.WriteAllText(_htmlPath, "<script src=/runtime.js data-no-hash></script>" +
                "<link href=/Content/styles.css rel=stylesheet>" +
                "<link rel=manifest href=/Content/Images/Icons/manifest.json>" +
                "<link rel=icon href=/Content/Images/Icons/apple-touch-icon.png>");

            var result = CreateMapper().Render();

            Assert.That(result, Does.Contain("src=\"/books/runtime.js\""));
            Assert.That(result, Does.Contain("href=\"/books/Content/styles.css?h=fixture\""));
            Assert.That(result, Does.Contain("href=\"/books/Content/Images/Icons/manifest.json?h=fixture\""));
            Assert.That(result, Does.Contain("href=\"/books/Content/Images/Icons/apple-touch-icon.png?h=fixture\""));
        }

        [Test]
        public void should_prefix_quoted_assets_and_keep_existing_query_strings()
        {
            File.WriteAllText(_htmlPath, "<link href=\"/theme.css?cb=existing\">" +
                "<script src='/bundle.js?version=1'></script>");

            var result = CreateMapper().Render();

            Assert.That(result, Does.Contain("href=\"/books/theme.css?cb=existing\""));
            Assert.That(result, Does.Contain("src=\"/books/bundle.js?version=1\""));
        }

        private TestHtmlMapper CreateMapper()
        {
            return new TestHtmlMapper(_htmlPath, "/books", new TestDiskProvider(), new TestCacheBreakerProvider());
        }

        private sealed class TestHtmlMapper : HtmlMapperBase
        {
            public TestHtmlMapper(string htmlPath, string urlBase, IDiskProvider diskProvider, ICacheBreakerProvider cacheBreakerProvider)
                : base(diskProvider, new Lazy<ICacheBreakerProvider>(() => cacheBreakerProvider), LogManager.GetLogger("HtmlMapperUrlBaseFixture"))
            {
                HtmlPath = htmlPath;
                UrlBase = urlBase;
            }

            public string Render() => GetHtmlText();

            public override string Map(string resourceUrl) => HtmlPath;

            public override bool CanHandle(string resourceUrl) => true;

            protected override string GetAllowedRoot(string resourceUrl) => Path.GetDirectoryName(HtmlPath);
        }

        private sealed class TestCacheBreakerProvider : ICacheBreakerProvider
        {
            public string AddCacheBreakerToPath(string resourceUrl) => resourceUrl + "?h=fixture";
        }

        private sealed class TestDiskProvider : DiskProviderBase
        {
            public TestDiskProvider()
                : base(new FileSystem())
            {
            }

            public override long? GetAvailableSpace(string path) => throw new NotImplementedException();
            public override void InheritFolderPermissions(string filename) => throw new NotImplementedException();
            public override void SetEveryonePermissions(string filename) => throw new NotImplementedException();
            public override void SetFilePermissions(string path, string mask, string group) => throw new NotImplementedException();
            public override void SetPermissions(string path, string mask, string group) => throw new NotImplementedException();
            public override void CopyPermissions(string sourcePath, string targetPath) => throw new NotImplementedException();
            public override long? GetTotalSize(string path) => throw new NotImplementedException();
            public override bool TryCreateHardLink(string source, string destination) => throw new NotImplementedException();
        }
    }
}
