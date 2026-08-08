using System;
using System.Diagnostics;
using System.Reflection;
using FluentMigrator.Runner;
using FluentMigrator.Runner.Generators;
using FluentMigrator.Runner.Initialization;
using FluentMigrator.Runner.Processors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Extensions.Logging;

namespace NzbDrone.Core.Datastore.Migration.Framework
{
    public interface IMigrationController
    {
        void Migrate(string connectionString, MigrationContext migrationContext, DatabaseType databaseType);
    }

    public class MigrationController : IMigrationController
    {
        private readonly Logger _logger;
        private readonly ILoggerProvider _migrationLoggerProvider;

        public MigrationController(Logger logger,
                                   ILoggerProvider migrationLoggerProvider)
        {
            _logger = logger;
            _migrationLoggerProvider = migrationLoggerProvider;
        }

        public void Migrate(string connectionString, MigrationContext migrationContext, DatabaseType databaseType)
        {
            var sw = Stopwatch.StartNew();

            _logger.Info("*** Migrating {0} database ***", databaseType);

            ServiceProvider serviceProvider;

            // FluentMigrator processor/generator ids are case-sensitive and use "SQLite"/"PostgreSQL"
            // (older versions used "sqlite"/"postgres").
            var db = databaseType == DatabaseType.SQLite ? "SQLite" : "PostgreSQL";

            serviceProvider = new ServiceCollection()
                .AddLogging(b => b.AddNLog())
                .AddFluentMigratorCore()
                .Configure<RunnerOptions>(cfg => cfg.IncludeUntaggedMaintenances = true)
                .ConfigureRunner(
                    builder => builder
                    .AddPostgres()
                    .AddSQLite() // Use SQLite runner (Microsoft.Data.Sqlite)
                    .WithGlobalConnectionString(connectionString)
                    .ScanIn(Assembly.GetExecutingAssembly()).For.All())
                .Configure<TypeFilterOptions>(opt => opt.Namespace = "NzbDrone.Core.Datastore.Migration")
                .Configure<ProcessorOptions>(opt =>
                {
                    opt.PreviewOnly = false;
                    opt.Timeout = TimeSpan.FromMinutes(5);
                })
                .Configure<SelectingProcessorAccessorOptions>(cfg =>
                {
                    cfg.ProcessorId = db;
                })
                .Configure<SelectingGeneratorAccessorOptions>(cfg =>
                {
                    cfg.GeneratorId = db;
                })
                .BuildServiceProvider();

            using (var scope = serviceProvider.CreateScope())
            {
                var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();

                MigrationContext.Current = migrationContext;

                if (migrationContext.DesiredVersion.HasValue)
                {
                    runner.MigrateUp(migrationContext.DesiredVersion.Value);
                }
                else
                {
                    runner.MigrateUp();
                }

                MigrationContext.Current = null;
            }

            sw.Stop();

            _logger.Debug("Took: {0}", sw.Elapsed);

            // Optional: validate baseline schema checksum on fresh installs
            try
            {
                if (databaseType == DatabaseType.SQLite && !string.IsNullOrWhiteSpace(BaselineSchema.ExpectedSqliteSha256))
                {
                    var actual = SchemaChecksum.ComputeSqliteHash(connectionString);
                    if (!string.Equals(actual, BaselineSchema.ExpectedSqliteSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ApplicationException($"Schema checksum mismatch. Expected {BaselineSchema.ExpectedSqliteSha256}, got {actual}");
                    }
                    _logger.Info("Baseline schema checksum OK: {0}", actual);
                }
                else if (databaseType == DatabaseType.PostgreSQL && !string.IsNullOrWhiteSpace(BaselineSchema.ExpectedPostgresSha256))
                {
                    var actual = SchemaChecksum.ComputePostgresHash(connectionString);
                    if (!string.Equals(actual, BaselineSchema.ExpectedPostgresSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ApplicationException($"Postgres schema checksum mismatch. Expected {BaselineSchema.ExpectedPostgresSha256}, got {actual}");
                    }
                    _logger.Info("Baseline Postgres schema checksum OK: {0}", actual);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Schema checksum validation failed");
                throw;
            }
        }
    }
}
