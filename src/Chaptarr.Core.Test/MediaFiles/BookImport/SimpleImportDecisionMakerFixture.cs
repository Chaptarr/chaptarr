using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Identification;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class SimpleImportDecisionMakerFixture
    {
        private class ThrowingProxy<T> : DispatchProxy where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private class MetadataTagServiceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMetadataTagService.ReadAllTagsAndDuration))
                {
                    return (new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["title"] = new List<string> { "Unmatched Book" },
                        ["author"] = new List<string> { "Suggested Author" }
                    }, (int?)null);
                }

                throw new NotImplementedException($"Test proxy does not implement IMetadataTagService.{targetMethod?.Name}");
            }
        }

        private class AuthorServiceProxy : DispatchProxy
        {
            public Author LocalAuthor { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.FindByProviderId))
                {
                    var provider = args?[0] as string;
                    var providerId = args?[1] as string;
                    return string.Equals(provider, "hc", StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(providerId, "123", StringComparison.OrdinalIgnoreCase)
                        ? LocalAuthor
                        : null;
                }

                throw new NotImplementedException($"Test proxy does not implement IAuthorService.{targetMethod?.Name}");
            }
        }

        private class FileMatchingServiceProxy : DispatchProxy
        {
            public FileMatch GroupedResult { get; set; }
            public List<int?> HolyGrailAuthorIds { get; } = new List<int?>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IFileMatchingService.MatchFilesToLibraryAsync) &&
                    args?[0] is DiscoveredFileWithMetadata[] files)
                {
                    if (GroupedResult != null)
                    {
                        GroupedResult.File = files.Single();
                        return Task.FromResult(new FileMatchResult
                        {
                            MatchedFiles = new[] { GroupedResult }
                        });
                    }

                    var result = new FileMatchResult
                    {
                        UnmatchedFiles = files.Select(file => new UnmatchedFile
                        {
                            File = file,
                            PotentialAuthors = new[]
                            {
                                new AuthorSuggestion
                                {
                                    ProviderId = "hc:123",
                                    AuthorName = "Suggested Author",
                                    BookProviderId = "hc:work-1",
                                    BookTitle = "Suggested Book",
                                    EditionHardcoverId = "hc:edition-1",
                                    EditionTitle = "Suggested Edition",
                                    Confidence = 0.95
                                }
                            }
                        }).ToArray()
                    };

                    return Task.FromResult(result);
                }

                if (targetMethod?.Name == nameof(IFileMatchingService.HolyGrailMatchFile))
                {
                    HolyGrailAuthorIds.Add(args?[2] as int?);
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement IFileMatchingService.{targetMethod?.Name}");
            }
        }

        private class BookServiceProxy : DispatchProxy
        {
            public Book Book { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetBook))
                {
                    return Book != null && args?[0] is int bookId && bookId == Book.Id ? Book : null;
                }

                throw new NotImplementedException($"Test proxy does not implement IBookService.{targetMethod?.Name}");
            }
        }

        private class EditionServiceProxy : DispatchProxy
        {
            public Edition ProviderEdition { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IEditionService.GetEditionByForeignEditionId))
                {
                    return null;
                }

                if (targetMethod?.Name == nameof(IEditionService.GetEditionByProviderAndId))
                {
                    return string.Equals(args?[0] as string, "hc", StringComparison.OrdinalIgnoreCase)
                        ? ProviderEdition
                        : null;
                }

                if (targetMethod?.Name == nameof(IEditionService.GetEdition))
                {
                    return ProviderEdition != null && args?[0] is int editionId && editionId == ProviderEdition.Id ? ProviderEdition : null;
                }

                throw new NotImplementedException($"Test proxy does not implement IEditionService.{targetMethod?.Name}");
            }
        }

        [Test]
        public void manual_preview_should_preserve_yellow_suggestion_without_a_late_per_file_rematch()
        {
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"manual-preview-{Guid.NewGuid():N}.mp3");
            File.WriteAllText(path, "audio");

            try
            {
                var localAuthor = new Author
                {
                    Id = 42,
                    Name = "Suggested Author"
                };

                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                ((AuthorServiceProxy)(object)authorService).LocalAuthor = localAuthor;

                var fileMatchingService = DispatchProxy.Create<IFileMatchingService, FileMatchingServiceProxy>();

                var sut = new SimpleImportDecisionMaker(
                    metadataTagService: DispatchProxy.Create<IMetadataTagService, MetadataTagServiceProxy>(),
                    fileMatchingService: fileMatchingService,
                    authorService: authorService,
                    bookService: DispatchProxy.Create<IBookService, ThrowingProxy<IBookService>>(),
                    editionService: DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                    mediaFileService: DispatchProxy.Create<IMediaFileService, ThrowingProxy<IMediaFileService>>(),
                    logger: LogManager.GetCurrentClassLogger());

                var file = new FileSystem().FileInfo.FromFileName(path);
                var decisions = sut.GetImportDecisions(
                    new List<IFileInfo> { file },
                    idOverrides: null,
                    itemInfo: new ImportDecisionMakerInfo(),
                    config: new ImportDecisionMakerConfig
                    {
                        Filter = FilterFilesType.None,
                        NewDownload = true,
                        IncludeExisting = true
                    });

                var localBook = decisions.Single().Item;

                Assert.Multiple(() =>
                {
                    Assert.That(localBook.Author, Is.Null);
                    Assert.That(localBook.SuggestedForeignAuthorId, Is.EqualTo("hc:123"));
                    Assert.That(localBook.SuggestedAuthorName, Is.EqualTo("Suggested Author"));
                    Assert.That(localBook.SuggestedForeignBookId, Is.EqualTo("hc:work-1"));
                    Assert.That(localBook.SuggestedBookTitle, Is.EqualTo("Suggested Book"));
                    Assert.That(localBook.SuggestedForeignEditionId, Is.EqualTo("hc:edition-1"));
                    Assert.That(localBook.SuggestedEditionTitle, Is.EqualTo("Suggested Edition"));
                    Assert.That(localBook.Book, Is.Null);
                    Assert.That(((FileMatchingServiceProxy)(object)fileMatchingService).HolyGrailAuthorIds, Is.Empty);
                });
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [Test]
        public void manual_preview_should_not_apply_server_suggested_edition_without_local_match()
        {
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"manual-preview-{Guid.NewGuid():N}.mp3");
            File.WriteAllText(path, "audio");

            try
            {
                var localAuthor = new Author
                {
                    Id = 42,
                    Name = "Suggested Author"
                };

                var serverSuggestedEdition = new Edition
                {
                    Id = 99,
                    BookId = 100,
                    Title = "Server Suggested Edition",
                    HardcoverEditionId = "edition-1"
                };

                var serverSuggestedBook = new Book
                {
                    Id = 100,
                    Title = "Server Suggested Book",
                    Author = localAuthor,
                    Editions = new List<Edition> { serverSuggestedEdition }
                };

                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                ((AuthorServiceProxy)(object)authorService).LocalAuthor = localAuthor;

                var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
                ((BookServiceProxy)(object)bookService).Book = serverSuggestedBook;

                var editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
                ((EditionServiceProxy)(object)editionService).ProviderEdition = serverSuggestedEdition;

                var fileMatchingService = DispatchProxy.Create<IFileMatchingService, FileMatchingServiceProxy>();

                var sut = new SimpleImportDecisionMaker(
                    metadataTagService: DispatchProxy.Create<IMetadataTagService, MetadataTagServiceProxy>(),
                    fileMatchingService: fileMatchingService,
                    authorService: authorService,
                    bookService: bookService,
                    editionService: editionService,
                    mediaFileService: DispatchProxy.Create<IMediaFileService, ThrowingProxy<IMediaFileService>>(),
                    logger: LogManager.GetCurrentClassLogger());

                var file = new FileSystem().FileInfo.FromFileName(path);
                var decisions = sut.GetImportDecisions(
                    new List<IFileInfo> { file },
                    idOverrides: null,
                    itemInfo: new ImportDecisionMakerInfo(),
                    config: new ImportDecisionMakerConfig
                    {
                        Filter = FilterFilesType.None,
                        NewDownload = true,
                        IncludeExisting = true
                    });

                var localBook = decisions.Single().Item;

                Assert.Multiple(() =>
                {
                    Assert.That(localBook.Author, Is.Null);
                    Assert.That(localBook.Book, Is.Null);
                    Assert.That(localBook.Edition, Is.Null);
                    Assert.That(localBook.SuggestedForeignAuthorId, Is.EqualTo("hc:123"));
                    Assert.That(((FileMatchingServiceProxy)(object)fileMatchingService).HolyGrailAuthorIds, Is.Empty);
                });
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [Test]
        public void manual_preview_should_apply_scoped_local_match_for_existing_suggested_author()
        {
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"manual-preview-{Guid.NewGuid():N}.mp3");
            File.WriteAllText(path, "audio");

            try
            {
                var localAuthor = new Author
                {
                    Id = 42,
                    Name = "Suggested Author"
                };

                var localEdition = new Edition
                {
                    Id = 200,
                    BookId = 100,
                    Title = "Local Matched Edition"
                };

                var localMatchedBook = new Book
                {
                    Id = 100,
                    Title = "Local Matched Book",
                    Author = localAuthor,
                    Editions = new List<Edition> { localEdition }
                };

                var authorService = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
                ((AuthorServiceProxy)(object)authorService).LocalAuthor = localAuthor;

                var bookService = DispatchProxy.Create<IBookService, BookServiceProxy>();
                ((BookServiceProxy)(object)bookService).Book = localMatchedBook;

                var fileMatchingService = DispatchProxy.Create<IFileMatchingService, FileMatchingServiceProxy>();
                ((FileMatchingServiceProxy)(object)fileMatchingService).GroupedResult = new FileMatch
                {
                    AuthorId = localAuthor.Id,
                    AuthorName = localAuthor.Name,
                    BookId = localMatchedBook.Id,
                    BookTitle = localMatchedBook.Title,
                    EditionId = localEdition.Id
                };

                var sut = new SimpleImportDecisionMaker(
                    metadataTagService: DispatchProxy.Create<IMetadataTagService, MetadataTagServiceProxy>(),
                    fileMatchingService: fileMatchingService,
                    authorService: authorService,
                    bookService: bookService,
                    editionService: DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                    mediaFileService: DispatchProxy.Create<IMediaFileService, ThrowingProxy<IMediaFileService>>(),
                    logger: LogManager.GetCurrentClassLogger());

                var file = new FileSystem().FileInfo.FromFileName(path);
                var decisions = sut.GetImportDecisions(
                    new List<IFileInfo> { file },
                    idOverrides: null,
                    itemInfo: new ImportDecisionMakerInfo(),
                    config: new ImportDecisionMakerConfig
                    {
                        Filter = FilterFilesType.None,
                        NewDownload = true,
                        IncludeExisting = true
                    });

                var localBook = decisions.Single().Item;

                Assert.Multiple(() =>
                {
                    Assert.That(localBook.Author, Is.SameAs(localAuthor));
                    Assert.That(localBook.Book, Is.SameAs(localMatchedBook));
                    Assert.That(localBook.Edition, Is.SameAs(localEdition));
                    Assert.That(((FileMatchingServiceProxy)(object)fileMatchingService).HolyGrailAuthorIds, Is.Empty);
                });
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
