using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ImpromptuInterface;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection;

namespace Chaptarr.Http.Validation
{
    public interface IDfaMatcherBuilder
    {
        void AddEndpoint(RouteEndpoint endpoint);
        object BuildDfaTree(bool includeLabel = false);
    }

    // https://github.com/dotnet/aspnetcore/blob/cc3d47f5501cdfae3e5b5be509ef2c0fb8cca069/src/Http/Routing/src/Matching/DfaNode.cs
    public interface IDfaNode
    {
        public string Label { get; set; }
        public List<Endpoint> Matches { get; }
        public IDictionary Literals { get; }
        public object Parameters { get; }
        public object CatchAll { get; }
        public IDictionary PolicyEdges { get; }
    }

    public class DuplicateEndpointDetector
    {
        private readonly IServiceProvider _services;

        public DuplicateEndpointDetector(IServiceProvider services)
        {
            _services = services;
        }

        public Dictionary<string, List<string>> GetDuplicateEndpoints(EndpointDataSource dataSource)
        {
            var matcherBuilder = typeof(IEndpointSelectorPolicy).Assembly
                .GetType("Microsoft.AspNetCore.Routing.Matching.DfaMatcherBuilder");

            if (matcherBuilder == null)
            {
                return new Dictionary<string, List<string>>();
            }

            var rawBuilder = _services.GetService(matcherBuilder);
            if (rawBuilder == null)
            {
                return new Dictionary<string, List<string>>();
            }

            var builder = rawBuilder.ActLike<IDfaMatcherBuilder>();

            var endpoints = dataSource.Endpoints;
            foreach (var t in endpoints)
            {
                if (t is RouteEndpoint endpoint && (endpoint.Metadata.GetMetadata<ISuppressMatchingMetadata>()?.SuppressMatching ?? false) == false)
                {
                    builder.AddEndpoint(endpoint);
                }
            }

            var duplicates = new Dictionary<string, List<string>>();

            var rawTree = builder.BuildDfaTree(includeLabel: true);

            Visit(rawTree, LogDuplicates);

            return duplicates;

            void LogDuplicates(IDfaNode node)
            {
                var filteredMatches = node?.Matches?
                    .Where(x => x?.DisplayName?.StartsWith("Chaptarr.Http.Frontend.StaticResourceController", StringComparison.Ordinal) != true)
                    .Select(x => x.DisplayName)
                    .ToList();

                var matchCount = filteredMatches?.Count ?? 0;
                if (matchCount > 1)
                {
                    var label = node.Label;
                    if (!string.IsNullOrWhiteSpace(label))
                    {
                        duplicates[label] = filteredMatches;
                    }
                }
            }
        }

        private static void Visit(object rawNode, Action<IDfaNode> visitor)
        {
            var node = rawNode.ActLike<IDfaNode>();
            if (node.Literals?.Values != null)
            {
                foreach (var dictValue in node.Literals.Values)
                {
                    Visit(dictValue, visitor);
                }
            }

            // Break cycles
            if (node.Parameters != null && !ReferenceEquals(rawNode, node.Parameters))
            {
                Visit(node.Parameters, visitor);
            }

            // Break cycles
            if (node.CatchAll != null && !ReferenceEquals(rawNode, node.CatchAll))
            {
                Visit(node.CatchAll, visitor);
            }

            if (node.PolicyEdges?.Values != null)
            {
                foreach (var dictValue in node.PolicyEdges.Values)
                {
                    Visit(dictValue, visitor);
                }
            }

            visitor(node);
        }
    }
}
