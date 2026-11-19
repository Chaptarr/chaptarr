using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    [NonParallelizable]
    public class MediaFileRepositoryBatchPathFixture
    {
        [TestCase(false)]
        [TestCase(true)]
        public void bulk_path_lookup_should_bound_large_batches_and_materialize_only_persisted_part_data(bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var repository = new MediaFileRepository(database, Proxy<IEventAggregator>((m, a) => null));
                var files = Enumerable.Range(1, 1101).Select(part => new BookFile
                {
                    Path = $"/library/Author/Book/part-{part:D4}.mp3", EditionId = 0,
                    Part = part, PartCount = 1101, DateAdded = DateTime.UtcNow, Modified = DateTime.UtcNow,
                    Quality = new QualityModel(Quality.MP3), MediaInfo = new MediaInfoModel()
                }).ToList();
                repository.InsertMany(files);
                var selectedPaths = files.Take(1100).Select(file => file.Path).Reverse().ToList();
                selectedPaths.Add(selectedPaths[0]);
                selectedPaths.Add(null);
                selectedPaths.Add(string.Empty);
                var timer = Stopwatch.StartNew();
                var actual = repository.GetFileWithPath(selectedPaths);
                timer.Stop();
                Assert.That(actual, Has.Count.EqualTo(1100));
                Assert.That(actual.Select(file => file.Path), Is.EquivalentTo(files.Take(1100).Select(file => file.Path)));
                Assert.That(actual.All(file => file.Part > 0 && file.PartCount == 0), Is.True,
                    "Part persists, while calculated PartCount must not trigger freshness writes");
                TestContext.Progress.WriteLine($"{(postgres ? "PostgreSQL" : "SQLite")} bounded path lookup: 1,100 results from 1,101 stored rows, {timer.ElapsedMilliseconds} ms");
            });
        }
    }
}
