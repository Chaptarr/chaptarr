namespace NzbDrone.Common.Options;

public class AuthOptions
{
    public string ApiKey { get; set; }
    public string SeerrApiKey { get; set; }
    public bool? Enabled { get; set; }
    public bool? AllowApiKeyQuery { get; set; }
    public string Method { get; set; }
    public string Required { get; set; }
    public bool? TrustCgnatIpAddresses { get; set; }
}
