using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Api.V1;
using Chaptarr.Api.V1.MediaTypes;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.MediaCover;

namespace Chaptarr.Api.V1.Series
{
    [V1ApiController]
    public class SeriesController : Controller
    {
        protected readonly ISeriesService _seriesService;
        private readonly ISeriesNarratorService _seriesNarratorService;
        private readonly ISeriesNarratorDiscoveryService _narratorDiscoveryService;
        private readonly ISeriesVariantService _seriesVariantService;
        private readonly IMediaCoverProxy _mediaCoverProxy;
        private readonly Logger _logger;

        public SeriesController(ISeriesService seriesService,
                                ISeriesNarratorService seriesNarratorService,
                                ISeriesNarratorDiscoveryService narratorDiscoveryService,
                                ISeriesVariantService seriesVariantService,
                                IMediaCoverProxy mediaCoverProxy,
                                Logger logger)
        {
            _seriesService = seriesService;
            _seriesNarratorService = seriesNarratorService;
            _narratorDiscoveryService = narratorDiscoveryService;
            _seriesVariantService = seriesVariantService;
            _mediaCoverProxy = mediaCoverProxy;
            _logger = logger;
        }

        [HttpGet]
        public List<SeriesResource> GetSeries(int authorId, [FromQuery] string mediaType = null)
        {
            var series = _seriesService.GetByAuthorId(authorId);
            var requestedMediaType = MediaTypeParameterParser.ParseOptional(mediaType);

            if (requestedMediaType.HasValue)
            {
                series = series.Where(s => s.MediaType == requestedMediaType.Value).ToList();
               _logger.Debug("Filtered series for author {0} by mediaType {1}: {2} series", authorId, mediaType, series.Count);
            }

            return ProxyRemoteImages(series.ToResource());
        }

        [HttpGet("{seriesId:int}")]
        public ActionResult<SeriesResource> GetSeriesById(int seriesId)
        {

            var series = _seriesService.GetSeries(seriesId);
            if (series == null)
            {
                return NotFound();
            }
            return ProxyRemoteImages(series.ToResource());
        }

        [HttpGet("{seriesId:int}/narrators")]
        public ActionResult<SeriesNarratorAnalysisResource> GetSeriesNarrators(int seriesId)
        {
            try
            {
                var series = _seriesService.GetSeries(seriesId);
                if (series == null)
                {
                    return NotFound($"Series with id {seriesId} not found");
                }

                var analysis = _seriesNarratorService.AnalyzeSeriesNarrators(seriesId);
                var sequentialNarrators = _seriesNarratorService.DetectSequentialOverlapNarrators(seriesId);
                var preferredNarrator = _seriesNarratorService.GetPreferredSeriesNarrator(seriesId);
                var booksWithoutNarrator = _seriesNarratorService.GetBooksWithoutPreferredNarrator(seriesId);

                var result = new SeriesNarratorAnalysisResource
                {
                    SeriesId = seriesId,
                    SeriesTitle = series.Title,
                    PreferredNarrator = preferredNarrator,
                    NarratorAnalysis = analysis.Select(a => new NarratorAnalysisResource
                    {
                        Narrator = a.Narrator,
                        BookCount = a.BookCount,
                        Percentage = Math.Round(a.Percentage, 1),
                        HasSequentialOverlap = a.HasSequentialOverlap,
                        IsMainNarrator = a.IsMainNarrator,
                        IsSporadicNarrator = a.IsSporadicNarrator
                    }).ToList(),
                    SequentialOverlapNarrators = sequentialNarrators,
                    BooksWithoutNarratorCount = booksWithoutNarrator.Count,
                    TotalBooksInSeries = _seriesNarratorService.GetNarratorBookCounts(seriesId).Values.Sum()
                };

                _logger.Debug("Retrieved narrator analysis for series {0}: {1} narrators, {2} with overlap", seriesId, result.NarratorAnalysis.Count, result.SequentialOverlapNarrators.Count);

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error getting series narrators for series {0}", seriesId);
                return StatusCode(500, new ApiErrorResource { Error = ex.Message });
            }
        }

        [HttpPost("{seriesId:int}/narrators/search")]
        public ActionResult<SeriesNarratorSearchResource> SearchSeriesNarrators(int seriesId)
        {
            try
            {
                var series = _seriesService.GetSeries(seriesId);
                if (series == null)
                {
                    return NotFound($"Series with id {seriesId} not found");
                }

                // This would integrate with the existing narrator search service
                // to discover narrators for all books in the series
                var analysisResult = _seriesNarratorService.AnalyzeSeriesNarrators(seriesId);
                var sequentialNarrators = _seriesNarratorService.DetectSequentialOverlapNarrators(seriesId);

                var result = new SeriesNarratorSearchResource
                {
                    SeriesId = seriesId,
                    SeriesTitle = series.Title,
                    FoundNarrators = analysisResult.Select(a => a.Narrator).ToList(),
                    RecommendedNarrator = sequentialNarrators.FirstOrDefault() ??
                                          analysisResult.Where(a => a.IsMainNarrator).Select(a => a.Narrator).FirstOrDefault(),
                    Success = true,
                    SearchDurationMs = 0 // TODO: Implement actual search timing
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error searching series narrators for series {0}", seriesId);
                return StatusCode(500, new SeriesNarratorSearchResource
                {
                    SeriesId = seriesId,
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }
        }

        [HttpPut("{seriesId:int}/narrators/preferred")]
        [ProducesResponseType(typeof(SeriesNarratorPreferenceResponseResource), 200)]
        [ProducesResponseType(typeof(ApiErrorResource), 400)]
        [ProducesResponseType(typeof(ApiErrorResource), 500)]
        public ActionResult<SeriesNarratorPreferenceResponseResource> SetPreferredSeriesNarrator(int seriesId, [FromBody] SeriesNarratorPreferenceRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Narrator))
            {
                return BadRequest(new ApiErrorResource { Error = "Narrator name is required" });
            }

            try
            {
                var series = _seriesService.GetSeries(seriesId);
                if (series == null)
                {
                    return NotFound($"Series with id {seriesId} not found");
                }

                _seriesNarratorService.SetPreferredSeriesNarrator(seriesId, request.Narrator);

                if (request.ApplyToAllBooks)
                {
                    var success = _seriesNarratorService.ApplySeriesNarratorToBooks(seriesId, request.Narrator, request.OverrideExisting);
                    if (!success)
                    {
                        return StatusCode(500, new ApiErrorResource { Error = "Failed to apply narrator to all books in series" });
                    }
                }

                return Ok(new SeriesNarratorPreferenceResponseResource
                {
                    Message = $"Set preferred narrator '{request.Narrator}' for series {seriesId}",
                    AppliedToBooks = request.ApplyToAllBooks
                });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error setting preferred series narrator for series {0}", seriesId);
                return StatusCode(500, new ApiErrorResource { Error = ex.Message });
            }
        }

        [HttpGet("{seriesId:int}/narrators/inheritance")]
        public ActionResult<SeriesNarratorInheritanceResource> GetNarratorInheritance(int seriesId)
        {
            try
            {
                var series = _seriesService.GetSeries(seriesId);
                if (series == null)
                {
                    return NotFound($"Series with id {seriesId} not found");
                }

                var preferredNarrator = _seriesNarratorService.GetPreferredSeriesNarrator(seriesId);
                var booksWithoutNarrator = _seriesNarratorService.GetBooksWithoutPreferredNarrator(seriesId);

                var result = new SeriesNarratorInheritanceResource
                {
                    SeriesId = seriesId,
                    SeriesTitle = series.Title,
                    PreferredSeriesNarrator = preferredNarrator,
                    BooksWithoutNarrator = booksWithoutNarrator.Select(b => new BookNarratorInheritanceInfo
                    {
                        BookId = b.Id,
                        BookTitle = b.Title,
                        CurrentNarrator = b.Narrator,
                        CanInheritFromSeries = preferredNarrator.IsNotNullOrWhiteSpace()
                    }).ToList(),
                    CanApplyInheritance = preferredNarrator.IsNotNullOrWhiteSpace() && booksWithoutNarrator.Any()
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error getting narrator inheritance for series {0}", seriesId);
                return StatusCode(500, new ApiErrorResource { Error = ex.Message });
            }
        }

        // New endpoints for narrator-aware series variants
        [HttpPost("{seriesId:int}/narrators/discover")]
        public async Task<ActionResult<SeriesNarratorDiscoveryResource>> DiscoverSeriesNarrators(int seriesId)
        {
            try
            {
                var result = await _narratorDiscoveryService.DiscoverNarratorsForSeries(seriesId);
                var resource = result.ToResource();
                ProxyRemoteImages(resource.ExistingVariants);
                return Ok(resource);
            }
            catch (ArgumentException ex)
            {
                return NotFound(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error discovering narrators for series {0}", seriesId);
                return StatusCode(500, new ApiErrorResource { Error = ex.Message });
            }
        }

        [HttpGet("{seriesId:int}/variants")]
        public ActionResult<List<SeriesResource>> GetSeriesVariants(int seriesId)
        {
            try
            {
                var variants = _seriesVariantService.GetSeriesVariants(seriesId);
                return Ok(ProxyRemoteImages(variants.ToResource()));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error getting variants for series {0}", seriesId);
                return StatusCode(500, new ApiErrorResource { Error = ex.Message });
            }
        }

        private List<SeriesResource> ProxyRemoteImages(List<SeriesResource> resources)
        {
            foreach (var resource in resources ?? new List<SeriesResource>())
            {
                ProxyRemoteImages(resource);
            }

            return resources;
        }

        private SeriesResource ProxyRemoteImages(SeriesResource resource)
        {
            if (resource == null)
            {
                return null;
            }

            _mediaCoverProxy.ProxyRemoteUrls(resource.Images);
            foreach (var book in resource.Books ?? new List<SeriesBookResource>())
            {
                _mediaCoverProxy.ProxyRemoteUrls(book.Images);
            }

            return resource;
        }

        [HttpDelete("{seriesId:int}/variants/{variantId:int}")]
        [ProducesResponseType(typeof(ApiMessageResource), 200)]
        [ProducesResponseType(typeof(ApiErrorResource), 400)]
        [ProducesResponseType(typeof(ApiErrorResource), 404)]
        [ProducesResponseType(typeof(ApiErrorResource), 500)]
        public ActionResult<ApiMessageResource> DeleteSeriesVariant(int seriesId, int variantId)
        {
            try
            {
                _seriesVariantService.DeleteSeriesVariant(variantId);
                return Ok(new ApiMessageResource { Message = $"Successfully deleted series variant {variantId}" });
            }
            catch (ArgumentException ex)
            {
                return NotFound(new ApiErrorResource { Error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new ApiErrorResource { Error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error deleting series variant {0}", variantId);
                return StatusCode(500, new ApiErrorResource { Error = ex.Message });
            }
        }
    }

}
