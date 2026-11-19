using FluentMigrator;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(114)]
    public class rebuild_shared_comparison_keys_casefold : rebuild_shared_comparison_keys
    {
    }
}
