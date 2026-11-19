namespace NzbDrone.Core.MediaFiles
{
    public enum ConversionRetentionStatus
    {
        None,
        Pending,
        Retained,
        Blocked
    }

    public sealed class ConversionRetentionResult
    {
        public ConversionRetentionStatus Status { get; }
        public string Message { get; }
        public string OutputPath { get; }

        public ConversionRetentionResult(ConversionRetentionStatus status, string message, string outputPath = null)
        {
            Status = status;
            OutputPath = outputPath;
            Message = AppendRetainedConversionPath(message, outputPath);
        }

        public static string AppendRetainedConversionPath(string message, string retainedPath)
        {
            if (string.IsNullOrWhiteSpace(retainedPath)) return message;
            var prefix = string.IsNullOrWhiteSpace(message) ? "Converted M4B was not imported." : message;
            return $"{prefix} Converted file retained at: {retainedPath}";
        }
    }
}
