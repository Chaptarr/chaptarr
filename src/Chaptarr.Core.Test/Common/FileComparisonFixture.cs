using System;
using System.IO;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;

namespace Chaptarr.Core.Test.Common
{
    [TestFixture]
    public class FileComparisonFixture
    {
        [TestCase("identical", true)]
        [TestCase("first-byte", false)]
        [TestCase("last-byte", false)]
        [TestCase("different-size", false)]
        [TestCase("missing-source", false)]
        [TestCase("missing-target", false)]
        [TestCase("empty", true)]
        public void streamed_comparison_checks_contents_and_short_circuits(string scenario, bool equal)
        {
            var root = Path.Combine(Path.GetTempPath(), "compare-files-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var source = Path.Combine(root, "source");
                var target = Path.Combine(root, "target");
                var bytes = new byte[scenario == "empty" ? 0 : 2 * 65536 + 17];
                File.WriteAllBytes(source, bytes);
                if (scenario == "first-byte") bytes[0] = 1;
                if (scenario == "last-byte") bytes[^1] = 1;
                if (scenario == "different-size") Array.Resize(ref bytes, bytes.Length + 1);
                File.WriteAllBytes(target, bytes);
                if (scenario == "missing-source") File.Delete(source);
                if (scenario == "missing-target") File.Delete(target);
                var disk = RealDiskProvider.Create(LogManager.GetCurrentClassLogger());
                var opened = 0;
                var read = 0;
                var disposed = 0;
                var proxy = InterfaceProxy.Proxy<IDiskProvider>((method, args) =>
                {
                    if (method.Name == nameof(IDiskProvider.OpenReadStream))
                    {
                        opened++;
                        return new ObservedStream((string)args[0], count => read += count, () => disposed++);
                    }
                    return method.Invoke(disk, args);
                });

                Assert.That(proxy.FilesEqual(source, target), Is.EqualTo(equal));
                Assert.That(disposed, Is.EqualTo(opened));
                if (scenario.StartsWith("missing-") || scenario == "different-size") Assert.That(opened, Is.Zero);
                if (scenario == "first-byte") Assert.That(read, Is.EqualTo(2 * 65536));
                if (scenario == "identical") Assert.That(read, Is.EqualTo(2 * bytes.Length));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private sealed class ObservedStream : FileStream
        {
            private readonly Action<int> _read;
            private readonly Action _disposed;

            public ObservedStream(string path, Action<int> read, Action disposed)
                : base(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            {
                _read = read;
                _disposed = disposed;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var result = base.Read(buffer, offset, count);
                _read(result);
                return result;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _disposed();
                base.Dispose(disposing);
            }
        }
    }
}
