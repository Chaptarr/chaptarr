using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download.Aggregation;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Download
{
    [TestFixture]
    public class PendingReleaseCustomFormatScoreFixture
    {
        [Test]
        public void restart_hydration_should_restore_the_direct_custom_format_score()
        {
            var format = new CustomFormat
            {
                Id = 10,
                Name = "Preferred Narrators",
                AppliesTo = CustomFormatMediaType.Audiobook
            };
            var profile = new QualityProfile
            {
                Id = 20,
                ProfileType = ProfileType.Audiobook,
                FormatItems = new List<ProfileFormatItem>
                {
                    new() { Format = format, Score = 50 }
                }
            };
            var author = new Author
            {
                Id = 30,
                Name = "Test Author",
                AudiobookQualityProfileId = profile.Id,
                AudiobookQualityProfile = new LazyLoaded<QualityProfile>(profile)
            };
            var book = new Book
            {
                Id = 40,
                AuthorId = author.Id,
                Title = "Test Book",
                MediaType = BookMediaType.Audiobook
            };
            var pending = new PendingRelease
            {
                Id = 50,
                AuthorId = author.Id,
                AdditionalInfo = new PendingReleaseAdditionalInfo
                {
                    ReleaseSource = ReleaseSourceType.InteractiveSearch
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = author.Name,
                    ReleaseTitle = "Test Author - Test Book",
                    Quality = new QualityModel(Quality.M4B)
                },
                Release = new ReleaseInfo
                {
                    Title = "Test Author - Test Book",
                    Size = 1234
                }
            };

            var repository = DispatchProxy.Create<IPendingReleaseRepository, PendingRepositoryProxy>();
            ((PendingRepositoryProxy)(object)repository).Releases = new List<PendingRelease> { pending };
            var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authorService).Authors = new List<Author> { author };
            var parsingService = DispatchProxy.Create<IParsingService, ParsingServiceProxy>();
            ((ParsingServiceProxy)(object)parsingService).Books = new List<Book> { book };
            var aggregationService = DispatchProxy.Create<IRemoteBookAggregationService, DefaultProxy<IRemoteBookAggregationService>>();
            var calculator = new FixedCustomFormatCalculationService(format);
            var service = new PendingReleaseService(
                indexerStatusService: null,
                repository,
                authorService,
                parsingService,
                delayProfileService: null,
                taskManager: null,
                configService: null,
                calculator,
                aggregationService,
                downloadClientFactory: null,
                indexerFactory: null,
                eventAggregator: null,
                LogManager.GetCurrentClassLogger());

            var remoteBook = service.GetPendingRemoteBooks(author.Id).Single();

            Assert.That(remoteBook.CustomFormats, Is.EqualTo(new[] { format }));
            Assert.That(remoteBook.CustomFormatScore, Is.EqualTo(50));
            Assert.That(remoteBook.ReleaseSource, Is.EqualTo(ReleaseSourceType.InteractiveSearch));
        }

        private class PendingRepositoryProxy : DispatchProxy
        {
            public List<PendingRelease> Releases { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IPendingReleaseRepository.AllByAuthorId))
                {
                    return Releases.Where(release => release.AuthorId == (int)args[0]).ToList();
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public List<Author> Authors { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.GetAuthors))
                {
                    var ids = ((IEnumerable<int>)args[0]).ToHashSet();
                    return Authors.Where(author => ids.Contains(author.Id)).ToList();
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class ParsingServiceProxy : DispatchProxy
        {
            public List<Book> Books { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IParsingService.GetBooks))
                {
                    return Books;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class DefaultProxy<T> : DispatchProxy
            where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.ReturnType == typeof(void)
                    ? null
                    : targetMethod?.ReturnType.IsValueType == true
                        ? Activator.CreateInstance(targetMethod.ReturnType)
                        : null;
            }
        }

        private sealed class FixedCustomFormatCalculationService : ICustomFormatCalculationService
        {
            private readonly List<CustomFormat> _formats;

            public FixedCustomFormatCalculationService(params CustomFormat[] formats)
            {
                _formats = formats.ToList();
            }

            public List<CustomFormat> ParseCustomFormat(RemoteBook remoteBook, long size) => _formats;
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile, Author artist) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(Blocklist blocklist, Author artist) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(EntityHistory history, Author artist) => throw new NotImplementedException();
            public List<CustomFormat> ParseCustomFormat(LocalBook localBook) => throw new NotImplementedException();
        }
    }
}
