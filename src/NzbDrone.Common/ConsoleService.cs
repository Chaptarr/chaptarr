using System;
using System.Diagnostics;
using System.IO;
using NzbDrone.Common.EnvironmentInfo;

namespace NzbDrone.Common
{
    public interface IConsoleService
    {
        void PrintHelp();
        void PrintServiceAlreadyExist();
        void PrintServiceDoesNotExist();
    }

    public class ConsoleService : IConsoleService
    {
        public static bool IsConsoleAvailable => Console.In != StreamReader.Null;

        public void PrintHelp()
        {
            Console.WriteLine();
            Console.WriteLine("     Usage: {0} <command> ", Process.GetCurrentProcess().MainModule.ModuleName);
            Console.WriteLine("     Commands:");
            if (OsInfo.IsWindows)
            {
                Console.WriteLine("                 /{0} Install the application as a Windows Service ({1}).", StartupContext.INSTALL_SERVICE, ServiceProvider.SERVICE_NAME);
                Console.WriteLine("                 /{0} Uninstall already installed Windows Service ({1}).", StartupContext.UNINSTALL_SERVICE, ServiceProvider.SERVICE_NAME);
                Console.WriteLine("                 /{0} Register URL and open firewall port (allows access from other devices on your network).", StartupContext.REGISTER_URL);
            }

            Console.WriteLine("                 /{0} Don't open Chaptarr in a browser", StartupContext.NO_BROWSER);
            Console.WriteLine("                 /{0} Start Chaptarr terminating any other instances", StartupContext.TERMINATE);
            Console.WriteLine("                 /{0}=path Path to use as the AppData location (stores database, config, logs, etc)", StartupContext.APPDATA);
            Console.WriteLine("                 /{0} Preview BookshelfNG SQLite or PostgreSQL conversion without changing source tables", StartupContext.PREVIEW_BOOKSHELF_MIGRATION);
            Console.WriteLine("                 /{0} Convert a BookshelfNG SQLite or PostgreSQL database and retain a source backup", StartupContext.MIGRATE_BOOKSHELF);
            Console.WriteLine("                 /{0} Explicitly merge populated readarr.db and audioarr.db during migration preview or apply", StartupContext.MERGE_BOOKSHELF_DATABASES);
            Console.WriteLine("                 /{0} Preview restoration of a retained BookshelfNG backup", StartupContext.PREVIEW_BOOKSHELF_RESTORE);
            Console.WriteLine("                 /{0} Restore BookshelfNG from a retained backup; specify /bookshelf-backup=path-or-schema", StartupContext.RESTORE_BOOKSHELF);
            Console.WriteLine("                 /{0} Check pg_dump and pg_restore before a Windows PostgreSQL handoff", StartupContext.CHECK_BOOKSHELF_POSTGRES_BACKUP);
            Console.WriteLine("                 /{0} Validate retained PostgreSQL rollback dumps without changing the databases", StartupContext.PREVIEW_BOOKSHELF_POSTGRES_RESTORE);
            Console.WriteLine("                 /{0} Create independent PostgreSQL database backups; specify /bookshelf-postgres-backup=directory", StartupContext.BACKUP_BOOKSHELF_POSTGRES);
            Console.WriteLine("                 /{0} Restore independent PostgreSQL database backups; specify /bookshelf-postgres-backup=directory", StartupContext.RESTORE_BOOKSHELF_POSTGRES);
            Console.WriteLine("                 /{0} Confirm that all BookshelfNG or ChaptarrNG processes using the selected databases are stopped", StartupContext.CONFIRM_BOOKSHELF_SERVICE_STOPPED);
            Console.WriteLine("                 <No Arguments>  Run application in console mode.");
        }

        public void PrintServiceAlreadyExist()
        {
            Console.WriteLine("A service with the same name ({0}) already exists. Aborting installation", ServiceProvider.SERVICE_NAME);
        }

        public void PrintServiceDoesNotExist()
        {
            Console.WriteLine("Can't find service ({0})", ServiceProvider.SERVICE_NAME);
        }
    }
}
