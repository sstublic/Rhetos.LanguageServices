﻿using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Rhetos.Dsl;
using Rhetos.LanguageServices.Server.Services;
using Rhetos.LanguageServices.Server.Tools;

namespace Rhetos.LanguageServices.Server.Parsing
{
    public class RhetosDocument
    {
        public TextDocument TextDocument { get; private set; }
        private readonly RhetosAppContext rhetosAppContext;
        private static readonly object _syncAnalysis = new object(); // don't rely on Rhetos parsing infrastructure to be thread-safe
        private readonly ILoggerFactory logFactory;
        private readonly ConceptQueries conceptQueries;
        private readonly Dictionary<int, CodeAnalysisResult> cachedAnalysisResults = new Dictionary<int, CodeAnalysisResult>();

        public RhetosDocument(RhetosAppContext rhetosAppContext, ConceptQueries conceptQueries, ILoggerFactory logFactory)
        {
            this.rhetosAppContext = rhetosAppContext;
            this.logFactory = logFactory;
            this.conceptQueries = conceptQueries;
            UpdateText("");
        }

        public void UpdateText(string text)
        {
            lock (cachedAnalysisResults)
            {
                TextDocument = new TextDocument(text);
                cachedAnalysisResults.Clear();
            }
        }

        public CodeAnalysisResult GetAnalysis()
            => GetAnalysis(null);

        public CodeAnalysisResult GetAnalysis(LineChr? lineChr)
        {
            lock (cachedAnalysisResults)
            {
                var cacheKey = lineChr == null
                    ? -1
                    : TextDocument.GetPosition(lineChr.Value);

                if (cachedAnalysisResults.TryGetValue(cacheKey, out var cachedResult))
                    return cachedResult;

                lock (_syncAnalysis)
                {
                    // gracefully return empty analysis if RhetosAppContext is not yet initialized
                    if (!rhetosAppContext.IsInitialized) return new CodeAnalysisResult(TextDocument, 0, 0);

                    var analysisRun = new CodeAnalysisRun(TextDocument, rhetosAppContext, logFactory);
                    var result = analysisRun.RunForPosition(lineChr);
                    cachedAnalysisResults[cacheKey] = result;
                    return result;
                }
            }
        }

        public List<string> GetCompletionKeywordsAtPosition(LineChr lineChr)
        {
            var analysisResult = GetAnalysis(lineChr);

            if (analysisResult.IsInsideComment)
                return new List<string>();

            var typingToken = analysisResult.GetTokenBeingTypedAtCursor(lineChr);
            if (analysisResult.KeywordToken != null && analysisResult.KeywordToken != typingToken)
                return new List<string>();

            var lastParent = analysisResult.ConceptContext.LastOrDefault();
            var validConcepts = lastParent == null
                ? rhetosAppContext.ConceptInfoTypes.ToList()
                : conceptQueries.ValidConceptsForParent(lastParent.GetType());

            var keywords = validConcepts
                .Select(concept => ConceptInfoHelper.GetKeyword(concept))
                .Where(keyword => keyword != null)
                .Distinct()
                .OrderBy(keyword => keyword)
                .ToList();

            return keywords;
        }

        public (string description, LineChr startPosition, LineChr endPosition) GetHoverDescriptionAtPosition(LineChr lineChr)
        {
            var analysis = GetAnalysis(lineChr);
            if (analysis.KeywordToken == null || analysis.IsAfterAnyErrorLine(lineChr))
                return (null, LineChr.Zero, LineChr.Zero);

            var description = conceptQueries.GetFullDescription(analysis.KeywordToken.Value);
            if (string.IsNullOrEmpty(description))
                description = $"No documentation found for '{analysis.KeywordToken.Value}'.";

            var startPosition = TextDocument.GetLineChr(analysis.KeywordToken.PositionInDslScript);

            var endPosition = lineChr;
            if (analysis.NextKeywordToken != null)
                endPosition = TextDocument.GetLineChr(analysis.NextKeywordToken.PositionInDslScript - 1);

            return (description, startPosition, endPosition);
        }

        public (List<RhetosSignature> signatures, int? activeSignature, int? activeParameter) GetSignatureHelpAtPosition(LineChr lineChr)
        {
            var analysis = GetAnalysis(lineChr);
            if (analysis.KeywordToken == null || analysis.IsAfterAnyErrorLine(lineChr))
                return (null, null, null);

            var signaturesWithDocumentation = conceptQueries.GetSignaturesWithDocumentation(analysis.KeywordToken.Value);
            var validConcepts = analysis.GetValidConceptsWithActiveParameter();

            if (!validConcepts.Any())
                return (signaturesWithDocumentation, null, null);

            var sortedConcepts = validConcepts
                .Select(valid =>
                (
                    valid.concept,
                    valid.activeParamater,
                    parameterCount: ConceptInfoType.GetParameters(valid.concept.GetType()).Count,
                    documentation: signaturesWithDocumentation.Single(sig => sig.ConceptInfoType == valid.concept.GetType())
                ))
                .OrderBy(valid => valid.activeParamater >= valid.parameterCount)
                .ThenBy(valid => valid.parameterCount)
                .ThenBy(valid => valid.concept.GetType().Name)
                .ToList();

            var activeParameter = sortedConcepts.First().activeParamater;

            return (sortedConcepts.Select(sorted => sorted.documentation).ToList(), 0, activeParameter);
        }
    }
}
