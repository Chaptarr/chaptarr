using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace NzbDrone.Core.Datastore
{
    public static class SchemaChecksum
    {
        public static string ComputeSqliteHash(string connectionString)
        {
            using var con = new SqliteConnection(connectionString);
            con.Open();
            var rows = con.Query<(string type, string name, string tbl, string sql)>(
                "SELECT type, name, tbl_name, sql FROM sqlite_master WHERE type IN ('table','index','trigger','view') ORDER BY type, name;");

            var sb = new StringBuilder();
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.sql)) continue;
                var sql = row.sql.Trim();
                if (!sql.EndsWith(";")) sql += ";";
                sb.AppendLine(sql);
            }

            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            var hash = sha.ComputeHash(bytes);
            return string.Concat(hash.Select(b => b.ToString("x2")));
        }

        public static string ComputePostgresHash(string connectionString)
        {
            using var con = new NpgsqlConnection(connectionString);
            con.Open();

            var sb = new StringBuilder();

            // Tables and columns
            var tables = con.Query<string>(
                "SELECT table_name FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE' ORDER BY table_name;");
            foreach (var t in tables)
            {
                var cols = con.Query<(string column_name, string data_type, string is_nullable, string column_default)>(
                    "SELECT column_name, data_type, is_nullable, column_default FROM information_schema.columns WHERE table_schema='public' AND table_name=@t ORDER BY ordinal_position;",
                    new { t });
                sb.AppendLine($"-- table: {t}");
                foreach (var c in cols)
                {
                    sb.AppendLine($"{t}.{c.column_name}:{c.data_type}:{c.is_nullable}:{(c.column_default ?? "").Trim()};");
                }
                // Constraints (primary/unique/foreign)
                var constraints = con.Query<(string constraint_name, string constraint_type)>(
                    "SELECT tc.constraint_name, tc.constraint_type FROM information_schema.table_constraints tc WHERE tc.table_schema='public' AND tc.table_name=@t ORDER BY tc.constraint_name;",
                    new { t });
                foreach (var ctr in constraints)
                {
                    sb.AppendLine($"constraint {ctr.constraint_name}:{ctr.constraint_type};");
                }
            }

            // Indexes
            var indexes = con.Query<(string indexname, string indexdef)>(
                "SELECT indexname, indexdef FROM pg_indexes WHERE schemaname='public' ORDER BY indexname;");
            foreach (var ix in indexes)
            {
                var def = (ix.indexdef ?? string.Empty).Trim();
                if (!def.EndsWith(";")) def += ";";
                sb.AppendLine(def);
            }

            // Views
            var views = con.Query<(string viewname, string definition)>(
                "SELECT viewname, definition FROM pg_views WHERE schemaname='public' ORDER BY viewname;");
            foreach (var v in views)
            {
                var def = (v.definition ?? string.Empty).Trim();
                if (!def.EndsWith(";")) def += ";";
                sb.AppendLine($"-- view: {v.viewname}");
                sb.AppendLine(def);
            }

            // Triggers
            var triggers = con.Query<(string tgname, string tgdef)>(
                @"SELECT t.tgname, pg_get_triggerdef(t.oid) AS tgdef
                  FROM pg_trigger t
                  JOIN pg_class c ON t.tgrelid=c.oid
                  JOIN pg_namespace n ON c.relnamespace=n.oid
                  WHERE n.nspname='public' AND NOT t.tgisinternal
                  ORDER BY t.tgname;");
            foreach (var tr in triggers)
            {
                var def = (tr.tgdef ?? string.Empty).Trim();
                if (!def.EndsWith(";")) def += ";";
                sb.AppendLine(def);
            }

            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            var hash = sha.ComputeHash(bytes);
            return string.Concat(hash.Select(b => b.ToString("x2")));
        }
    }
}
