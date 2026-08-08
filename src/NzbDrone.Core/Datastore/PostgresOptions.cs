using Microsoft.Extensions.Configuration;

namespace NzbDrone.Core.Datastore
{
    public class PostgresOptions
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public string User { get; set; }
        public string Password { get; set; }
        public string MainDb { get; set; }
        public string LogDb { get; set; }
        public string CacheDb { get; set; }

        public static PostgresOptions GetOptions()
        {
            var config = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build();

            var postgresOptions = new PostgresOptions();
            config.GetSection("Chaptarr:Postgres").Bind(postgresOptions);

            if (string.IsNullOrWhiteSpace(postgresOptions.Host))
            {
                // Rebrand compatibility (Readarr/AudioArr → Chaptarr): accept legacy section names/env vars.
                config.GetSection("Readarr:Postgres").Bind(postgresOptions);
                config.GetSection("Audioarr:Postgres").Bind(postgresOptions);
            }

            return postgresOptions;
        }
    }
}
