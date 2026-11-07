namespace NzbDrone.Host
{
    public enum ApplicationModes
    {
        Interactive,
        Help,
        InstallService,
        UninstallService,
        Service,
        RegisterUrl,
        MigrateBookshelf,
        CheckBookshelfMigration,
        RestoreBookshelf,
        CheckBookshelfPostgresBackup,
        PreviewBookshelfPostgresRestore,
        BackupBookshelfPostgres,
        RestoreBookshelfPostgres
    }
}
