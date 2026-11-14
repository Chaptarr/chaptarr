using System.Data;
using System.Diagnostics;
using System.Linq;
using Dapper;
using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(126)]
    public class provider_identity_projection : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            foreach (var entity in new[] { "Author", "Book", "Edition" })
            {
                var table = entity + "s";
                Alter.Table(table).AddColumn("ProviderIds").AsString(int.MaxValue).Nullable();

                IfPostgres().Execute.WithConnection((connection, transaction) => Backfill(connection, transaction, entity, table, true));
                IfDatabase("SQLite").Execute.WithConnection((connection, transaction) => Backfill(connection, transaction, entity, table, false));

                CreateSqliteTriggers(entity, table);
            }

            CreatePostgresTriggers();
            foreach (var item in new[] { ("ExtraFiles", "BookId"), ("ExtraFiles", "BookFileId"), ("MetadataFiles", "BookId"),
                ("OtherFiles", "EditionId"), ("ABSLookupHistory", "BookId") })
                Execute.Sql($"CREATE INDEX IF NOT EXISTS \"IX_{item.Item1}_{item.Item2}\" ON \"{item.Item1}\" (\"{item.Item2}\");");

        }

        private void Backfill(IDbConnection connection, IDbTransaction transaction, string entity, string table, bool postgres)
        {
            // Preserve the released snapshot and aggregation order; restrict its input to one
            // entity page so aggregation and indexed-row maintenance share a bounded command.
            var ids = postgres
                ? "jsonb_agg(\"Provider\" || ':' || \"NormalizedProviderId\" ORDER BY \"Provider\", \"NormalizedProviderId\")"
                : "json_group_array(\"Provider\" || ':' || \"NormalizedProviderId\")";
            var pagePredicate = postgres ? "= ANY(@Ids)" : "IN @Ids";
            var snapshot = postgres ? "jsonb_object_agg(\"Scope\", ids)::text" : "json_group_object(\"Scope\", json(ids))";
            RunPages(connection, transaction, entity, "snapshot",
                $"SELECT \"Id\" FROM \"{table}\" WHERE \"Id\" > @AfterId ORDER BY \"Id\" LIMIT @BatchSize",
                $@"WITH scopes AS (
                    SELECT ""EntityId"", ""Scope"", {ids} AS ids FROM ""ProviderAliasIndex""
                    WHERE ""EntityType"" = @Entity AND ""EntityId"" {pagePredicate} GROUP BY ""EntityId"", ""Scope""
                ), snapshots AS (
                    SELECT ""EntityId"", {snapshot} AS ids FROM scopes GROUP BY ""EntityId""
                ) UPDATE ""{table}"" SET ""ProviderIds"" = snapshots.ids FROM snapshots WHERE ""Id"" = snapshots.""EntityId"";");

            // Page the existing (EntityType, EntityId, ...) index, including absent owners.
            // Limiting only the DELETE result would still rescan all retained aliases per page.
            RunPages(connection, transaction, entity, "orphan cleanup",
                @"SELECT DISTINCT ""EntityId"" FROM ""ProviderAliasIndex"" WHERE ""EntityType"" = @Entity
                    AND ""EntityId"" > @AfterId ORDER BY ""EntityId"" LIMIT @BatchSize",
                $@"DELETE FROM ""ProviderAliasIndex"" WHERE ""EntityType"" = @Entity AND ""EntityId"" {pagePredicate}
                    AND NOT EXISTS (SELECT 1 FROM ""{table}"" WHERE ""Id"" = ""EntityId"");");
        }

        private void RunPages(IDbConnection connection, IDbTransaction transaction, string entity, string operation, string selectSql, string writeSql)
        {
            const int batchSize = SqliteVariableLimit.MaxParameters / 2;
            var timer = Stopwatch.StartNew();
            long afterId = long.MinValue;
            long scanned = 0;
            long changed = 0;
            var reads = 0;
            var writes = 0;
            while (true)
            {
                var page = connection.Query<int>(selectSql, new { Entity = entity, AfterId = afterId, BatchSize = batchSize },
                    transaction, commandTimeout: MigrationController.CommandTimeoutSeconds).ToArray();
                reads++;
                if (page.Length == 0) break;
                changed += connection.Execute(writeSql, new { Entity = entity, Ids = page }, transaction,
                    commandTimeout: MigrationController.CommandTimeoutSeconds);
                writes++;
                scanned += page.Length;
                afterId = page[^1];
                if (writes % 100 == 0)
                    _logger.Info("Provider identity {0} {1} progress: scanned {2} entity IDs, changed {3} rows, pages {4}, elapsed {5} ms",
                        entity, operation, scanned, changed, writes, timer.ElapsedMilliseconds);
                if (page.Length < batchSize) break;
            }

            _logger.Info("Provider identity {0} {1}: scanned {2} entity IDs, changed {3} rows, read queries {4}, write batches {5}, elapsed {6} ms",
                entity, operation, scanned, changed, reads, writes, timer.ElapsedMilliseconds);
        }

        private void CreateSqliteTriggers(string entity, string table)
        {
            var insert = $@"SELECT CASE WHEN EXISTS (
                    SELECT 1 FROM json_each(COALESCE(NEW.""ProviderIds"", '{{}}')) scope, json_each(scope.value) id
                    WHERE instr(id.value, ':') <= 1 OR length(substr(id.value, instr(id.value, ':') + 1)) = 0
                ) THEN RAISE(ABORT, 'Invalid provider identity token') END;
                INSERT INTO ""ProviderAliasIndex"" (""EntityType"", ""EntityId"", ""Scope"", ""Provider"", ""NormalizedProviderId"", ""CreatedAt"", ""UpdatedAt"")
                SELECT '{entity}', NEW.""Id"", scope.key, substr(id.value, 1, instr(id.value, ':') - 1),
                    substr(id.value, instr(id.value, ':') + 1), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                FROM json_each(COALESCE(NEW.""ProviderIds"", '{{}}')) scope, json_each(scope.value) id;";
            var delete = $@"DELETE FROM ""ProviderAliasIndex"" WHERE ""EntityType"" = '{entity}' AND ""EntityId"" = OLD.""Id"";";
            IfDatabase("SQLite").Execute.Sql($@"CREATE TRIGGER ""{table}_provider_insert"" AFTER INSERT ON ""{table}"" BEGIN {insert} END;
                CREATE TRIGGER ""{table}_provider_update"" AFTER UPDATE OF ""ProviderIds"" ON ""{table}""
                    WHEN NEW.""ProviderIds"" IS NOT OLD.""ProviderIds"" BEGIN {delete} {insert} END;
                CREATE TRIGGER ""{table}_provider_delete"" AFTER DELETE ON ""{table}"" BEGIN {delete} END;");
        }

        private void CreatePostgresTriggers()
        {
            IfPostgres().Execute.Sql(@"CREATE FUNCTION chaptarr_project_provider_ids() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP <> 'INSERT' THEN
                        DELETE FROM ""ProviderAliasIndex"" WHERE ""EntityType"" = TG_ARGV[0] AND ""EntityId"" = OLD.""Id"";
                    END IF;
                    IF TG_OP <> 'DELETE' THEN
                        IF EXISTS (SELECT 1 FROM jsonb_each(COALESCE(NEW.""ProviderIds"", '{}')::jsonb) scope,
                            jsonb_array_elements_text(scope.value) id
                            WHERE strpos(id.value, ':') <= 1 OR length(substr(id.value, strpos(id.value, ':') + 1)) = 0) THEN
                            RAISE EXCEPTION 'Invalid provider identity token' USING ERRCODE = '22023';
                        END IF;
                        INSERT INTO ""ProviderAliasIndex"" (""EntityType"", ""EntityId"", ""Scope"", ""Provider"", ""NormalizedProviderId"", ""CreatedAt"", ""UpdatedAt"")
                        SELECT TG_ARGV[0], NEW.""Id"", scope.key, split_part(id.value, ':', 1),
                            substr(id.value, strpos(id.value, ':') + 1), CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                        FROM jsonb_each(COALESCE(NEW.""ProviderIds"", '{}')::jsonb) scope,
                            jsonb_array_elements_text(scope.value) id;
                    END IF;
                    RETURN NULL;
                END $$;");
            foreach (var entity in new[] { "Author", "Book", "Edition" })
            {
                var table = entity + "s";
                IfPostgres().Execute.Sql($@"CREATE TRIGGER ""{table}_provider_insert"" AFTER INSERT ON ""{table}""
                        FOR EACH ROW EXECUTE FUNCTION chaptarr_project_provider_ids('{entity}');
                    CREATE TRIGGER ""{table}_provider_update"" AFTER UPDATE OF ""ProviderIds"" ON ""{table}""
                        FOR EACH ROW WHEN (OLD.""ProviderIds"" IS DISTINCT FROM NEW.""ProviderIds"") EXECUTE FUNCTION chaptarr_project_provider_ids('{entity}');
                    CREATE TRIGGER ""{table}_provider_delete"" AFTER DELETE ON ""{table}""
                        FOR EACH ROW EXECUTE FUNCTION chaptarr_project_provider_ids('{entity}');");
            }
        }
    }
}
