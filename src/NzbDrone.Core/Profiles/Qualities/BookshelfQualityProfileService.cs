using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using Dapper;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Profiles;

namespace NzbDrone.Core.Profiles.Qualities
{
    public sealed class BookshelfQualityProfile
    {
        public int LegacyId { get; set; }
        public QualityProfile Profile { get; set; }
    }

    public interface IBookshelfQualityProfileService
    {
        List<BookshelfQualityProfile> All();
        BookshelfQualityProfile Get(int legacyId);
        BookshelfQualityProfile Create(QualityProfile profile);
        BookshelfQualityProfile Update(int legacyId, QualityProfile profile);
        void Delete(int legacyId);
    }

    public class BookshelfQualityProfileService : IBookshelfQualityProfileService
    {
        private const string CounterKey = BookshelfNGCompatibilityProfile.QualityProfileIdentitySequenceKey;
        private static readonly object WriteGate = new();
        private readonly IMainDatabase _database;
        private readonly IProfileRepository _repository;
        private readonly IQualityProfileService _profiles;
        private readonly IBookshelfLegacyIdentityMapService _identities;
        private readonly ICustomFormatService _formats;

        public BookshelfQualityProfileService(IMainDatabase database, IProfileRepository repository,
            IQualityProfileService profiles, IBookshelfLegacyIdentityMapService identities, ICustomFormatService formats)
        {
            _database = database;
            _repository = repository;
            _profiles = profiles;
            _identities = identities;
            _formats = formats;
        }

        public List<BookshelfQualityProfile> All()
        {
            var profiles = _profiles.GetByType(ProfileType.Audiobook);
            var mappings = _identities.GetMappingsByTargetIds("QualityProfile", profiles.Select(profile => profile.Id));
            var result = profiles.Select(profile =>
            {
                var sourceIds = mappings.Where(mapping => mapping.TargetId == profile.Id && mapping.MediaType == 0)
                    .Select(mapping => mapping.SourceId).Distinct().ToList();
                if (sourceIds.Count > 1)
                {
                    throw new InvalidOperationException("Ambiguous BookshelfNG quality profile identity.");
                }

                return new BookshelfQualityProfile { LegacyId = sourceIds.SingleOrDefault(profile.Id), Profile = profile };
            }).ToList();
            if (result.Any(profile => profile.LegacyId <= 0) || result.GroupBy(profile => profile.LegacyId).Any(group => group.Count() > 1))
            {
                throw new InvalidOperationException("Ambiguous BookshelfNG quality profile identity.");
            }

            return result;
        }

        public BookshelfQualityProfile Get(int legacyId)
        {
            using var connection = _database.OpenConnection();
            var rows = Resolve(connection, null, legacyId);
            return new BookshelfQualityProfile
            {
                LegacyId = legacyId,
                Profile = _profiles.Get(rows.Single(profile => profile.ProfileType == ProfileType.Audiobook).Id)
            };
        }

        public BookshelfQualityProfile Create(QualityProfile profile)
        {
            if (profile.Id != 0)
            {
                throw new InvalidOperationException("Can't insert a BookshelfNG profile with an existing ID.");
            }

            int legacyId;
            lock (WriteGate)
            {
                using var connection = _database.OpenConnection();
                using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
                LockPostgres(connection, transaction);
                legacyId = checked(ReadHighWater(connection, transaction) + 1);
                var pair = new List<QualityProfile> { NewClone(profile, ProfileType.Audiobook), NewClone(profile, ProfileType.Ebook) };
                _repository.InsertMany(pair, connection, transaction);
                Register(connection, transaction, legacyId, pair);
                WriteHighWater(connection, transaction, legacyId);
                transaction.Commit();
            }

            return Get(legacyId);
        }

        public BookshelfQualityProfile Update(int legacyId, QualityProfile profile)
        {
            lock (WriteGate)
            {
                using var connection = _database.OpenConnection();
                using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
                LockPostgres(connection, transaction);
                var pair = Resolve(connection, transaction, legacyId);
                if (pair.Count == 1)
                {
                    var ebook = NewClone(profile, ProfileType.Ebook);
                    _repository.InsertMany(new[] { ebook }, connection, transaction);
                    pair.Add(ebook);
                    Register(connection, transaction, legacyId, pair);
                }

                var formats = _formats.All().ToDictionary(format => format.Id);
                foreach (var existing in pair)
                {
                    var formatItems = (profile.FormatItems ?? new List<ProfileFormatItem>())
                        .Where(item => !formats.TryGetValue(item.Format.Id, out var format) || format.AppliesTo == CustomFormatMediaType.Both)
                        .Concat((existing.FormatItems ?? new List<ProfileFormatItem>())
                            .Where(item => formats.TryGetValue(item.Format.Id, out var format) && format.AppliesTo != CustomFormatMediaType.Both))
                        .ToList();
                    // Only the predecessor's shared fields change. Per-format native conversion
                    // settings and preferences are not fields an unchanged legacy client knows.
                    connection.Execute(@"UPDATE ""QualityProfiles"" SET
""Name""=@Name, ""UpgradeAllowed""=@UpgradeAllowed, ""Cutoff""=@Cutoff, ""Items""=@Items,
""MinFormatScore""=@MinFormatScore, ""CutoffFormatScore""=@CutoffFormatScore, ""FormatItems""=@FormatItems
WHERE ""Id""=@Id", new
                    {
                        existing.Id, profile.Name, profile.UpgradeAllowed, profile.Cutoff, profile.Items,
                        profile.MinFormatScore, profile.CutoffFormatScore, FormatItems = formatItems
                    }, transaction);
                }

                WriteHighWater(connection, transaction, Math.Max(legacyId, ReadHighWater(connection, transaction)));
                transaction.Commit();
            }

            return Get(legacyId);
        }

        public void Delete(int legacyId)
        {
            lock (WriteGate)
            {
                using var connection = _database.OpenConnection();
                using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
                LockPostgres(connection, transaction);
                List<QualityProfile> pair;
                try
                {
                    pair = Resolve(connection, transaction, legacyId);
                }
                catch (ModelNotFoundException)
                {
                    // The predecessor treats deletion of an unknown ID as a no-op.
                    return;
                }

                _profiles.EnsureNotInUse(pair.Select(profile => profile.Id));
                WriteHighWater(connection, transaction, Math.Max(legacyId, ReadHighWater(connection, transaction)));
                connection.Execute("DELETE FROM \"QualityProfiles\" WHERE \"Id\" IN (@FirstId,@SecondId)",
                    new { FirstId = pair[0].Id, SecondId = pair.Count == 2 ? pair[1].Id : pair[0].Id }, transaction);
                connection.Execute(@"DELETE FROM ""BookshelfLegacyIdentityMap""
WHERE ""Source""='BookshelfNG' AND LOWER(""EntityType"")='qualityprofile' AND ""SourceId""=@LegacyId", new { LegacyId = legacyId }, transaction);
                transaction.Commit();
            }
        }

        private QualityProfile NewClone(QualityProfile source, ProfileType type)
        {
            var formats = _formats.All().ToDictionary(format => format.Id);
            return new QualityProfile
            {
                Name = source.Name, ProfileType = type, UpgradeAllowed = source.UpgradeAllowed,
                Cutoff = source.Cutoff, Items = source.Items, MinFormatScore = source.MinFormatScore,
                CutoffFormatScore = source.CutoffFormatScore,
                FormatItems = (source.FormatItems ?? new List<ProfileFormatItem>())
                    .Where(item => !formats.TryGetValue(item.Format.Id, out var format) || format.AppliesTo == CustomFormatMediaType.Both)
                    .Concat(formats.Values.Where(format => format.AppliesTo != CustomFormatMediaType.Both && format.AppliesToProfile(type))
                        .Select(format => new ProfileFormatItem { Format = format, Score = 0 })).ToList()
            };
        }

        private static List<QualityProfile> Resolve(IDbConnection connection, IDbTransaction transaction, int legacyId)
        {
            var mappings = connection.Query<BookshelfLegacyIdentityMapEntry>(@"SELECT ""SourceId"", ""TargetId"", ""MediaType""
FROM ""BookshelfLegacyIdentityMap"" WHERE ""Source""='BookshelfNG' AND LOWER(""EntityType"")='qualityprofile' AND ""SourceId""=@LegacyId",
                new { LegacyId = legacyId }, transaction).ToList();
            if (mappings.Count == 0)
            {
                var native = connection.QuerySingleOrDefault<QualityProfile>(@"SELECT p.* FROM ""QualityProfiles"" p
WHERE p.""Id""=@LegacyId AND p.""ProfileType""=1
AND NOT EXISTS (SELECT 1 FROM ""BookshelfLegacyIdentityMap"" m
WHERE m.""Source""='BookshelfNG' AND LOWER(m.""EntityType"")='qualityprofile' AND m.""TargetId""=p.""Id"")",
                    new { LegacyId = legacyId }, transaction);
                if (native == null)
                {
                    throw new ModelNotFoundException(typeof(QualityProfile), legacyId);
                }

                return new List<QualityProfile> { native };
            }

            if (mappings.Count != 2 || mappings.Count(mapping => mapping.MediaType == 0) != 1 || mappings.Count(mapping => mapping.MediaType == 1) != 1)
            {
                throw new InvalidOperationException("Ambiguous or incomplete BookshelfNG quality profile identity.");
            }

            var profiles = connection.Query<QualityProfile>("SELECT * FROM \"QualityProfiles\" WHERE \"Id\" IN (@AudioId,@EbookId)",
                new { AudioId = mappings.Single(mapping => mapping.MediaType == 0).TargetId, EbookId = mappings.Single(mapping => mapping.MediaType == 1).TargetId }, transaction).ToList();
            if (profiles.Count != 2 || mappings.Any(mapping => !profiles.Any(profile => profile.Id == mapping.TargetId && (int)profile.ProfileType == mapping.MediaType + 1)))
            {
                throw new InvalidOperationException("BookshelfNG quality profile counterparts are missing or have changed type.");
            }

            return profiles;
        }

        private static void Register(IDbConnection connection, IDbTransaction transaction, int legacyId, IEnumerable<QualityProfile> profiles)
        {
            foreach (var profile in profiles)
            {
                connection.Execute(@"INSERT INTO ""BookshelfLegacyIdentityMap"" (""Source"", ""EntityType"", ""SourceId"", ""TargetId"", ""MediaType"")
VALUES ('BookshelfNG', 'QualityProfile', @LegacyId, @TargetId, @MediaType)",
                    new { LegacyId = legacyId, TargetId = profile.Id, MediaType = (int)profile.ProfileType - 1 }, transaction);
            }
        }

        private static int ReadHighWater(IDbConnection connection, IDbTransaction transaction)
        {
            var values = connection.Query<string>("SELECT \"Value\" FROM \"Config\" WHERE LOWER(\"Key\")=@Key", new { Key = CounterKey }, transaction).ToList();
            if (values.Count > 1 || (values.Count == 1 && (!int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0)))
            {
                throw new InvalidOperationException("Invalid BookshelfNG quality profile identity counter.");
            }

            var counter = values.Count == 0 ? 0 : int.Parse(values[0], CultureInfo.InvariantCulture);
            var mapped = connection.ExecuteScalar<int>("SELECT COALESCE(MAX(\"SourceId\"),0) FROM \"BookshelfLegacyIdentityMap\" WHERE \"Source\"='BookshelfNG' AND LOWER(\"EntityType\")='qualityprofile'", transaction: transaction);
            var unmapped = connection.ExecuteScalar<int>(@"SELECT COALESCE(MAX(p.""Id""),0) FROM ""QualityProfiles"" p WHERE p.""ProfileType""=1
AND NOT EXISTS (SELECT 1 FROM ""BookshelfLegacyIdentityMap"" m WHERE m.""Source""='BookshelfNG' AND LOWER(m.""EntityType"")='qualityprofile' AND m.""TargetId""=p.""Id"")", transaction: transaction);
            return Math.Max(counter, Math.Max(mapped, unmapped));
        }

        private static void WriteHighWater(IDbConnection connection, IDbTransaction transaction, int value)
        {
            var parameters = new { Key = CounterKey, Value = value.ToString(CultureInfo.InvariantCulture) };
            if (connection.Execute("UPDATE \"Config\" SET \"Value\"=@Value WHERE LOWER(\"Key\")=@Key", parameters, transaction) == 0)
            {
                connection.Execute("INSERT INTO \"Config\" (\"Key\", \"Value\") VALUES (@Key,@Value)", parameters, transaction);
            }
        }

        private void LockPostgres(IDbConnection connection, IDbTransaction transaction)
        {
            if (_database.DatabaseType == DatabaseType.PostgreSQL)
            {
                connection.Execute("SELECT pg_advisory_xact_lock(hashtext(current_schema()), hashtext('BookshelfNGQualityProfile'))", transaction: transaction);
            }
        }
    }
}
