using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Goodreads
{
    public enum OwnedBookCondition
    {
        [FieldOption(Label = "provider.option.oa515fc82b14f")]
        BrandNew = 10,
        [FieldOption(Label = "provider.option.o343deecd9d8f")]
        LikeNew = 20,
        [FieldOption(Label = "provider.option.o388655e25e91")]
        VeryGood = 30,
        [FieldOption(Label = "provider.option.oc939327ca16d")]
        Good = 40,
        [FieldOption(Label = "provider.option.o4b9cfa1c7da0")]
        Acceptable = 50,
        [FieldOption(Label = "provider.option.o94dfd46d9dae")]
        Poor = 60
    }

    public class GoodreadsOwnedBooksNotificationSettings : GoodreadsSettingsBase<GoodreadsOwnedBooksNotificationSettings>
    {
        private static readonly GoodreadsSettingsBaseValidator<GoodreadsOwnedBooksNotificationSettings> Validator = new GoodreadsSettingsBaseValidator<GoodreadsOwnedBooksNotificationSettings>();

        [FieldDefinition(1, Label = "Condition", Type = FieldType.Select, SelectOptions = typeof(OwnedBookCondition))]
        public int Condition { get; set; } = (int)OwnedBookCondition.BrandNew;

        [FieldDefinition(1, Label = "provider.field.fe200ec848620", Type = FieldType.Textbox)]
        public string Description { get; set; }

        [FieldDefinition(1, Label = "provider.field.f03db69a5ae6d", HelpText = "provider.field.f106db5facb2e", Type = FieldType.Textbox)]
        public string Location { get; set; }

        public override NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
