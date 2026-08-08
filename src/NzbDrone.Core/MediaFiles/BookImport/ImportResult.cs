using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.EnsureThat;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.BookImport
{
    public class ImportResult
    {
        private readonly ImportResultType? _result;

        public ImportDecision<LocalBook> ImportDecision { get; private set; }
        public List<string> Errors { get; private set; }

        public ImportResultType Result
        {
            get
            {
                if (_result.HasValue)
                {
                    return _result.Value;
                }

                if (Errors.Any())
                {
                    if (ImportDecision.Approved)
                    {
                        return ImportResultType.Skipped;
                    }

                    return ImportResultType.Rejected;
                }

                return ImportResultType.Imported;
            }
        }

        public ImportResult(ImportDecision<LocalBook> importDecision, params string[] errors)
            : this(importDecision, null, errors)
        {
        }

        public ImportResult(ImportDecision<LocalBook> importDecision, ImportResultType result, params string[] errors)
            : this(importDecision, (ImportResultType?)result, errors)
        {
        }

        private ImportResult(ImportDecision<LocalBook> importDecision, ImportResultType? result, params string[] errors)
        {
            Ensure.That(importDecision, () => importDecision).IsNotNull();

            ImportDecision = importDecision;
            Errors = errors.ToList();
            _result = result;
        }
    }
}
