using Microsoft.Extensions.Configuration;
using NzbDrone.Core.Configuration;

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
        public string SslMode { get; set; }
        public string RootCert { get; set; }
        public bool? NoResetOnClose { get; set; }

        public static PostgresOptions GetOptions()
        {
            var config = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build();

            var compatibleConfig = LegacyConfigurationCompatibility.AddFallbackSections(config);
            var postgresOptions = new PostgresOptions();
            compatibleConfig.GetSection("Chaptarr:Postgres").Bind(postgresOptions);

            return postgresOptions;
        }
    }
}
