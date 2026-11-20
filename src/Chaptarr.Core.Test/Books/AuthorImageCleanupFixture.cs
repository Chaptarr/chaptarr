using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Chaptarr.Core.Test.Common;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorImageCleanupFixture
    {
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void image_cleanup_preserves_user_settings_and_newer_images(bool postgres, bool concurrentImages)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                using var setup = database.OpenConnection();
                // Isolate the operation's columns; schema does not affect its conditional update.
                setup.Execute("CREATE TABLE \"ImageCleanupAuthors\" (\"Id\" INTEGER PRIMARY KEY, \"Images\" TEXT, \"Monitored\" INTEGER)");
                var before = STJson.ToJson(new List<NzbDrone.Core.MediaCover.MediaCover>
                {
                    new(MediaCoverTypes.Poster, "https://images.example/placeholder.jpg"),
                    new(MediaCoverTypes.Poster, "https://images.example/kept.jpg")
                });
                var newer = STJson.ToJson(new List<NzbDrone.Core.MediaCover.MediaCover>
                {
                    new(MediaCoverTypes.Poster, "https://images.example/newer.jpg")
                });
                setup.Execute("INSERT INTO \"ImageCleanupAuthors\" VALUES (1, @before, 0)", new { before });
                var reads = 0;
                var writes = 0;
                var observed = InterfaceProxy.Proxy<IMainDatabase>((method, args) =>
                {
                    if (method.Name != nameof(IDatabase.OpenConnection)) return method.Invoke(database, args);
                    var connection = database.OpenConnection();
                    return InterfaceProxy.Proxy<IDbConnection>((member, values) =>
                    {
                        var result = member.Invoke(connection, values);
                        if (result is not IDbCommand command) return result;
                        return InterfaceProxy.Proxy<IDbCommand>((operation, parameters) =>
                        {
                            if (operation.Name.StartsWith("Execute", StringComparison.Ordinal))
                            {
                                command.CommandText = command.CommandText.Replace("\"Authors\"", "\"ImageCleanupAuthors\"", StringComparison.Ordinal);
                                if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)) reads++;
                                else
                                {
                                    writes++;
                                    if (concurrentImages) setup.Execute("UPDATE \"ImageCleanupAuthors\" SET \"Images\"=@newer WHERE \"Id\"=1", new { newer });
                                }
                            }
                            return operation.Invoke(command, parameters);
                        });
                    });
                });
                var repository = new AuthorRepository(observed, InterfaceProxy.Proxy<IEventAggregator>((_, _) => null), LogManager.GetCurrentClassLogger());
                Assert.That(repository.RemoveImageUrls(1, new[] { "https://images.example/placeholder.jpg" }), Is.EqualTo(!concurrentImages));
                Assert.That(setup.QuerySingle<int>("SELECT \"Monitored\" FROM \"ImageCleanupAuthors\""), Is.Zero);
                var images = STJson.Deserialize<List<NzbDrone.Core.MediaCover.MediaCover>>(setup.QuerySingle<string>("SELECT \"Images\" FROM \"ImageCleanupAuthors\""));
                Assert.That(images.Select(image => image.Url), Is.EqualTo(new[] { concurrentImages ? "https://images.example/newer.jpg" : "https://images.example/kept.jpg" }));
                Assert.That(reads, Is.EqualTo(1));
                Assert.That(writes, Is.EqualTo(1));
                Assert.That(repository.RemoveImageUrls(1, new[] { "https://images.example/placeholder.jpg" }), Is.False);
                Assert.That(writes, Is.EqualTo(1), "Repeated cleanup never rewrites the row");
                Assert.That(repository.RemoveImageUrls(999, new[] { "https://images.example/placeholder.jpg" }), Is.False);
            });
        }
    }
}
