using System;
using System.Runtime.InteropServices;

namespace NzbDrone.Common.EnvironmentInfo
{
    public static class ProcessUserInfo
    {
        [DllImport("libc")]
        private static extern uint geteuid();

        [DllImport("libc")]
        private static extern uint getegid();

        public static (uint? Uid, uint? Gid) GetEffectiveUserAndGroupIds()
        {
            if (OsInfo.IsWindows)
            {
                return (null, null);
            }

            try
            {
                return (geteuid(), getegid());
            }
            catch
            {
                return (null, null);
            }
        }

        public static string GetUserNameWithIds()
        {
            var userName = Environment.UserName;
            var (uid, gid) = GetEffectiveUserAndGroupIds();

            if (uid.HasValue && gid.HasValue)
            {
                return $"{userName} (uid={uid.Value}, gid={gid.Value})";
            }

            return userName;
        }

        public static string GetDockerUserEnvSummary()
        {
            var puid = Environment.GetEnvironmentVariable("PUID");
            var pgid = Environment.GetEnvironmentVariable("PGID");

            if (string.IsNullOrWhiteSpace(puid) && string.IsNullOrWhiteSpace(pgid))
            {
                return null;
            }

            return $"PUID={puid ?? string.Empty} PGID={pgid ?? string.Empty}".Trim();
        }
    }
}
