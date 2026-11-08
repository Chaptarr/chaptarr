using System;
using Chaptarr.Api.V1.Indexers;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class ReleaseResourceMapperFixture
    {
        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void unclassified_newznab_release_must_not_inherit_target_media_type(BookMediaType target)
        {
            var release = new ReleaseInfo
            {
                Title = "Patrick Stewart - Making It So",
                Indexer = "NZBFinder",
                Size = 7587090,
                Categories = new System.Collections.Generic.List<int> { 7060, 107060 }
            };
            var quality = QualityParser.ParseQuality(release.Title, categories: release.Categories);
            var resource = new DownloadDecision(new RemoteBook
            {
                Books = new System.Collections.Generic.List<Book> { new() { MediaType = target } },
                Release = release,
                ParsedBookInfo = new ParsedBookInfo { Quality = quality }
            }).ToResource();

            Assert.That(quality.Quality.Id, Is.EqualTo(0));
            Assert.That(QualityMediaTypeHelper.DetectMediaType(quality.Quality, release), Is.Null);
            Assert.That(resource.Quality.Quality.Name, Is.EqualTo("Unknown"));
            Assert.That(resource.FormatType, Is.Null);
            Assert.That(System.Text.Json.JsonSerializer.Serialize(resource), Does.Not.Contain("Unknown Text"));
        }

        [TestCase("Patrick Stewart - Making It So [Audiobook]", 7000, "Unknown Audio", "audiobook")]
        [TestCase("Patrick Stewart - Making It So [ebook]", 7060, "Unknown", null)]
        [TestCase("Patrick Stewart - Making It So (Ungekuerzt)", 0, "Unknown Audio", "audiobook")]
        [TestCase("Patrick Stewart - Making It So [Audiobook]", 7020, "Unknown", "ebook")]
        [TestCase("Patrick Stewart - Making It So (Ungekuerzt)", 7020, "Unknown", "ebook")]
        [TestCase("Patrick Stewart - Making It So [Audiobook].epub", 3030, "EPUB", "ebook")]
        [TestCase("Patrick Stewart - Making It So [Audiobook].m4b", 7020, "M4B", "audiobook")]
        [TestCase("Patrick Stewart - Making It So", 7000, "Unknown", null)]
        [TestCase("Patrick Stewart - Making It So", 7020, "Unknown", "ebook")]
        [TestCase("Patrick Stewart - Making It So", 3030, "Unknown Audio", "audiobook")]
        [TestCase("Patrick Stewart - Making It So.epub", 7060, "EPUB", "ebook")]
        [TestCase("Patrick Stewart - Making It So.m4b", 7060, "M4B", "audiobook")]
        public void release_display_media_requires_positive_evidence(string title, int category, string qualityName, string mediaType)
        {
            var release = new ReleaseInfo { Title = title, Categories = new System.Collections.Generic.List<int> { category } };
            var quality = QualityParser.ParseQuality(title, categories: release.Categories);
            var resource = new DownloadDecision(new RemoteBook
            {
                Release = release,
                ParsedBookInfo = new ParsedBookInfo { Quality = quality }
            }).ToResource();

            Assert.That(resource.Quality.Quality.Name, Is.EqualTo(qualityName));
            Assert.That(resource.FormatType, Is.EqualTo(mediaType));
        }

        [Test]
        public void conflicting_media_categories_do_not_assert_an_unknown_audio_format()
        {
            var release = new ReleaseInfo { Title = "Patrick Stewart - Making It So", Categories = new() { 3030, 7020 } };
            var quality = QualityParser.ParseQuality(release.Title, categories: release.Categories);
            var resource = new DownloadDecision(new RemoteBook { Release = release, ParsedBookInfo = new() { Quality = quality } }).ToResource();
            Assert.That(resource.Quality.Quality.Name, Is.EqualTo("Unknown"));
            Assert.That(resource.FormatType, Is.Null);
        }

        [Test]
        public void conflicting_media_categories_can_use_canonical_audio_hint_fallback()
        {
            var release = new ReleaseInfo { Title = "Patrick Stewart - Making It So [Audiobook]", Categories = new() { 3030, 7020 } };
            var quality = QualityParser.ParseQuality(release.Title, categories: release.Categories);
            var resource = new DownloadDecision(new RemoteBook { Release = release, ParsedBookInfo = new() { Quality = quality } }).ToResource();
            Assert.That(resource.Quality.Quality.Name, Is.EqualTo("Unknown Audio"));
            Assert.That(resource.FormatType, Is.EqualTo("audiobook"));
        }

        [Test]
        public void release_without_quality_or_format_evidence_does_not_default_to_audio()
        {
            var resource = new DownloadDecision(new RemoteBook { Release = new ReleaseInfo { Title = "Unclassified release" } }).ToResource();
            Assert.That(resource.FormatType, Is.Null);
            Assert.That(resource.Quality.Quality.Name, Is.EqualTo("Unknown"));
        }

        [TestCase("Scott Brick", null)]
        [TestCase(null, "10:30:00")]
        [TestCase("Scott Brick", "10:30:00")]
        public void torrent_narrator_and_duration_do_not_prove_media_type(string narrator, string duration)
        {
            var release = new TorrentInfo
            {
                Title = "Unclassified release",
                Categories = new() { 7000 },
                Narrator = narrator,
                Duration = duration
            };
            var resource = new DownloadDecision(new RemoteBook
            {
                Release = release,
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.Unknown) }
            }).ToResource();

            Assert.That(resource.FormatType, Is.Null);
            Assert.That(resource.Quality.Quality.Name, Is.EqualTo("Unknown"));
        }

        [TestCase(1, null, "audiobook")]
        [TestCase(2, null, "ebook")]
        [TestCase(0, "Audiobooks - Fantasy", "audiobook")]
        [TestCase(0, "Ebooks - Fantasy", "ebook")]
        public void legacy_tracker_hints_label_the_api_without_changing_acquisition(int mediaType, string categoryName, string expected)
        {
            var release = new TorrentInfo { Title = "Known tracker media", MediaType = mediaType, CategoryName = categoryName };
            var resource = new DownloadDecision(new RemoteBook
            {
                Release = release,
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.Unknown) }
            }).ToResource();
            Assert.That(resource.FormatType, Is.EqualTo(expected));
            Assert.That(QualityMediaTypeHelper.DetectMediaType(Quality.Unknown, release), Is.Null);
        }

        [TestCase(1, "Audiobooks - Fantasy", "ebook")]
        [TestCase(2, "Ebooks - Fantasy", "audiobook")]
        public void canonical_format_evidence_wins_over_conflicting_display_hints(int mediaType, string categoryName, string expected)
        {
            var release = new TorrentInfo { Title = "Known format", MediaType = mediaType, CategoryName = categoryName };
            var resource = new DownloadDecision(new RemoteBook
            {
                Release = release,
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(mediaType == 1 ? Quality.EPUB : Quality.M4B) }
            }).ToResource();
            Assert.That(resource.FormatType, Is.EqualTo(expected));
        }

        [TestCase(NzbDrone.Core.Books.BookMediaType.Audiobook, "Scott Brick")]
        [TestCase(NzbDrone.Core.Books.BookMediaType.Ebook, null)]
        public void raw_narrator_fields_should_display_only_known_audio_target_names(NzbDrone.Core.Books.BookMediaType media, string expected)
        {
            var book = new NzbDrone.Core.Books.Book
            {
                MediaType = media, AnyEditionOk = false,
                Editions = new System.Collections.Generic.List<NzbDrone.Core.Books.Edition>
                {
                    new NzbDrone.Core.Books.Edition { ReadingFormatId = 2, ManualAdd = true, NarratorNames = new System.Collections.Generic.List<string> { "Scott Brick" } }
                }
            };
            var remote = new RemoteBook
            {
                Books = new System.Collections.Generic.List<NzbDrone.Core.Books.Book> { book },
                Release = new ReleaseInfo { Title = "Dune", NarratorEvidenceFields = new System.Collections.Generic.List<string> { "Private tracker text: lu par Scott Brick; other text" } },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(media == NzbDrone.Core.Books.BookMediaType.Audiobook ? Quality.MP3 : Quality.EPUB) }
            };
            var resource = new DownloadDecision(remote).ToResource();
            var json = System.Text.Json.JsonSerializer.Serialize(resource);
            Assert.Multiple(() =>
            {
                Assert.That(resource.Narrator, Is.EqualTo(expected));
                Assert.That(json, Does.Not.Contain("NarratorEvidenceFields"));
                Assert.That(json, Does.Not.Contain("Private tracker text"));
            });
        }

        [Test]
        public void raw_narrator_fields_should_display_known_same_row_names_without_a_pin()
        {
            var remote = new RemoteBook
            {
                Books = new System.Collections.Generic.List<Book>
                {
                    new Book { MediaType = BookMediaType.Audiobook, AnyEditionOk = true,
                        Editions = new System.Collections.Generic.List<Edition> { new Edition { ReadingFormatId = 2, NarratorNames = new System.Collections.Generic.List<string> { "Scott Brick" } } } }
                },
                Release = new ReleaseInfo { Title = "Dune", NarratorEvidenceFields = new System.Collections.Generic.List<string> { "Lu par Scott Brick; private text" } },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.MP3) }
            };
            Assert.That(new DownloadDecision(remote).ToResource().Narrator, Is.EqualTo("Scott Brick"));
        }

        [Test]
        public void structured_mam_author_mismatch_should_flow_to_interactive_search_rejection_reason()
        {
            var book = new Book
            {
                Title = "Galatea",
                MediaType = BookMediaType.Ebook
            };
            var author = new Author
            {
                Name = "Philip Pullman",
                Books = new System.Collections.Generic.List<Book> { book }
            };
            book.Author = author;

            var criteria = new BookSearchCriteria
            {
                Author = author,
                Books = new System.Collections.Generic.List<Book> { book },
                InteractiveSearch = true
            };
            var remoteBook = new RemoteBook
            {
                Author = author,
                Books = new System.Collections.Generic.List<Book> { book },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = author.Name,
                    BookTitle = book.Title,
                    Quality = new QualityModel(Quality.Unknown)
                },
                Release = new ReleaseInfo
                {
                    Guid = "MAM-556642",
                    Title = "Galatea",
                    Author = "Madeline Miller",
                    Indexer = "MyAnonaMouse",
                    PublishDate = DateTime.UtcNow
                }
            };

            var specification = new ReleaseTitleMatchSpecification(NLog.LogManager.GetCurrentClassLogger());
            var specificationDecision = specification.IsSatisfiedBy(remoteBook, criteria);

            Assert.That(specificationDecision.Accepted, Is.False);
            Assert.That(specificationDecision.CanBypass, Is.True);
            Assert.That(specificationDecision.Category, Is.EqualTo("Matching"));
            Assert.That(specificationDecision.Reason, Is.EqualTo("Author mismatch: Madeline Miller"));

            var rejection = new Rejection(
                specificationDecision.Reason,
                RejectionType.Permanent,
                specificationDecision.CanBypass,
                specificationDecision.Category,
                specificationDecision.Severity);
            var resource = new DownloadDecision(remoteBook, rejection).ToResource();

            Assert.That(resource.Rejections, Does.Contain("Author mismatch: Madeline Miller"));
            Assert.That(resource.RejectionDetails, Has.Count.EqualTo(1));
            Assert.That(resource.RejectionDetails[0].Reason, Is.EqualTo("Author mismatch: Madeline Miller"));
            Assert.That(resource.RejectionDetails[0].Category, Is.EqualTo("Matching"));
            Assert.That(resource.RejectionDetails[0].CanBypass, Is.True);
            Assert.That(resource.HasSoftFilters, Is.True);
            Assert.That(resource.HasHardFilters, Is.False);
            Assert.That(resource.CanBypassFilters, Is.True);
        }

        [Test]
        public void structured_book_match_should_not_highlight_unrelated_release_title_offsets()
        {
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo { Title = "MP3 unabridged", Book = "Dune", Author = "Frank Herbert", Indexer = "MyAnonaMouse" },
                ParsedBookInfo = new ParsedBookInfo { AuthorName = "Frank Herbert", BookTitle = "Dune", Quality = new QualityModel(Quality.MP3) },
                SearchCriteriaMatch = new TitleMatchResult { IsMatch = true, PrimaryTitle = "Dune", MatchedVariant = "Dune", EvidenceTitle = "Dune", MatchedStart = 0, MatchedEnd = 0 }
            };
            var resource = new DownloadDecision(remoteBook).ToResource();
            Assert.That(resource.MatchedTitle, Is.EqualTo("Dune"));
            Assert.That(resource.MatchedTitleCharStart, Is.Null);
            Assert.That(resource.MatchedTitleCharEnd, Is.Null);
        }

        [Test]
        public void should_not_throw_when_parsed_book_info_is_missing()
        {
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Title = "The Housemaid",
                    Indexer = "MyAnonaMouse",
                    PublishDate = DateTime.UtcNow
                }
            };

            var decision = new DownloadDecision(remoteBook);

            Assert.DoesNotThrow(() => decision.ToResource());

            var resource = decision.ToResource();
            Assert.That(resource.Quality, Is.Not.Null);
            Assert.That(resource.Quality.Quality, Is.EqualTo(Quality.Unknown));
        }

        [Test]
        public void should_include_resolved_author_and_book_ids_for_single_book_results()
        {
            var author = new Author
            {
                Id = 11,
                Name = "Freida McFadden"
            };

            var book = new Book
            {
                Id = 1493,
                AuthorId = author.Id,
                Title = "Dead Med"
            };

            var remoteBook = new RemoteBook
            {
                Author = author,
                Books = new System.Collections.Generic.List<Book> { book },
                Release = new ReleaseInfo
                {
                    Title = "Dead Med",
                    Indexer = "MyAnonaMouse",
                    PublishDate = DateTime.UtcNow
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = author.Name,
                    BookTitle = book.Title,
                    Quality = new QualityModel(Quality.M4B)
                }
            };

            var resource = new DownloadDecision(remoteBook).ToResource();

            Assert.That(resource.AuthorId, Is.EqualTo(author.Id));
            Assert.That(resource.BookId, Is.EqualTo(book.Id));
        }

        [Test]
        public void should_fallback_to_book_author_id_when_remote_author_is_missing()
        {
            var book = new Book
            {
                Id = 1493,
                AuthorId = 11,
                Title = "Dead Med"
            };

            var remoteBook = new RemoteBook
            {
                Books = new System.Collections.Generic.List<Book> { book },
                Release = new ReleaseInfo
                {
                    Title = "Dead Med",
                    Indexer = "MyAnonaMouse",
                    PublishDate = DateTime.UtcNow
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "Freida McFadden",
                    BookTitle = book.Title,
                    Quality = new QualityModel(Quality.M4B)
                }
            };

            var resource = new DownloadDecision(remoteBook).ToResource();

            Assert.That(resource.AuthorId, Is.EqualTo(book.AuthorId));
            Assert.That(resource.BookId, Is.EqualTo(book.Id));
        }

        [Test]
        public void should_clean_only_metadata_displayed_in_other_release_columns()
        {
            var remoteBook = new RemoteBook
            {
                Release = new TorrentInfo
                {
                    Title = "Pierce Brown - Red Rising [Tim Gerard Reynolds] [MP3] [Freeleech]",
                    Indexer = "MyAnonaMouse",
                    PublishDate = DateTime.UtcNow,
                    Narrator = "Tim Gerard Reynolds",
                    IndexerFlags = IndexerFlags.Freeleech
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "Pierce Brown",
                    BookTitle = "Red Rising",
                    Quality = new QualityModel(Quality.MP3)
                }
            };

            var resource = new DownloadDecision(remoteBook).ToResource();

            Assert.That(resource.Title, Is.EqualTo("Pierce Brown - Red Rising [Tim Gerard Reynolds] [MP3] [Freeleech]"));
            Assert.That(resource.DisplayTitle, Is.EqualTo("Pierce Brown - Red Rising"));
        }

        [Test]
        public void should_not_clean_author_or_unshown_edition_text_from_release_title()
        {
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Title = "Pierce Brown - Red Rising (Unabridged) Part 1, Chapter 08",
                    Indexer = "MyAnonaMouse",
                    PublishDate = DateTime.UtcNow,
                    Narrator = "Pierce Brown"
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "Pierce Brown",
                    BookTitle = "Red Rising",
                    Quality = new QualityModel(Quality.MP3)
                }
            };

            var resource = new DownloadDecision(remoteBook).ToResource();

            Assert.That(resource.DisplayTitle, Is.EqualTo("Pierce Brown - Red Rising (Unabridged) Part 1, Chapter 08"));
        }

        [Test]
        public void should_only_set_matched_title_for_search_criteria_match()
        {
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Title = "Test Author - Bury Me",
                    Indexer = "TestIndexer",
                    PublishDate = DateTime.UtcNow
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "Test Author",
                    BookTitle = "Bury Me",
                    Quality = new QualityModel(Quality.M4B)
                },
                SearchCriteriaMatch = new TitleMatchResult
                {
                    IsMatch = true,
                    PrimaryTitle = "Bury Me",
                    MatchedVariant = "Bury Me",
                    MatchedStart = 2,
                    MatchedEnd = 3
                }
            };

            var resource = new DownloadDecision(remoteBook).ToResource();

            Assert.That(resource.BookTitle, Is.EqualTo("Bury Me"));
            Assert.That(resource.MatchedTitle, Is.EqualTo("Bury Me"));
            Assert.That(resource.MatchedTitleCharStart, Is.EqualTo(14));
            Assert.That(resource.MatchedTitleCharEnd, Is.EqualTo(21));
        }

        [Test]
        public void should_set_matched_title_char_span_for_symbol_normalized_title()
        {
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Title = "First Word & Other Words",
                    Indexer = "TestIndexer",
                    PublishDate = DateTime.UtcNow
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "Test Author",
                    BookTitle = "First Word and Other Words",
                    Quality = new QualityModel(Quality.M4B)
                },
                SearchCriteriaMatch = new TitleMatchResult
                {
                    IsMatch = true,
                    PrimaryTitle = "First Word and Other Words",
                    MatchedVariant = "First Word and Other Words",
                    MatchedStart = 0,
                    MatchedEnd = 4
                }
            };

            var resource = new DownloadDecision(remoteBook).ToResource();

            Assert.That(resource.MatchedTitle, Is.EqualTo("First Word and Other Words"));
            Assert.That(resource.MatchedTitleCharStart, Is.EqualTo(0));
            Assert.That(resource.MatchedTitleCharEnd, Is.EqualTo(24));
        }

        [Test]
        public void should_use_stored_span_occurrence_for_repeated_title_highlight()
        {
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Title = "Bury Me - Bury Me",
                    Indexer = "TestIndexer",
                    PublishDate = DateTime.UtcNow
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "Test Author",
                    BookTitle = "Bury Me",
                    Quality = new QualityModel(Quality.M4B)
                },
                SearchCriteriaMatch = new TitleMatchResult
                {
                    IsMatch = true,
                    PrimaryTitle = "Bury Me",
                    MatchedVariant = "Bury Me",
                    MatchedStart = 2,
                    MatchedEnd = 3
                }
            };

            var resource = new DownloadDecision(remoteBook).ToResource();

            Assert.That(resource.MatchedTitleCharStart, Is.EqualTo(10));
            Assert.That(resource.MatchedTitleCharEnd, Is.EqualTo(17));
        }

        [Test]
        public void should_not_use_parser_book_title_as_matched_title_without_search_match()
        {
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Title = "Jim Hendricks-Home On The Range(1996)) - 14 The Colorado Trail_Bury Me Not On",
                    Indexer = "DrunkenSlug (Prowlarr)",
                    PublishDate = DateTime.UtcNow
                },
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = "Jim Hendricks",
                    BookTitle = "Home On The Range",
                    Quality = new QualityModel(Quality.MP3)
                },
                SearchCriteriaMatch = null
            };

            var resource = new DownloadDecision(remoteBook).ToResource();

            Assert.That(resource.BookTitle, Is.EqualTo("Home On The Range"));
            Assert.That(resource.MatchedTitle, Is.Null);
        }

        private sealed class MappingController : ReleaseControllerBase
        {
            public ReleaseResource Map(DownloadDecision decision) => MapDecision(decision, 4);
        }

        [TestCase(BookMediaType.Ebook, "m4b", 232)] [TestCase(BookMediaType.Audiobook, "epub", 132)]
        [TestCase(BookMediaType.Ebook, "unknown", 232)] [TestCase(BookMediaType.Audiobook, "unknown", 132)]
        [TestCase(null, "epub", 232)] [TestCase(null, "m4b", 132)]
        public void quality_weight_should_use_the_resolved_target_profile(BookMediaType? media, string format, int expectedWeight)
        {
            var quality = format == "epub" ? Quality.EPUB : format == "m4b" ? Quality.M4B : Quality.Unknown;
            var remote = new RemoteBook
            {
                Author = WeightProfiles(quality),
                Books = media.HasValue ? new System.Collections.Generic.List<Book> { new() { MediaType = media.Value } } : new System.Collections.Generic.List<Book>(),
                Release = new ReleaseInfo { Title = "Paper Lanterns" },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(quality, new Revision(2, 3)) }
            };
            var resource = new MappingController().Map(new DownloadDecision(remote));
            Assert.Multiple(() =>
            {
                Assert.That(resource.QualityWeight, Is.EqualTo(expectedWeight));
                Assert.That(resource.Quality.Quality, Is.EqualTo(quality));
                Assert.That(resource.ReleaseWeight, Is.EqualTo(4));
                Assert.That(resource.Rank, Is.EqualTo(5));
                Assert.That(resource.IsPreferredChoice, Is.False);
            });
        }

        [TestCase(false)] [TestCase(true)]
        public void unresolved_media_should_not_borrow_a_profile_quality_weight(bool mixed)
        {
            var remote = new RemoteBook
            {
                Author = WeightProfiles(Quality.Unknown),
                Books = mixed ? new System.Collections.Generic.List<Book> { new() { MediaType = BookMediaType.Audiobook }, new() { MediaType = BookMediaType.Ebook } } : new System.Collections.Generic.List<Book>(),
                Release = new ReleaseInfo { Title = "The Ebook Handbook" },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.Unknown, new Revision(2, 3)) }
            };
            Assert.That(new MappingController().Map(new DownloadDecision(remote)).QualityWeight, Is.EqualTo(32));
        }

        private static Author WeightProfiles(Quality quality) => new()
        {
            AudiobookQualityProfileId = 2,
            AudiobookQualityProfile = new NzbDrone.Core.Profiles.Qualities.QualityProfile
            {
                Id = 2, Items = new System.Collections.Generic.List<NzbDrone.Core.Profiles.Qualities.QualityProfileQualityItem>
                {
                    new() { Quality = Quality.FLAC }, new() { Quality = quality }
                }
            },
            EbookQualityProfileId = 3,
            EbookQualityProfile = new NzbDrone.Core.Profiles.Qualities.QualityProfile
            {
                Id = 3, Items = new System.Collections.Generic.List<NzbDrone.Core.Profiles.Qualities.QualityProfileQualityItem>
                {
                    new() { Quality = Quality.PDF }, new() { Quality = Quality.FLAC }, new() { Quality = quality }
                }
            }
        };
    }
}
