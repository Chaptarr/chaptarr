using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Chaptarr.Api.V1.CustomFormats;
using Chaptarr.Http.ClientSchema;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore.Converters;
using NzbDrone.Core.History;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.CustomFormats
{
    [TestFixture]
    public class AudioProductionCustomFormatFixture
    {
        private sealed class TestLocalizationService : ILocalizationService
        {
            public Dictionary<string, string> GetLocalizationDictionary()
            {
                return new Dictionary<string, string>();
            }

            public string GetLocalizedString(string phrase)
            {
                return phrase;
            }

            public string GetLocalizedString(string phrase, Dictionary<string, object> tokens)
            {
                return phrase;
            }
        }

        [SetUp]
        public void SetUpSchemaBuilder()
        {
            typeof(SchemaBuilder)
                .GetField("_localizationService", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, new TestLocalizationService());
        }

        [Test]
        public void should_match_structured_graphic_audio_signal()
        {
            var spec = new AudioProductionSpecification();

            var result = spec.IsSatisfiedBy(new CustomFormatInput
            {
                IsGraphicAudio = true
            });

            Assert.That(result, Is.True);
        }

        [Test]
        public void should_match_text_fallback_signal()
        {
            var spec = new AudioProductionSpecification();

            var result = spec.IsSatisfiedBy(new CustomFormatInput
            {
                AudioProductionFields = new List<string> { "Storm Front (Dramatized Adaptation)" }
            });

            Assert.That(result, Is.True);
        }

        [Test]
        public void should_invert_match_when_negated()
        {
            var spec = new AudioProductionSpecification { Negate = true };

            var dramatized = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                AudioProductionFields = new List<string> { "A full cast production" }
            });

            var standard = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                AudioProductionFields = new List<string> { "Standard audiobook release" }
            });

            Assert.That(dramatized, Is.False);
            Assert.That(standard, Is.True);
        }

        [Test]
        public void negated_spec_should_not_match_ebook_input()
        {
            var spec = new AudioProductionSpecification { Negate = true };

            var result = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Ebook,
                AudioProductionFields = new List<string> { "Storm Front.epub" }
            });

            Assert.That(result, Is.False);
        }

        [Test]
        public void spec_should_not_match_ebook_with_dramatized_markers()
        {
            var spec = new AudioProductionSpecification();

            var result = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Ebook,
                AudioProductionFields = new List<string> { "Storm Front (Dramatized Adaptation)" }
            });

            Assert.That(result, Is.False);
        }

        [Test]
        public void preferred_narrator_spec_should_match_release_narrator()
        {
            var spec = new PreferredNarratorSpecification();

            var result = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                PreferredNarratorNames = new List<string> { "Jim Dale" },
                Narrator = "Jim Dale"
            });

            Assert.That(result, Is.True);
        }

        [Test]
        public void preferred_narrator_spec_should_not_match_different_release_narrator()
        {
            var spec = new PreferredNarratorSpecification();

            var result = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                PreferredNarratorNames = new List<string> { "Jim Dale" },
                Narrator = "Stephen Fry"
            });

            Assert.That(result, Is.False);
        }

        [TestCase("1984 [M4B]")]
        [TestCase("1984 - GROUP")]
        [TestCase("George Orwell - 1984")]
        public void generic_release_title_tokens_should_not_be_extracted_as_narrators(string title)
        {
            Assert.That(PreferredNarratorMatcher.ExtractNarratorFromFields(new[] { title }), Is.Null);
        }

        [Test]
        public void unlabelled_narrator_should_match_only_when_it_matches_the_target()
        {
            var spec = new PreferredNarratorSpecification();

            var result = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                PreferredNarratorNames = new List<string> { "Andrew Wincott" },
                AudioProductionFields = new List<string> { "1984 [Andrew Wincott]" }
            });

            Assert.That(result, Is.True);
        }

        [Test]
        public void shared_middle_and_last_names_should_not_match_a_different_first_name()
        {
            var spec = new PreferredNarratorSpecification();

            var result = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                PreferredNarratorNames = new List<string> { "David Lee Smith" },
                Narrator = "John Lee Smith"
            });

            Assert.That(result, Is.False);
        }

        [Test]
        public void preferred_narrator_spec_should_not_match_ebook_input()
        {
            var spec = new PreferredNarratorSpecification();

            var result = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Ebook,
                PreferredNarratorNames = new List<string> { "Jim Dale" },
                Narrator = "Jim Dale"
            });

            Assert.That(result, Is.False);
        }

        [Test]
        public void preferred_narrator_spec_should_not_match_graphic_audio_label_for_single_narrator_target()
        {
            var spec = new PreferredNarratorSpecification();

            var result = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                PreferredNarratorNames = new List<string> { "Andrew Wincott" },
                IsGraphicAudio = true,
                Narrator = "GraphicAudio"
            });

            Assert.That(result, Is.False);
        }

        [Test]
        public void custom_format_schema_should_offer_only_the_concise_book_aware_templates()
        {
            var formats = BuiltInCustomFormats.All().Select((format, index) =>
            {
                format.Id = index + 1;
                return format;
            }).ToList();
            var controller = new CustomFormatController(
                new StubCustomFormatService(formats),
                new List<ICustomFormatSpecification>
                {
                    new AudioProductionSpecification(),
                    new NarratorNamesSpecification(),
                    new PreferredNarratorSpecification(),
                    new PreferredNarratorMajoritySpecification(),
                    new PreferredNarratorCompleteSpecification(),
                    new NarratorSpecification()
                });

            var schema = (List<CustomFormatSpecificationSchema>)controller.GetTemplates();
            var implementations = schema.Select(item => item.Implementation).ToList();
            var audioProduction = schema.Single(item => item.Implementation == nameof(AudioProductionSpecification));
            var narratorMatch = schema.Single(item => item.Implementation == nameof(PreferredNarratorSpecification));
            var narratorNames = schema.Single(item => item.Implementation == nameof(NarratorNamesSpecification));
            var narratorNamesField = narratorNames.Fields.Single(field => field.Name == "names");
            var narratorAdvanced = schema.Single(item => item.Implementation == nameof(NarratorSpecification));

            Assert.Multiple(() =>
            {
                Assert.That(implementations, Does.Not.Contain(nameof(PreferredNarratorMajoritySpecification)));
                Assert.That(implementations, Does.Not.Contain(nameof(PreferredNarratorCompleteSpecification)));
                Assert.That(implementations, Does.Contain(nameof(NarratorNamesSpecification)));
                Assert.That(implementations, Does.Contain(nameof(NarratorSpecification)));
                Assert.That(narratorNames.Name, Is.EqualTo("Narrator Names"));
                Assert.That(narratorNamesField.Type, Is.EqualTo("tag"));
                Assert.That(((IEnumerable<string>)narratorNamesField.Value).ToList(), Is.Empty);
                Assert.That(narratorAdvanced.ImplementationName, Is.EqualTo("Narrator (Advanced)"));
                Assert.That(audioProduction.Presets.Select(preset => preset.Name), Is.EqualTo(new[]
                {
                    BuiltInCustomFormats.DramatizedAudioName
                }));
                Assert.That(narratorMatch.Presets.Select(preset => preset.Name), Is.EqualTo(new[]
                {
                    BuiltInCustomFormats.PreferredNarratorName,
                    "Narrator Mismatch"
                }));
            });
        }

        [Test]
        public void preferred_narrator_spec_should_match_named_narrator_inside_full_cast_release()
        {
            var spec = new PreferredNarratorSpecification();

            var result = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                PreferredNarratorNames = new List<string> { "Andrew Wincott" },
                IsGraphicAudio = true,
                Narrator = "Andrew Wincott, GraphicAudio"
            });

            Assert.That(result, Is.True);
        }

        [Test]
        public void mixed_author_target_should_require_a_non_author_anchor()
        {
            var authorOnly = PreferredNarratorMatcher.Evaluate(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                Author = new Author { Name = "George Orwell" },
                PreferredNarratorNames = new List<string> { "George Orwell", "Andrew Wincott" },
                Narrator = "George Orwell"
            });

            var narratorMatch = PreferredNarratorMatcher.Evaluate(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                Author = new Author { Name = "George Orwell" },
                PreferredNarratorNames = new List<string> { "George Orwell", "Andrew Wincott" },
                Narrator = "Andrew Wincott"
            });

            var completeMatch = PreferredNarratorMatcher.Evaluate(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                Author = new Author { Name = "George Orwell" },
                PreferredNarratorNames = new List<string> { "George Orwell", "Andrew Wincott" },
                Narrator = "George Orwell + Andrew Wincott"
            });

            Assert.That(authorOnly.Any, Is.False);
            Assert.That(authorOnly.NonAuthorTargetCount, Is.EqualTo(1));
            Assert.That(narratorMatch.Any, Is.True);
            Assert.That(narratorMatch.NonAuthorOverlapCount, Is.EqualTo(1));
            Assert.That(narratorMatch.Complete, Is.False);
            Assert.That(completeMatch.Complete, Is.True);
        }

        [Test]
        public void primary_author_only_target_should_allow_self_narration()
        {
            var result = PreferredNarratorMatcher.Evaluate(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                Author = new Author { Name = "Mel Robbins" },
                PreferredNarratorNames = new List<string> { "Mel Robbins" },
                Narrator = "Mel Robbins"
            });

            Assert.That(result.Any, Is.True);
            Assert.That(result.NonAuthorTargetCount, Is.EqualTo(0));
        }

        [Test]
        public void primary_author_only_target_should_not_infer_self_narration_from_an_unlabelled_author_tail()
        {
            var result = PreferredNarratorMatcher.Evaluate(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                Author = new Author { Name = "Mel Robbins" },
                PreferredNarratorNames = new List<string> { "Mel Robbins" },
                AudioProductionFields = new List<string> { "The 5 Second Rule - Mel Robbins" }
            });

            Assert.That(result.Any, Is.False);
        }

        [Test]
        public void preferred_narrator_tiers_should_grade_named_cast_coverage()
        {
            var input = new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                PreferredNarratorNames = new List<string> { "Annie Ellicott", "Jeff Hays", "Patrick Warburton" }
            };

            input.Narrator = "Jeff Hays";
            var any = PreferredNarratorMatcher.Evaluate(input);

            input.Narrator = "Jeff Hays + Patrick Warburton";
            var majority = PreferredNarratorMatcher.Evaluate(input);

            input.Narrator = "Annie Ellicott + Jeff Hays + Patrick Warburton";
            var complete = PreferredNarratorMatcher.Evaluate(input);

            Assert.That((any.Any, any.Majority, any.Complete), Is.EqualTo((true, false, false)));
            Assert.That((majority.Any, majority.Majority, majority.Complete), Is.EqualTo((true, true, false)));
            Assert.That((complete.Any, complete.Majority, complete.Complete), Is.EqualTo((true, true, true)));
        }

        [Test]
        public void one_release_narrator_should_not_satisfy_multiple_target_people()
        {
            var result = PreferredNarratorMatcher.Evaluate(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                PreferredNarratorNames = new List<string> { "John Smith", "John Lee Smith", "Annie Ellicott" },
                Narrator = "John Lee Smith"
            });

            Assert.That(result.OverlapCount, Is.EqualTo(1));
            Assert.That(result.Majority, Is.False);
            Assert.That(result.Complete, Is.False);
        }

        [Test]
        public void unresolved_target_names_should_block_complete_without_padding_majority()
        {
            var result = PreferredNarratorMatcher.Evaluate(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                PreferredNarratorNames = new List<string> { "Annie Ellicott", "Jeff Hays", "Patrick Warburton" },
                PreferredNarratorHasUnresolvedNames = true,
                Narrator = "Annie Ellicott + Jeff Hays + Patrick Warburton"
            });

            Assert.That(result.TargetCount, Is.EqualTo(3));
            Assert.That(result.Majority, Is.True);
            Assert.That(result.Complete, Is.False);
        }

        [Test]
        public void structured_narrator_list_should_resolve_abbreviated_summary_count()
        {
            var completeTarget = PreferredNarratorMatcher.BuildTarget(new Book
            {
                AnyEditionOk = false,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Monitored = true,
                        IsEbook = false,
                        NarratorNames = new List<string> { "Annie Ellicott", "Jeff Hays", "Patrick Warburton" },
                        Narrator = "Annie Ellicott + 2 more narrators"
                    }
                }
            });
            var unresolvedTarget = PreferredNarratorMatcher.BuildTarget(new Book
            {
                AnyEditionOk = false,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Monitored = true,
                        IsEbook = false,
                        NarratorNames = new List<string> { "Annie Ellicott" },
                        Narrator = "Annie Ellicott + 2 more narrators"
                    }
                }
            });

            Assert.That(completeTarget.HasUnresolvedNames, Is.False);
            Assert.That(unresolvedTarget.HasUnresolvedNames, Is.True);
        }

        [Test]
        public void narrator_regex_should_match_each_detected_name_independently()
        {
            var spec = new NarratorSpecification { Value = @"^Jeff\s+Hays$" };

            var result = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                Narrator = "Annie Ellicott + Jeff Hays"
            });

            Assert.That(result, Is.True);
        }
        [TestCase("James Marsters", "James Marster, Mary-Louise Parker", true)]
        [TestCase("Brian Herbert", "Frank Herbert", false)]
        [TestCase("Jose Garcia", "José García", true)]
        [TestCase("村上春樹", "村上春樹", true)]
        [TestCase("かとう たろう", "がとう たろう", false)]
        public void narrator_names_should_use_literal_unicode_aware_identity_matching(
            string configuredName,
            string releaseNarrator,
            bool expected)
        {
            var spec = new NarratorNamesSpecification
            {
                Names = new[] { configuredName, "Stephen Fry" }
            };

            var result = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                Narrator = releaseNarrator
            });

            Assert.That(result, Is.EqualTo(expected));
        }

        [Test]
        public void narrator_names_should_require_explicit_narrator_evidence_for_an_author_name()
        {
            var spec = new NarratorNamesSpecification
            {
                Names = new[] { "Brian Herbert" }
            };

            var authorTail = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                Author = new Author { Name = "Brian Herbert" },
                AudioProductionFields = new List<string> { "Dune - Brian Herbert" }
            });
            var structuredNarrator = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                Author = new Author { Name = "Brian Herbert" },
                Narrator = "Brian Herbert"
            });

            Assert.Multiple(() =>
            {
                Assert.That(authorTail, Is.False);
                Assert.That(structuredNarrator, Is.True);
            });
        }

        [Test]
        public void narrator_names_should_match_once_when_multiple_names_intersect()
        {
            var format = new CustomFormat
            {
                Id = 20,
                Name = "Preferred Narrators",
                Specifications = new List<ICustomFormatSpecification>
                {
                    new NarratorNamesSpecification
                    {
                        Names = new[] { "James Marsters", "Stephen Fry" }
                    }
                }
            };
            var service = CreateCalculationService(format);
            var remoteBook = new RemoteBook
            {
                Author = new Author { Name = "Example Author" },
                Release = new ReleaseInfo
                {
                    Title = "Example Book",
                    Narrator = "James Marster + Stephen Fry"
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "Example Author",
                    ReleaseTitle = "Example Book",
                    Quality = new QualityModel(Quality.M4B)
                }
            };
            var profile = new QualityProfile
            {
                FormatItems = new List<ProfileFormatItem>
                {
                    new ProfileFormatItem { Format = format, Score = 50 }
                }
            };

            var matches = service.ParseCustomFormat(remoteBook, 0);

            Assert.That(matches, Has.Count.EqualTo(1));
            Assert.That(profile.CalculateCustomFormatScore(matches), Is.EqualTo(50));
        }

        [Test]
        public void negated_narrator_names_should_mean_none_of_these_names_and_stay_ebook_inert()
        {
            var spec = new NarratorNamesSpecification
            {
                Names = new[] { "James Marsters", "Stephen Fry" },
                Negate = true
            };

            var selectedNarrator = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                Narrator = "James Marsters"
            });
            var otherNarrator = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                Narrator = "Jeff Hays"
            });
            var ebook = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Ebook,
                Narrator = "Jeff Hays"
            });

            Assert.That(selectedNarrator, Is.False);
            Assert.That(otherNarrator, Is.True);
            Assert.That(ebook, Is.False);
        }

        [Test]
        public void narrator_names_should_round_trip_as_an_array_through_api_and_database_storage()
        {
            var expectedNames = new[] { "James Marsters", "José García", "村上春樹" };
            var original = new CustomFormat
            {
                Id = 21,
                Name = "Preferred Narrators",
                AppliesTo = CustomFormatMediaType.Ebook,
                Specifications = new List<ICustomFormatSpecification>
                {
                    new NarratorNamesSpecification
                    {
                        Name = "Narrator Names",
                        Names = expectedNames
                    }
                }
            };

            var resource = original.ToResource(true);
            var wireJson = JsonSerializer.Serialize(resource);
            var wireResource = JsonSerializer.Deserialize<CustomFormatResource>(wireJson);
            Assert.That(wireResource.AppliesTo, Is.EqualTo(CustomFormatMediaType.Ebook));
            var wireNamesField = wireResource.Specifications
                .Single()
                .Fields
                .Single(field => field.Name == "names");

            Assert.That(((JsonElement)wireNamesField.Value).ValueKind, Is.EqualTo(JsonValueKind.Array));

            var apiModel = wireResource.ToModel(new List<ICustomFormatSpecification>
            {
                new NarratorNamesSpecification()
            });
            var apiNames = ((NarratorNamesSpecification)apiModel.Specifications.Single()).Names;
            Assert.That(apiModel.AppliesTo, Is.EqualTo(CustomFormatMediaType.Ebook));
            Assert.That(apiNames, Is.EqualTo(expectedNames));

            var options = new JsonSerializerOptions();
            options.Converters.Add(new CustomFormatSpecificationListConverter());
            var storedJson = JsonSerializer.Serialize(original.Specifications, options);
            var restored = JsonSerializer.Deserialize<List<ICustomFormatSpecification>>(storedJson, options);
            var storedNames = ((NarratorNamesSpecification)restored.Single()).Names;

            Assert.That(storedNames, Is.EqualTo(expectedNames));
        }


        [Test]
        public void plain_narrator_name_should_allow_one_missing_final_character()
        {
            var spec = new NarratorSpecification { Value = "James Marsters" };

            var result = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                Narrator = "James Marster, Mary-Louise Parker"
            });

            Assert.That(result, Is.True);
        }

        [Test]
        public void plain_narrator_name_should_not_match_a_different_given_name_with_the_same_surname()
        {
            var spec = new NarratorSpecification { Value = "Brian Herbert" };

            var result = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                Narrator = "Frank Herbert"
            });

            Assert.That(result, Is.False);
        }

        [Test]
        public void plain_narrator_name_should_not_treat_an_unlabelled_author_tail_as_narrator_evidence()
        {
            var spec = new NarratorSpecification { Value = "Brian Herbert" };

            var result = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                Author = new Author { Name = "Brian Herbert" },
                AudioProductionFields = new List<string> { "Dune - Brian Herbert" }
            });

            Assert.That(result, Is.False);
        }

        [TestCase(null, "Dune - Narrated by Brian Herbert")]
        [TestCase("Brian Herbert", "Dune - Brian Herbert")]
        public void plain_narrator_name_should_allow_explicit_self_narration(string narrator, string title)
        {
            var spec = new NarratorSpecification { Value = "Brian Herbert" };

            var result = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                Author = new Author { Name = "Brian Herbert" },
                Narrator = narrator,
                AudioProductionFields = new List<string> { title }
            });

            Assert.That(result, Is.True);
        }

        [TestCase("Jose Garcia", "José García")]
        [TestCase("村上春樹", "村上春樹")]
        [TestCase("𠮷田 太郎", "𠮷田 太郎")]
        public void plain_narrator_name_should_support_unicode_names(string configuredName, string releaseName)
        {
            var spec = new NarratorSpecification { Value = configuredName };

            var result = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                Narrator = releaseName
            });

            Assert.That(result, Is.True);
        }

        [Test]
        public void plain_narrator_name_should_preserve_identity_bearing_marks_in_non_latin_scripts()
        {
            var spec = new NarratorSpecification { Value = "かとう たろう" };

            var result = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                Narrator = "がとう たろう"
            });

            Assert.That(result, Is.False);
        }

        [Test]
        public void plain_narrator_name_should_not_match_a_longer_surname_by_substring()
        {
            var spec = new NarratorSpecification { Value = "James Marsters" };

            var result = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                Narrator = "James Marstersson"
            });

            Assert.That(result, Is.False);
        }

        [Test]
        public void narrator_regex_should_preserve_hyphenated_names_from_explicit_title_evidence()
        {
            var spec = new NarratorSpecification { Value = @"^Mary[-\s]+Louise\s+Parker$" };

            var result = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                AudioProductionFields = new List<string> { "Book Title - Narrated by Mary-Louise Parker - M4B" }
            });

            Assert.That(result, Is.True);
        }

        [Test]
        public void narrator_regex_should_support_confirmed_ban_and_negated_requirement_semantics()
        {
            var ban = new NarratorSpecification { Value = @"^Jeff\s+Hays$" };
            var require = new NarratorSpecification { Value = @"^Jeff\s+Hays$", Negate = true };
            var unknown = new CustomFormatInput { MediaType = BookMediaType.Audiobook };

            Assert.That(ban.IsSatisfiedBy(unknown), Is.False);
            Assert.That(require.IsSatisfiedBy(unknown), Is.True);
        }

        [Test]
        public void narrator_regex_should_not_apply_to_ebooks_even_when_negated()
        {
            var spec = new NarratorSpecification { Value = @"^Jeff\s+Hays$", Negate = true };

            var result = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Ebook,
                Narrator = "Someone Else"
            });

            Assert.That(result, Is.False);
        }

        [Test]
        public void wrong_narrator_preset_should_match_only_when_a_pinned_target_mismatches()
        {
            var spec = new PreferredNarratorSpecification { Negate = true };

            var mismatch = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                PreferredNarratorNames = new List<string> { "Jim Dale" },
                Narrator = "Stephen Fry"
            });

            var noTarget = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                Narrator = "Stephen Fry"
            });

            Assert.That(mismatch, Is.True);
            Assert.That(noTarget, Is.False);
        }

        [Test]
        public void wrong_narrator_preset_should_reject_when_a_pinned_target_has_no_release_evidence()
        {
            var spec = new PreferredNarratorSpecification { Negate = true };

            var result = spec.IsSatisfiedBy(new CustomFormatInput
            {
                MediaType = BookMediaType.Audiobook,
                PreferredNarratorNames = new List<string> { "Jim Dale" }
            });

            Assert.That(result, Is.True);
        }

        [Test]
        public void negated_spec_should_not_match_unknown_media_type_without_signal()
        {
            var spec = new AudioProductionSpecification { Negate = true };

            var result = spec.IsSatisfiedBy(new CustomFormatInput
            {
                AudioProductionFields = new List<string> { "Some release with no audio markers" }
            });

            Assert.That(result, Is.False);
        }

        [Test]
        public void custom_format_calculation_should_match_local_import_tags()
        {
            var format = CreateDramatizedFormat();
            var service = CreateCalculationService(format);
            var localBook = new LocalBook
            {
                Author = new Author { Name = "Jim Butcher" },
                SceneName = "Storm Front",
                Quality = new QualityModel(Quality.M4B),
                RawTags = new RawFileTags
                {
                    AllTags = new Dictionary<string, List<string>>
                    {
                        ["description"] = new List<string> { "A GraphicAudio production" }
                    }
                }
            };

            var matches = service.ParseCustomFormat(localBook);

            Assert.That(matches.Select(x => x.Name), Has.Member(BuiltInCustomFormats.DramatizedAudioName));
        }

        [Test]
        public void custom_format_calculation_should_match_book_file_structured_signal()
        {
            var format = CreateDramatizedFormat();
            var service = CreateCalculationService(format);
            var author = new Author { Name = "Jim Butcher" };
            var bookFile = new BookFile
            {
                Author = author,
                Path = "/books/Storm Front.m4b",
                Quality = new QualityModel(Quality.M4B),
                IsGraphicAudio = true
            };

            var matches = service.ParseCustomFormat(bookFile, author);

            Assert.That(matches.Select(x => x.Name), Has.Member(BuiltInCustomFormats.DramatizedAudioName));
        }

        [Test]
        public void custom_format_calculation_should_not_match_dramatized_for_ebook_local_import_tags()
        {
            // Regression: an ebook whose tags/title carry dramatized markers must not match the
            // dramatized format, otherwise Reject Dramatized profiles reject the import.
            var service = CreateCalculationService(CreateDramatizedFormat());
            var localBook = new LocalBook
            {
                Author = new Author { Name = "Jim Butcher" },
                Path = "/books/Storm Front (Dramatized Adaptation).epub",
                SceneName = "Storm Front (Dramatized Adaptation)",
                Quality = new QualityModel(Quality.EPUB),
                RawTags = new RawFileTags
                {
                    AllTags = new Dictionary<string, List<string>>
                    {
                        ["description"] = new List<string> { "A GraphicAudio production" }
                    }
                }
            };

            var matches = service.ParseCustomFormat(localBook);

            Assert.That(matches, Is.Empty);
        }

        [Test]
        public void custom_format_calculation_should_not_match_dramatized_for_ebook_book_file()
        {
            var service = CreateCalculationService(CreateDramatizedFormat());
            var author = new Author { Name = "Jim Butcher" };
            var bookFile = new BookFile
            {
                Author = author,
                Path = "/books/Storm Front.epub",
                Quality = new QualityModel(Quality.EPUB),
                IsGraphicAudio = true
            };

            var matches = service.ParseCustomFormat(bookFile, author);

            Assert.That(matches, Is.Empty);
        }

        [Test]
        public void custom_format_calculation_should_not_match_standard_for_ebook_remote_book()
        {
            var service = CreateCalculationService(CreateStandardFormat());
            var remoteBook = new RemoteBook
            {
                Author = new Author { Name = "Jim Butcher" },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "Jim Butcher",
                    ReleaseTitle = "Jim Butcher - Storm Front",
                    Quality = new QualityModel(Quality.EPUB)
                }
            };

            var matches = service.ParseCustomFormat(remoteBook, 0);

            Assert.That(matches, Is.Empty);
        }

        [Test]
        public void custom_format_calculation_should_match_standard_for_plain_audiobook_remote_book()
        {
            var service = CreateCalculationService(CreateStandardFormat());
            var remoteBook = new RemoteBook
            {
                Author = new Author { Name = "Jim Butcher" },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "Jim Butcher",
                    ReleaseTitle = "Jim Butcher - Storm Front",
                    Quality = new QualityModel(Quality.M4B)
                }
            };

            var matches = service.ParseCustomFormat(remoteBook, 0);

            Assert.That(matches.Select(x => x.Name), Has.Member(BuiltInCustomFormats.StandardAudioName));
        }

        [Test]
        public void custom_format_calculation_should_match_preferred_narrator_for_strict_monitored_edition()
        {
            var service = CreateCalculationService(CreatePreferredNarratorFormat());
            var remoteBook = new RemoteBook
            {
                Author = new Author { Name = "George Orwell" },
                Release = new ReleaseInfo
                {
                    Title = "1984",
                    Narrator = "Andrew Wincott"
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "George Orwell",
                    ReleaseTitle = "1984",
                    Quality = new QualityModel(Quality.M4B)
                },
                Books = new List<Book>
                {
                    new Book
                    {
                        Title = "1984",
                        AnyEditionOk = false,
                        Editions = new List<Edition>
                        {
                            new Edition
                            {
                                Id = 10,
                                Monitored = true,
                                IsEbook = false,
                                ReadingFormatId = 2,
                                NarratorNames = new List<string> { "Andrew Wincott" }
                            }
                        }
                    }
                }
            };

            var matches = service.ParseCustomFormat(remoteBook, 0);

            Assert.That(matches.Select(x => x.Name), Has.Member(BuiltInCustomFormats.PreferredNarratorName));
        }

        [Test]
        public void custom_format_calculation_should_not_match_preferred_narrator_for_unpinned_any_edition_ok_book()
        {
            var service = CreateCalculationService(CreatePreferredNarratorFormat());
            var remoteBook = new RemoteBook
            {
                Author = new Author { Name = "George Orwell" },
                Release = new ReleaseInfo
                {
                    Title = "1984",
                    Narrator = "Andrew Wincott"
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "George Orwell",
                    ReleaseTitle = "1984",
                    Quality = new QualityModel(Quality.M4B)
                },
                Books = new List<Book>
                {
                    new Book
                    {
                        Title = "1984",
                        AnyEditionOk = true,
                        Editions = new List<Edition>
                        {
                            new Edition
                            {
                                Id = 10,
                                Monitored = true,
                                IsEbook = false,
                                ReadingFormatId = 2,
                                NarratorNames = new List<string> { "Andrew Wincott" }
                            }
                        }
                    }
                }
            };

            var matches = service.ParseCustomFormat(remoteBook, 0);

            Assert.That(matches, Is.Empty);
        }

        [Test]
        public void custom_format_calculation_should_match_named_narrator_for_manual_edition_even_when_any_edition_ok()
        {
            var service = CreateCalculationService(CreatePreferredNarratorFormat());
            var remoteBook = new RemoteBook
            {
                Author = new Author { Name = "Brandon Sanderson" },
                Release = new ReleaseInfo
                {
                    Title = "The Final Empire",
                    Narrator = "Michael Kramer"
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "Brandon Sanderson",
                    ReleaseTitle = "The Final Empire",
                    Quality = new QualityModel(Quality.M4B)
                },
                Books = new List<Book>
                {
                    new Book
                    {
                        Title = "The Final Empire",
                        AnyEditionOk = true,
                        Editions = new List<Edition>
                        {
                            new Edition
                            {
                                Id = 20,
                                ManualAdd = true,
                                Monitored = true,
                                IsEbook = false,
                                ReadingFormatId = 2,
                                NarratorNames = new List<string> { "Michael Kramer" }
                            }
                        }
                    }
                }
            };

            var matches = service.ParseCustomFormat(remoteBook, 0);

            Assert.That(matches.Select(x => x.Name), Has.Member(BuiltInCustomFormats.PreferredNarratorName));
        }

        [Test]
        public void custom_format_calculation_should_score_graphic_audio_as_production_not_narrator()
        {
            var service = CreateCalculationService(CreatePreferredNarratorFormat(), CreateDramatizedFormat());
            var remoteBook = new RemoteBook
            {
                Author = new Author { Name = "Brandon Sanderson" },
                Release = new ReleaseInfo
                {
                    Title = "The Final Empire",
                    IsGraphicAudio = true,
                    Narrator = "GraphicAudio"
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "Brandon Sanderson",
                    ReleaseTitle = "The Final Empire",
                    Quality = new QualityModel(Quality.M4B)
                },
                Books = new List<Book>
                {
                    new Book
                    {
                        Title = "The Final Empire",
                        AnyEditionOk = true,
                        Editions = new List<Edition>
                        {
                            new Edition
                            {
                                Id = 20,
                                ManualAdd = true,
                                Monitored = true,
                                IsEbook = false,
                                ReadingFormatId = 2,
                                IsGraphicAudio = true,
                                Narrator = "GraphicAudio"
                            }
                        }
                    }
                }
            };

            var matches = service.ParseCustomFormat(remoteBook, 0);

            Assert.That(matches.Select(x => x.Name), Does.Not.Contain(BuiltInCustomFormats.PreferredNarratorName));
            Assert.That(matches.Select(x => x.Name), Does.Contain(BuiltInCustomFormats.DramatizedAudioName));
        }

        [Test]
        public void custom_format_calculation_should_require_narrator_role_evidence_for_an_author_name()
        {
            var narratorFormat = CreateNarratorFormat(4, "Avoid Brian Herbert", "Brian Herbert");
            var service = CreateCalculationService(narratorFormat);
            var remoteBook = new RemoteBook
            {
                Author = new Author { Name = "Brian Herbert" },
                Release = new ReleaseInfo { Title = "Dune - Brian Herbert" },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "Brian Herbert",
                    ReleaseTitle = "Dune - Brian Herbert",
                    Quality = new QualityModel(Quality.M4B)
                }
            };

            var authorOnlyMatches = service.ParseCustomFormat(remoteBook, 0);
            remoteBook.Release.Narrator = "Brian Herbert";
            var selfNarratedMatches = service.ParseCustomFormat(remoteBook, 0);

            Assert.That(authorOnlyMatches.Select(x => x.Name), Does.Not.Contain("Avoid Brian Herbert"));
            Assert.That(selfNarratedMatches.Select(x => x.Name), Does.Contain("Avoid Brian Herbert"));
        }

        [Test]
        public void unselected_edition_should_allow_user_authored_narrator_condition_without_enabling_narrator_match()
        {
            var narratorFormat = CreateNarratorFormat(4, "Prefer Andrew Wincott", @"^Andrew\s+Wincott$");
            var service = CreateCalculationService(CreatePreferredNarratorFormat(), narratorFormat);
            var remoteBook = new RemoteBook
            {
                Author = new Author { Name = "George Orwell" },
                Release = new ReleaseInfo { Title = "1984", Narrator = "Andrew Wincott" },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "George Orwell",
                    ReleaseTitle = "1984",
                    Quality = new QualityModel(Quality.M4B)
                },
                Books = new List<Book>
                {
                    new Book
                    {
                        AnyEditionOk = true,
                        Editions = new List<Edition>
                        {
                            new Edition
                            {
                                Id = 10,
                                Monitored = true,
                                IsEbook = false,
                                ReadingFormatId = 2,
                                NarratorNames = new List<string> { "Andrew Wincott" }
                            }
                        }
                    }
                }
            };

            var matches = service.ParseCustomFormat(remoteBook, 0);

            Assert.That(matches.Select(x => x.Name), Does.Contain("Prefer Andrew Wincott"));
            Assert.That(matches.Select(x => x.Name), Does.Not.Contain(BuiltInCustomFormats.PreferredNarratorName));
        }

        [Test]
        public void profile_scores_should_apply_one_narrator_match_and_keep_hard_production_reject_dominant()
        {
            var any = CreatePreferredNarratorFormat();
            var dramatized = CreateDramatizedFormat();
            var service = CreateCalculationService(any, dramatized);
            var remoteBook = new RemoteBook
            {
                Author = new Author { Name = "George Orwell" },
                Release = new ReleaseInfo
                {
                    Title = "1984",
                    Narrator = "Andrew Wincott + Annie Ellicott + Jeff Hays",
                    IsGraphicAudio = true
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "George Orwell",
                    ReleaseTitle = "1984",
                    Quality = new QualityModel(Quality.M4B)
                },
                Books = new List<Book>
                {
                    new Book
                    {
                        AnyEditionOk = false,
                        Editions = new List<Edition>
                        {
                            new Edition
                            {
                                Id = 10,
                                Monitored = true,
                                IsEbook = false,
                                ReadingFormatId = 2,
                                NarratorNames = new List<string> { "Andrew Wincott", "Annie Ellicott", "Jeff Hays" }
                            }
                        }
                    }
                }
            };

            var matches = service.ParseCustomFormat(remoteBook, 0);
            var profile = new QualityProfile
            {
                MinFormatScore = 0,
                FormatItems = new List<ProfileFormatItem>
                {
                    new ProfileFormatItem { Format = any, Score = 50 },
                    new ProfileFormatItem { Format = dramatized, Score = 50 }
                }
            };

            Assert.That(profile.CalculateCustomFormatScore(matches), Is.EqualTo(100));

            profile.FormatItems.Single(item => item.Format == dramatized).Score = -10000;
            Assert.That(profile.CalculateCustomFormatScore(matches), Is.EqualTo(-9950));
            Assert.That(profile.CalculateCustomFormatScore(matches), Is.LessThan(profile.MinFormatScore));
        }

        [Test]
        public void custom_format_media_scope_should_gate_the_central_release_calculator()
        {
            CustomFormat CreateTitleFormat(int id, string name, CustomFormatMediaType appliesTo)
            {
                return new CustomFormat
                {
                    Id = id,
                    Name = name,
                    AppliesTo = appliesTo,
                    Specifications = new List<ICustomFormatSpecification>
                    {
                        new ReleaseTitleSpecification
                        {
                            Name = name,
                            Value = "Storm Front"
                        }
                    }
                };
            }

            RemoteBook CreateRemoteBook(Quality quality)
            {
                return new RemoteBook
                {
                    Release = new ReleaseInfo { Title = "Jim Butcher - Storm Front" },
                    ParsedBookInfo = new ParsedBookInfo
                    {
                        AuthorName = "Jim Butcher",
                        ReleaseTitle = "Jim Butcher - Storm Front",
                        Quality = new QualityModel(quality)
                    }
                };
            }

            var audioOnly = CreateTitleFormat(10, "Audio only", CustomFormatMediaType.Audiobook);
            var ebookOnly = CreateTitleFormat(11, "eBook only", CustomFormatMediaType.Ebook);
            var both = CreateTitleFormat(12, "Both", CustomFormatMediaType.Both);
            var service = CreateCalculationService(audioOnly, ebookOnly, both);

            var audiobookMatches = service.ParseCustomFormat(CreateRemoteBook(Quality.M4B), 0);
            var ebookMatches = service.ParseCustomFormat(CreateRemoteBook(Quality.EPUB), 0);
            var unknownMatches = service.ParseCustomFormat(CreateRemoteBook(Quality.Unknown), 0);

            Assert.Multiple(() =>
            {
                Assert.That(audiobookMatches.Select(format => format.Id), Is.EquivalentTo(new[] { 10, 12 }));
                Assert.That(ebookMatches.Select(format => format.Id), Is.EquivalentTo(new[] { 11, 12 }));
                Assert.That(unknownMatches.Select(format => format.Id), Is.EquivalentTo(new[] { 12 }));
            });
        }

        [Test]
        public void history_calculation_should_reapply_the_selected_narrator_target_from_the_book()
        {
            var format = CreatePreferredNarratorFormat();
            format.AppliesTo = CustomFormatMediaType.Audiobook;
            var service = CreateCalculationService(format);
            var book = new Book
            {
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = false,
                Editions = new List<Edition>
                {
                    new()
                    {
                        Id = 10,
                        Monitored = true,
                        IsEbook = false,
                        ReadingFormatId = 2,
                        NarratorNames = new List<string> { "Andrew Wincott" }
                    }
                }
            };
            var history = new EntityHistory
            {
                Book = book,
                SourceTitle = "George Orwell - 1984",
                Quality = new QualityModel(Quality.M4B),
                Data = new Dictionary<string, string>
                {
                    ["Narrator"] = "Andrew Wincott"
                }
            };

            var matches = service.ParseCustomFormat(
                history,
                new Author { Name = "George Orwell" });

            Assert.That(matches.Select(match => match.Id), Does.Contain(format.Id));
        }

        private static CustomFormatCalculationService CreateCalculationService(params CustomFormat[] formats)
        {
            return new CustomFormatCalculationService(
                new StubCustomFormatService(formats),
                LogManager.GetCurrentClassLogger());
        }

        private static CustomFormat CreateDramatizedFormat()
        {
            return new CustomFormat
            {
                Id = 1,
                Name = BuiltInCustomFormats.DramatizedAudioName,
                Specifications = new List<ICustomFormatSpecification>
                {
                    new AudioProductionSpecification
                    {
                        Name = BuiltInCustomFormats.DramatizedAudioName
                    }
                }
            };
        }

        private static CustomFormat CreateStandardFormat()
        {
            return new CustomFormat
            {
                Id = 2,
                Name = BuiltInCustomFormats.StandardAudioName,
                BuiltInKey = BuiltInCustomFormats.StandardAudioKey,
                Specifications = new List<ICustomFormatSpecification>
                {
                    new AudioProductionSpecification
                    {
                        Name = BuiltInCustomFormats.StandardAudioName,
                        Negate = true
                    }
                }
            };
        }

        private static CustomFormat CreatePreferredNarratorFormat()
        {
            return new CustomFormat
            {
                Id = 3,
                Name = BuiltInCustomFormats.PreferredNarratorName,
                BuiltInKey = BuiltInCustomFormats.PreferredNarratorKey,
                Specifications = new List<ICustomFormatSpecification>
                {
                    new PreferredNarratorSpecification
                    {
                        Name = BuiltInCustomFormats.PreferredNarratorName
                    }
                }
            };
        }

        private static CustomFormat CreateNarratorFormat(int id, string name, string pattern)
        {
            return new CustomFormat
            {
                Id = id,
                Name = name,
                Specifications = new List<ICustomFormatSpecification>
                {
                    new NarratorSpecification
                    {
                        Name = name,
                        Value = pattern
                    }
                }
            };
        }

        private sealed class StubCustomFormatService : ICustomFormatService
        {
            private readonly List<CustomFormat> _formats;

            public StubCustomFormatService(IEnumerable<CustomFormat> formats)
            {
                _formats = formats.ToList();
            }

            public List<CustomFormat> All() => _formats;
            public CustomFormat GetById(int id) => _formats.Single(x => x.Id == id);
            public CustomFormat Insert(CustomFormat customFormat) => throw new NotImplementedException();
            public void Update(CustomFormat customFormat) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
        }
    }
}
