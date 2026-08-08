namespace NzbDrone.Core.CustomFormats
{
    public class ReleaseGroupSpecification : RegexSpecificationBase
    {
        public override int Order => 9;
        public override string ImplementationName => "Release Group";
        public override string InfoLink => "https://discord.gg/nqFGsGUug2";

        protected override bool IsSatisfiedByWithoutNegate(CustomFormatInput input)
        {
            return MatchString(input.BookInfo?.ReleaseGroup);
        }
    }
}
