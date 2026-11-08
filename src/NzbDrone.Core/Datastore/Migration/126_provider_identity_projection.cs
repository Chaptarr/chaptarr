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

                // Preserve the last known snapshot, including legacy Book/edition IDs with
                // no retained local edition. A successful refresh replaces the entire set.
                {
                    IfPostgres().Execute.Sql($@"WITH scopes AS (
                        SELECT ""EntityId"", ""Scope"", jsonb_agg(""Provider"" || ':' || ""NormalizedProviderId"" ORDER BY ""Provider"", ""NormalizedProviderId"") AS ids
                        FROM ""ProviderAliasIndex"" WHERE ""EntityType"" = '{entity}' GROUP BY ""EntityId"", ""Scope""
                    ), snapshots AS (
                        SELECT ""EntityId"", jsonb_object_agg(""Scope"", ids)::text AS ids FROM scopes GROUP BY ""EntityId""
                    ) UPDATE ""{table}"" SET ""ProviderIds"" = snapshots.ids FROM snapshots WHERE ""Id"" = snapshots.""EntityId"";");
                }
                {
                    IfDatabase("SQLite").Execute.Sql($@"WITH scopes AS (
                        SELECT ""EntityId"", ""Scope"", json_group_array(""Provider"" || ':' || ""NormalizedProviderId"") AS ids
                        FROM ""ProviderAliasIndex"" WHERE ""EntityType"" = '{entity}' GROUP BY ""EntityId"", ""Scope""
                    ), snapshots AS (
                        SELECT ""EntityId"", json_group_object(""Scope"", json(ids)) AS ids FROM scopes GROUP BY ""EntityId""
                    ) UPDATE ""{table}"" SET ""ProviderIds"" = snapshots.ids FROM snapshots WHERE ""Id"" = snapshots.""EntityId"";");
                }

                Execute.Sql($@"DELETE FROM ""ProviderAliasIndex"" WHERE ""EntityType"" = '{entity}'
                    AND NOT EXISTS (SELECT 1 FROM ""{table}"" WHERE ""Id"" = ""EntityId"");");

                CreateSqliteTriggers(entity, table);
            }

            CreatePostgresTriggers();
            foreach (var item in new[] { ("ExtraFiles", "BookId"), ("ExtraFiles", "BookFileId"), ("MetadataFiles", "BookId"),
                ("OtherFiles", "EditionId"), ("ABSLookupHistory", "BookId") })
                Execute.Sql($"CREATE INDEX IF NOT EXISTS \"IX_{item.Item1}_{item.Item2}\" ON \"{item.Item1}\" (\"{item.Item2}\");");

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
