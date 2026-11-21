using System.Linq;
using System.Runtime.InteropServices;

namespace NzbDrone.Host
{
    // Replaces the running process image with a new one (POSIX exec). The PID stays the same, so a
    // container's main process can restart itself, and nothing from the old process survives.
    public static class ProcessReplacement
    {
        [DllImport("libc", SetLastError = true)]
        private static extern int execv(string path, string[] argv);

        // Does not return on success. Returns the OS error number when the process could not be replaced.
        public static int Replace(string[] argv)
        {
            if (argv == null || argv.Length == 0 || string.IsNullOrWhiteSpace(argv[0]))
            {
                return -1;
            }

            try
            {
                // argv must end with a null entry.
                execv(argv[0], argv.Append(null).ToArray());
                return Marshal.GetLastWin32Error();
            }
            catch (System.Exception)
            {
                // No libc exec on this platform.
                return -1;
            }
        }
    }
}
