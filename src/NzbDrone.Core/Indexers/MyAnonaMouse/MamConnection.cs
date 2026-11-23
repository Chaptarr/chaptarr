namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    public sealed record MamConnection(string BaseUrl, string MamId, bool MamSsl, int? ProxyId)
    {
        public static MamConnection For(IndexerDefinition definition) =>
            definition?.Settings is MyAnonaMouseSettings settings
                ? new MamConnection(settings.BaseUrl?.TrimEnd('/'), settings.MamId, settings.MamSsl, definition.ProxyId)
                : null;

        public override string ToString() => BaseUrl;
    }
}
