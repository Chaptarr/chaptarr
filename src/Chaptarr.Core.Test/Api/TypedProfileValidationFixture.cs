using System;
using System.Collections.Generic;
using Chaptarr.Core.Test.Common;
using FluentValidation;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Validation;

namespace Chaptarr.Core.Test.Api
{
    [TestFixture]
    public class TypedProfileValidationFixture
    {
        private sealed class Selection
        {
            public int? Quality { get; set; }
            public int? StoredQuality { get; set; }
            public int? Metadata { get; set; }
            public int? StoredMetadata { get; set; }
        }

        [Test]
        public void typed_validators_reject_the_other_side_and_allow_general_metadata(
            [Values(false, true)] bool postgres, [Values(ProfileType.Audiobook, ProfileType.Ebook)] ProfileType side)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var events = InterfaceProxy.Proxy<IEventAggregator>((method, args) => null);
                var formats = InterfaceProxy.Proxy<ICustomFormatService>((method, args) =>
                    method.Name == "All" ? new List<CustomFormat>() : throw new NotImplementedException(method.Name));
                var qualityRepository = new QualityProfileRepository(database, events, formats);
                var metadataRepository = new MetadataProfileRepository(database, events);
                var quality = new QualityProfileService(qualityRepository, null, null, formats, null, null, LogManager.GetCurrentClassLogger());
                var metadata = new MetadataProfileService(metadataRepository, null, null, null, null, null, null, null, events, LogManager.GetCurrentClassLogger());
                var acceptedQuality = quality.Add(new QualityProfile { Name = "Requested", ProfileType = side, Items = new List<QualityProfileQualityItem>() });
                var rejectedQuality = quality.Add(new QualityProfile { Name = "Other", ProfileType = side == ProfileType.Audiobook ? ProfileType.Ebook : ProfileType.Audiobook, Items = new List<QualityProfileQualityItem>() });
                var acceptedMetadata = metadata.Add(new MetadataProfile { Name = "Requested", ProfileType = (MetadataProfileType)side });
                var rejectedMetadata = metadata.Add(new MetadataProfile { Name = "Other", ProfileType = side == ProfileType.Audiobook ? MetadataProfileType.Ebook : MetadataProfileType.Audiobook });
                var general = metadata.Add(new MetadataProfile { Name = "Either side", ProfileType = MetadataProfileType.General });
                var validator = new InlineValidator<Selection>();
                validator.RuleFor(selection => selection.Quality).SetValidator(new QualityProfileExistsValidator(quality).ForType(side, value => ((Selection)value).StoredQuality));
                validator.RuleFor(selection => selection.Metadata).SetValidator(new MetadataProfileExistsValidator(metadata).ForType((MetadataProfileType)side, value => ((Selection)value).StoredMetadata));
                Assert.That(validator.Validate(new Selection { Quality = acceptedQuality.Id, Metadata = acceptedMetadata.Id }).IsValid, Is.True);
                Assert.That(validator.Validate(new Selection { Quality = acceptedQuality.Id, Metadata = general.Id }).IsValid, Is.True);
                var failures = validator.Validate(new Selection { Quality = rejectedQuality.Id, Metadata = rejectedMetadata.Id });
                Assert.That(failures.Errors, Has.Count.EqualTo(2));
                Assert.That(failures.Errors, Has.All.Matches<FluentValidation.Results.ValidationFailure>(failure => failure.ErrorMessage.Contains("incompatible")));
                Assert.That(validator.Validate(new Selection { Quality = int.MaxValue, Metadata = int.MaxValue }).Errors, Has.Count.EqualTo(2));
                Assert.That(validator.Validate(new Selection { Quality = rejectedQuality.Id, StoredQuality = rejectedQuality.Id, Metadata = general.Id }).IsValid, Is.True,
                    "An unchanged legacy type mismatch must not block an unrelated save");
                Assert.That(validator.Validate(new Selection { Quality = int.MaxValue, StoredQuality = int.MaxValue, Metadata = general.Id }).IsValid, Is.False,
                    "An unchanged missing profile is still invalid");
                Assert.That(validator.Validate(new Selection { Quality = acceptedQuality.Id, Metadata = rejectedMetadata.Id, StoredMetadata = rejectedMetadata.Id }).IsValid, Is.True,
                    "An unchanged metadata type mismatch must not block an unrelated save");
                Assert.That(validator.Validate(new Selection { Quality = acceptedQuality.Id, Metadata = int.MaxValue, StoredMetadata = int.MaxValue }).IsValid, Is.False,
                    "An unchanged missing metadata profile is still invalid");
            });
        }
    }
}
