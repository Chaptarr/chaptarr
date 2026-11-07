using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles.BookImport;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class ImportApprovedBooksCalibreSourceCleanupFixture
    {
        private class ThrowingDiskProxy : DispatchProxy
        {
            public List<string> DeleteAttempts { get; } = new List<string>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDiskProvider.DeleteFile))
                {
                    DeleteAttempts.Add((string)args[0]);
                    throw new UnauthorizedAccessException("read-only");
                }

                throw new NotImplementedException($"IDiskProvider.{targetMethod?.Name}");
            }
        }

        [Test]
        public void should_not_fail_import_when_source_file_cannot_be_deleted_after_calibre_add()
        {
            var disk = DispatchProxy.Create<IDiskProvider, ThrowingDiskProxy>();

            var subject = new ImportApprovedBooks(
                null, null, null, null, null, null, null, null, null, null, null,
                null, null, null, null, null, null,
                LogManager.GetLogger("ImportApprovedBooksCalibreSourceCleanupFixture"),
                diskProvider: disk);

            Assert.DoesNotThrow(() => subject.TryDeleteCalibreSource("/downloads/book.epub"));
            Assert.That(((ThrowingDiskProxy)(object)disk).DeleteAttempts, Is.EqualTo(new[] { "/downloads/book.epub" }));
        }
    }
}
