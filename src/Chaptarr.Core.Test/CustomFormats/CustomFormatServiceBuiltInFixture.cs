using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Expressions;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.CustomFormats.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.CustomFormats
{
    [TestFixture]
    public class CustomFormatServiceBuiltInFixture
    {
        [Test]
        public void should_not_recreate_deleted_built_ins_after_key_was_seeded()
        {
            var repository = new InMemoryCustomFormatRepository();
            var config = ConfigServiceTestProxy.Create();
            var configProxy = (ConfigServiceTestProxy)config;
            configProxy.SeededBuiltInCustomFormatKeys = string.Join(",",
                BuiltInCustomFormats.DramatizedAudioKey,
                BuiltInCustomFormats.StandardAudioKey,
                BuiltInCustomFormats.PreferredNarratorKey,
                BuiltInCustomFormats.PreferredNarratorMajorityKey,
                BuiltInCustomFormats.CompletePreferredCastKey);

            var events = new CapturingEventAggregator();
            var service = CreateCustomFormatService(repository, config, events);

            var formats = service.All();

            Assert.That(formats, Is.Empty);
            Assert.That(events.Published.OfType<CustomFormatAddedEvent>(), Is.Empty);
        }

        [Test]
        public void should_seed_preferred_narrator_once_without_reviving_legacy_deleted_built_ins()
        {
            var repository = new InMemoryCustomFormatRepository();
            var config = ConfigServiceTestProxy.Create();
            var configProxy = (ConfigServiceTestProxy)config;
            configProxy.AudioProductionCustomFormatsSeeded = true;

            var events = new CapturingEventAggregator();
            var service = CreateCustomFormatService(repository, config, events);

            var formats = service.All();

            Assert.That(formats.Select(format => format.BuiltInKey), Is.EquivalentTo(new[]
            {
                BuiltInCustomFormats.PreferredNarratorKey
            }));
            Assert.That(configProxy.SeededBuiltInCustomFormatKeys.Split(','),
                Is.EquivalentTo(new[]
                {
                    BuiltInCustomFormats.DramatizedAudioKey,
                    BuiltInCustomFormats.StandardAudioKey,
                    BuiltInCustomFormats.PreferredNarratorKey
                }));

            var added = events.Published.OfType<CustomFormatAddedEvent>().ToList();
            Assert.That(added, Has.Count.EqualTo(1));
            Assert.That(added.Single().AudiobookProfileScore,
                Is.EqualTo(BuiltInCustomFormats.PreferredNarratorDefaultAudiobookScore));
        }

        [Test]
        public void should_rename_only_unmodified_legacy_narrator_match_and_adopt_known_retired_formats()
        {
            var repository = new InMemoryCustomFormatRepository();
            repository.Insert(new CustomFormat
            {
                Name = BuiltInCustomFormats.LegacyPreferredNarratorName,
                BuiltInKey = BuiltInCustomFormats.PreferredNarratorKey,
                Specifications = new List<ICustomFormatSpecification>
                {
                    new PreferredNarratorSpecification { Name = BuiltInCustomFormats.LegacyPreferredNarratorName }
                }
            });
            repository.Insert(new CustomFormat
            {
                Name = "My Pinned Cast Rule",
                BuiltInKey = BuiltInCustomFormats.PreferredNarratorMajorityKey,
                Specifications = new List<ICustomFormatSpecification>
                {
                    new PreferredNarratorMajoritySpecification { Name = "My Pinned Cast Rule" }
                }
            });
            repository.Insert(new CustomFormat
            {
                Name = BuiltInCustomFormats.CompletePreferredCastName,
                Specifications = new List<ICustomFormatSpecification>
                {
                    new PreferredNarratorCompleteSpecification { Name = BuiltInCustomFormats.CompletePreferredCastName }
                }
            });

            var config = ConfigServiceTestProxy.Create();
            ((ConfigServiceTestProxy)config).SeededBuiltInCustomFormatKeys = string.Join(",",
                BuiltInCustomFormats.DramatizedAudioKey,
                BuiltInCustomFormats.StandardAudioKey,
                BuiltInCustomFormats.PreferredNarratorKey,
                BuiltInCustomFormats.PreferredNarratorMajorityKey,
                BuiltInCustomFormats.CompletePreferredCastKey);

            var formats = CreateCustomFormatService(repository, config, new CapturingEventAggregator()).All();
            var migrated = formats.Single(format => format.BuiltInKey == BuiltInCustomFormats.PreferredNarratorKey);
            var customized = formats.Single(format => format.BuiltInKey == BuiltInCustomFormats.PreferredNarratorMajorityKey);
            var migratedUnkeyed = formats.Single(format => format.BuiltInKey == BuiltInCustomFormats.CompletePreferredCastKey);

            Assert.That(migrated.Name, Is.EqualTo(BuiltInCustomFormats.PreferredNarratorName));
            Assert.That(migrated.Specifications.Single().Name, Is.EqualTo(BuiltInCustomFormats.PreferredNarratorName));
            Assert.That(customized.Name, Is.EqualTo("My Pinned Cast Rule"));
            Assert.That(customized.Specifications.Single().Name, Is.EqualTo("My Pinned Cast Rule"));
            Assert.That(migratedUnkeyed.Name, Is.EqualTo(BuiltInCustomFormats.CompletePreferredCastName));
            Assert.That(migratedUnkeyed.Specifications.Single().Name, Is.EqualTo(BuiltInCustomFormats.CompletePreferredCastName));
        }
        [TestCase(BuiltInCustomFormats.LegacyPreferredNarratorName)]
        [TestCase(BuiltInCustomFormats.InterimPreferredNarratorName)]
        [TestCase(BuiltInCustomFormats.InterimNarratorMatchName)]
        public void should_migrate_every_known_deployed_or_interim_narrator_label_directly_to_the_final_name(string legacyName)
        {
            var repository = new InMemoryCustomFormatRepository();
            repository.Insert(new CustomFormat
            {
                Name = legacyName,
                BuiltInKey = BuiltInCustomFormats.PreferredNarratorKey,
                Specifications = new List<ICustomFormatSpecification>
                {
                    new PreferredNarratorSpecification { Name = legacyName }
                }
            });

            var config = ConfigServiceTestProxy.Create();
            ((ConfigServiceTestProxy)config).SeededBuiltInCustomFormatKeys = string.Join(",",
                BuiltInCustomFormats.DramatizedAudioKey,
                BuiltInCustomFormats.StandardAudioKey,
                BuiltInCustomFormats.PreferredNarratorKey,
                BuiltInCustomFormats.PreferredNarratorMajorityKey,
                BuiltInCustomFormats.CompletePreferredCastKey);

            var migrated = CreateCustomFormatService(repository, config, new CapturingEventAggregator()).All().Single();

            Assert.That(migrated.Name, Is.EqualTo(BuiltInCustomFormats.PreferredNarratorName));
            Assert.That(migrated.Specifications.Single().Name, Is.EqualTo(BuiltInCustomFormats.PreferredNarratorName));
        }

        [Test]
        public void should_preserve_a_user_renamed_selected_narrator_format()
        {
            var repository = new InMemoryCustomFormatRepository();
            repository.Insert(new CustomFormat
            {
                Name = "My Selected Narrator Rule",
                BuiltInKey = BuiltInCustomFormats.PreferredNarratorKey,
                Specifications = new List<ICustomFormatSpecification>
                {
                    new PreferredNarratorSpecification { Name = "My Selected Narrator Rule" }
                }
            });

            var config = ConfigServiceTestProxy.Create();
            ((ConfigServiceTestProxy)config).SeededBuiltInCustomFormatKeys = string.Join(",",
                BuiltInCustomFormats.DramatizedAudioKey,
                BuiltInCustomFormats.StandardAudioKey,
                BuiltInCustomFormats.PreferredNarratorKey,
                BuiltInCustomFormats.PreferredNarratorMajorityKey,
                BuiltInCustomFormats.CompletePreferredCastKey);

            var preserved = CreateCustomFormatService(repository, config, new CapturingEventAggregator()).All().Single();

            Assert.That(preserved.Name, Is.EqualTo("My Selected Narrator Rule"));
            Assert.That(preserved.Specifications.Single().Name, Is.EqualTo("My Selected Narrator Rule"));
        }

        [Test]
        public void startup_should_seed_audiobook_qualities_in_default_preference_order()
        {
            var repository = new InMemoryProfileRepository();
            var service = CreateQualityProfileService(
                repository,
                new InMemoryCustomFormatService(Array.Empty<CustomFormat>()));

            service.Handle(new ApplicationStartedEvent());

            var profile = repository.All().Single(item => item.ProfileType == ProfileType.Audiobook);
            var expectedOrder = new[]
            {
                Quality.UnknownAudio.Id,
                Quality.FLAC.Id,
                Quality.MP3.Id,
                Quality.M4B.Id
            };
            var audioDefinitions = Quality.DefaultQualityDefinitions
                .Where(item => expectedOrder.Contains(item.Quality.Id))
                .ToList();

            Assert.Multiple(() =>
            {
                Assert.That(profile.Items.TakeLast(expectedOrder.Length).Select(item => item.Quality.Id), Is.EqualTo(expectedOrder));
                Assert.That(profile.Items.Where(item => item.Allowed).Select(item => item.Quality.Id), Is.EqualTo(expectedOrder));
                Assert.That(profile.Cutoff, Is.EqualTo(Quality.M4B.Id));
                Assert.That(audioDefinitions.OrderBy(item => item.Weight).Select(item => item.Quality.Id), Is.EqualTo(expectedOrder));
                Assert.That(audioDefinitions.OrderBy(item => item.GroupWeight).Select(item => item.Quality.Id), Is.EqualTo(expectedOrder));
            });
        }

        [Test]
        public void startup_should_preserve_an_existing_profiles_quality_order()
        {
            var expectedOrder = new[]
            {
                Quality.M4B,
                Quality.UnknownAudio,
                Quality.MP3,
                Quality.FLAC
            };
            var profile = new QualityProfile
            {
                Id = 42,
                Name = "My custom order",
                ProfileType = ProfileType.Audiobook,
                Cutoff = Quality.MP3.Id,
                Items = expectedOrder.Select(quality => new QualityProfileQualityItem
                {
                    Quality = quality,
                    Allowed = true
                }).ToList(),
                FormatItems = new List<ProfileFormatItem>()
            };
            var repository = new InMemoryProfileRepository(profile);
            var service = CreateQualityProfileService(
                repository,
                new InMemoryCustomFormatService(Array.Empty<CustomFormat>()));

            service.Handle(new ApplicationStartedEvent());

            var stored = repository.Get(profile.Id);

            Assert.Multiple(() =>
            {
                Assert.That(stored, Is.SameAs(profile));
                Assert.That(stored.Items.Select(item => item.Quality.Id), Is.EqualTo(expectedOrder.Select(quality => quality.Id)));
                Assert.That(stored.Cutoff, Is.EqualTo(Quality.MP3.Id));
            });
        }

        [Test]
        public void startup_should_delete_untouched_retired_formats_and_their_profile_rows()
        {
            var formats = CreateActiveAndRetiredFormats();
            var audiobook = CreateProfile(1, ProfileType.Audiobook, formats);
            var ebook = CreateProfile(2, ProfileType.Ebook, formats);
            var profileRepository = new InMemoryProfileRepository(audiobook, ebook);
            var formatService = new InMemoryCustomFormatService(formats);
            var service = CreateQualityProfileService(profileRepository, formatService);
            formatService.OnDelete = format => service.Handle(new CustomFormatDeletedEvent(format));

            service.Handle(new ApplicationStartedEvent());

            Assert.Multiple(() =>
            {
                Assert.That(formatService.All().Select(format => format.BuiltInKey), Is.EquivalentTo(new[]
                {
                    BuiltInCustomFormats.DramatizedAudioKey,
                    BuiltInCustomFormats.PreferredNarratorKey
                }));
                Assert.That(audiobook.FormatItems.Select(item => item.Format.BuiltInKey), Is.EquivalentTo(new[]
                {
                    BuiltInCustomFormats.DramatizedAudioKey,
                    BuiltInCustomFormats.PreferredNarratorKey
                }));
                Assert.That(ebook.FormatItems, Is.Empty);
            });
        }

        [Test]
        public void startup_should_preserve_customized_retired_formats_as_ordinary_formats()
        {
            var formats = CreateActiveAndRetiredFormats();
            var standard = formats.Single(format => format.BuiltInKey == BuiltInCustomFormats.StandardAudioKey);
            var majority = formats.Single(format => format.BuiltInKey == BuiltInCustomFormats.PreferredNarratorMajorityKey);
            var complete = formats.Single(format => format.BuiltInKey == BuiltInCustomFormats.CompletePreferredCastKey);
            standard.IncludeCustomFormatWhenRenaming = true;

            var audiobook = CreateProfile(1, ProfileType.Audiobook, formats);
            audiobook.FormatItems.Single(item => item.Format == majority).Score = 99;
            var profileRepository = new InMemoryProfileRepository(audiobook);
            var formatService = new InMemoryCustomFormatService(formats);
            var service = CreateQualityProfileService(profileRepository, formatService);
            formatService.OnDelete = format => service.Handle(new CustomFormatDeletedEvent(format));

            service.Handle(new ApplicationStartedEvent());

            Assert.Multiple(() =>
            {
                Assert.That(formatService.All(), Does.Contain(standard));
                Assert.That(formatService.All(), Does.Contain(majority));
                Assert.That(formatService.All(), Does.Not.Contain(complete));
                Assert.That(standard.BuiltInKey, Is.Null);
                Assert.That(majority.BuiltInKey, Is.Null);
                Assert.That(audiobook.FormatItems.Single(item => item.Format == majority).Score, Is.EqualTo(99));
                Assert.That(audiobook.FormatItems.Select(item => item.Format), Does.Contain(standard));
                Assert.That(audiobook.FormatItems.Select(item => item.Format), Does.Not.Contain(complete));
            });
        }

        [Test]
        public void preferred_narrator_seed_score_should_apply_to_audiobook_profiles_only()
        {
            var format = new CustomFormat
            {
                Id = 10,
                Name = BuiltInCustomFormats.PreferredNarratorName,
                BuiltInKey = BuiltInCustomFormats.PreferredNarratorKey,
                AppliesTo = CustomFormatMediaType.Audiobook
            };

            var audiobook = new QualityProfile
            {
                Id = 1,
                Name = "Audiobook",
                ProfileType = ProfileType.Audiobook,
                FormatItems = new List<ProfileFormatItem>(),
                Items = new List<QualityProfileQualityItem>()
            };

            var ebook = new QualityProfile
            {
                Id = 2,
                Name = "Ebook",
                ProfileType = ProfileType.Ebook,
                FormatItems = new List<ProfileFormatItem>(),
                Items = new List<QualityProfileQualityItem>()
            };

            var repository = new InMemoryProfileRepository(audiobook, ebook);
            var service = new QualityProfileService(
                repository,
                authorService: null,
                importListFactory: null,
                formatService: null,
                rootFolderService: null,
                qualityDefinitionService: null,
                logger: LogManager.GetCurrentClassLogger());

            service.Handle(new CustomFormatAddedEvent(format, BuiltInCustomFormats.PreferredNarratorDefaultAudiobookScore));

            Assert.That(audiobook.FormatItems.Single().Score, Is.EqualTo(BuiltInCustomFormats.PreferredNarratorDefaultAudiobookScore));
            Assert.That(audiobook.FormatItems.Single().Format, Is.SameAs(format));
            Assert.That(ebook.FormatItems, Is.Empty);
        }

        [Test]
        public void changing_media_scope_should_remove_incompatible_profiles_and_add_newly_compatible_profiles_at_zero()
        {
            var format = new CustomFormat
            {
                Id = 20,
                Name = "Shared preference",
                AppliesTo = CustomFormatMediaType.Both
            };
            var audiobook = new QualityProfile
            {
                Id = 1,
                ProfileType = ProfileType.Audiobook,
                Items = new List<QualityProfileQualityItem>(),
                FormatItems = new List<ProfileFormatItem>
                {
                    new() { Format = format, Score = 75 }
                }
            };
            var ebook = new QualityProfile
            {
                Id = 2,
                ProfileType = ProfileType.Ebook,
                Items = new List<QualityProfileQualityItem>(),
                FormatItems = new List<ProfileFormatItem>
                {
                    new() { Format = format, Score = -25 }
                }
            };
            var formatService = new InMemoryCustomFormatService(new[] { format });
            var service = CreateQualityProfileService(new InMemoryProfileRepository(audiobook, ebook), formatService);

            format.AppliesTo = CustomFormatMediaType.Audiobook;
            service.Handle(new CustomFormatUpdatedEvent(format, CustomFormatMediaType.Both));

            Assert.That(audiobook.FormatItems.Single().Score, Is.EqualTo(75));
            Assert.That(ebook.FormatItems, Is.Empty);

            format.AppliesTo = CustomFormatMediaType.Ebook;
            service.Handle(new CustomFormatUpdatedEvent(format, CustomFormatMediaType.Audiobook));

            Assert.That(audiobook.FormatItems, Is.Empty);
            Assert.That(ebook.FormatItems.Single().Format, Is.SameAs(format));
            Assert.That(ebook.FormatItems.Single().Score, Is.Zero);
        }

        private static CustomFormatService CreateCustomFormatService(InMemoryCustomFormatRepository repository, IConfigService config, IEventAggregator events)
        {
            return new CustomFormatService(repository, new CacheManager(), config, events);
        }

        private static QualityProfileService CreateQualityProfileService(IProfileRepository repository, ICustomFormatService formatService)
        {
            return new QualityProfileService(
                repository,
                authorService: null,
                importListFactory: null,
                formatService: formatService,
                rootFolderService: null,
                qualityDefinitionService: null,
                logger: LogManager.GetCurrentClassLogger());
        }

        private static List<CustomFormat> CreateActiveAndRetiredFormats()
        {
            var formats = BuiltInCustomFormats.All().Select((format, index) =>
            {
                format.Id = index + 1;
                return format;
            }).ToList();
            var nextId = formats.Count + 1;

            formats.Add(new CustomFormat
            {
                Id = nextId++,
                Name = BuiltInCustomFormats.StandardAudioName,
                BuiltInKey = BuiltInCustomFormats.StandardAudioKey,
                AppliesTo = CustomFormatMediaType.Audiobook,
                Specifications = new List<ICustomFormatSpecification>
                {
                    new AudioProductionSpecification { Name = BuiltInCustomFormats.StandardAudioName, Negate = true }
                }
            });
            formats.Add(new CustomFormat
            {
                Id = nextId++,
                Name = BuiltInCustomFormats.PreferredNarratorMajorityName,
                BuiltInKey = BuiltInCustomFormats.PreferredNarratorMajorityKey,
                AppliesTo = CustomFormatMediaType.Audiobook,
                Specifications = new List<ICustomFormatSpecification>
                {
                    new PreferredNarratorMajoritySpecification { Name = BuiltInCustomFormats.PreferredNarratorMajorityName }
                }
            });
            formats.Add(new CustomFormat
            {
                Id = nextId,
                Name = BuiltInCustomFormats.CompletePreferredCastName,
                BuiltInKey = BuiltInCustomFormats.CompletePreferredCastKey,
                AppliesTo = CustomFormatMediaType.Audiobook,
                Specifications = new List<ICustomFormatSpecification>
                {
                    new PreferredNarratorCompleteSpecification { Name = BuiltInCustomFormats.CompletePreferredCastName }
                }
            });

            return formats;
        }

        private static QualityProfile CreateProfile(int id, ProfileType profileType, List<CustomFormat> formats)
        {
            return new QualityProfile
            {
                Id = id,
                Name = profileType.ToString(),
                ProfileType = profileType,
                Items = new List<QualityProfileQualityItem>(),
                FormatItems = formats.Select(format => new ProfileFormatItem
                {
                    Format = format,
                    Score = profileType == ProfileType.Audiobook &&
                            (format.BuiltInKey == BuiltInCustomFormats.PreferredNarratorMajorityKey ||
                             format.BuiltInKey == BuiltInCustomFormats.CompletePreferredCastKey)
                        ? BuiltInCustomFormats.RetiredNarratorTierDefaultAudiobookScore
                        : profileType == ProfileType.Audiobook && format.BuiltInKey == BuiltInCustomFormats.PreferredNarratorKey
                            ? BuiltInCustomFormats.PreferredNarratorDefaultAudiobookScore
                            : 0
                }).ToList()
            };
        }

        private sealed class CapturingEventAggregator : IEventAggregator
        {
            public List<IEvent> Published { get; } = new List<IEvent>();

            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
                Published.Add(@event);
            }
        }

        private sealed class InMemoryCustomFormatRepository : ICustomFormatRepository
        {
            private readonly List<CustomFormat> _formats = new List<CustomFormat>();
            private int _nextId = 1;

            public IEnumerable<CustomFormat> All() => _formats.ToList();
            public CustomFormat Insert(CustomFormat model)
            {
                model.Id = _nextId++;
                _formats.Add(model);
                return model;
            }

            public CustomFormat Update(CustomFormat model)
            {
                var index = _formats.FindIndex(format => format.Id == model.Id);
                if (index >= 0)
                {
                    _formats[index] = model;
                }

                return model;
            }

            public int Count() => _formats.Count;
            public bool HasItems() => _formats.Any();
            public CustomFormat Find(int id) => _formats.SingleOrDefault(format => format.Id == id);
            public CustomFormat Get(int id) => Find(id) ?? throw new InvalidOperationException();
            public void Delete(int id) => _formats.RemoveAll(format => format.Id == id);
            public void Delete(CustomFormat model) => Delete(model.Id);
            public IEnumerable<CustomFormat> Get(IEnumerable<int> ids) => _formats.Where(format => ids.Contains(format.Id)).ToList();
            public void Purge(bool vacuum = false) => _formats.Clear();

            public CustomFormat Upsert(CustomFormat model) => throw new NotImplementedException();
            public void SetFields(CustomFormat model, params Expression<Func<CustomFormat, object>>[] properties) => throw new NotImplementedException();
            public void InsertMany(IList<CustomFormat> model) => throw new NotImplementedException();
            public void InsertMany(IList<CustomFormat> model, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(IList<CustomFormat> model) => throw new NotImplementedException();
            public void SetFields(IList<CustomFormat> models, params Expression<Func<CustomFormat, object>>[] properties) => throw new NotImplementedException();
            public void DeleteMany(List<CustomFormat> model) => throw new NotImplementedException();
            public void DeleteMany(IEnumerable<int> ids) => throw new NotImplementedException();
            public CustomFormat Single() => _formats.Single();
            public CustomFormat SingleOrDefault() => _formats.SingleOrDefault();
            public PagingSpec<CustomFormat> GetPaged(PagingSpec<CustomFormat> pagingSpec) => throw new NotImplementedException();
        }

        private sealed class InMemoryProfileRepository : IProfileRepository
        {
            private readonly List<QualityProfile> _profiles;

            public InMemoryProfileRepository(params QualityProfile[] profiles)
            {
                _profiles = profiles.ToList();
            }

            public IEnumerable<QualityProfile> All() => _profiles;
            public QualityProfile Update(QualityProfile model) => model;
            public bool Exists(int id) => _profiles.Any(profile => profile.Id == id);
            public QualityProfile Get(int id) => _profiles.Single(profile => profile.Id == id);
            public QualityProfile Find(int id) => _profiles.SingleOrDefault(profile => profile.Id == id);
            public int Count() => _profiles.Count;
            public bool HasItems() => _profiles.Any();

            public QualityProfile Insert(QualityProfile model)
            {
                if (model.Id == 0)
                {
                    model.Id = _profiles
                        .Select(profile => profile.Id)
                        .DefaultIfEmpty()
                        .Max() + 1;
                }

                _profiles.Add(model);
                return model;
            }

            public QualityProfile Upsert(QualityProfile model) => throw new NotImplementedException();
            public void SetFields(QualityProfile model, params Expression<Func<QualityProfile, object>>[] properties) => throw new NotImplementedException();
            public void Delete(QualityProfile model) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public IEnumerable<QualityProfile> Get(IEnumerable<int> ids) => _profiles.Where(profile => ids.Contains(profile.Id)).ToList();
            public void InsertMany(IList<QualityProfile> model) => throw new NotImplementedException();
            public void InsertMany(IList<QualityProfile> model, IDbConnection connection, IDbTransaction transaction) => throw new NotImplementedException();
            public void UpdateMany(IList<QualityProfile> model) => throw new NotImplementedException();
            public void SetFields(IList<QualityProfile> models, params Expression<Func<QualityProfile, object>>[] properties) => throw new NotImplementedException();
            public void DeleteMany(List<QualityProfile> model) => throw new NotImplementedException();
            public void DeleteMany(IEnumerable<int> ids) => throw new NotImplementedException();
            public void Purge(bool vacuum = false) => throw new NotImplementedException();
            public QualityProfile Single() => _profiles.Single();
            public QualityProfile SingleOrDefault() => _profiles.SingleOrDefault();
            public PagingSpec<QualityProfile> GetPaged(PagingSpec<QualityProfile> pagingSpec) => throw new NotImplementedException();
        }

        private sealed class InMemoryCustomFormatService : ICustomFormatService
        {
            private readonly List<CustomFormat> _formats;

            public InMemoryCustomFormatService(IEnumerable<CustomFormat> formats)
            {
                _formats = formats.ToList();
            }

            public Action<CustomFormat> OnDelete { get; set; }

            public List<CustomFormat> All() => _formats.ToList();
            public CustomFormat GetById(int id) => _formats.Single(format => format.Id == id);
            public CustomFormat Insert(CustomFormat customFormat)
            {
                _formats.Add(customFormat);
                return customFormat;
            }

            public void Update(CustomFormat customFormat)
            {
                var index = _formats.FindIndex(format => format.Id == customFormat.Id);
                _formats[index] = customFormat;
            }

            public void Delete(int id)
            {
                var format = GetById(id);
                OnDelete?.Invoke(format);
                _formats.Remove(format);
            }
        }
    }
}
