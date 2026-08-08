using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using NzbDrone.Common.EnvironmentInfo;
using SQLitePCL;

namespace NzbDrone.Common.Composition
{
    public class AssemblyLoader
    {
        static AssemblyLoader()
        {
            AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler(ContainerResolveEventHandler);
            // Initialize bundled SQLite to ensure a modern engine is used across platforms
            try { Batteries_V2.Init(); } catch { }
        }

        public static IEnumerable<Assembly> Load(IEnumerable<string> assemblies)
        {
            var toLoad = assemblies.ToList();
            toLoad.Add("Chaptarr.Common");
            toLoad.Add(OsInfo.IsWindows ? "Chaptarr.Windows" : "Chaptarr.Mono");

            var startupPath = AppDomain.CurrentDomain.BaseDirectory;

            return toLoad.Select(x =>
                AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(startupPath, $"{x}.dll")));
        }

        private static Assembly ContainerResolveEventHandler(object sender, ResolveEventArgs args)
        {
            var resolver = new AssemblyDependencyResolver(args.RequestingAssembly.Location);
            var assemblyPath = resolver.ResolveAssemblyToPath(new AssemblyName(args.Name));

            if (assemblyPath == null)
            {
                return null;
            }

            return AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        }

        // System.Data.SQLite-specific resolver no longer required with Microsoft.Data.Sqlite + SQLitePCLRaw
        // Batteries_V2.Init() above ensures the appropriate native library is available.
    }
}
