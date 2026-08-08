using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using Newtonsoft.Json;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class CleanupUnusedTags : IHousekeepingTask
    {
        private readonly IMainDatabase _database;

        public CleanupUnusedTags(IMainDatabase database)
        {
            _database = database;
        }

        public void Clean()
        {
            using var mapper = _database.OpenConnection();
            var usedTags = new HashSet<int>();

            usedTags.UnionWith(GetUsedTagsFromArrayColumn("Authors", "Tags", mapper));
            usedTags.UnionWith(GetUsedTagsFromArrayColumn("Authors", "AudiobookTags", mapper));
            usedTags.UnionWith(GetUsedTagsFromArrayColumn("Authors", "EbookTags", mapper));
            usedTags.UnionWith(GetUsedTagsFromArrayColumn("Notifications", "Tags", mapper));
            usedTags.UnionWith(GetUsedTagsFromArrayColumn("DelayProfiles", "Tags", mapper));
            usedTags.UnionWith(GetUsedTagsFromArrayColumn("ReleaseProfiles", "Tags", mapper));
            usedTags.UnionWith(GetUsedTagsFromArrayColumn("ImportLists", "Tags", mapper));
            usedTags.UnionWith(GetUsedTagsFromArrayColumn("Indexers", "Tags", mapper));
            usedTags.UnionWith(GetUsedTagsFromArrayColumn("DownloadClients", "Tags", mapper));
            usedTags.UnionWith(GetUsedTagsFromArrayColumn("DownloadClients", "AudiobookTags", mapper));
            usedTags.UnionWith(GetUsedTagsFromArrayColumn("DownloadClients", "EbookTags", mapper));
            usedTags.UnionWith(GetUsedTagsFromArrayColumn("Restrictions", "Tags", mapper));
            usedTags.UnionWith(GetUsedTagsFromArrayColumn("Narrators", "Tags", mapper));
            usedTags.UnionWith(GetUsedTagsFromArrayColumn("PendingAuthorImport", "Tags", mapper));
            usedTags.UnionWith(GetUsedTagsFromArrayColumn("RootFolders", "DefaultTags", mapper));
            usedTags.UnionWith(GetUsedTagsFromRootFolderSettings(mapper));

            if (usedTags.Any())
            {
                var allTagIds = mapper.Query<int>("SELECT \"Id\" FROM \"Tags\"").ToArray();
                var unusedTagIds = allTagIds.Except(usedTags).ToArray();

                if (!unusedTagIds.Any())
                {
                    return;
                }

                if (_database.DatabaseType == DatabaseType.PostgreSQL)
                {
                    mapper.Execute("DELETE FROM \"Tags\" WHERE \"Id\" = ANY(@Ids)", new { Ids = unusedTagIds });
                }
                else if (_database.DatabaseType == DatabaseType.SQLite && unusedTagIds.Length > SqliteVariableLimit.MaxParameters)
                {
                    foreach (var batch in unusedTagIds.Chunk(SqliteVariableLimit.MaxParameters))
                    {
                        mapper.Execute("DELETE FROM \"Tags\" WHERE \"Id\" IN @Ids", new { Ids = batch.ToArray() });
                    }
                }
                else
                {
                    mapper.Execute("DELETE FROM \"Tags\" WHERE \"Id\" IN @Ids", new { Ids = unusedTagIds });
                }
            }
            else
            {
                mapper.Execute("DELETE FROM \"Tags\"");
            }
        }

        private int[] GetUsedTagsFromArrayColumn(string table, string column, IDbConnection mapper)
        {
            return mapper.Query<List<int>>($"""
                    SELECT DISTINCT "{column}"
                    FROM "{table}"
                    WHERE "{column}" IS NOT NULL AND "{column}" != '' AND "{column}" != '[]'
                """)
                .SelectMany(x => x)
                .Distinct()
                .ToArray();
        }

        private static IEnumerable<int> GetUsedTagsFromRootFolderSettings(IDbConnection mapper)
        {
            foreach (var json in mapper.Query<string>("SELECT \"AudiobookSettings\" FROM \"RootFolders\" WHERE \"AudiobookSettings\" IS NOT NULL AND \"AudiobookSettings\" != ''"))
            {
                foreach (var tagId in ExtractTagsFromMediaTypeSettingsJson(json))
                {
                    yield return tagId;
                }
            }

            foreach (var json in mapper.Query<string>("SELECT \"EbookSettings\" FROM \"RootFolders\" WHERE \"EbookSettings\" IS NOT NULL AND \"EbookSettings\" != ''"))
            {
                foreach (var tagId in ExtractTagsFromMediaTypeSettingsJson(json))
                {
                    yield return tagId;
                }
            }
        }

        private static IEnumerable<int> ExtractTagsFromMediaTypeSettingsJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                yield break;
            }

            MediaTypeSettings settings;
            try
            {
                settings = JsonConvert.DeserializeObject<MediaTypeSettings>(json);
            }
            catch
            {
                yield break;
            }

            if (settings?.Tags == null || settings.Tags.Count == 0)
            {
                yield break;
            }

            foreach (var tagId in settings.Tags)
            {
                yield return tagId;
            }
        }
    }
}
