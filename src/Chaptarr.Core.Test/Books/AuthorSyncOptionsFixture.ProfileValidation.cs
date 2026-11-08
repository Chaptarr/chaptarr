using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Author;
using Chaptarr.Core.Test.Common;
using FluentValidation;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using static Chaptarr.Core.Test.Common.InterfaceProxy;

namespace Chaptarr.Core.Test.Books
{
    public partial class AuthorSyncOptionsFixture
    {
        private static (AuthorImportResource Request, int GeneralId) UseStoredProfiles(Harness h, IMainDatabase database, string media)
        {
            var quality = new QualityProfileRepository(database, h.Events,
                Proxy<ICustomFormatService>((_, _) => new List<CustomFormat>()));
            var audioQuality = quality.Insert(new QualityProfile { Name = "Audio", ProfileType = ProfileType.Audiobook, Items = new List<QualityProfileQualityItem>() });
            var ebookQuality = quality.Insert(new QualityProfile { Name = "Ebook", ProfileType = ProfileType.Ebook, Items = new List<QualityProfileQualityItem>() });
            Set(h.Library, "_qualityProfileService", new QualityProfileService(quality, h.Authors, null, null, h.Roots, null, h.Logger));
            var metadata = new MetadataProfileRepository(database, h.Events);
            var audioMetadata = metadata.Insert(new MetadataProfile { Name = "Audio", ProfileType = MetadataProfileType.Audiobook });
            var ebookMetadata = metadata.Insert(new MetadataProfile { Name = "Ebook", ProfileType = MetadataProfileType.Ebook });
            var general = metadata.Insert(new MetadataProfile { Name = "General", ProfileType = MetadataProfileType.General });
            Set(h.Profiles, "_profileRepository", metadata);
            var audio = h.AudioRoot.GetAudiobookSettings();
            audio.QualityProfileId = audioQuality.Id;
            audio.MetadataProfileId = audioMetadata.Id;
            h.AudioRoot.SetAudiobookSettings(audio);
            var ebook = h.EbookRoot.GetEbookSettings();
            ebook.QualityProfileId = ebookQuality.Id;
            ebook.MetadataProfileId = ebookMetadata.Id;
            h.EbookRoot.SetEbookSettings(ebook);
            var request = h.Request(media, false);
            request.AudiobookQualityProfileId = audioQuality.Id;
            request.EbookQualityProfileId = ebookQuality.Id;
            request.AudiobookMetadataProfileId = audioMetadata.Id;
            request.EbookMetadataProfileId = ebookMetadata.Id;
            request.SearchForMissingBooks = false;
            return (request, general.Id);
        }

        [Test]
        public void import_profiles_reject_wrong_format_and_missing_ids_before_writes(
            [Values(false, true)] bool postgres, [Values("audiobook", "ebook", "both")] string media,
            [Values("quality", "metadata", "missing-quality", "missing-metadata")] string invalid,
            [Values(false, true)] bool existing)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var queries = new List<string>();
                database = ObserveCommands(database, (_, sql) => queries.Add(sql));
                var h = new Harness(database);
                var (request, _) = UseStoredProfiles(h, database, media);
                object[] before = null;
                if (existing)
                {
                    h.Import(request);
                    h.Scan();
                    before = SavedSettings(h.Author);
                }
                var audio = media == "audiobook";
                var quality = invalid.EndsWith("quality", StringComparison.Ordinal);
                var wrongId = invalid.StartsWith("missing", StringComparison.Ordinal) ? int.MaxValue :
                    quality ? (audio ? request.EbookQualityProfileId : request.AudiobookQualityProfileId).Value :
                    (audio ? request.EbookMetadataProfileId : request.AudiobookMetadataProfileId).Value;
                if (quality)
                {
                    if (audio) request.AudiobookQualityProfileId = wrongId;
                    else request.EbookQualityProfileId = wrongId;
                }
                else
                {
                    if (audio) request.AudiobookMetadataProfileId = wrongId;
                    else request.EbookMetadataProfileId = wrongId;
                }
                h.Available = false;
                h.MetadataFetches = 0;
                h.Commands.Clear();
                queries.Clear();
                var error = Assert.Throws<ValidationException>(() => h.Import(request));
                Assert.Multiple(() =>
                {
                    Assert.That(error.Errors.Select(failure => failure.PropertyName), Has.Member(
                        (audio ? "Audiobook" : "Ebook") + (quality ? "QualityProfileId" : "MetadataProfileId")));
                    Assert.That(h.MetadataFetches, Is.Zero);
                    Assert.That(h.Commands, Is.Empty);
                    Assert.That(h.Pending.GetByProviderId("hc:123456"), Is.Null);
                    Assert.That(queries.Count(sql => sql.Contains("FROM \"QualityProfiles\"") || sql.Contains("FROM \"MetadataProfiles\"")), Is.EqualTo((media == "both" ? 2 : 0) + (quality ? 1 : 2)));
                    if (existing) Assert.That(SavedSettings(h.Author), Is.EqualTo(before));
                    else Assert.That(h.Author, Is.Null);
                });
            });
        }

        [Test]
        public void import_profiles_validate_legacy_shared_quality_on_both_sides([Values(false, true)] bool postgres)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var (request, _) = UseStoredProfiles(h, database, "both");
                request.QualityProfileId = request.AudiobookQualityProfileId.Value;
                request.AudiobookQualityProfileId = request.EbookQualityProfileId = null;
                var error = Assert.Throws<ValidationException>(() => h.Import(request));
                Assert.That(error.Errors.Select(failure => failure.PropertyName), Has.Member("EbookQualityProfileId"));
                Assert.That(h.Author, Is.Null);
                Assert.That(h.MetadataFetches, Is.Zero);
            });
        }

        [Test]
        public void import_profiles_validate_inherited_root_profiles(
            [Values(false, true)] bool postgres, [Values("audiobook", "ebook")] string media,
            [Values(false, true)] bool metadata)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database);
                var (request, _) = UseStoredProfiles(h, database, media);
                var audio = media == "audiobook";
                var root = audio ? h.AudioRoot.GetAudiobookSettings() : h.EbookRoot.GetEbookSettings();
                if (metadata) root.MetadataProfileId = (audio ? request.EbookMetadataProfileId : request.AudiobookMetadataProfileId).Value;
                else root.QualityProfileId = (audio ? request.EbookQualityProfileId : request.AudiobookQualityProfileId).Value;
                if (audio) h.AudioRoot.SetAudiobookSettings(root);
                else h.EbookRoot.SetEbookSettings(root);
                request.AudiobookQualityProfileId = request.EbookQualityProfileId = null;
                request.AudiobookMetadataProfileId = request.EbookMetadataProfileId = null;
                Assert.Throws<ValidationException>(() => h.Import(request));
                Assert.That(h.Author, Is.Null);
                Assert.That(h.MetadataFetches, Is.Zero);
            });
        }

        [Test]
        public void import_profiles_accept_typed_quality_and_general_or_typed_metadata(
            [Values(false, true)] bool postgres, [Values("audiobook", "ebook", "both")] string media,
            [Values(false, true)] bool general, [Values(false, true)] bool pending)
        {
            DualDatabaseFixture.Run(postgres, database =>
            {
                var h = new Harness(database) { Available = !pending };
                var (request, generalId) = UseStoredProfiles(h, database, media);
                if (general)
                {
                    request.AudiobookMetadataProfileId = request.EbookMetadataProfileId = null;
                    request.MetadataProfileId = generalId;
                }
                h.Import(request);
                if (pending)
                {
                    Assert.That(h.Pending.GetByProviderId("hc:123456"), Is.Not.Null);
                    h.Available = true;
                    h.ProcessPending();
                }
                var author = h.Author;
                Assert.That(author, Is.Not.Null);
                if (media is "audiobook" or "both")
                {
                    Assert.That(author.AudiobookQualityProfileId, Is.EqualTo(request.AudiobookQualityProfileId));
                    Assert.That(author.AudiobookMetadataProfileId, Is.EqualTo(general ? generalId : request.AudiobookMetadataProfileId));
                }
                if (media is "ebook" or "both")
                {
                    Assert.That(author.EbookQualityProfileId, Is.EqualTo(request.EbookQualityProfileId));
                    Assert.That(author.EbookMetadataProfileId, Is.EqualTo(general ? generalId : request.EbookMetadataProfileId));
                }
            });
        }
    }
}
