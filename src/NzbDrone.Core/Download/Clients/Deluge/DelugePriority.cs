using NzbDrone.Core.Annotations;

namespace NzbDrone.Core.Download.Clients.Deluge
{
    public enum DelugePriority
    {
        [FieldOption(Label = "provider.option.oeb970eb0951c")]
        Last = 0,
        [FieldOption(Label = "provider.option.oa151ceb1711a")]
        First = 1
    }
}
