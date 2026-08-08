using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(62)]
    public class fix_postgres_datetime_range_all_tables : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            IfPostgres().Execute.Sql(@"
DO $$
DECLARE
    tbl record;
    col record;
    set_clauses text;
    where_clauses text;
    invalid_expr text;
    replacement text;
    constraint_name text;
    check_expr text;
BEGIN
    -- Use a stable timezone for deterministic bounds checks.
    PERFORM set_config('TimeZone', 'UTC', true);

    -- 1) Repair any existing out-of-range/infinite values so Npgsql can hydrate DateTime columns safely.
    -- We do one UPDATE per table (not per column) to keep this reasonably fast on large libraries.
    FOR tbl IN
        SELECT t.table_schema, t.table_name
        FROM information_schema.tables t
        WHERE t.table_schema = 'public'
          AND t.table_type = 'BASE TABLE'
        ORDER BY t.table_name
    LOOP
        set_clauses := '';
        where_clauses := '';

        FOR col IN
            SELECT c.column_name, c.is_nullable, c.data_type
            FROM information_schema.columns c
            WHERE c.table_schema = tbl.table_schema
              AND c.table_name = tbl.table_name
              AND c.data_type IN ('timestamp without time zone', 'timestamp with time zone', 'date')
            ORDER BY c.ordinal_position
        LOOP
            IF col.data_type = 'date' THEN
                invalid_expr := format(
                    '(NOT isfinite(%1$I) OR %1$I < DATE %2$L OR %1$I > DATE %3$L)',
                    col.column_name,
                    '0001-01-01',
                    '9999-12-31');

                replacement := CASE
                    WHEN col.is_nullable = 'YES' THEN 'NULL'
                    ELSE 'CURRENT_DATE'
                END;
            ELSE
                invalid_expr := format(
                    '(NOT isfinite(%1$I) OR %1$I < TIMESTAMP %2$L OR %1$I > TIMESTAMP %3$L)',
                    col.column_name,
                    '0001-01-01 00:00:00',
                    '9999-12-31 23:59:59.999999');

                replacement := CASE
                    WHEN col.is_nullable = 'YES' THEN 'NULL'
                    ELSE 'CURRENT_TIMESTAMP'
                END;
            END IF;

            IF set_clauses <> '' THEN
                set_clauses := set_clauses || ', ';
                where_clauses := where_clauses || ' OR ';
            END IF;

            set_clauses := set_clauses || format(
                '%1$I = CASE WHEN %2$s THEN %3$s ELSE %1$I END',
                col.column_name,
                invalid_expr,
                replacement);

            where_clauses := where_clauses || invalid_expr;
        END LOOP;

        IF set_clauses <> '' THEN
            EXECUTE format(
                'UPDATE %I.%I SET %s WHERE %s;',
                tbl.table_schema,
                tbl.table_name,
                set_clauses,
                where_clauses);
        END IF;
    END LOOP;

    -- 2) Prevent future pollution on all write paths: add infinity/range CHECK constraints for every date/timestamp column.
    -- We add constraints as NOT VALID to avoid full-table validation scans on large databases. These are still enforced for
    -- all new/updated rows, which is what we need for ongoing protection.
    FOR tbl IN
        SELECT t.table_schema, t.table_name
        FROM information_schema.tables t
        WHERE t.table_schema = 'public'
          AND t.table_type = 'BASE TABLE'
        ORDER BY t.table_name
    LOOP
        FOR col IN
            SELECT c.column_name, c.data_type
            FROM information_schema.columns c
            WHERE c.table_schema = tbl.table_schema
              AND c.table_name = tbl.table_name
              AND c.data_type IN ('timestamp without time zone', 'timestamp with time zone', 'date')
            ORDER BY c.ordinal_position
        LOOP
            constraint_name := format(
                'ck_dt_%s',
                substr(md5(tbl.table_schema || '.' || tbl.table_name || '.' || col.column_name), 1, 16));

            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint pc
                JOIN pg_class rel ON rel.oid = pc.conrelid
                JOIN pg_namespace ns ON ns.oid = rel.relnamespace
                WHERE ns.nspname = tbl.table_schema
                  AND rel.relname = tbl.table_name
                  AND pc.conname = constraint_name
            ) THEN
                IF col.data_type = 'date' THEN
                    check_expr := format(
                        '%1$I IS NULL OR (isfinite(%1$I) AND %1$I >= DATE %2$L AND %1$I <= DATE %3$L)',
                        col.column_name,
                        '0001-01-01',
                        '9999-12-31');
                ELSE
                    check_expr := format(
                        '%1$I IS NULL OR (isfinite(%1$I) AND %1$I >= TIMESTAMP %2$L AND %1$I <= TIMESTAMP %3$L)',
                        col.column_name,
                        '0001-01-01 00:00:00',
                        '9999-12-31 23:59:59.999999');
                END IF;

                EXECUTE format(
                    'ALTER TABLE %I.%I ADD CONSTRAINT %I CHECK (%s) NOT VALID;',
                    tbl.table_schema,
                    tbl.table_name,
                    constraint_name,
                    check_expr);
            END IF;
        END LOOP;
    END LOOP;
END $$;
");
        }
    }
}

