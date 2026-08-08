namespace NzbDrone.Core.Books
{
    public class NarratorCredit
    {
        public string Name { get; set; }
        public string GoodreadsNarratorId { get; set; }
        public string HardcoverNarratorId { get; set; }
        public int Order { get; set; }
        public bool IsPrimary { get; set; }
        public string Role { get; set; } = "Narrator";
    }
}

