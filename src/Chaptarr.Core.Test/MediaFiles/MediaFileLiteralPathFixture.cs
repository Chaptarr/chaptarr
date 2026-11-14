using System;
using System.Collections.Generic;
using System.Diagnostics;
using Dapper;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class MediaFileLiteralPathFixture
    {
        [Test]
        public void base_path_routes_treat_folder_punctuation_literally([Values] bool postgres, [Values("_", "%", "!")] string marker)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var root = Path.Combine(Path.GetTempPath(), "Library" + marker + "one");
                var sibling = Path.Combine(Path.GetTempPath(), "LibraryXone");
                var repository = new MediaFileRepository(db, null);
                BookFile Add(string folder, string media) => repository.Insert(new BookFile
                {
                    Path = Path.Combine(folder, "Dune." + (media == "ebook" ? "epub" : "m4b")),
                    ReplicaPaths = new List<string> { Path.Combine(folder, "copy.epub") },
                    MediaType = media, Quality = new QualityModel(Quality.EPUB), MediaInfo = new MediaInfoModel(),
                    Modified = DateTime.UtcNow, DateAdded = DateTime.UtcNow
                });
                var ebook = Add(root, "ebook");
                var audio = Add(root, "audiobook");
                Add(sibling, "ebook");
                Add(root + "suffix", "ebook");

                Assert.That(repository.GetFilesWithBasePath(root).Select(file => file.Id), Is.EquivalentTo(new[] { ebook.Id, audio.Id }));
                Assert.That(repository.GetFilesWithBasePath(root, "ebook").Select(file => file.Id), Is.EqualTo(new[] { ebook.Id }));
                Assert.That(repository.GetFileStatsWithBasePath(root).Select(file => file.Id), Is.EquivalentTo(new[] { ebook.Id, audio.Id }));
                Assert.That(repository.GetFileStatsWithBasePath(root, "audiobook").Select(file => file.Id), Is.EqualTo(new[] { audio.Id }));
                Assert.That(repository.GetReplicaPathsWithBasePath(root), Is.EquivalentTo(ebook.ReplicaPaths));
            });
        }

        [Test]
        public void base_path_routes_follow_platform_case_for_the_same_inventory(
            [Values] bool postgres, [Values("Library", "Åsa")] string folder)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var root = Path.Combine(Path.GetTempPath(), folder);
                var other = Path.Combine(Path.GetTempPath(), folder.ToLowerInvariant());
                var repository = new MediaFileRepository(db, null);
                BookFile Add(string path) => repository.Insert(new BookFile
                {
                    Path = Path.Combine(path, "book.epub"), MediaType = "ebook",
                    ReplicaPaths = new List<string> { Path.Combine(path, "copies", "book.epub") },
                    Quality = new QualityModel(Quality.EPUB), MediaInfo = new MediaInfoModel(),
                    Modified = DateTime.UtcNow, DateAdded = DateTime.UtcNow
                });
                var exact = Add(root);
                var differentCase = Add(other);
                Add(root + "suffix");
                var expected = OperatingSystem.IsWindows() ? new[] { exact, differentCase } : new[] { exact };
                var ids = expected.Select(file => file.Id).ToArray();
                Assert.Multiple(() =>
                {
                    Assert.That(repository.GetFilesWithBasePath(root).Select(file => file.Id), Is.EquivalentTo(ids));
                    Assert.That(repository.GetFilesWithBasePath(root, "ebook").Select(file => file.Id), Is.EquivalentTo(ids));
                    Assert.That(repository.GetFileStatsWithBasePath(root).Select(file => file.Id), Is.EquivalentTo(ids));
                    Assert.That(repository.GetFileStatsWithBasePath(root, "ebook").Select(file => file.Id), Is.EquivalentTo(ids));
                    Assert.That(repository.GetReplicaPathsWithBasePath(root), Is.EquivalentTo(expected.SelectMany(file => file.ReplicaPaths).Distinct(NzbDrone.Common.PathEqualityComparer.Instance)));
                    Assert.That(repository.GetFilePathOwnership(Array.Empty<int>(), new[] { root }).Select(file => file.Id), Is.EquivalentTo(ids));
                });
            });
        }

        [Test]
        public void base_path_lookup_has_identical_inventory_without_loading_tag_json([Values] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var root = Path.Combine(Path.GetTempPath(), "Library");
                var repository = new MediaFileRepository(db, null);
                var files = Enumerable.Range(0, 20000).Select(i => new BookFile
                {
                    Path = Path.Combine(root, "Author" + (i % 100), "book" + i + ".m4b"),
                    MediaType = "audiobook", Size = i + 1, EditionId = 0,
                    Quality = new QualityModel(Quality.M4B), MediaInfo = new MediaInfoModel(),
                    Modified = DateTime.UtcNow, DateAdded = DateTime.UtcNow
                }).ToList();
                repository.InsertMany(files);
                var folder = Path.Combine(root, "Author1");
                List<BookFile> Previous()
                {
                    using var connection = db.OpenConnection();
                    return connection.Query<BookFile>(
                    @"SELECT ""Id"", ""Path"", ""Size"", ""Modified"", ""EditionId"", ""MediaType"",
                        CASE WHEN substr(""MatchDetails"", 1, 13) = 'APPLY_FAILED:' THEN 'APPLY_FAILED:' ELSE NULL END AS ""MatchDetails""
                      FROM ""BookFiles"" WHERE ""Path"" LIKE @prefix ESCAPE '!'",
                    new { prefix = (folder + Path.DirectorySeparatorChar).Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%" }).ToList();
                }
                var before = Previous();
                var actual = repository.GetFileStatsWithBasePath(folder);
                Assert.That(actual.Select(file => (file.Id, file.Path, file.Size, file.Modified, file.EditionId, file.MediaType, file.MatchDetails)),
                    Is.EquivalentTo(before.Select(file => (file.Id, file.Path, file.Size, file.Modified, file.EditionId, file.MediaType, file.MatchDetails))));
                Assert.That(actual, Has.Count.EqualTo(200));
                var ownershipWatch = Stopwatch.StartNew();
                var ownership = repository.GetFilePathOwnership(Array.Empty<int>(), new[] { folder });
                Assert.That(ownership, Has.Count.EqualTo(200));
                TestContext.Progress.WriteLine($"OWNERSHIP-SCOPE {(postgres ? "PostgreSQL" : "SQLite")}: stored=20000 selected={ownership.Count} elapsedMs={ownershipWatch.Elapsed.TotalMilliseconds:F3}");
                var previousMs = new List<double>();
                var currentMs = new List<double>();
                for (var i = 0; i < 7; i++)
                {
                    var watch = Stopwatch.StartNew();
                    Previous();
                    previousMs.Add(watch.Elapsed.TotalMilliseconds);
                    watch.Restart();
                    repository.GetFileStatsWithBasePath(folder);
                    currentMs.Add(watch.Elapsed.TotalMilliseconds);
                }
                previousMs.Sort();
                currentMs.Sort();
                TestContext.Progress.WriteLine($"PATH-SCOPE {(postgres ? "PostgreSQL" : "SQLite")}: stored=20000 selected=200 runs=7 oldMedianMs={previousMs[3]:F3} newMedianMs={currentMs[3]:F3} oldMaxMs={previousMs.Max():F3} newMaxMs={currentMs.Max():F3}");
            });
        }

        [Test]
        public void ownership_keeps_unicode_and_replica_paths_when_roots_overlap([Values] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, db =>
            {
                var root = Path.Combine(Path.GetTempPath(), "Åsa Larsson");
                var storedRoot = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
                var repository = new MediaFileRepository(db, null);
                var file = repository.Insert(new BookFile
                {
                    Path = Path.Combine(storedRoot, "Dune.epub"), MediaType = "ebook",
                    Quality = new QualityModel(Quality.EPUB), MediaInfo = new MediaInfoModel(),
                    Modified = DateTime.UtcNow, DateAdded = DateTime.UtcNow
                });
                var replicaOwner = repository.Insert(new BookFile
                {
                    Path = Path.Combine(Path.GetTempPath(), "elsewhere", "book.epub"), MediaType = "ebook",
                    ReplicaPaths = new List<string> { Path.Combine(storedRoot, "nested", "copy.epub") },
                    Quality = new QualityModel(Quality.EPUB), MediaInfo = new MediaInfoModel(),
                    Modified = DateTime.UtcNow, DateAdded = DateTime.UtcNow
                });
                Assert.That(repository.GetFilePathOwnership(Array.Empty<int>(), new[] { root, Path.Combine(root, "nested") })
                    .Select(item => item.Id), Is.EquivalentTo(new[] { file.Id, replicaOwner.Id }));
            });
        }
    }
}
