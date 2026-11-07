using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Download.Clients.Flood.Models;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Download.Clients.Flood
{
    public class FloodSettingsValidator : AbstractValidator<FloodSettings>
    {
        public FloodSettingsValidator()
        {
            RuleFor(c => c.Host).ValidHost();
            RuleFor(c => c.Port).InclusiveBetween(1, 65535);
        }
    }

    public class FloodSettings : IProviderConfig
    {
        private static readonly FloodSettingsValidator Validator = new FloodSettingsValidator();

        private IEnumerable<string> _audiobookTags;
        private IEnumerable<string> _ebookTags;

        public FloodSettings()
        {
            UseSsl = false;
            Host = "localhost";
            Port = 3000;
            Tags = new string[]
            {
                "chaptarr"
            };
            AdditionalTags = Enumerable.Empty<int>();
            AddPaused = false;
        }

        [FieldDefinition(0, Label = "Host", Type = FieldType.Textbox)]
        public string Host { get; set; }

        [FieldDefinition(1, Label = "Port", Type = FieldType.Textbox)]
        public int Port { get; set; }

        [FieldDefinition(2, Label = "UseSSL", Type = FieldType.Checkbox, HelpText = "provider.field.f1bb8647b253d")]
        public bool UseSsl { get; set; }

        [FieldDefinition(3, Label = "provider.field.fafbbcc619aac", Type = FieldType.Textbox, HelpText = "provider.field.fcba7a5944e56")]
        public string UrlBase { get; set; }

        [FieldDefinition(4, Label = "Username", Type = FieldType.Textbox, Privacy = PrivacyLevel.UserName)]
        public string Username { get; set; }

        [FieldDefinition(5, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password)]
        public string Password { get; set; }

        [FieldDefinition(6, Label = "provider.field.f293d404a500f", Type = FieldType.Textbox, HelpText = "provider.field.f27ef4d05f33c")]
        public string Destination { get; set; }

        [FieldDefinition(7, Label = "AudiobookTags", Type = FieldType.Tag, HelpText = "provider.field.f634ff14e26d8")]
        public IEnumerable<string> AudiobookTags
        {
            get => _audiobookTags ?? Tags;
            set => _audiobookTags = value;
        }

        [FieldDefinition(8, Label = "EbookTags", Type = FieldType.Tag, HelpText = "provider.field.f634ff14e26d8")]
        public IEnumerable<string> EbookTags
        {
            get => _ebookTags ?? Tags;
            set => _ebookTags = value;
        }

        [FieldDefinition(100, Label = "Tags", Type = FieldType.Tag, Hidden = HiddenType.Hidden)]
        public IEnumerable<string> Tags { get; set; }

        [FieldDefinition(9, Label = "provider.field.f82974f95fded", Type = FieldType.Tag, HelpText = "provider.field.f05cfbd3ed92f", Advanced = true)]
        public IEnumerable<string> PostImportTags { get; set; }

        [FieldDefinition(10, Label = "provider.field.fa3d105e2ffb9", Type = FieldType.Select, SelectOptions = typeof(AdditionalTags), HelpText = "provider.field.f1cbe5ac7e066", Advanced = true)]
        public IEnumerable<int> AdditionalTags { get; set; }

        [FieldDefinition(11, Label = "provider.field.fee2bb4b2e037", Type = FieldType.Checkbox)]
        public bool AddPaused { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
