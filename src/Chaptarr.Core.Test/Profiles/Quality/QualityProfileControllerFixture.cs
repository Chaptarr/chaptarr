using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using Chaptarr.Http.REST;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using CoreQuality = NzbDrone.Core.Qualities.Quality;
using QualityApi = Chaptarr.Api.V1.Profiles.Quality;

namespace Chaptarr.Core.Test.Profiles.Quality
{
    [TestFixture]
    public class QualityProfileControllerFixture
    {
        private sealed class StubCustomFormatService : ICustomFormatService
        {
            private readonly List<CustomFormat> _formats;

            public StubCustomFormatService(IEnumerable<CustomFormat> formats = null)
            {
                _formats = formats?.ToList() ?? new List<CustomFormat>();
            }

            public List<CustomFormat> All() => _formats;
            public void Update(CustomFormat customFormat) => throw new NotImplementedException();
            public CustomFormat Insert(CustomFormat customFormat) => throw new NotImplementedException();
            public CustomFormat GetById(int id) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
        }

        private sealed class StubQualityProfileService : IQualityProfileService
        {
            private readonly List<QualityProfile> _profiles;
            private readonly QualityProfileService _defaultProfileService;

            public StubQualityProfileService(params QualityProfile[] profiles)
                : this(null, profiles)
            {
            }

            public StubQualityProfileService(IEnumerable<CustomFormat> formats, params QualityProfile[] profiles)
            {
                _profiles = profiles.ToList();
                _defaultProfileService = new QualityProfileService(
                    profileRepository: null,
                    authorService: null,
                    importListFactory: null,
                    formatService: new StubCustomFormatService(formats),
                    rootFolderService: null,
                    qualityDefinitionService: null,
                    logger: LogManager.GetCurrentClassLogger());
            }

            public ProfileType? LastRequestedType { get; private set; }

            public List<QualityProfile> All()
            {
                return _profiles;
            }

            public List<QualityProfile> GetByType(ProfileType type)
            {
                LastRequestedType = type;
                return _profiles.Where(p => p.ProfileType == type).ToList();
            }

            public QualityProfile GetDefaultProfile(string name, CoreQuality cutoff = null, params CoreQuality[] allowed)
            {
                return _defaultProfileService.GetDefaultProfile(name, cutoff, allowed);
            }

            public QualityProfile Add(QualityProfile profile) => throw new NotImplementedException();
            public void Update(QualityProfile profile) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public QualityProfile Get(int id) => throw new NotImplementedException();
            public bool Exists(int id) => throw new NotImplementedException();
        }

        private sealed class TestQualityProfileController : QualityApi.QualityProfileController
        {
            public TestQualityProfileController(IEnumerable<CustomFormat> formats = null)
                : base(new StubQualityProfileService(), new StubCustomFormatService(formats))
            {
                ControllerContext.HttpContext = new DefaultHttpContext();
                ControllerContext.HttpContext.Request.Method = "POST";
            }

            public void ValidateForTest(QualityApi.QualityProfileResource resource)
            {
                ValidateResource(resource, skipValidate: true);
            }
        }

        [Test]
        public void schema_should_return_legacy_audiobook_default_when_media_type_is_not_provided()
        {
            var controller = new QualityApi.QualityProfileSchemaController(new StubQualityProfileService());

            var resource = controller.GetSchema();

            Assert.That(resource.Name, Is.Empty);
            Assert.That(resource.ProfileType, Is.EqualTo(ProfileType.Audiobook));
            Assert.That(resource.Cutoff, Is.EqualTo(CoreQuality.Unknown.Id));            Assert.That(AllowedQualityIds(resource), Is.EqualTo(new[] { CoreQuality.Unknown.Id }));
            Assert.DoesNotThrow(() => QualityApi.ProfileResourceMapper.ToModel(resource));
        }

        [Test]
        public void schema_should_return_audiobook_default_for_audiobook_media_type()
        {
            var controller = new QualityApi.QualityProfileSchemaController(new StubQualityProfileService());

            var resource = controller.GetSchema("audiobook");

            Assert.That(resource.Name, Is.EqualTo("New Audiobook Profile"));
            Assert.That(resource.ProfileType, Is.EqualTo(ProfileType.Audiobook));
            Assert.That(resource.Cutoff, Is.EqualTo(CoreQuality.M4B.Id));
            Assert.That(QualityIdsInPreferenceOrder(resource), Is.EqualTo(new[]
            {
                CoreQuality.UnknownAudio.Id,
                CoreQuality.FLAC.Id,
                CoreQuality.MP3.Id,
                CoreQuality.M4B.Id
            }));
            Assert.That(AllowedQualityIds(resource), Is.EquivalentTo(new[]
            {
                CoreQuality.UnknownAudio.Id,
                CoreQuality.FLAC.Id,
                CoreQuality.MP3.Id,
                CoreQuality.M4B.Id
            }));
            Assert.That(AllQualityIds(resource), Is.EquivalentTo(new[]
            {
                CoreQuality.UnknownAudio.Id,
                CoreQuality.FLAC.Id,
                CoreQuality.MP3.Id,
                CoreQuality.M4B.Id
            }));
        }

        [Test]
        public void schema_should_default_only_narrator_match_for_new_audiobook_profiles()
        {
            var formats = BuiltInCustomFormats.All().Select((format, index) =>
            {
                format.Id = index + 1;
                return format;
            }).ToList();
            var controller = new QualityApi.QualityProfileSchemaController(new StubQualityProfileService(formats));

            var resource = controller.GetSchema("audiobook");
            var ebookResource = controller.GetSchema("ebook");

            Assert.Multiple(() =>
            {
                Assert.That(resource.PreferCustomFormatsOverQuality, Is.True);
                Assert.That(ebookResource.PreferCustomFormatsOverQuality, Is.False);
                Assert.That(resource.FormatItems.Single(item => item.BuiltInKey == BuiltInCustomFormats.PreferredNarratorKey).Score,
                    Is.EqualTo(BuiltInCustomFormats.PreferredNarratorDefaultAudiobookScore));
                Assert.That(resource.FormatItems.Single(item => item.BuiltInKey == BuiltInCustomFormats.DramatizedAudioKey).Score, Is.Zero);
                Assert.That(resource.FormatItems.Select(item => item.BuiltInKey), Is.EquivalentTo(new[]
                {
                    BuiltInCustomFormats.DramatizedAudioKey,
                    BuiltInCustomFormats.PreferredNarratorKey
                }));
                Assert.That(ebookResource.FormatItems, Is.Empty);
            });
        }

        [Test]
        public void schema_should_return_ebook_default_for_ebook_media_type()
        {
            var controller = new QualityApi.QualityProfileSchemaController(new StubQualityProfileService());

            var resource = controller.GetSchema("ebook");

            Assert.That(resource.Name, Is.EqualTo("New E-Book Profile"));
            Assert.That(resource.ProfileType, Is.EqualTo(ProfileType.Ebook));
            Assert.That(resource.Cutoff, Is.EqualTo(CoreQuality.MOBI.Id));
            Assert.That(QualityIdsInPreferenceOrder(resource), Is.EqualTo(new[]
            {
                CoreQuality.Unknown.Id,
                CoreQuality.PDF.Id,
                CoreQuality.MOBI.Id,
                CoreQuality.EPUB.Id,
                CoreQuality.AZW3.Id
            }));
            Assert.That(AllowedQualityIds(resource), Is.EquivalentTo(new[]
            {
                CoreQuality.MOBI.Id,
                CoreQuality.EPUB.Id,
                CoreQuality.AZW3.Id
            }));
            Assert.That(AllQualityIds(resource), Is.EquivalentTo(new[]
            {
                CoreQuality.Unknown.Id,
                CoreQuality.PDF.Id,
                CoreQuality.MOBI.Id,
                CoreQuality.EPUB.Id,
                CoreQuality.AZW3.Id
            }));
        }

        [Test]
        public void schema_should_reject_unknown_media_type()
        {
            var controller = new QualityApi.QualityProfileSchemaController(new StubQualityProfileService());

            var exception = Assert.Throws<BadRequestException>(() => controller.GetSchema("audio"));

            Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(exception.Content.ToString(), Does.Contain("mediaType"));
        }

        [Test]
        public void list_should_return_all_profiles_when_media_type_is_not_provided()
        {
            var service = new StubQualityProfileService(
                CreateProfile(1, "Audio", ProfileType.Audiobook),
                CreateProfile(2, "Text", ProfileType.Ebook));
            var controller = new QualityApi.QualityProfileController(service, new StubCustomFormatService());

            var resources = controller.GetAll();

            Assert.That(resources.Select(p => p.Id), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(service.LastRequestedType, Is.Null);
        }

        [Test]
        public void list_should_return_only_audiobook_profiles_for_audiobook_media_type()
        {
            var service = new StubQualityProfileService(
                CreateProfile(1, "Audio A", ProfileType.Audiobook),
                CreateProfile(2, "Text", ProfileType.Ebook),
                CreateProfile(3, "Audio B", ProfileType.Audiobook));
            var controller = new QualityApi.QualityProfileController(service, new StubCustomFormatService());

            var resources = controller.GetAll("audiobook");

            Assert.That(resources.Select(p => p.Id), Is.EqualTo(new[] { 1, 3 }));
            Assert.That(resources.All(p => p.ProfileType == ProfileType.Audiobook), Is.True);
            Assert.That(service.LastRequestedType, Is.EqualTo(ProfileType.Audiobook));
        }

        [Test]
        public void list_should_return_only_ebook_profiles_for_ebook_media_type()
        {
            var service = new StubQualityProfileService(
                CreateProfile(1, "Audio", ProfileType.Audiobook),
                CreateProfile(2, "Text A", ProfileType.Ebook),
                CreateProfile(3, "Text B", ProfileType.Ebook));
            var controller = new QualityApi.QualityProfileController(service, new StubCustomFormatService());

            var resources = controller.GetAll("ebook");

            Assert.That(resources.Select(p => p.Id), Is.EqualTo(new[] { 2, 3 }));
            Assert.That(resources.All(p => p.ProfileType == ProfileType.Ebook), Is.True);
            Assert.That(service.LastRequestedType, Is.EqualTo(ProfileType.Ebook));
        }

        [Test]
        public void list_should_reject_unknown_media_type()
        {
            var controller = new QualityApi.QualityProfileController(new StubQualityProfileService(), new StubCustomFormatService());

            var exception = Assert.Throws<BadRequestException>(() => controller.GetAll("audio"));

            Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(exception.Content.ToString(), Does.Contain("mediaType"));
        }

        [Test]
        public void resource_should_clear_conversion_when_convert_to_quality_is_explicit_zero()
        {
            var resource = CreateResource(ProfileType.Audiobook);
            resource.ConvertMp3ToM4b = true;
            resource.ConvertToQualityId = 0;

            var model = QualityApi.ProfileResourceMapper.ToModel(resource);

            Assert.That(model.ConvertToQualityId, Is.Null);
            Assert.That(model.ConvertMp3ToM4b, Is.False);
        }

        [Test]
        public void resource_should_map_legacy_conversion_flag_when_convert_to_quality_is_omitted()
        {
            var resource = CreateResource(ProfileType.Audiobook);
            resource.ConvertMp3ToM4b = true;
            resource.ConvertToQualityId = null;

            var model = QualityApi.ProfileResourceMapper.ToModel(resource);

            Assert.That(model.ConvertToQualityId, Is.EqualTo(CoreQuality.M4B.Id));
            Assert.That(model.ConvertMp3ToM4b, Is.True);
        }

        [Test]
        public void resource_should_set_legacy_conversion_flag_for_m4b_target()
        {
            var resource = CreateResource(ProfileType.Audiobook);
            resource.ConvertMp3ToM4b = false;
            resource.ConvertToQualityId = CoreQuality.M4B.Id;

            var model = QualityApi.ProfileResourceMapper.ToModel(resource);

            Assert.That(model.ConvertToQualityId, Is.EqualTo(CoreQuality.M4B.Id));
            Assert.That(model.ConvertMp3ToM4b, Is.True);
        }

        [Test]
        public void resource_should_keep_non_m4b_target_without_legacy_conversion_flag()
        {
            var resource = CreateResource(ProfileType.Audiobook);
            resource.ConvertMp3ToM4b = true;
            resource.ConvertToQualityId = CoreQuality.FLAC.Id;

            var model = QualityApi.ProfileResourceMapper.ToModel(resource);

            Assert.That(model.ConvertToQualityId, Is.EqualTo(CoreQuality.FLAC.Id));
            Assert.That(model.ConvertMp3ToM4b, Is.False);
        }

        [Test]
        public void resource_should_round_trip_audiobook_preference_priority_and_disable_it_for_ebooks()
        {
            var audiobookResource = CreateResource(ProfileType.Audiobook);
            audiobookResource.PreferCustomFormatsOverQuality = true;

            var audiobookModel = QualityApi.ProfileResourceMapper.ToModel(audiobookResource);
            var audiobookRoundTrip = QualityApi.ProfileResourceMapper.ToResource(audiobookModel);

            var ebookResource = CreateResource(ProfileType.Ebook);
            ebookResource.PreferCustomFormatsOverQuality = true;

            var ebookModel = QualityApi.ProfileResourceMapper.ToModel(ebookResource);
            var ebookRoundTrip = QualityApi.ProfileResourceMapper.ToResource(ebookModel);

            Assert.Multiple(() =>
            {
                Assert.That(audiobookModel.PreferCustomFormatsOverQuality, Is.True);
                Assert.That(audiobookRoundTrip.PreferCustomFormatsOverQuality, Is.True);
                Assert.That(ebookModel.PreferCustomFormatsOverQuality, Is.False);
                Assert.That(ebookRoundTrip.PreferCustomFormatsOverQuality, Is.False);
            });
        }

        [Test]
        public void resource_should_reject_invalid_profile_type()
        {
            var resource = CreateResource((ProfileType)99);

            var exception = Assert.Throws<BadRequestException>(() => QualityApi.ProfileResourceMapper.ToModel(resource));

            Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(exception.Content.ToString(), Does.Contain("Profile type"));
        }

        [Test]
        public void validator_should_reject_enabled_ebook_quality_on_audiobook_profile()
        {
            var controller = new TestQualityProfileController();
            var resource = CreateResource(ProfileType.Audiobook);
            resource.Items.Add(new QualityApi.QualityProfileQualityItemResource
            {
                Quality = CoreQuality.EPUB,
                Allowed = true
            });

            var exception = Assert.Throws<ValidationException>(() => controller.ValidateForTest(resource));

            Assert.That(exception.Errors.Select(error => error.PropertyName), Does.Contain("Items"));
        }

        [Test]
        public void validator_should_allow_disabled_wrong_type_quality()
        {
            var controller = new TestQualityProfileController();
            var resource = CreateResource(ProfileType.Audiobook);
            resource.Items.Add(new QualityApi.QualityProfileQualityItemResource
            {
                Quality = CoreQuality.EPUB,
                Allowed = false
            });

            Assert.DoesNotThrow(() => controller.ValidateForTest(resource));
        }

        [Test]
        public void validator_should_allow_unknown_text_quality_on_ebook_profile()
        {
            var controller = new TestQualityProfileController();
            var resource = new QualityApi.QualityProfileResource
            {
                Id = 1,
                Name = "Text",
                ProfileType = ProfileType.Ebook,
                Cutoff = CoreQuality.Unknown.Id,
                Items = new List<QualityApi.QualityProfileQualityItemResource>
                {
                    new()
                    {
                        Quality = CoreQuality.Unknown,
                        Allowed = true
                    }
                },
                FormatItems = new List<QualityApi.ProfileFormatItemResource>()
            };

            Assert.DoesNotThrow(() => controller.ValidateForTest(resource));
        }

        [Test]
        public void validator_should_reject_wrong_type_cutoff()
        {
            var controller = new TestQualityProfileController();
            var resource = CreateResource(ProfileType.Audiobook);
            resource.Cutoff = CoreQuality.EPUB.Id;
            resource.Items.Add(new QualityApi.QualityProfileQualityItemResource
            {
                Quality = CoreQuality.EPUB,
                Allowed = true
            });

            var exception = Assert.Throws<ValidationException>(() => controller.ValidateForTest(resource));

            Assert.That(exception.Errors.Select(error => error.PropertyName), Does.Contain("Cutoff"));
        }

        [Test]
        public void validator_should_reject_group_cutoff_when_group_contains_wrong_type_quality()
        {
            var controller = new TestQualityProfileController();
            var resource = CreateResource(ProfileType.Audiobook);
            resource.Cutoff = 1000;
            resource.Items = new List<QualityApi.QualityProfileQualityItemResource>
            {
                new()
                {
                    Id = 1000,
                    Name = "Mixed",
                    Allowed = true,
                    Items = new List<QualityApi.QualityProfileQualityItemResource>
                    {
                        new()
                        {
                            Quality = CoreQuality.M4B,
                            Allowed = true
                        },
                        new()
                        {
                            Quality = CoreQuality.EPUB,
                            Allowed = false
                        }
                    }
                }
            };

            var exception = Assert.Throws<ValidationException>(() => controller.ValidateForTest(resource));

            Assert.That(exception.Errors.Select(error => error.PropertyName), Does.Contain("Cutoff"));
            Assert.That(exception.Errors.Select(error => error.PropertyName), Does.Not.Contain("Items"));
        }

        [Test]
        public void to_resource_should_preserve_surviving_group_cutoff_when_filtering_by_profile_type()
        {
            var profile = CreateProfile(1, "Audio", ProfileType.Audiobook);
            profile.Cutoff = 1001;
            profile.Items = new List<QualityProfileQualityItem>
            {
                new()
                {
                    Quality = CoreQuality.MP3,
                    Allowed = true
                },
                new()
                {
                    Id = 1001,
                    Name = "Lossless",
                    Allowed = true,
                    Items = new List<QualityProfileQualityItem>
                    {
                        new()
                        {
                            Quality = CoreQuality.M4B,
                            Allowed = true
                        },
                        new()
                        {
                            Quality = CoreQuality.MP3,
                            Allowed = true
                        },
                        new()
                        {
                            Quality = CoreQuality.EPUB,
                            Allowed = false
                        }
                    }
                }
            };

            var resource = QualityApi.ProfileResourceMapper.ToResource(profile, filterToProfileType: true);

            Assert.That(resource.Cutoff, Is.EqualTo(1001));
            Assert.That(AllQualityIds(resource), Does.Not.Contain(CoreQuality.EPUB.Id));
        }

        [Test]
        public void validator_should_reject_wrong_type_conversion_target()
        {
            var controller = new TestQualityProfileController();
            var resource = CreateResource(ProfileType.Audiobook);
            resource.ConvertToQualityId = CoreQuality.EPUB.Id;

            var exception = Assert.Throws<ValidationException>(() => controller.ValidateForTest(resource));

            Assert.That(exception.Errors.Select(error => error.PropertyName), Does.Contain("ConvertToQualityId"));
        }

        [Test]
        public void resource_mapper_should_only_expose_custom_formats_compatible_with_the_profile_type()
        {
            var audiobookOnly = new CustomFormat
            {
                Id = 10,
                Name = "Audiobook",
                AppliesTo = CustomFormatMediaType.Audiobook
            };
            var ebookOnly = new CustomFormat
            {
                Id = 11,
                Name = "eBook",
                AppliesTo = CustomFormatMediaType.Ebook
            };
            var both = new CustomFormat
            {
                Id = 12,
                Name = "Both",
                AppliesTo = CustomFormatMediaType.Both
            };
            var profile = CreateProfile(1, "Audio", ProfileType.Audiobook);
            profile.FormatItems = new List<ProfileFormatItem>
            {
                new() { Format = audiobookOnly, Score = 50 },
                new() { Format = ebookOnly, Score = 100 },
                new() { Format = both, Score = 25 }
            };

            var resource = QualityApi.ProfileResourceMapper.ToResource(profile);

            Assert.That(resource.FormatItems.Select(item => item.Format), Is.EquivalentTo(new[] { 10, 12 }));

            profile.ProfileType = ProfileType.Ebook;
            resource = QualityApi.ProfileResourceMapper.ToResource(profile);

            Assert.That(resource.FormatItems.Select(item => item.Format), Is.EquivalentTo(new[] { 11, 12 }));
        }

        [Test]
        public void validator_should_require_every_compatible_custom_format_and_reject_incompatible_ones()
        {
            var formats = new[]
            {
                new CustomFormat
                {
                    Id = 10,
                    Name = "Audiobook",
                    AppliesTo = CustomFormatMediaType.Audiobook
                },
                new CustomFormat
                {
                    Id = 11,
                    Name = "eBook",
                    AppliesTo = CustomFormatMediaType.Ebook
                },
                new CustomFormat
                {
                    Id = 12,
                    Name = "Both",
                    AppliesTo = CustomFormatMediaType.Both
                }
            };
            var controller = new TestQualityProfileController(formats);
            var resource = CreateResource(ProfileType.Audiobook);
            resource.FormatItems = new List<QualityApi.ProfileFormatItemResource>
            {
                new() { Format = 10, Score = 50 },
                new() { Format = 12, Score = 25 }
            };

            Assert.DoesNotThrow(() => controller.ValidateForTest(resource));

            resource.FormatItems.Add(new QualityApi.ProfileFormatItemResource { Format = 11, Score = 100 });
            var incompatible = Assert.Throws<ValidationException>(() => controller.ValidateForTest(resource));

            Assert.That(incompatible.Errors.Select(error => error.PropertyName), Does.Contain("FormatItems"));

            resource.FormatItems = new List<QualityApi.ProfileFormatItemResource>
            {
                new() { Format = 10, Score = 50 }
            };
            var missing = Assert.Throws<ValidationException>(() => controller.ValidateForTest(resource));

            Assert.That(missing.Errors.Select(error => error.PropertyName), Does.Contain("FormatItems"));
        }

        private static QualityProfile CreateProfile(int id, string name, ProfileType type)
        {
            return new QualityProfile
            {
                Id = id,
                Name = name,
                ProfileType = type,
                Items = new List<QualityProfileQualityItem>(),
                FormatItems = new List<ProfileFormatItem>()
            };
        }

        private static QualityApi.QualityProfileResource CreateResource(ProfileType type)
        {
            return new QualityApi.QualityProfileResource
            {
                Id = 1,
                Name = "Audio",
                ProfileType = type,
                Cutoff = CoreQuality.M4B.Id,
                Items = new List<QualityApi.QualityProfileQualityItemResource>
                {
                    new()
                    {
                        Quality = CoreQuality.M4B,
                        Allowed = true
                    }
                },
                FormatItems = new List<QualityApi.ProfileFormatItemResource>()
            };
        }

        private static List<int> QualityIdsInPreferenceOrder(QualityApi.QualityProfileResource resource)
        {
            return resource.Items.SelectMany(AllQualityIds).ToList();
        }

        private static List<int> AllowedQualityIds(QualityApi.QualityProfileResource resource)
        {
            return resource.Items.SelectMany(AllowedQualityIds).OrderBy(id => id).ToList();
        }

        private static List<int> AllQualityIds(QualityApi.QualityProfileResource resource)
        {
            return resource.Items.SelectMany(AllQualityIds).OrderBy(id => id).ToList();
        }

        private static IEnumerable<int> AllowedQualityIds(QualityApi.QualityProfileQualityItemResource item)
        {
            if (item.Quality != null && item.Allowed)
            {
                yield return item.Quality.Id;
            }

            foreach (var child in item.Items ?? new List<QualityApi.QualityProfileQualityItemResource>())
            {
                foreach (var id in AllowedQualityIds(child))
                {
                    yield return id;
                }
            }
        }

        private static IEnumerable<int> AllQualityIds(QualityApi.QualityProfileQualityItemResource item)
        {
            if (item.Quality != null)
            {
                yield return item.Quality.Id;
            }

            foreach (var child in item.Items ?? new List<QualityApi.QualityProfileQualityItemResource>())
            {
                foreach (var id in AllQualityIds(child))
                {
                    yield return id;
                }
            }
        }
    }
}
