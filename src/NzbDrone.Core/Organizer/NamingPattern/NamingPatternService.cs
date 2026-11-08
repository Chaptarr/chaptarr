using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Organizer.NamingPattern
{
    public interface INamingPatternService
    {
        string CompilePattern(PatternAst ast);
        PatternAst DecompilePattern(string pattern);
        ValidationResult ValidatePattern(string pattern = null, PatternAst ast = null);
        PreviewResult PreviewPattern(string pattern = null, PatternAst ast = null, SampleContext sampleContext = null);
    }

    public class NamingPatternService : INamingPatternService
    {
        private readonly IPatternCompiler _compiler;
        private readonly IBuildFileNames _fileNameBuilder;

        public NamingPatternService(IPatternCompiler compiler, IBuildFileNames fileNameBuilder)
        {
            _compiler = compiler;
            _fileNameBuilder = fileNameBuilder;
        }

        public string CompilePattern(PatternAst ast)
        {
            try
            {
                return _compiler.Compile(ast);
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Failed to compile pattern: {ex.Message}", ex);
            }
        }

        public PatternAst DecompilePattern(string pattern)
        {
            try
            {
                return _compiler.Decompile(pattern);
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Failed to decompile pattern: {ex.Message}", ex);
            }
        }

        public ValidationResult ValidatePattern(string pattern = null, PatternAst ast = null)
        {
            var result = new ValidationResult();

            try
            {
                var patternToValidate = pattern;

                if (ast != null)
                {
                    var compiled = CompilePattern(ast);
                    result.NormalizedPattern = compiled;

                    if (patternToValidate == null)
                    {
                        patternToValidate = compiled;
                    }
                }

                if (string.IsNullOrWhiteSpace(patternToValidate))
                {
                    result.Errors.Add(new ValidationError 
                    { 
                        Code = "EMPTY_PATTERN", 
                        Message = "Pattern cannot be empty" 
                    });
                }
                else if (!IsValidStandardBookFormat(patternToValidate))
                {
                    result.Errors.Add(new ValidationError 
                    { 
                        Code = "INVALID_FORMAT", 
                        Message = "Must contain Book Title AND PartNumber, OR Original Title" 
                    });
                }

                // Additional AST-specific validations
                if (ast != null)
                {
                    ValidateAst(ast, result);
                }

                // Validate against actual runtime naming behavior.
                // Single-file samples omit part tokens, so multipart formats need their own evaluation.
                if (!result.Errors.Any())
                {
                    try
                    {
                        var sampleContext = CreateDefaultSampleContext();
                        _ = BuildSampleFileName(patternToValidate, sampleContext);
                        var firstPartName = BuildSampleFileName(patternToValidate, sampleContext, partCount: 2, partNumber: 1);
                        var secondPartName = BuildSampleFileName(patternToValidate, sampleContext, partCount: 2, partNumber: 2);
                        // Compare completed names so one token can distinguish a constant token elsewhere.
                        if (string.Equals(firstPartName, secondPartName, DiskProviderBase.PathStringComparison))
                        {
                            throw new NamingFormatException("Multipart pattern produces the same filename for separate parts.");
                        }
                    }
                    catch (NamingFormatException ex)
                    {
                        result.Errors.Add(new ValidationError
                        {
                            Code = "VALIDATION_ERROR",
                            Message = ex.Message
                        });
                    }
                }

                result.IsValid = !result.Errors.Any();
            }
            catch (Exception ex)
            {
                result.Errors.Add(new ValidationError 
                { 
                    Code = "EXCEPTION", 
                    Message = ex.Message 
                });
                result.IsValid = false;
            }

            return result;
        }

        public PreviewResult PreviewPattern(string pattern = null, PatternAst ast = null, SampleContext sampleContext = null)
        {
            try
            {
                var patternToPreview = pattern;

                if (ast != null)
                {
                    var compiled = CompilePattern(ast);

                    if (patternToPreview == null)
                    {
                        patternToPreview = compiled;
                    }
                }

                if (string.IsNullOrWhiteSpace(patternToPreview))
                {
                    return new PreviewResult { Path = string.Empty };
                }

                var previewPath = BuildSampleFileName(patternToPreview, sampleContext ?? CreateDefaultSampleContext());

                return new PreviewResult
                {
                    Path = previewPath,
                    Segments = new List<PreviewSegment> { new PreviewSegment { Text = previewPath, NodeId = null } }
                };
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Failed to preview pattern: {ex.Message}", ex);
            }
        }

        private void ValidateAst(PatternAst ast, ValidationResult result)
        {
            // Check for consecutive folder separators
            var consecutiveFolders = 0;
            foreach (var rootId in ast.RootIds)
            {
                if (ast.NodesById.TryGetValue(rootId, out var node) && 
                    node is SeparatorNode sep && sep.Value == "/")
                {
                    consecutiveFolders++;
                    if (consecutiveFolders > 1)
                    {
                        result.Errors.Add(new ValidationError
                        {
                            Code = "CONSECUTIVE_FOLDERS",
                            Message = "Cannot have consecutive folder separators",
                            Path = rootId
                        });
                    }
                }
                else
                {
                    consecutiveFolders = 0;
                }
            }

            // Check for empty groups
            foreach (var kvp in ast.NodesById)
            {
                if (kvp.Value is GroupNode group && !group.Children.Any())
                {
                    result.Errors.Add(new ValidationError
                    {
                        Code = "EMPTY_GROUP",
                        Message = "Group cannot be empty",
                        Path = kvp.Key
                    });
                }
            }

            // Check for valid token keys
            foreach (var kvp in ast.NodesById)
            {
                if (kvp.Value is TokenNode token)
                {
                    if (string.IsNullOrEmpty(token.TokenKey))
                    {
                        result.Errors.Add(new ValidationError
                        {
                            Code = "INVALID_TOKEN",
                            Message = "Token key cannot be empty",
                            Path = kvp.Key
                        });
                    }
                }
            }
        }

        private static bool IsValidStandardBookFormat(string pattern)
        {
            return (FileNameBuilder.BookTitleRegex.IsMatch(pattern) && FileNameBuilder.PartRegex.IsMatch(pattern)) ||
                   FileNameValidation.OriginalTokenRegex.IsMatch(pattern);
        }

        private string BuildSampleFileName(string pattern, SampleContext context, int partCount = 1, int partNumber = 1)
        {
            var braceError = FileNameValidation.GetBraceError(pattern);
            if (braceError != null)
            {
                throw new NamingFormatException(braceError);
            }

            var (author, edition, bookFile) = CreateSampleEntities(context, partNumber);
            bookFile.PartCount = partCount;

            var namingConfig = NamingConfig.Default;
            namingConfig.RenameBooks = namingConfig.EbookRenameBooks = true;
            namingConfig.StandardBookFormat = namingConfig.EbookStandardBookFormat = pattern;

            return _fileNameBuilder.BuildBookFileName(author, edition, bookFile, namingConfig, new List<CustomFormat>());
        }

        private static (Author author, Edition edition, BookFile bookFile) CreateSampleEntities(SampleContext context, int partNumber)
        {
            context ??= CreateDefaultSampleContext();

            var authorName = string.IsNullOrWhiteSpace(context.AuthorName) ? "Author Name" : context.AuthorName.Trim();
            var bookTitle = string.IsNullOrWhiteSpace(context.BookTitle) ? "Book Title" : context.BookTitle.Trim();
            var seriesTitle = string.IsNullOrWhiteSpace(context.SeriesTitle) ? "Series Title" : context.SeriesTitle.Trim();
            var seriesPosition = string.IsNullOrWhiteSpace(context.SeriesPosition) ? "1" : context.SeriesPosition.Trim();
            var narratorName = context.NarratorName?.Trim() ?? "Narrator Name";

            var year = DateTime.UtcNow.Year;
            if (int.TryParse(context.Year, out var parsedYear) && parsedYear is >= 1 and <= 9999)
            {
                year = parsedYear;
            }

            var author = new Author
            {
                Name = authorName,
                NameLastFirst = authorName
            };

            var series = new Series { Title = seriesTitle, GoodreadsSeriesId = "gr:40346" };
            var seriesLink = new SeriesBookLink
            {
                Position = seriesPosition,
                Series = series
            };

            var book = new Book
            {
                Title = bookTitle,
                ReleaseDate = new DateTime(year, 1, 1),
                Author = author,
                AuthorId = author.Id,
                SeriesLinks = new List<SeriesBookLink> { seriesLink }
            };

            var edition = new Edition
            {
                Title = bookTitle,
                Book = book,
                ReleaseDate = new DateTime(year, 1, 1),
                Narrator = narratorName,
                NarratorNames = context.NarratorNames ?? new List<string> { narratorName },
                Publisher = context.Publisher,
                AudioProductionType = context.AudioProductionType,
                IsGraphicAudio = context.IsGraphicAudio
            };

            var quality = Quality.MP3;
            if (!string.IsNullOrWhiteSpace(context.Quality))
            {
                var qualityName = context.Quality.Trim();
                var match = Quality.All.FirstOrDefault(q => q.Name.Equals(qualityName, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    quality = match;
                }
            }

            var bookFile = new BookFile
            {
                Quality = new QualityModel(quality, new Revision(1)),
                // Each part sample uses a distinct source filename for Original Title patterns.
                Path = "/music/Author.Name.Book.Name.Part" + partNumber.ToString("00", CultureInfo.InvariantCulture) + ".mp3",
                SceneName = "Author.Name.Book.Name.Part" + partNumber.ToString("00", CultureInfo.InvariantCulture),
                ReleaseGroup = "RlsGrp",
                Edition = edition,
                Part = partNumber,
                PartCount = 1
            };
            bookFile.MediaType = BookFile.DetermineMediaType(bookFile.Quality);
            if (bookFile.MediaType == "ebook")
            {
                book.MediaType = BookMediaType.Ebook;
                edition.Narrator = null;
                edition.NarratorNames = new List<string>();
            }

            series.MediaType = book.MediaType;

            return (author, edition, bookFile);
        }

        private static SampleContext CreateDefaultSampleContext()
        {
            return new SampleContext
            {
                AuthorName = "Author Name",
                BookTitle = "Book Title",
                SeriesTitle = "Series Title",
                SeriesPosition = "1",
                NarratorName = "Narrator Name",
                Quality = "MP3",
                Year = "2024"
            };
        }
    }

    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public List<ValidationError> Errors { get; set; } = new List<ValidationError>();
        public string NormalizedPattern { get; set; }
    }

    public class ValidationError
    {
        public string Code { get; set; }
        public string Message { get; set; }
        public string Path { get; set; } // Node ID path for AST errors
    }

    public class PreviewResult
    {
        public string Path { get; set; }
        public List<PreviewSegment> Segments { get; set; } = new List<PreviewSegment>();
    }

    public class PreviewSegment
    {
        public string Text { get; set; }
        public string NodeId { get; set; } // Links to AST node for highlighting
    }

    public class SampleContext
    {
        public string AuthorName { get; set; }
        public string BookTitle { get; set; }
        public string SeriesTitle { get; set; }
        public string SeriesPosition { get; set; }
        public string NarratorName { get; set; }
        public List<string> NarratorNames { get; set; }
        public string AudioProductionType { get; set; }
        public string Publisher { get; set; }
        public bool IsGraphicAudio { get; set; }
        public string Quality { get; set; }
        public string Year { get; set; }
    }
}
