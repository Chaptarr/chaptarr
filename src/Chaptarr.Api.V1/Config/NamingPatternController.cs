using System;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Organizer.NamingPattern;
using Chaptarr.Http;

namespace Chaptarr.Api.V1.Config
{
    [V1ApiController("config/naming-pattern")]
    public class NamingPatternController : Controller
    {
        private readonly INamingPatternService _namingPatternService;

        public NamingPatternController(INamingPatternService namingPatternService)
        {
            _namingPatternService = namingPatternService;
        }

        [HttpPost("compile")]
        public object Compile([FromBody] CompileRequest request)
        {
            if (request?.Ast == null)
            {
                return BadRequest(new { error = "ast is required" });
            }

            try
            {
                var pattern = _namingPatternService.CompilePattern(request.Ast);
                return new { pattern };
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("decompile")]
        public object Decompile([FromBody] DecompileRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Pattern))
            {
                return BadRequest(new { error = "pattern is required" });
            }

            try
            {
                var ast = _namingPatternService.DecompilePattern(request.Pattern);
                return new { ast };
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("validate")]
        public object Validate([FromBody] ValidateRequest request)
        {
            if (request == null)
            {
                return BadRequest(new { error = "request body is required" });
            }

            var result = _namingPatternService.ValidatePattern(request.Pattern, request.Ast);
            return new 
            { 
                ok = result.IsValid,
                errors = result.Errors,
                normalizedPattern = result.NormalizedPattern
            };
        }

        [HttpPost("preview")]
        public object Preview([FromBody] PreviewRequest request)
        {
            if (request == null)
            {
                return BadRequest(new { error = "request body is required" });
            }

            try
            {
                var result = _namingPatternService.PreviewPattern(request.Pattern, request.Ast, request.Sample);
                return new
                {
                    path = result.Path,
                    segments = result.Segments
                };
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }
    }

    public class CompileRequest
    {
        public PatternAst Ast { get; set; }
    }

    public class DecompileRequest
    {
        public string Pattern { get; set; }
    }

    public class ValidateRequest
    {
        public string Pattern { get; set; }
        public PatternAst Ast { get; set; }
    }

    public class PreviewRequest
    {
        public string Pattern { get; set; }
        public PatternAst Ast { get; set; }
        public SampleContext Sample { get; set; }
    }
}
