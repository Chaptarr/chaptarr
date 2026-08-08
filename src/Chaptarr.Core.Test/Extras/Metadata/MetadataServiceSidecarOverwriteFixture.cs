using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using FluentValidation.Results;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.Extras.Others;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace Chaptarr.Core.Test.Extras.Metadata
{
    [TestFixture]
    public class MetadataServiceSidecarOverwriteFixture
    {
        [Test]
        public void should_not_overwrite_existing_untracked_book_metadata()
        {
            var disk = new TestDisk();
            disk.TextFiles["/library/A Author/A Book/metadata.json"] = "existing";
            var subject = CreateSubject(disk);
            var consumer = new TestMetadataConsumer
            {
                Metadata = new MetadataFileResult("A Book/metadata.json", "{}", overwriteExisting: true)
            };

            var result = InvokeProcessBookMetadata(subject, consumer, BuildAuthor(), BuildBookFile(), new List<MetadataFile>());

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.Null);
                Assert.That(disk.TextFiles["/library/A Author/A Book/metadata.json"], Is.EqualTo("existing"));
            });
        }

        [Test]
        public void should_not_overwrite_existing_tracked_book_metadata_when_consumer_disables_overwrite()
        {
            var disk = new TestDisk();
            disk.TextFiles["/library/A Author/A Book/metadata.json"] = "old";
            var subject = CreateSubject(disk);
            var consumer = new TestMetadataConsumer
            {
                Metadata = new MetadataFileResult("A Book/metadata.json", "new")
            };
            var existingMetadataFiles = new List<MetadataFile>
            {
                new MetadataFile
                {
                    Id = 50,
                    AuthorId = 10,
                    BookId = 20,
                    BookFileId = 40,
                    Consumer = consumer.GetType().Name,
                    Type = MetadataType.BookMetadata,
                    RelativePath = "A Book/metadata.json",
                    Hash = "old".SHA256Hash()
                }
            };

            var result = InvokeProcessBookMetadata(subject, consumer, BuildAuthor(), BuildBookFile(), existingMetadataFiles);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.Null);
                Assert.That(disk.TextFiles["/library/A Author/A Book/metadata.json"], Is.EqualTo("old"));
                Assert.That(disk.DeletedMetadataIds, Is.Empty);
            });
        }

        [Test]
        public void should_update_existing_tracked_book_metadata_when_file_still_matches_previous_hash()
        {
            var disk = new TestDisk();
            disk.TextFiles["/library/A Author/A Book/metadata.json"] = "old";
            var subject = CreateSubject(disk);
            var consumer = new TestMetadataConsumer
            {
                Metadata = new MetadataFileResult("A Book/metadata.json", "new", overwriteExisting: true)
            };
            var existingMetadataFiles = new List<MetadataFile>
            {
                new MetadataFile
                {
                    Id = 51,
                    AuthorId = 10,
                    BookId = 20,
                    BookFileId = 40,
                    Consumer = consumer.GetType().Name,
                    Type = MetadataType.BookMetadata,
                    RelativePath = "A Book/metadata.json",
                    Hash = "old".SHA256Hash()
                }
            };

            var result = InvokeProcessBookMetadata(subject, consumer, BuildAuthor(), BuildBookFile(), existingMetadataFiles);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.Not.Null);
                Assert.That(result.Hash, Is.EqualTo("new".SHA256Hash()));
                Assert.That(disk.TextFiles["/library/A Author/A Book/metadata.json"], Is.EqualTo("new"));
                Assert.That(disk.DeletedMetadataIds, Is.Empty);
            });
        }

        [Test]
        public void should_keep_metadata_extra_file_tracking_but_disable_overwrites_when_existing_tracked_book_metadata_was_edited()
        {
            var disk = new TestDisk();
            disk.TextFiles["/library/A Author/A Book/metadata.json"] = "edited elsewhere";
            var subject = CreateSubject(disk);
            var consumer = new TestMetadataConsumer
            {
                Metadata = new MetadataFileResult("A Book/metadata.json", "new", overwriteExisting: true)
            };
            var existingMetadataFiles = new List<MetadataFile>
            {
                new MetadataFile
                {
                    Id = 52,
                    AuthorId = 10,
                    BookId = 20,
                    BookFileId = 40,
                    Consumer = consumer.GetType().Name,
                    Type = MetadataType.BookMetadata,
                    RelativePath = "A Book/metadata.json",
                    Hash = "old".SHA256Hash()
                }
            };

            var result = InvokeProcessBookMetadata(subject, consumer, BuildAuthor(), BuildBookFile(), existingMetadataFiles);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.Null);
                Assert.That(disk.TextFiles["/library/A Author/A Book/metadata.json"], Is.EqualTo("edited elsewhere"));
                Assert.That(disk.DeletedMetadataIds, Is.Empty);
                Assert.That(disk.UpsertedMetadata, Has.Count.EqualTo(1));
                Assert.That(disk.UpsertedMetadata[0].Id, Is.EqualTo(52));
                Assert.That(disk.UpsertedMetadata[0].Hash, Is.Null);
            });
        }

        [Test]
        public void should_not_overwrite_existing_untracked_book_image()
        {
            var disk = new TestDisk();
            disk.BinaryFiles["/library/A Author/A Book/cover.webp"] = Bytes("existing cover");
            disk.BinaryFiles["/source/edition.webp"] = Bytes("source cover");
            var subject = CreateSubject(disk);
            var consumer = new TestMetadataConsumer
            {
                Images = new List<ImageFileResult>
                {
                    new ImageFileResult("A Book/cover.webp", "/source/edition.webp", overwriteExisting: true)
                }
            };

            var result = InvokeProcessBookImages(subject, consumer, BuildAuthor(), BuildBookFile(), new List<MetadataFile>());

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.Empty);
                Assert.That(disk.BinaryFiles["/library/A Author/A Book/cover.webp"], Is.EqualTo(Bytes("existing cover")));
            });
        }

        [Test]
        public void should_update_existing_tracked_book_image_when_file_still_matches_previous_hash_and_source_changes()
        {
            var disk = new TestDisk();
            disk.BinaryFiles["/library/A Author/A Book/cover.webp"] = Bytes("old cover");
            disk.BinaryFiles["/source/new.webp"] = Bytes("new cover");
            var subject = CreateSubject(disk);
            var consumer = new TestMetadataConsumer
            {
                Images = new List<ImageFileResult>
                {
                    new ImageFileResult("A Book/cover.webp", "/source/new.webp", overwriteExisting: true)
                }
            };
            var existingMetadataFiles = new List<MetadataFile>
            {
                new MetadataFile
                {
                    Id = 60,
                    AuthorId = 10,
                    BookId = 20,
                    BookFileId = 40,
                    Consumer = consumer.GetType().Name,
                    Type = MetadataType.BookImage,
                    RelativePath = "A Book/cover.webp",
                    Hash = TrackedImageHash(Bytes("old cover"), "/source/old.webp")
                }
            };

            var result = InvokeProcessBookImages(subject, consumer, BuildAuthor(), BuildBookFile(), existingMetadataFiles);

            Assert.Multiple(() =>
            {
                Assert.That(result, Has.Count.EqualTo(1));
                Assert.That(disk.BinaryFiles["/library/A Author/A Book/cover.webp"], Is.EqualTo(Bytes("new cover")));
                Assert.That(result[0].Hash, Is.EqualTo(TrackedImageHash(Bytes("new cover"), "/source/new.webp")));
                Assert.That(disk.DeletedMetadataIds, Is.Empty);
            });
        }

        [Test]
        public void should_skip_existing_tracked_book_image_when_source_is_unchanged()
        {
            var disk = new TestDisk();
            disk.BinaryFiles["/library/A Author/A Book/cover.webp"] = Bytes("old cover");
            disk.BinaryFiles["/source/old.webp"] = Bytes("source cover");
            var subject = CreateSubject(disk);
            var consumer = new TestMetadataConsumer
            {
                Images = new List<ImageFileResult>
                {
                    new ImageFileResult("A Book/cover.webp", "/source/old.webp", overwriteExisting: true)
                }
            };
            var existingMetadataFiles = new List<MetadataFile>
            {
                new MetadataFile
                {
                    Id = 61,
                    AuthorId = 10,
                    BookId = 20,
                    BookFileId = 40,
                    Consumer = consumer.GetType().Name,
                    Type = MetadataType.BookImage,
                    RelativePath = "A Book/cover.webp",
                    Hash = TrackedImageHash(Bytes("old cover"), "/source/old.webp")
                }
            };

            var result = InvokeProcessBookImages(subject, consumer, BuildAuthor(), BuildBookFile(), existingMetadataFiles);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.Empty);
                Assert.That(disk.BinaryFiles["/library/A Author/A Book/cover.webp"], Is.EqualTo(Bytes("old cover")));
                Assert.That(disk.DeletedMetadataIds, Is.Empty);
            });
        }

        [Test]
        public void should_keep_image_extra_file_tracking_but_disable_overwrites_when_existing_tracked_book_image_was_edited()
        {
            var disk = new TestDisk();
            disk.BinaryFiles["/library/A Author/A Book/cover.webp"] = Bytes("edited elsewhere");
            disk.BinaryFiles["/source/new.webp"] = Bytes("new cover");
            var subject = CreateSubject(disk);
            var consumer = new TestMetadataConsumer
            {
                Images = new List<ImageFileResult>
                {
                    new ImageFileResult("A Book/cover.webp", "/source/new.webp", overwriteExisting: true)
                }
            };
            var existingMetadataFiles = new List<MetadataFile>
            {
                new MetadataFile
                {
                    Id = 62,
                    AuthorId = 10,
                    BookId = 20,
                    BookFileId = 40,
                    Consumer = consumer.GetType().Name,
                    Type = MetadataType.BookImage,
                    RelativePath = "A Book/cover.webp",
                    Hash = TrackedImageHash(Bytes("old cover"), "/source/old.webp")
                }
            };

            var result = InvokeProcessBookImages(subject, consumer, BuildAuthor(), BuildBookFile(), existingMetadataFiles);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.Empty);
                Assert.That(disk.BinaryFiles["/library/A Author/A Book/cover.webp"], Is.EqualTo(Bytes("edited elsewhere")));
                Assert.That(disk.DeletedMetadataIds, Is.Empty);
                Assert.That(disk.UpsertedMetadata, Has.Count.EqualTo(1));
                Assert.That(disk.UpsertedMetadata[0].Id, Is.EqualTo(62));
                Assert.That(disk.UpsertedMetadata[0].Hash, Is.Null);
            });
        }

        private static MetadataService CreateSubject(TestDisk disk)
        {
            return new MetadataService(
                configService: null,
                diskProvider: disk.DiskProvider,
                diskTransferService: null,
                recycleBinProvider: null,
                otherExtraFileRenamer: NoOp<IOtherExtraFileRenamer>(),
                metadataFactory: null,
                cleanMetadataService: null,
                httpClient: Throwing<IHttpClient>(),
                mediaFileAttributeService: NoOp<IMediaFileAttributeService>(),
                metadataFileService: disk.MetadataFileService,
                bookService: null,
                logger: LogManager.GetCurrentClassLogger());
        }

        private static MetadataFile InvokeProcessBookMetadata(MetadataService subject, IMetadata consumer, Author author, BookFile bookFile, List<MetadataFile> existingMetadataFiles)
        {
            var method = typeof(MetadataService).GetMethod("ProcessBookMetadata", BindingFlags.Instance | BindingFlags.NonPublic);
            return (MetadataFile)method.Invoke(subject, new object[] { consumer, author, bookFile, existingMetadataFiles });
        }

        private static List<MetadataFile> InvokeProcessBookImages(MetadataService subject, IMetadata consumer, Author author, BookFile bookFile, List<MetadataFile> existingMetadataFiles)
        {
            var method = typeof(MetadataService).GetMethod("ProcessBookImages", BindingFlags.Instance | BindingFlags.NonPublic);
            return (List<MetadataFile>)method.Invoke(subject, new object[] { consumer, author, bookFile, existingMetadataFiles });
        }

        private static Author BuildAuthor()
        {
            return new Author
            {
                Id = 10,
                Name = "A Author",
                Path = "/library/A Author",
                AudiobookPath = "/library/A Author",
                EbookPath = "/library/A Author"
            };
        }

        private static BookFile BuildBookFile()
        {
            var book = new Book
            {
                Id = 20,
                Title = "A Book"
            };

            var edition = new Edition
            {
                Id = 30,
                BookId = book.Id,
                Title = book.Title,
                Book = book
            };

            return new BookFile
            {
                Id = 40,
                EditionId = edition.Id,
                Edition = edition,
                MediaType = "ebook",
                Path = "/library/A Author/A Book/book.epub",
                Part = 1
            };
        }

        private static byte[] Bytes(string value)
        {
            return Encoding.UTF8.GetBytes(value);
        }

        private static string TrackedImageHash(byte[] fileBytes, string source)
        {
            using (var stream = new MemoryStream(fileBytes))
            {
                return stream.SHA256Hash() + ":" + source.SHA256Hash();
            }
        }

        private static T Throwing<T>()
            where T : class
        {
            return DispatchProxy.Create<T, ThrowingProxy<T>>();
        }

        private static T NoOp<T>()
            where T : class
        {
            return DispatchProxy.Create<T, NoOpProxy<T>>();
        }

        private class TestDisk
        {
            public Dictionary<string, string> TextFiles { get; } = new();
            public Dictionary<string, byte[]> BinaryFiles { get; } = new();
            public List<int> DeletedMetadataIds { get; } = new();
            public List<MetadataFile> UpsertedMetadata { get; } = new();

            public IDiskProvider DiskProvider
            {
                get
                {
                    var proxy = DispatchProxy.Create<IDiskProvider, DiskProviderProxy>();
                    ((DiskProviderProxy)(object)proxy).Disk = this;
                    return proxy;
                }
            }

            public IMetadataFileService MetadataFileService
            {
                get
                {
                    var proxy = DispatchProxy.Create<IMetadataFileService, MetadataFileServiceProxy>();
                    ((MetadataFileServiceProxy)(object)proxy).Disk = this;
                    return proxy;
                }
            }

            public static string NormalizePath(string path)
            {
                return path?.Replace('\\', '/');
            }
        }

        private class DiskProviderProxy : DispatchProxy
        {
            public TestDisk Disk { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IDiskProvider.FileExists))
                {
                    var path = TestDisk.NormalizePath((string)args[0]);
                    return Disk.TextFiles.ContainsKey(path) || Disk.BinaryFiles.ContainsKey(path);
                }

                if (targetMethod?.Name == nameof(IDiskProvider.ReadAllText))
                {
                    return Disk.TextFiles[TestDisk.NormalizePath((string)args[0])];
                }

                if (targetMethod?.Name == nameof(IDiskProvider.WriteAllText))
                {
                    Disk.TextFiles[TestDisk.NormalizePath((string)args[0])] = (string)args[1];
                    return null;
                }

                if (targetMethod?.Name == nameof(IDiskProvider.OpenReadStream))
                {
                    var path = TestDisk.NormalizePath((string)args[0]);
                    var bytes = Disk.BinaryFiles.TryGetValue(path, out var binaryBytes)
                        ? binaryBytes
                        : Encoding.UTF8.GetBytes(Disk.TextFiles[path]);
                    var tempPath = Path.GetTempFileName();
                    File.WriteAllBytes(tempPath, bytes);

                    return File.OpenRead(tempPath);
                }

                if (targetMethod?.Name == nameof(IDiskProvider.CopyFile))
                {
                    var source = TestDisk.NormalizePath((string)args[0]);
                    var destination = TestDisk.NormalizePath((string)args[1]);
                    var overwrite = args.Length > 2 && (bool)args[2];
                    if (!overwrite && Disk.BinaryFiles.ContainsKey(destination))
                    {
                        throw new IOException("Destination exists");
                    }

                    Disk.BinaryFiles[destination] = Disk.BinaryFiles[source].ToArray();
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement IDiskProvider.{targetMethod?.Name}");
            }
        }

        private class MetadataFileServiceProxy : DispatchProxy
        {
            public TestDisk Disk { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMetadataFileService.Delete))
                {
                    Disk.DeletedMetadataIds.Add((int)args[0]);
                    return null;
                }

                if (targetMethod?.Name == nameof(IMetadataFileService.Upsert))
                {
                    if (args[0] is MetadataFile metadataFile)
                    {
                        Disk.UpsertedMetadata.Add(metadataFile);
                    }
                    else if (args[0] is List<MetadataFile> metadataFiles)
                    {
                        Disk.UpsertedMetadata.AddRange(metadataFiles);
                    }

                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement IMetadataFileService.{targetMethod?.Name}");
            }
        }

        private class NoOpProxy<T> : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.ReturnType == typeof(void) ? null : GetDefault(targetMethod?.ReturnType);
            }
        }

        private class ThrowingProxy<T> : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Unexpected call to {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private static object GetDefault(Type type)
        {
            return type == null || type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);
        }

        private class TestMetadataConsumer : IMetadata
        {
            public string Name => "Test Metadata";
            public Type ConfigContract => typeof(TestProviderConfig);
            public ProviderMessage Message => null;
            public IEnumerable<ProviderDefinition> DefaultDefinitions => new List<ProviderDefinition>();
            public ProviderDefinition Definition { get; set; }
            public MetadataFileResult Metadata { get; set; }
            public List<ImageFileResult> Images { get; set; } = new();

            public ValidationResult Test()
            {
                return new ValidationResult();
            }

            public object RequestAction(string stage, IDictionary<string, string> query)
            {
                return null;
            }

            public string GetFilenameAfterMove(Author author, BookFile bookFile, MetadataFile metadataFile)
            {
                throw new NotImplementedException();
            }

            public string GetFilenameAfterMove(Author author, string bookPath, MetadataFile metadataFile)
            {
                throw new NotImplementedException();
            }

            public MetadataFile FindMetadataFile(Author author, string path)
            {
                throw new NotImplementedException();
            }

            public MetadataFileResult AuthorMetadata(Author author)
            {
                return null;
            }

            public MetadataFileResult BookMetadata(Author author, BookFile bookFile)
            {
                return Metadata;
            }

            public List<ImageFileResult> AuthorImages(Author author)
            {
                return new List<ImageFileResult>();
            }

            public List<ImageFileResult> BookImages(Author author, BookFile bookFile)
            {
                return Images;
            }
        }

        private class TestProviderConfig : IProviderConfig
        {
            public NzbDroneValidationResult Validate()
            {
                return new NzbDroneValidationResult();
            }
        }
    }
}
