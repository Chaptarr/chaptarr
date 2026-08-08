namespace Chaptarr.Api.V1.System
{
    public class SystemResetResource
    {
        public string Message { get; set; }
    }

    public class SystemShutdownResource
    {
        public bool ShuttingDown { get; set; }
    }

    public class SystemRestartResource
    {
        public bool Restarting { get; set; }
    }
}
