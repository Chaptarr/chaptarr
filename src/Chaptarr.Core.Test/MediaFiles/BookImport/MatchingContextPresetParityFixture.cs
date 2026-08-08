using System;
using System.IO;
using NUnit.Framework;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class MatchingContextPresetParityFixture
    {
        [Test]
        public void matchbench_libraryaudit_should_use_shared_matching_context_presets()
        {
            var source = ReadSource("src/NzbDrone.MatchBench/Program.cs");

            Assert.That(source, Does.Contain("\"scan-v5\" => MatchingContextPresets.ForScanV5(pathFallback)"));
            Assert.That(source, Does.Contain("\"scan-scoped-rematch\" => MatchingContextPresets.ForScanScopedRematch()"));
            Assert.That(source, Does.Contain("\"downloaded\" => MatchingContextPresets.ForDownloaded(false, targetBookIds, pathFallback)"));
            Assert.That(source, Does.Contain("\"manual-download\" => MatchingContextPresets.ForDownloaded(true, targetBookIds, pathFallback)"));
            Assert.That(source, Does.Contain("\"author-ready\" => MatchingContextPresets.ForAuthorReady()"));
            Assert.That(source, Does.Contain("\"direct-default\" => MatchingContextPresets.ForDirectDefault(pathFallback)"));
            Assert.That(source, Does.Contain("_ => MatchingContextPresets.ForScanLocal(pathFallback)"));
        }

        [Test]
        public void production_matching_flows_should_use_shared_matching_context_presets()
        {
            var orchestrator = ReadSource("src/NzbDrone.Core/MediaFiles/BookImport/ImportOrchestratorV2.cs");
            Assert.That(orchestrator, Does.Contain("MatchingContextPresets.ForScanLocal()"));
            Assert.That(orchestrator, Does.Contain("MatchingContextPresets.ForScanV5()"));
            Assert.That(orchestrator, Does.Contain("MatchingContextPresets.ForScanScopedRematch()"));

            var authorReady = ReadSource("src/NzbDrone.Core/MediaFiles/BookImport/Services/IngestQueueOnAuthorReadyHandler.cs");
            Assert.That(authorReady, Does.Contain("return MatchingContextPresets.ForAuthorReady();"));

            var downloaded = ReadSource("src/NzbDrone.Core/MediaFiles/DownloadedBooksImportService.cs");
            Assert.That(downloaded, Does.Contain("var matchCtx = CreateStrictMatchingContext("));
            Assert.That(downloaded, Does.Contain("return MatchingContextPresets.ForDownloaded(allowV5Identification, targetBookIds, allowPathFallback);"));

            var manual = ReadSource("src/NzbDrone.Core/MediaFiles/BookImport/SimpleImportDecisionMaker.cs");
            Assert.That(manual, Does.Contain("MatchingContextPresets.ForManualPreview()"));
        }

        private static string ReadSource(string relativePath)
        {
            var root = FindRepositoryRoot();
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            return File.ReadAllText(path);
        }

        private static string FindRepositoryRoot()
        {
            var current = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "src", "NzbDrone.Core", "MediaFiles", "BookImport", "MatchingContextPresets.cs")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException("Could not locate repository root for matching context parity test.");
        }
    }
}
