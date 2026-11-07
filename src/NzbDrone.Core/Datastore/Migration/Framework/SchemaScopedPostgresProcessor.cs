using System;
using System.Collections.Generic;
using FluentMigrator;
using FluentMigrator.Runner.Generators.Postgres;
using FluentMigrator.Runner.Initialization;
using FluentMigrator.Runner.Processors;
using FluentMigrator.Runner.Processors.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FluentMigratorPostgresOptions = FluentMigrator.Runner.Processors.Postgres.PostgresOptions;

namespace NzbDrone.Core.Datastore.Migration.Framework
{
    /// <summary>
    /// PostgreSQL processor that resolves unqualified migration schema checks against the active schema.
    /// </summary>
    internal sealed class SchemaScopedPostgresProcessor : Postgres15_0Processor
    {
        public const string ProcessorId = "ChaptarrPostgreSQL";

        private readonly string _schemaName;

        public SchemaScopedPostgresProcessor(
            PostgresDbFactory factory,
            Postgres15_0Generator generator,
            ILogger<Postgres15_0Processor> logger,
            IOptionsSnapshot<ProcessorOptions> options,
            IConnectionStringAccessor connectionStringAccessor,
            FluentMigratorPostgresOptions postgresOptions,
            string schemaName)
            : base(factory, generator, logger, options, connectionStringAccessor, postgresOptions)
        {
            _schemaName = schemaName;
        }

        public override string DatabaseType => "PostgreSQL15_0";

        public override IList<string> DatabaseTypeAliases { get; } = new List<string>
        {
            ProcessorId,
            "PostgreSQL15_0",
            "PostgreSQL"
        };

        public override bool SchemaExists(string schemaName)
        {
            // Call the base implementation directly. Delegating through an IQuerySchema
            // wrapper around this processor would dispatch back into this override.
            return base.SchemaExists(ResolveSchemaName(schemaName, _schemaName));
        }

        public override bool TableExists(string schemaName, string tableName)
        {
            return base.TableExists(ResolveSchemaName(schemaName, _schemaName), tableName);
        }

        public override bool ColumnExists(string schemaName, string tableName, string columnName)
        {
            return base.ColumnExists(ResolveSchemaName(schemaName, _schemaName), tableName, columnName);
        }

        public override bool ConstraintExists(string schemaName, string tableName, string constraintName)
        {
            return base.ConstraintExists(ResolveSchemaName(schemaName, _schemaName), tableName, constraintName);
        }

        public override bool IndexExists(string schemaName, string tableName, string indexName)
        {
            return base.IndexExists(ResolveSchemaName(schemaName, _schemaName), tableName, indexName);
        }

        public override bool SequenceExists(string schemaName, string sequenceName)
        {
            return base.SequenceExists(ResolveSchemaName(schemaName, _schemaName), sequenceName);
        }

        public override bool DefaultValueExists(string schemaName, string tableName, string columnName, object defaultValue)
        {
            return base.DefaultValueExists(ResolveSchemaName(schemaName, _schemaName), tableName, columnName, defaultValue);
        }

        internal static string ResolveSchemaName(string schemaName, string defaultSchemaName)
        {
            return string.IsNullOrWhiteSpace(schemaName) ? defaultSchemaName : schemaName;
        }

        public static void Register(IServiceCollection services, string schemaName)
        {
            services.AddScoped(sp => new SchemaScopedPostgresProcessor(
                sp.GetRequiredService<PostgresDbFactory>(),
                sp.GetRequiredService<Postgres15_0Generator>(),
                sp.GetRequiredService<ILogger<Postgres15_0Processor>>(),
                sp.GetRequiredService<IOptionsSnapshot<ProcessorOptions>>(),
                sp.GetRequiredService<IConnectionStringAccessor>(),
                sp.GetRequiredService<FluentMigratorPostgresOptions>(),
                schemaName));
            services.AddScoped<IMigrationProcessor>(sp => sp.GetRequiredService<SchemaScopedPostgresProcessor>());
        }
    }
}
