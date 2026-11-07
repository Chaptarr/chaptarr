using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Kavita;

public class KavitaSettingsValidator : AbstractValidator<KavitaSettings>
{
    public KavitaSettingsValidator()
    {
        RuleFor(c => c.Host).ValidHost();
        RuleFor(c => c.Port).InclusiveBetween(1, 65535);
        RuleFor(c => c.ApiKey).NotEmpty();
    }
}

public class KavitaSettings : IProviderConfig
{
    private static readonly KavitaSettingsValidator Validator = new KavitaSettingsValidator();

    public KavitaSettings()
    {
        Port = 4040;
    }

    [FieldDefinition(0, Label = "Host")]
    public string Host { get; set; }

    [FieldDefinition(1, Label = "Port")]
    public int Port { get; set; }

    [FieldDefinition(2, Label = "ApiKey", Privacy = PrivacyLevel.ApiKey, HelpLink = "https://wiki.kavitareader.com/en/guides/settings/opds")]
    public string ApiKey { get; set; }

    [FieldDefinition(3, Label = "UseSSL", Type = FieldType.Checkbox, HelpText = "provider.field.f19bfd1110017")]
    public bool UseSsl { get; set; }

    [FieldDefinition(4, Label = "NotificationsSettingsUpdateLibrary", Type = FieldType.Checkbox)]
    public bool Notify { get; set; }

    public NzbDroneValidationResult Validate()
    {
        return new NzbDroneValidationResult(Validator.Validate(this));
    }
}
