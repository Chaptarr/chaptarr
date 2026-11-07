using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using Chaptarr.Http.Middleware;
using Chaptarr.Http.REST;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
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
                    configService: null,
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
            public void Update(QualityProfile profile) => _profiles[_profiles.FindIndex(existing => existing.Id == profile.Id)] = profile;
            public void Delete(int id) => throw new NotImplementedException();
            public void EnsureNotInUse(IEnumerable<int> ids) => throw new NotImplementedException();
            public QualityProfile Get(int id) => _profiles.Single(profile => profile.Id == id);
            public bool Exists(int id) => throw new NotImplementedException();
        }

        private sealed class StubLegacyIdentityMap : IBookshelfLegacyIdentityMapService
        {
            public List<BookshelfLegacyIdentityMapEntry> Mappings { get; } = new();

            public List<BookshelfLegacyIdentityMapEntry> GetMappingsByTargetIds(string entityType, IEnumerable<int> targetIds) =>
                Mappings.Where(mapping => mapping.EntityType == entityType && targetIds.Contains(mapping.TargetId)).ToList();
            public List<BookshelfLegacyIdentityMapEntry> GetMappings(string entityType, IEnumerable<int> sourceIds) => throw new NotImplementedException();
            public List<int> ResolveBookIds(IEnumerable<int> sourceIds, BookMediaType? mediaType = null) => throw new NotImplementedException();
        }

        private sealed class StubBookshelfProfiles : IBookshelfQualityProfileService
        {
            private readonly IQualityProfileService _profiles;
            public StubBookshelfProfiles(IQualityProfileService profiles) => _profiles = profiles;
            public string LastOperation { get; private set; }
            public int LastId { get; private set; }
            public QualityProfile LastProfile { get; private set; }
            public List<BookshelfQualityProfile> All() => _profiles.GetByType(ProfileType.Audiobook)
                .Select(profile => new BookshelfQualityProfile { LegacyId = profile.Id, Profile = profile }).ToList();
            public BookshelfQualityProfile Get(int id) => new()
            {
                LegacyId = id, Profile = LastProfile != null && LastId == id ? LastProfile : _profiles.Get(id)
            };
            public BookshelfQualityProfile Create(QualityProfile profile)
            {
                LastOperation = "create";
                LastId = 7;
                LastProfile = profile;
                return Get(7);
            }

            public BookshelfQualityProfile Update(int id, QualityProfile profile)
            {
                LastOperation = "update";
                LastId = id;
                LastProfile = profile;
                return Get(id);
            }

            public void Delete(int id)
            {
                LastOperation = "delete";
                LastId = id;
            }
        }

        private sealed class TestQualityProfileController : QualityApi.QualityProfileController
        {
            public TestQualityProfileController(IEnumerable<CustomFormat> formats = null, params QualityProfile[] profiles)
                : this(new StubLegacyIdentityMap(), new StubQualityProfileService(formats, profiles), formats)
            {
            }

            private TestQualityProfileController(StubLegacyIdentityMap identityMap, StubQualityProfileService profiles, IEnumerable<CustomFormat> formats)
                : this(identityMap, profiles, new StubBookshelfProfiles(profiles), formats)
            {
            }

            private TestQualityProfileController(StubLegacyIdentityMap identityMap, StubQualityProfileService profiles,
                StubBookshelfProfiles bookshelfProfiles, IEnumerable<CustomFormat> formats)
                : base(profiles, new StubCustomFormatService(formats), identityMap, bookshelfProfiles)
            {
                LegacyMappings = identityMap.Mappings;
                BookshelfService = bookshelfProfiles;
                ControllerContext.HttpContext = new DefaultHttpContext();
                ControllerContext.HttpContext.Request.Method = "POST";
            }

            public List<BookshelfLegacyIdentityMapEntry> LegacyMappings { get; }
            public StubBookshelfProfiles BookshelfService { get; }
            public QualityApi.QualityProfileResource GetResourceForTest(int id) => GetResourceById(id);
            public void ValidateForTest(QualityApi.QualityProfileResource resource) => ValidateResource(resource, skipValidate: true);
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
        public void bookshelf_compatibility_should_return_only_original_profiles_without_filtering_the_legacy_quality_list()
        {
            var ebookQuality = new QualityProfileQualityItem
            {
                Quality = CoreQuality.MOBI,
                Allowed = true
            };
            var controller = new TestQualityProfileController(
                profiles: new[]
                {
                    CreateProfile(1, "Audio default", ProfileType.Audiobook),
                    new QualityProfile
                    {
                        Id = 2,
                        Name = "Ebook override",
                        ProfileType = ProfileType.Audiobook,
                        Cutoff = CoreQuality.MOBI.Id,
                        Items = new List<QualityProfileQualityItem> { ebookQuality },
                        FormatItems = new List<ProfileFormatItem>()
                    },
                    CreateProfile(3, "Ebook default", ProfileType.Ebook)
                });
            controller.ControllerContext.HttpContext.Items[ReadarrFacadeContext.ItemKey] =
                new ReadarrFacadeContext("bookshelf", null, "", isBookshelfCompatibility: true);

            var profiles = controller.GetAll();
            var legacyProfile = controller.GetResourceForTest(2);

            Assert.That(profiles.Select(profile => profile.Id), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(legacyProfile.Items.SelectMany(AllQualityIds), Does.Contain(CoreQuality.MOBI.Id));
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

        [TestCase(ProfileType.Audiobook)]
        [TestCase(ProfileType.Ebook)]
        public void update_should_retain_unchanged_generic_qualities_on_an_imported_profile(ProfileType type)
        {
            var (controller, resource) = ImportedGenericProfile(type);
            resource.Name = "Renamed imported profile";

            Assert.DoesNotThrow(() => controller.ValidateForTest(resource));
            controller.Update(resource);
            var persisted = controller.GetAll().Single(profile => profile.Id == resource.Id);
            Assert.That(persisted.Name, Is.EqualTo(resource.Name));
            Assert.That(AllowedQualityIds(persisted), Is.EqualTo(AllowedQualityIds(resource)));
            Assert.That(persisted.Cutoff, Is.EqualTo(resource.Cutoff));
            Assert.That(persisted.ConvertToQualityId, Is.EqualTo(resource.ConvertToQualityId));
        }

        [Test]
        public void retaining_generic_qualities_requires_a_put_and_an_imported_mapping()
        {
            var (controller, resource) = ImportedGenericProfile(ProfileType.Audiobook);
            controller.ControllerContext.HttpContext.Request.Method = "POST";
            Assert.Throws<ValidationException>(() => controller.ValidateForTest(resource));

            controller.ControllerContext.HttpContext.Request.Method = "PUT";
            controller.LegacyMappings.Clear();
            Assert.Throws<ValidationException>(() => controller.ValidateForTest(resource));
        }

        [Test]
        public void imported_profile_must_not_enable_a_new_incompatible_quality_or_cutoff()
        {
            var (controller, resource) = ImportedGenericProfile(ProfileType.Audiobook);
            resource.Items.Add(new QualityApi.QualityProfileQualityItemResource { Quality = CoreQuality.EPUB, Allowed = true });
            resource.Cutoff = CoreQuality.EPUB.Id;

            var exception = Assert.Throws<ValidationException>(() => controller.ValidateForTest(resource));
            Assert.That(exception.Errors.Select(error => error.PropertyName), Does.Contain("Items"));
            Assert.That(exception.Errors.Select(error => error.PropertyName), Does.Contain("Cutoff"));
        }

        [Test]
        public void imported_profile_must_not_select_a_new_incompatible_conversion_target()
        {
            var (controller, resource) = ImportedGenericProfile(ProfileType.Audiobook);
            resource.ConvertToQualityId = CoreQuality.EPUB.Id;

            var exception = Assert.Throws<ValidationException>(() => controller.ValidateForTest(resource));
            Assert.That(exception.Errors.Select(error => error.PropertyName), Does.Contain("ConvertToQualityId"));
        }

        [Test]
        public void imported_profile_must_not_use_retained_choices_after_changing_its_format()
        {
            var (controller, resource) = ImportedGenericProfile(ProfileType.Audiobook);
            resource.ProfileType = ProfileType.Ebook;

            Assert.Throws<ValidationException>(() => controller.ValidateForTest(resource));
        }

        [Test]
        public void imported_profile_can_remove_incompatible_choices()
        {
            var (controller, resource) = ImportedGenericProfile(ProfileType.Audiobook);
            resource.Items.RemoveAll(item => item.Quality.Id == CoreQuality.PDF.Id);
            resource.Cutoff = CoreQuality.M4B.Id;
            resource.ConvertToQualityId = null;

            Assert.DoesNotThrow(() => controller.ValidateForTest(resource));
        }

        [Test]
        public void imported_mapping_must_match_the_requested_format()
        {
            var (controller, resource) = ImportedGenericProfile(ProfileType.Audiobook);
            controller.LegacyMappings[0].MediaType = 1;

            Assert.Throws<ValidationException>(() => controller.ValidateForTest(resource));
        }

        [Test]
        public void retained_group_cutoff_must_not_hide_new_incompatible_children()
        {
            var (controller, resource) = ImportedGenericProfile(ProfileType.Audiobook, grouped: true);
            resource.Items[0].Items.Add(new QualityApi.QualityProfileQualityItemResource { Quality = CoreQuality.EPUB, Allowed = false });

            var exception = Assert.Throws<ValidationException>(() => controller.ValidateForTest(resource));
            Assert.That(exception.Errors.Select(error => error.PropertyName), Does.Contain("Cutoff"));
        }

        private static (TestQualityProfileController Controller, QualityApi.QualityProfileResource Resource) ImportedGenericProfile(ProfileType type, bool grouped = false)
        {
            var wrongQuality = type == ProfileType.Audiobook ? CoreQuality.PDF : CoreQuality.MP3;
            var validQuality = type == ProfileType.Audiobook ? CoreQuality.M4B : CoreQuality.EPUB;
            var profile = CreateProfile(42, "Imported generic", type);
            profile.Items.Add(new QualityProfileQualityItem { Quality = wrongQuality, Allowed = true, Items = new List<QualityProfileQualityItem>() });
            profile.Items.Add(new QualityProfileQualityItem { Quality = validQuality, Allowed = true, Items = new List<QualityProfileQualityItem>() });
            profile.Cutoff = wrongQuality.Id;
            profile.ConvertToQualityId = wrongQuality.Id;
            if (grouped)
            {
                profile.Items = new List<QualityProfileQualityItem>
                {
                    new() { Id = 1000, Name = "Imported group", Allowed = true, Items = profile.Items }
                };
                profile.Cutoff = 1000;
            }

            var controller = new TestQualityProfileController(profiles: new[] { profile });
            controller.ControllerContext.HttpContext.Request.Method = "PUT";
            controller.LegacyMappings.Add(new BookshelfLegacyIdentityMapEntry
            {
                Source = "BookshelfNG", EntityType = "QualityProfile", SourceId = 7, TargetId = 42,
                MediaType = type == ProfileType.Audiobook ? 0 : 1
            });
            return (controller, QualityApi.ProfileResourceMapper.ToResource(profile));
        }

        [Test]
        public void legacy_create_accepts_the_generic_predecessor_payload_and_returns_its_wire_id()
        {
            var controller = new TestQualityProfileController();
            SetGenericFacade(controller);
            var resource = GenericResource();
            Assert.DoesNotThrow(() => controller.ValidateForTest(resource));
            var result = controller.Create(resource);
            var created = (Microsoft.AspNetCore.Mvc.CreatedAtActionResult)result.Result;
            Assert.That(controller.BookshelfService.LastOperation, Is.EqualTo("create"));
            Assert.That(((QualityApi.QualityProfileResource)created.Value).Id, Is.EqualTo(7));
            Assert.That(created.RouteValues["id"], Is.EqualTo(7));
        }

        [Test]
        public void legacy_update_and_delete_use_the_legacy_identity_service()
        {
            var controller = new TestQualityProfileController();
            SetGenericFacade(controller);
            controller.ControllerContext.HttpContext.Request.Method = "PUT";
            var resource = GenericResource();
            resource.Id = 9;
            controller.ValidateForTest(resource);
            var result = controller.Update(resource);
            var updated = (Microsoft.AspNetCore.Mvc.AcceptedAtActionResult)result.Result;
            Assert.That(controller.BookshelfService.LastOperation, Is.EqualTo("update"));
            Assert.That(((QualityApi.QualityProfileResource)updated.Value).Id, Is.EqualTo(9));
            controller.DeleteProfile(9);
            Assert.That(controller.BookshelfService.LastOperation, Is.EqualTo("delete"));
            Assert.That(controller.BookshelfService.LastId, Is.EqualTo(9));
        }

        [Test]
        public void legacy_validation_retains_the_source_all_qualities_requirement()
        {
            var controller = new TestQualityProfileController();
            SetGenericFacade(controller);
            var resource = GenericResource();
            resource.Items.RemoveAt(0);
            var exception = Assert.Throws<ValidationException>(() => controller.ValidateForTest(resource));
            Assert.That(exception.Errors.Any(error => error.PropertyName == "Items" && error.ErrorMessage == "Must contain all qualities"), Is.True);
        }

        [Test]
        public void generic_legacy_validation_does_not_relax_native_or_format_scoped_requests()
        {
            var controller = new TestQualityProfileController();
            Assert.Throws<ValidationException>(() => controller.ValidateForTest(GenericResource()));
            SetGenericFacade(controller, "ebook");
            Assert.Throws<ValidationException>(() => controller.ValidateForTest(GenericResource()));
        }

        [Test]
        public void generic_profiles_expose_shared_custom_formats_and_preserve_native_specific_formats_outside_the_wire()
        {
            var formats = new[]
            {
                new CustomFormat { Id = 11, Name = "Shared" },
                new CustomFormat { Id = 22, Name = "Native audio", AppliesTo = CustomFormatMediaType.Audiobook }
            };
            var profile = CreateProfile(1, "Imported", ProfileType.Audiobook);
            profile.FormatItems = formats.Select(format => new ProfileFormatItem { Format = format, Score = 1 }).ToList();
            var controller = new TestQualityProfileController(formats, profile);
            SetGenericFacade(controller);
            var resource = GenericResource();
            resource.FormatItems.Add(new QualityApi.ProfileFormatItemResource { Format = 11, Score = 1 });
            Assert.DoesNotThrow(() => controller.ValidateForTest(resource));
            Assert.That(controller.GetAll().Single().FormatItems.Select(item => item.Format), Is.EqualTo(new[] { 11 }));
        }

        [Test]
        public void legacy_custom_format_validation_preserves_the_source_permissive_extra_item_contract()
        {
            var controller = new TestQualityProfileController(new[] { new CustomFormat { Id = 11, Name = "Shared" } });
            SetGenericFacade(controller);
            var resource = GenericResource();
            resource.FormatItems.Add(new QualityApi.ProfileFormatItemResource { Format = 11, Score = 1 });
            resource.FormatItems.Add(new QualityApi.ProfileFormatItemResource { Format = 999, Score = 2 });
            Assert.DoesNotThrow(() => controller.ValidateForTest(resource));
            resource.FormatItems.RemoveAt(0);
            Assert.Throws<ValidationException>(() => controller.ValidateForTest(resource));
        }

        private static void SetGenericFacade(TestQualityProfileController controller, string mediaType = null)
        {
            controller.ControllerContext.HttpContext.Items[ReadarrFacadeContext.ItemKey] =
                new ReadarrFacadeContext("bookshelf", mediaType, "", isBookshelfCompatibility: true);
        }

        private static QualityApi.QualityProfileResource GenericResource() => new()
        {
            Name = "Source generic", Cutoff = CoreQuality.M4B.Id,
            Items = CoreQuality.All.Select(quality => new QualityApi.QualityProfileQualityItemResource { Quality = quality, Allowed = true }).ToList(),
            FormatItems = new List<QualityApi.ProfileFormatItemResource>()
        };

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
