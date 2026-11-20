using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Common;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    // Converting files that are already in the library. The conversion runs as a library job on
    // the same worker as download conversions (ConversionJobService), so it shares ffmpeg
    // detection, concurrency and CPU limits, tagging and chapters, retained-artifact reuse and
    // workspace cleanup. The job is owned by its book and edition, not by a download. When the job
    // is ready, ImportLibraryConversion hands the converted file to the normal import as a
    // replacement for exactly the files it was made from.
    public partial class ImportApprovedBooks
    {
        public LibraryConversionQueueResult QueueLibraryConversion(List<ImportDecision<LocalBook>> decisions, Author author, Book book)
        {
            if (_conversionJobService == null)
            {
                return LibraryConversionQueueResult.Refused("The conversion job service is unavailable; conversion cannot start safely.");
            }

            var items = (decisions ?? new List<ImportDecision<LocalBook>>()).Select(decision => decision?.Item).Where(item => item != null).ToList();
            var first = items.FirstOrDefault();
            if (first?.Edition == null || first.Edition.Id <= 0 || book == null || book.Id <= 0 || author == null)
            {
                return LibraryConversionQueueResult.Refused("The book's files could not be matched to a library edition.");
            }

            if (items.Any(item => item.Edition?.Id != first.Edition.Id))
            {
                return LibraryConversionQueueResult.Refused("A library conversion converts one edition at a time.");
            }

            var inputFiles = items.Select(item => item.Path)
                .Where(path => path.IsNotNullOrWhiteSpace())
                .Distinct(PathEqualityComparer.Instance)
                .OrderBy(path => path, NaturalSortComparer.Instance)
                .ToArray();
            if (inputFiles.Length == 0)
            {
                return LibraryConversionQueueResult.Refused("No files to convert.");
            }

            var key = LibraryConversionJob.Key(first.Edition.Id);
            var existingJob = _conversionJobService.GetLibraryJob(first.Edition.Id);
            if (existingJob != null && existingJob.Status is ConversionJobStatus.Queued or ConversionJobStatus.Converting or ConversionJobStatus.Cancelling or ConversionJobStatus.ReadyToImport)
            {
                // Already on its way. Clicking Convert again must not start a second encode.
                return new LibraryConversionQueueResult { Job = existingJob };
            }

            if (!_m4bConversionService.CanConvert(inputFiles))
            {
                return LibraryConversionQueueResult.Refused("One or more files are not compatible with the M4B converter.");
            }

            var convertedQuality = new QualityModel(Quality.M4B);
            var outputName = ConversionWorkPath.SafeName(inputFiles.Length == 1 ? Path.GetFileNameWithoutExtension(inputFiles[0]) : book.Title);

            try
            {
                var finalDestinationPath = GetConvertedImportDestinationPath(first, book, author, outputName, inputFiles, convertedQuality);
                var finalDestinationFolder = Path.GetDirectoryName(finalDestinationPath);
                if (finalDestinationFolder.IsNullOrWhiteSpace())
                {
                    return LibraryConversionQueueResult.Refused("Unable to determine destination folder for converted M4B.");
                }

                // The files this conversion replaces may sit at its destination (an M4B being
                // re-encoded keeps its name); anything else at that path is a real conflict.
                if (!inputFiles.Any(path => path.PathEquals(finalDestinationPath)))
                {
                    var destinationConflict = GetConversionDestinationConflictReason(finalDestinationPath, first, replaceExisting: true);
                    if (destinationConflict.IsNotNullOrWhiteSpace())
                    {
                        return LibraryConversionQueueResult.Refused(destinationConflict);
                    }
                }

                var workRoot = Path.Combine(finalDestinationFolder, ".chaptarr-conversions", ConversionWorkPath.SafeName(key));
                var tagOptions = ConversionTagProposalBuilder.BuildOptions(items, book, author, first.Edition, _containmentValidator, _configService?.AudiobookConversionTagMode);
                var sourceDuration = GetAudiobookConversionSourceDuration(inputFiles);
                TryApplyMatchedEditionChapters(tagOptions, first.Edition, sourceDuration, book);
                TryApplySourceSidecarCover(tagOptions, inputFiles, book, first.Edition);
                ApplyConversionCoverFallback(tagOptions, book, first.Edition);
                ConversionTagProposalBuilder.RefreshManifestJson(tagOptions, items);
                var audioBitrate = GetAudiobookConversionBitrate(inputFiles);
                var audioChannels = GetAudiobookConversionAudioChannels();

                string workFolder;
                string outputPath;
                if (TryFindReusableConversionArtifact(key, workRoot, inputFiles, convertedQuality, tagOptions, audioBitrate, audioChannels, out var reusableOutputPath))
                {
                    // A previous attempt already produced this exact conversion; its import failed
                    // or was cancelled. Queue the retained output so the worker hands it straight
                    // to import instead of encoding the book again.
                    _logger.Info("[CONVERSION] Reusing retained library conversion for '{0}': {1}", book.Title, reusableOutputPath);
                    workFolder = Path.GetDirectoryName(reusableOutputPath);
                    outputPath = reusableOutputPath;
                }
                else
                {
                    if (existingJob != null)
                    {
                        // Retire the previous finished, failed or cancelled attempt and its workspace
                        // before starting a new one. A pending cleanup keeps the new attempt waiting.
                        _conversionJobService.Reset(key, existingJob);
                    }

                    var estimate = EstimateConversionWorkspace(inputFiles);
                    if (!HasEnoughConversionWorkspaceSpace(finalDestinationFolder, estimate, out var freeSpaceError))
                    {
                        return LibraryConversionQueueResult.Refused(freeSpaceError);
                    }

                    workFolder = Path.Combine(workRoot, Guid.NewGuid().ToString("N"));
                    var outputFileName = Path.GetFileName(finalDestinationPath);
                    outputPath = Path.Combine(workFolder, outputFileName.IsNullOrWhiteSpace() ? outputName + ".m4b" : outputFileName);
                }

                var sources = BuildConversionArtifactSourceSignatures(inputFiles);
                if (sources == null || sources.Count == 0)
                {
                    return LibraryConversionQueueResult.Refused("Unable to capture stable source-file identities for M4B conversion.");
                }

                var job = _conversionJobService.EnqueueLibrary(new ConversionJobRequest
                {
                    BookId = book.Id,
                    EditionId = first.Edition.Id,
                    BookTitle = book.Title,
                    WorkRoot = workRoot,
                    WorkFolder = workFolder,
                    OutputPath = outputPath,
                    Sources = sources,
                    TargetQualityId = convertedQuality.Quality.Id,
                    TargetQualityName = convertedQuality.Quality.Name,
                    AudioBitrate = audioBitrate,
                    AudioChannels = audioChannels,
                    ExpectedSourceDurationTicks = sourceDuration.Ticks,
                    TagSignature = GetConversionTagSignature(tagOptions),
                    TagOptions = tagOptions
                });

                _logger.Info("[CONVERSION] Queued library conversion of '{0}' ({1} file(s)) to {2}", book.Title, inputFiles.Length, convertedQuality.Quality.Name);
                return new LibraryConversionQueueResult { Job = job };
            }
            catch (InvalidOperationException ex)
            {
                // The job service refuses while a previous attempt's cleanup is pending.
                _logger.Warn(ex, "[CONVERSION] Library conversion of '{0}' was not queued", book.Title);
                return LibraryConversionQueueResult.Refused(ex.Message);
            }
        }

        public List<ImportResult> ImportLibraryConversion(ConversionJobRequest request, List<ImportDecision<LocalBook>> sourceDecisions)
        {
            var sources = (sourceDecisions ?? new List<ImportDecision<LocalBook>>()).Where(decision => decision?.Item != null).ToList();
            var first = sources.FirstOrDefault()?.Item;
            if (request == null || first == null)
            {
                return new List<ImportResult>();
            }

            var inputFiles = request.Sources.Select(source => source.Path).ToArray();
            var selected = sources.Select(decision => decision.Item.Path).ToHashSet(PathEqualityComparer.Instance);
            if (!selected.SetEquals(inputFiles))
            {
                // Import only ever replaces the exact files the job converted.
                const string error = "The library files changed since this conversion was queued. Nothing was replaced; convert the book again.";
                return sources.Select(decision => new ImportResult(decision, error)).ToList();
            }

            foreach (var decision in sources)
            {
                decision.Item.IsLibraryConversion = true;
            }

            var converted = CreateGeneratedConversionLocalBook(first, sources, request.OutputPath, inputFiles,
                new QualityModel((Quality)request.TargetQualityId), request.TagOptions ?? new ConversionTagOptions());
            converted.IsLibraryConversion = true;

            return Import(new List<ImportDecision<LocalBook>> { new(converted) }, replaceExisting: true, importMode: ImportMode.Move);
        }
    }
}
