using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(108)]
    public class normalize_comparison_keys : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            _logger.Info("Comparison-key migration superseded by the bounded shared rebuild in migration 113");
        }
    }
}
