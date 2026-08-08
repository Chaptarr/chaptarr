namespace NzbDrone.Core.Books
{
    public enum MonitorTypes
    {
        All,
        Future,
        Missing,
        Existing,
        Latest,
        First,
        None,
        SpecificBook,  // Monitor only the specific book being added
        Unknown
    }
}
