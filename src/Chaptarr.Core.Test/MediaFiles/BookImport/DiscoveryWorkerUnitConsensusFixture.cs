using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using System.IO.Abstractions;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Authors;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class DiscoveryWorkerUnitConsensusFixture
    {
        private class InterfaceProxy<T> : DispatchProxy
            where T : class
        {
            public Func<MethodInfo, object[], object> Handler { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return Handler?.Invoke(targetMethod, args)
                       ?? throw new NotSupportedException($"Unexpected {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private static T CreateProxy<T>(Func<MethodInfo, object[], object> handler)
            where T : class
        {
            var proxy = DispatchProxy.Create<T, InterfaceProxy<T>>();
            ((InterfaceProxy<T>)(object)proxy).Handler = handler;
            return proxy;
        }

        private sealed class RecordingFileMatchingService : IFileMatchingService
        {
            public FileMatchResult Result { get; set; } = new FileMatchResult();
            public List<MatchingContext> Contexts { get; } = new();

            public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata)
                => MatchFilesToLibraryAsync(filesWithMetadata, null, MatchingContextPresets.ForScanLocal());

            public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata, int? restrictToAuthorId)
                => MatchFilesToLibraryAsync(filesWithMetadata, restrictToAuthorId, MatchingContextPresets.ForScanLocal());

            public Task<FileMatchResult> MatchFilesToLibraryAsync(DiscoveredFileWithMetadata[] filesWithMetadata, int? restrictToAuthorId, bool forDownloads)
                => MatchFilesToLibraryAsync(filesWithMetadata, restrictToAuthorId, MatchingContextPresets.ForScanLocal());

            public Task<FileMatchResult> MatchFilesToLibraryAsync(
                DiscoveredFileWithMetadata[] filesWithMetadata,
                int? restrictToAuthorId,
                MatchingContext context)
            {
                Contexts.Add(context);
                return Task.FromResult(Result);
            }

            public EditionFtsMatch HolyGrailMatch(int? authorId, IEnumerable<string> allTagTokens, BookMediaType mediaType)
                => throw new NotSupportedException();

            public FileMatch HolyGrailMatchFile(DiscoveredFileWithMetadata file, BookMediaType mediaType, int? restrictToAuthorId = null)
                => throw new NotSupportedException();
        }

        private sealed class RecordingV5MatchingService : IV5MatchingService
        {
            public List<Dictionary<string, List<string>>> Calls { get; } = new();
            public Func<string, IDictionary<string, List<string>>, string, string, List<V5MatchedAuthor>> OnSearch { get; set; }


            public void ProcessSeriesLinks(List<Book> books)
            {
            }

            public List<V5MatchedAuthor> SearchV5Matching(string query, IDictionary<string, List<string>> tags, string mediaType, string filePath)
            {
                Calls.Add(CloneTags(tags));
                return OnSearch?.Invoke(query, tags, mediaType, filePath) ?? new List<V5MatchedAuthor>();
            }
        }

        [Test]
#pragma warning disable SYSLIB0050
        public async Task streaming_candidate_should_short_circuit_v5_when_local_match_succeeds()
        {
            var worker = (DiscoveryWorker)FormatterServices.GetUninitializedObject(typeof(DiscoveryWorker));
            var method = typeof(DiscoveryWorker).GetMethod("TryResolveAuthorUnitCandidateAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var path = "/library/Frank Herbert/Dune/Dune.epub";
            var matching = new RecordingFileMatchingService
            {
                Result = new FileMatchResult
                {
                    MatchedFiles = new[]
                    {
                        new FileMatch
                        {
                            File = new DiscoveredFileWithMetadata { Path = path },
                            AuthorId = 10,
                            AuthorName = "Frank Herbert",
                            BookId = 20,
                            BookTitle = "Dune",
                            EditionId = 30
                        }
                    }
                }
            };
            var v5 = new RecordingV5MatchingService();
            SetField(worker, "_fileMatchingService", matching);
            SetField(worker, "_v5MatchingService", v5);
            SetField(worker, "_logger", LogManager.GetCurrentClassLogger());

            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = new List<string> { "Dune" },
                ["ARTIST"] = new List<string> { "Frank Herbert" }
            };
            var task = (Task<bool>)method.Invoke(worker, new object[]
            {
                QueueItem(1, path, "{}"),
                tags,
                new List<DiscoveredFileWithMetadata> { new() { Path = path, AllTags = tags } },
                BookMediaType.Ebook,
                null,
                "/library/Frank Herbert",
                null,
                "/library",
                true,
                false,
                true
            });

            Assert.That(await task, Is.True);
            Assert.That(v5.Calls, Is.Empty, "a proven local book must never call V5 author discovery");
            Assert.That(matching.Contexts, Has.Count.EqualTo(1));
            Assert.Multiple(() =>
            {
                Assert.That(matching.Contexts[0].AllowV5Identification, Is.False);
                Assert.That(matching.Contexts[0].AllowAuthorImport, Is.False);
                Assert.That(matching.Contexts[0].DeferUnmatchedToAuthorReady, Is.False);
            });
        }

        [Test]
        public async Task known_author_missing_media_catalog_should_backfill_by_stored_provider_id_without_v5_rediscovery()
        {
            var worker = (DiscoveryWorker)FormatterServices.GetUninitializedObject(typeof(DiscoveryWorker));
            var method = typeof(DiscoveryWorker).GetMethod("TryResolveAuthorUnitCandidateAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var author = new Author
            {
                Id = 10,
                Name = "Frank Herbert",
                NameLastFirst = "Herbert, Frank",
                HardcoverAuthorId = "hc:author-10"
            };
            var matching = new RecordingFileMatchingService
            {
                Result = new FileMatchResult
                {
                    MatchedFiles = Array.Empty<FileMatch>(),
                    UnmatchedFiles = Array.Empty<UnmatchedFile>()
                }
            };
            var v5 = new RecordingV5MatchingService();
            var authors = CreateProxy<IAuthorService>((methodInfo, _) => methodInfo.Name switch
            {
                nameof(IAuthorService.GetCandidates) => new List<Author> { author },
                _ => throw new NotSupportedException(methodInfo.Name)
            });
            var books = CreateProxy<IBookService>((methodInfo, _) => methodInfo.Name switch
            {
                nameof(IBookService.GetBooksByAuthor) => new List<Book>
                {
                    new Book { AuthorId = author.Id, MediaType = BookMediaType.Audiobook }
                },
                _ => throw new NotSupportedException(methodInfo.Name)
            });
            var addCalled = false;
            var library = CreateProxy<IAuthorLibraryService>((methodInfo, args) =>
            {
                if (methodInfo.Name != nameof(IAuthorLibraryService.AddAuthorAsync))
                {
                    throw new NotSupportedException(methodInfo.Name);
                }

                Assert.That(args[0], Is.EqualTo("hc:author-10"));
                var config = (MonitoringConfig)args[1];
                Assert.That(config.CreateEbook, Is.True);
                Assert.That(config.CreateAudiobook, Is.False);
                addCalled = true;
                return Task.FromResult(author);
            });
            var folders = CreateProxy<IAuthorFolderMatchingService>((methodInfo, _) => methodInfo.Name switch
            {
                nameof(IAuthorFolderMatchingService.ValidateFolderMatchesAuthor) => true,
                _ => throw new NotSupportedException(methodInfo.Name)
            });

            SetField(worker, "_fileMatchingService", matching);
            SetField(worker, "_v5MatchingService", v5);
            SetField(worker, "_containmentValidator", new ContainmentValidator(new TagNormalizer(), LogManager.GetCurrentClassLogger()));
            SetField(worker, "_authorService", authors);
            SetField(worker, "_bookService", books);
            SetField(worker, "_authorLibraryService", library);
            SetField(worker, "_authorFolderMatchingService", folders);
            SetField(worker, "_logger", LogManager.GetCurrentClassLogger());

            var path = "/library/Frank Herbert/Dune Messiah/Dune Messiah.epub";
            var root = new RootFolder { Path = "/library", FolderType = FolderType.Ebook };
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = new List<string> { "Dune Messiah" },
                ["ODD_FIELD"] = new List<string> { "Frank Herbert" },
                ["COMMENT"] = new List<string> { "For readers of Stephen King" }
            };
            var task = (Task<bool>)method.Invoke(worker, new object[]
            {
                QueueItem(2, path, "{}"),
                tags,
                new List<DiscoveredFileWithMetadata> { new() { Path = path, AllTags = tags } },
                BookMediaType.Ebook,
                root,
                "/library/Frank Herbert",
                null,
                "/library",
                true,
                false,
                true
            });

            Assert.That(await task, Is.True);
            Assert.That(v5.Calls, Is.Empty, "known author identity must use its stored provider ID");
            Assert.That(addCalled, Is.True);
        }

        [Test]
        public async Task ambiguous_local_author_evidence_should_not_backfill_and_should_fall_through_to_v5()
        {
            var worker = (DiscoveryWorker)FormatterServices.GetUninitializedObject(typeof(DiscoveryWorker));
            var method = typeof(DiscoveryWorker).GetMethod("TryResolveAuthorUnitCandidateAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var matching = new RecordingFileMatchingService
            {
                Result = new FileMatchResult
                {
                    MatchedFiles = Array.Empty<FileMatch>(),
                    UnmatchedFiles = Array.Empty<UnmatchedFile>()
                }
            };
            var v5 = new RecordingV5MatchingService();
            var authors = CreateProxy<IAuthorService>((methodInfo, _) => methodInfo.Name switch
            {
                nameof(IAuthorService.GetCandidates) => new List<Author>
                {
                    new Author { Id = 1, Name = "Alice Smith", HardcoverAuthorId = "hc:alice" },
                    new Author { Id = 2, Name = "Bob Jones", HardcoverAuthorId = "hc:bob" }
                },
                _ => throw new NotSupportedException(methodInfo.Name)
            });
            var folders = CreateProxy<IAuthorFolderMatchingService>((methodInfo, _) => methodInfo.Name switch
            {
                nameof(IAuthorFolderMatchingService.ValidateFolderMatchesAuthor) => true,
                _ => throw new NotSupportedException(methodInfo.Name)
            });

            SetField(worker, "_fileMatchingService", matching);
            SetField(worker, "_v5MatchingService", v5);
            SetField(worker, "_containmentValidator", new ContainmentValidator(new TagNormalizer(), LogManager.GetCurrentClassLogger()));
            SetField(worker, "_authorService", authors);
            SetField(worker, "_bookService", CreateProxy<IBookService>((methodInfo, _) => throw new AssertionException($"Unexpected {methodInfo.Name}")));
            SetField(worker, "_authorLibraryService", CreateProxy<IAuthorLibraryService>((methodInfo, _) => throw new AssertionException($"Unexpected {methodInfo.Name}")));
            SetField(worker, "_authorFolderMatchingService", folders);
            SetField(worker, "_logger", LogManager.GetCurrentClassLogger());

            var path = "/library/Shared Authors/Shared Title/Shared Title.epub";
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["FIELD_ONE"] = new List<string> { "Alice Smith" },
                ["FIELD_TWO"] = new List<string> { "Bob Jones" }
            };
            var task = (Task<bool>)method.Invoke(worker, new object[]
            {
                QueueItem(3, path, "{}"),
                tags,
                new List<DiscoveredFileWithMetadata> { new() { Path = path, AllTags = tags } },
                BookMediaType.Ebook,
                new RootFolder { Path = "/library", FolderType = FolderType.Ebook },
                "/library/Shared Authors",
                null,
                "/library",
                false,
                false,
                true
            });

            Assert.That(await task, Is.False);
            Assert.That(v5.Calls, Has.Count.EqualTo(1), "ambiguous local evidence must be left to server discovery");
        }

        [Test]
        public async Task discovery_final_sweep_should_leave_unresolved_rows_queued_for_local_first_drain()
        {
            var worker = (DiscoveryWorker)FormatterServices.GetUninitializedObject(typeof(DiscoveryWorker));
            var item = QueueItem(
                1,
                "/library/Unknown Author/Unknown Book/Unknown Book.epub",
                "{\"TITLE\":[\"Unknown Book\"]}");
            var completed = false;
            var queriedPrefixes = new List<string>();
            var queue = CreateProxy<IIngestQueueRepository>((methodInfo, args) => methodInfo.Name switch
            {
                nameof(IIngestQueueRepository.GetQueuedItemsUnderPath) => GetScopedItems(args),
                nameof(IIngestQueueRepository.CompleteItemWithResult) => CompleteUnexpectedly(),
                _ => throw new NotSupportedException(methodInfo.Name)
            });
            object GetScopedItems(object[] args)
            {
                queriedPrefixes.Add((string)args[0]);
                return (int)args[2] < item.Id
                    ? new List<IngestQueueItem> { item }
                    : new List<IngestQueueItem>();
            }
            object CompleteUnexpectedly()
            {
                completed = true;
                return null;
            }

            var matching = new RecordingFileMatchingService
            {
                Result = new FileMatchResult
                {
                    MatchedFiles = Array.Empty<FileMatch>(),
                    UnmatchedFiles = new[]
                    {
                        new UnmatchedFile
                        {
                            File = new DiscoveredFileWithMetadata { Path = item.Path },
                            Reason = "NO_LOCAL_MATCH"
                        }
                    }
                }
            };
            var v5 = new RecordingV5MatchingService();

            SetField(worker, "_ingestQueue", queue);
            SetField(worker, "_fileMatchingService", matching);
            SetField(worker, "_v5MatchingService", v5);
            SetField(worker, "_logger", LogManager.GetCurrentClassLogger());

            const int commandId = 987654;
            ImportSessionProgressTracker.Activate(commandId);
            ImportSessionProgressTracker.MarkStagingComplete(commandId);

            var handled = await worker.DiscoverAndImportAuthorsStreamingAsync(
                new RootFolder { Path = "/library", FolderType = FolderType.Ebook },
                new IngestQueueScanScope("/library/Unknown Author"),
                commandId);

            Assert.That(handled, Is.Zero);
            Assert.That(completed, Is.False, "discovery failure must not terminalize before Drain can retry locally");
            Assert.That(matching.Contexts, Is.Not.Empty, "the live streaming path must attempt local matching");
            Assert.That(v5.Calls, Is.Not.Empty, "unknown authors may still use V5 after local matching is exhausted");
            Assert.That(queriedPrefixes, Is.Not.Empty);
            Assert.That(queriedPrefixes.All(path => path == "/library/Unknown Author"), Is.True, "subtree discovery and its final pass must not query the configured root");
        }

        [Test]
        public void build_grouping_unit_key_should_group_m4b_tracks_by_folder_but_keep_ebooks_standalone()
        {
            var firstTrack = BookCoalescingHelper.BuildGroupingUnitKey("/library/Cory Doctorow/Attack Surface/01.m4b");
            var secondTrack = BookCoalescingHelper.BuildGroupingUnitKey("/library/Cory Doctorow/Attack Surface/02.m4b");
            var firstEbook = BookCoalescingHelper.BuildGroupingUnitKey("/library/Cory Doctorow/Attack Surface.epub");
            var secondEbook = BookCoalescingHelper.BuildGroupingUnitKey("/library/Cory Doctorow/Homeland.epub");

            Assert.That(firstTrack, Is.EqualTo(secondTrack));
            Assert.That(firstEbook, Is.Not.EqualTo(secondEbook));
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        public async Task local_discovery_should_require_the_same_exact_identity_field_and_value_across_the_unit(
            bool changeSecondField,
            bool expected)
        {
            var worker = (DiscoveryWorker)FormatterServices.GetUninitializedObject(typeof(DiscoveryWorker));
            var method = typeof(DiscoveryWorker).GetMethod("MatchesExistingLibraryAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            MatchIdentityProof Proof(string field)
            {
                return new MatchIdentityProof(new[]
                {
                    new MatchIdentityProofValue(MatchIdentityRole.Author, "embedded_tag", field, "Test Author - Alpha", "Test Author", "book", "author"),
                    new MatchIdentityProofValue(MatchIdentityRole.Title, "embedded_tag", field, "Test Author - Alpha", "Alpha", "book", "title")
                });
            }

            var firstPath = "/library/Test Author/Alpha/01.mp3";
            var secondPath = "/library/Test Author/Alpha/02.mp3";
            var matching = new RecordingFileMatchingService
            {
                Result = new FileMatchResult
                {
                    MatchedFiles = new[]
                    {
                        new FileMatch { File = new DiscoveredFileWithMetadata { Path = firstPath }, AuthorId = 1, BookId = 2, EditionId = 3, IdentityProof = Proof("BOOKIDENTITY") },
                        new FileMatch { File = new DiscoveredFileWithMetadata { Path = secondPath }, AuthorId = 1, BookId = 2, EditionId = 3, IdentityProof = Proof(changeSecondField ? "OTHERIDENTITY" : "BOOKIDENTITY") }
                    }
                }
            };
            SetField(worker, "_fileMatchingService", matching);
            SetField(worker, "_logger", LogManager.GetCurrentClassLogger());

            var files = new List<DiscoveredFileWithMetadata>
            {
                new DiscoveredFileWithMetadata { Path = firstPath, AllTags = new Dictionary<string, List<string>> { ["BOOKIDENTITY"] = new() { "Test Author - Alpha" } } },
                new DiscoveredFileWithMetadata { Path = secondPath, AllTags = new Dictionary<string, List<string>> { [changeSecondField ? "OTHERIDENTITY" : "BOOKIDENTITY"] = new() { "Test Author - Alpha" } } }
            };
            var task = (Task<bool>)method.Invoke(worker, new object[]
            {
                QueueItem(1, firstPath, "{}"),
                files[0].AllTags,
                files
            });

            Assert.That(await task, Is.EqualTo(expected));
        }

        [Test]
        public void discovery_should_hydrate_every_physical_member_with_the_same_extension()
        {
            var worker = (DiscoveryWorker)FormatterServices.GetUninitializedObject(typeof(DiscoveryWorker));
            var method = typeof(DiscoveryWorker).GetMethod("HydrateDiscoveryUnit", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var firstPath = "/library/Test Author/Alpha/01.mp3";
            var secondPath = "/library/Test Author/Alpha/02.mp3";
            var otherExtension = "/library/Test Author/Alpha/Alpha.m4b";
            IFileInfo FileInfo(string path)
            {
                return CreateProxy<IFileInfo>((methodInfo, _) => methodInfo.Name switch
                {
                    "get_FullName" => path,
                    "get_Exists" => true,
                    "get_Length" => 123L,
                    _ => throw new NotSupportedException(methodInfo.Name)
                });
            }

            var disk = CreateProxy<IDiskProvider>((methodInfo, args) => methodInfo.Name switch
            {
                nameof(IDiskProvider.GetFiles) => new[] { firstPath, secondPath, otherExtension },
                nameof(IDiskProvider.GetFileInfo) => FileInfo((string)args[0]),
                _ => throw new NotSupportedException(methodInfo.Name)
            });
            var readPaths = new List<string>();
            var metadata = CreateProxy<IMetadataTagService>((methodInfo, args) => methodInfo.Name switch
            {
                nameof(IMetadataTagService.ReadAllTagsAndDuration) => Read((IFileInfo)args[0]),
                _ => throw new NotSupportedException(methodInfo.Name)
            });
            object Read(IFileInfo info)
            {
                readPaths.Add(info.FullName);
                var track = info.FullName.EndsWith("01.mp3") ? "Track 1" : "Track 2";
                return (new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ARTIST"] = new() { "Test Author" },
                    ["ALBUM"] = new() { "Alpha" },
                    ["TITLE"] = new() { track }
                }, (int?)60);
            }

            var persisted = new List<int>();
            var queue = CreateProxy<IIngestQueueRepository>((methodInfo, args) => methodInfo.Name switch
            {
                nameof(IIngestQueueRepository.UpdateBatchTagsAndDuration) => RecordUpdates(args[0]),
                _ => throw new NotSupportedException(methodInfo.Name)
            });
            object RecordUpdates(object value)
            {
                foreach (var update in (IEnumerable<(int Id, string TagsJson, int? DurationSeconds)>)value)
                {
                    persisted.Add(update.Id);
                }

                return null;
            }

            SetField(worker, "_diskProvider", disk);
            SetField(worker, "_metadataTagService", metadata);
            SetField(worker, "_ingestQueue", queue);
            SetField(worker, "_logger", LogManager.GetCurrentClassLogger());

            var staged = new List<IngestQueueItem>
            {
                QueueItem(1, firstPath, "{}"),
                QueueItem(2, secondPath, "{}")
            };
            var unit = method.Invoke(worker, new object[] { staged[0], staged, new RootFolder { Path = "/library" } });
            var files = (IEnumerable<DiscoveredFileWithMetadata>)unit.GetType().GetProperty("Files")?.GetValue(unit);

            Assert.Multiple(() =>
            {
                Assert.That(files?.Select(file => file.Path), Is.EquivalentTo(new[] { firstPath, secondPath }));
                Assert.That(readPaths, Is.EquivalentTo(new[] { firstPath, secondPath }));
                Assert.That(readPaths, Does.Not.Contain(otherExtension));
                Assert.That(persisted, Is.EquivalentTo(new[] { 1, 2 }));
            });
        }

        [Test]
        public void try_import_author_with_optional_path_fallback_should_try_embedded_tags_before_path_tags()
        {
            var worker = (DiscoveryWorker)FormatterServices.GetUninitializedObject(typeof(DiscoveryWorker));
            var method = typeof(DiscoveryWorker).GetMethod("TryImportAuthorUnitWithOptionalPathFallback", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var v5 = new RecordingV5MatchingService();
            SetField(worker, "_v5MatchingService", v5);
            SetField(worker, "_logger", LogManager.GetCurrentClassLogger());

            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ALBUM"] = new List<string> { "Dreamer of Dune" }
            };

            var result = (bool)method.Invoke(worker, new object[]
            {
                tags,
                new List<DiscoveredFileWithMetadata> { new() { Path = "/library/Frank Herbert/Dreamer of Dune/01.mp3", AllTags = tags } },
                "/library/Frank Herbert/Dreamer of Dune/01.mp3",
                BookMediaType.Audiobook,
                null,
                "/library/Frank Herbert",
                null,
                "/library",
                true,
                true,
                false
            });

            Assert.That(result, Is.False);
            Assert.That(v5.Calls.Count, Is.EqualTo(2), "embedded-tag miss should still allow one path fallback attempt");
            Assert.That(v5.Calls[0].ContainsKey("ALBUM"), Is.True, "embedded tags should be sent first");
            Assert.That(v5.Calls[0]["ALBUM"], Is.EquivalentTo(new[] { "Dreamer of Dune" }));
            Assert.That(v5.Calls[1].ContainsKey("AUTHOR"), Is.True, "path tags should only be sent after embedded tags fail");
            Assert.That(v5.Calls[1]["AUTHOR"], Is.EquivalentTo(new[] { "Frank Herbert" }));
        }

        [Test]
        public void discovery_should_retry_with_path_when_an_authorish_field_contains_a_narrator_not_a_competing_author()
        {
            var worker = (DiscoveryWorker)FormatterServices.GetUninitializedObject(typeof(DiscoveryWorker));
            var method = typeof(DiscoveryWorker).GetMethod("TryImportAuthorUnitWithOptionalPathFallback", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var v5 = new RecordingV5MatchingService();
            v5.OnSearch = (_, _, _, _) => v5.Calls.Count == 1
                ? new List<V5MatchedAuthor>
                {
                    new V5MatchedAuthor { id = "hc:jk-rowling", name = "J.K. Rowling" }
                }
                : new List<V5MatchedAuthor>();

            SetField(worker, "_v5MatchingService", v5);
            SetField(worker, "_containmentValidator", new ContainmentValidator(new TagNormalizer(), LogManager.GetCurrentClassLogger()));
            SetField(worker, "_logger", LogManager.GetCurrentClassLogger());

            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = new List<string> { "Harry Potter and the Order of the Phoenix" },
                ["ARTIST"] = new List<string> { "Jim Dale" }
            };

            var result = (bool)method.Invoke(worker, new object[]
            {
                tags,
                new List<DiscoveredFileWithMetadata> { new() { Path = "/library/J.K. Rowling/Harry Potter and the Order of the Phoenix/01.mp3", AllTags = tags } },
                "/library/J.K. Rowling/Harry Potter and the Order of the Phoenix/01.mp3",
                BookMediaType.Audiobook,
                null,
                "/library/J.K. Rowling",
                null,
                "/library",
                true,
                true,
                false
            });

            Assert.That(result, Is.False);
            Assert.That(v5.Calls, Has.Count.EqualTo(2), "A field label alone must not suppress path recovery.");
            Assert.That(v5.Calls[1]["ARTIST"], Does.Contain("Jim Dale"), "Embedded evidence must be retained on retry.");
            Assert.That(v5.Calls[1]["AUTHOR"], Is.EquivalentTo(new[] { "J.K. Rowling" }), "The path must supply the missing author evidence.");
        }

        [Test]
        public void discovery_should_block_path_retry_when_a_surviving_value_proves_a_competing_v5_author()
        {
            var worker = (DiscoveryWorker)FormatterServices.GetUninitializedObject(typeof(DiscoveryWorker));
            var method = typeof(DiscoveryWorker).GetMethod("TryImportAuthorUnitWithOptionalPathFallback", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var v5 = new RecordingV5MatchingService
            {
                OnSearch = (_, _, _, _) => new List<V5MatchedAuthor>
                {
                    new V5MatchedAuthor { id = "hc:jk-rowling", name = "J.K. Rowling" },
                    new V5MatchedAuthor { id = "hc:stephen-king", name = "Stephen King" }
                }
            };

            SetField(worker, "_v5MatchingService", v5);
            SetField(worker, "_containmentValidator", new ContainmentValidator(new TagNormalizer(), LogManager.GetCurrentClassLogger()));
            SetField(worker, "_logger", LogManager.GetCurrentClassLogger());

            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = new List<string> { "The Shining" },
                ["ODD_FIELD"] = new List<string> { "Stephen King" }
            };

            var result = (bool)method.Invoke(worker, new object[]
            {
                tags,
                new List<DiscoveredFileWithMetadata> { new() { Path = "/library/J.K. Rowling/The Shining/The Shining.epub", AllTags = tags } },
                "/library/J.K. Rowling/The Shining/The Shining.epub",
                BookMediaType.Ebook,
                null,
                "/library/J.K. Rowling",
                null,
                "/library",
                true,
                false,
                true
            });

            Assert.That(result, Is.False);
            Assert.That(v5.Calls, Has.Count.EqualTo(1), "Positive competing-author evidence must prevent a path from overwriting it.");
        }

        [Test]
        public void discovery_should_not_treat_an_excluded_comment_as_competing_author_evidence()
        {
            var worker = (DiscoveryWorker)FormatterServices.GetUninitializedObject(typeof(DiscoveryWorker));
            var method = typeof(DiscoveryWorker).GetMethod("TryImportAuthorUnitWithOptionalPathFallback", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var v5 = new RecordingV5MatchingService();
            v5.OnSearch = (_, _, _, _) => v5.Calls.Count == 1
                ? new List<V5MatchedAuthor>
                {
                    new V5MatchedAuthor { id = "hc:jk-rowling", name = "J.K. Rowling" },
                    new V5MatchedAuthor { id = "hc:stephen-king", name = "Stephen King" }
                }
                : new List<V5MatchedAuthor>();

            SetField(worker, "_v5MatchingService", v5);
            SetField(worker, "_containmentValidator", new ContainmentValidator(new TagNormalizer(), LogManager.GetCurrentClassLogger()));
            SetField(worker, "_logger", LogManager.GetCurrentClassLogger());

            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["TITLE"] = new List<string> { "Harry Potter and the Order of the Phoenix" },
                ["COMMENT"] = new List<string> { "For readers of Stephen King" }
            };

            var result = (bool)method.Invoke(worker, new object[]
            {
                tags,
                new List<DiscoveredFileWithMetadata> { new() { Path = "/library/J.K. Rowling/Harry Potter and the Order of the Phoenix/book.epub", AllTags = tags } },
                "/library/J.K. Rowling/Harry Potter and the Order of the Phoenix/book.epub",
                BookMediaType.Ebook,
                null,
                "/library/J.K. Rowling",
                null,
                "/library",
                true,
                false,
                true
            });

            Assert.That(result, Is.False);
            Assert.That(v5.Calls, Has.Count.EqualTo(2), "Excluded comments must not manufacture a competing-author contradiction.");
            Assert.That(v5.Calls[1]["AUTHOR"], Is.EquivalentTo(new[] { "J.K. Rowling" }));
        }

        [Test]
        public void mixed_root_discovery_should_create_only_file_types_found()
        {
            var method = typeof(DiscoveryWorker).GetMethod("ResolveCreateMediaTypes", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var mixed = new RootFolder { FolderType = FolderType.Mixed };

            var audioOnly = ((bool CreateAudiobook, bool CreateEbook))method.Invoke(null, new object[] { mixed, true, false });
            var both = ((bool CreateAudiobook, bool CreateEbook))method.Invoke(null, new object[] { mixed, true, true });
            var ebookOnly = ((bool CreateAudiobook, bool CreateEbook))method.Invoke(null, new object[] { mixed, false, true });

            Assert.Multiple(() =>
            {
                Assert.That(audioOnly.CreateAudiobook, Is.True);
                Assert.That(audioOnly.CreateEbook, Is.False);
                Assert.That(both.CreateAudiobook, Is.True);
                Assert.That(both.CreateEbook, Is.True);
                Assert.That(ebookOnly.CreateAudiobook, Is.False);
                Assert.That(ebookOnly.CreateEbook, Is.True);
            });
        }

        [Test]
        public void dedicated_root_discovery_should_keep_root_media_type_even_before_files_are_counted()
        {
            var method = typeof(DiscoveryWorker).GetMethod("ResolveCreateMediaTypes", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var audiobookRoot = new RootFolder { FolderType = FolderType.Audiobook };
            var ebookRoot = new RootFolder { FolderType = FolderType.Ebook };

            var audiobook = ((bool CreateAudiobook, bool CreateEbook))method.Invoke(null, new object[] { audiobookRoot, false, false });
            var ebook = ((bool CreateAudiobook, bool CreateEbook))method.Invoke(null, new object[] { ebookRoot, false, false });

            Assert.Multiple(() =>
            {
                Assert.That(audiobook.CreateAudiobook, Is.True);
                Assert.That(audiobook.CreateEbook, Is.False);
                Assert.That(ebook.CreateAudiobook, Is.False);
                Assert.That(ebook.CreateEbook, Is.True);
            });
        }
#pragma warning restore SYSLIB0050

        private static IngestQueueItem QueueItem(int id, string path, string tagsJson)
        {
            return new IngestQueueItem
            {
                Id = id,
                Path = path,
                TagsJson = tagsJson,
                Status = "queued"
            };
        }

        private static Dictionary<string, List<string>> CloneTags(IDictionary<string, List<string>> tags)
        {
            var clone = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (tags == null)
            {
                return clone;
            }

            foreach (var kv in tags)
            {
                if (string.IsNullOrWhiteSpace(kv.Key))
                {
                    continue;
                }

                clone[kv.Key] = kv.Value != null ? new List<string>(kv.Value) : new List<string>();
            }

            return clone;
        }

        private static void SetField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Unable to locate private field {fieldName}");
            field.SetValue(target, value);
        }
    }
}
