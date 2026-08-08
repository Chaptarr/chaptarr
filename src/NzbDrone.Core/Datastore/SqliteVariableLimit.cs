namespace NzbDrone.Core.Datastore
{
    internal static class SqliteVariableLimit
    {
        // SQLite's default host parameter limit is 999. Use a lower value to leave headroom for other parameters.
        internal const int MaxParameters = 900;
    }
}

