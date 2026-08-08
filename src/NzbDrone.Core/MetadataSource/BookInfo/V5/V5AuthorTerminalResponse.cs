namespace NzbDrone.Core.MetadataSource.BookInfo.V5
{
    public class V5AuthorTerminalResponse
    {
        public string Code { get; set; }
        public string ProviderId { get; set; }
        public string ResolvedProviderId { get; set; }
        public string Message { get; set; }
        public bool Retryable { get; set; }
        public bool Reopenable { get; set; }
    }
}
