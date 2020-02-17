﻿using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Rhetos.Dsl;
using Rhetos.LanguageServices.Server.Services;
using Rhetos.LanguageServices.Server.Tools;
using Rhetos.Logging;
using Rhetos.Utilities;

namespace Rhetos.LanguageServices.Server.Parsing
{
    internal class CodeAnalysisRun
    {
        private CodeAnalysisResult result;
        private readonly RhetosAppContext rhetosAppContext;
        private int targetPos;
        private Token lastTokenBeforeTarget;
        private readonly TextDocument fullTextDocument;
        private TextDocument textDocument;
        private Tokenizer tokenizer;
        private readonly ILogProvider rhetosLogProvider;

        public CodeAnalysisRun(TextDocument textDocument, RhetosAppContext rhetosAppContext, ILoggerFactory logFactory)
        {
            this.rhetosAppContext = rhetosAppContext;
            this.fullTextDocument = textDocument;
            this.rhetosLogProvider = new RhetosNetCoreLogProvider(logFactory);
        }

        public CodeAnalysisResult RunForDocument()
        {
            return RunForPosition(null);
        }

        public CodeAnalysisResult RunForPosition(LineChr? lineChr)
        {
            if (!rhetosAppContext.IsInitialized) throw new InvalidOperationException($"Attempted CodeAnalysisRun before RhetosAppContext was initialized.");

            InitializeResult(lineChr);
            InitializeTokenizers();

            // run-scoped variables needed for parse and callbacks
            targetPos = textDocument.GetPosition(result.Line, result.Chr);
            lastTokenBeforeTarget = result.Tokens.LastOrDefault(token => token.PositionInDslScript <= targetPos);

            ParseAndCaptureErrors();
            ApplyCommentsToResult();

            result.SuccessfulRun = true;
            return result;
        }

        private void InitializeResult(LineChr? lineChr)
        {
            if (result != null) throw new InvalidOperationException("Analysis already run.");

            if (lineChr == null)
                textDocument = fullTextDocument;
            else
                textDocument = new TextDocument(fullTextDocument.GetTruncatedAtNextEndOfLine(lineChr.Value));

            result = lineChr == null
                ? new CodeAnalysisResult(textDocument, 0, 0)
                : new CodeAnalysisResult(textDocument, lineChr.Value.Line, lineChr.Value.Chr);
        }

        private void InitializeTokenizers()
        {
            var (createdTokenizer, capturedErrors) = CreateTokenizerWithCapturedErrors();
            tokenizer = createdTokenizer;
            result.Tokens = tokenizer.GetTokens();
            result.TokenizerErrors.AddRange(capturedErrors);

            result.CommentTokens = ParseCommentTokens();
        }

        private void ParseAndCaptureErrors()
        {
            var dslParser = new DslParser(tokenizer, rhetosAppContext.ConceptInfoInstances, rhetosLogProvider);
            try
            {
                dslParser.ParseConceptsWithCallbacks(OnKeyword, OnMemberRead, OnUpdateContext);
            }
            catch (DslParseSyntaxException e)
            {
                result.DslParserErrors.Add(CreateAnalysisError(e));
            }
            catch (Exception e)
            {
                result.DslParserErrors.Add(new CodeAnalysisError() { LineChr = LineChr.Zero, Message = e.Message });
            }
        }

        private void ApplyCommentsToResult()
        {
            Token lastCommentTokenBeforeTarget = null;
            foreach (var commentToken in result.CommentTokens)
            {
                // leading '//' characters are not included in token.Value
                if (targetPos >= commentToken.PositionInDslScript && targetPos < commentToken.PositionInDslScript + commentToken.Value.Length + 2)
                {
                    result.KeywordToken = null;
                    result.IsInsideComment = true;
                    break;
                }

                if (commentToken.PositionInDslScript > targetPos)
                    break;

                lastCommentTokenBeforeTarget = commentToken;
            }
            // handle situation where position is at the EOL after the comment
            if (lastCommentTokenBeforeTarget != null)
            {
                var lastTokenLine = textDocument.GetLineChr(lastCommentTokenBeforeTarget.PositionInDslScript).Line;
                if (lastTokenLine == result.Line)
                {
                    result.KeywordToken = null;
                    result.IsInsideComment = true;
                }
            }
        }

        private void OnMemberRead(ITokenReader iTokenReader, IConceptInfo conceptInfo, ConceptMember conceptMember, ValueOrError<object> valueOrError)
        {
            var tokenReader = (TokenReader)iTokenReader;
            if (tokenReader.PositionInTokenList > 0 && lastTokenBeforeTarget != null)
            {
                var conceptInfoType = conceptInfo.GetType();
                var lastTokenRead = result.Tokens[tokenReader.PositionInTokenList - 1];
                // Console.WriteLine($"[OnMemberRead]  LastTokenRead='{lastTokenRead.Value}', LastTokenBeforeTarget='{lastTokenBeforeTarget.Value}'");
                if (lastTokenRead.PositionInDslScript >= lastTokenBeforeTarget.PositionInDslScript)
                {
                    if (result.ValidConcepts.All(valid => valid.GetType() != conceptInfo.GetType())) result.ValidConcepts.Add(conceptInfo);
                }
                if (lastTokenRead.PositionInDslScript <= lastTokenBeforeTarget.PositionInDslScript && !valueOrError.IsError)
                {
                    // Console.WriteLine($"{conceptInfoType.Name}: {conceptMember.Name}");
                    result.LastTokenParsed[conceptInfoType] = lastTokenRead;
                    result.LastMemberReadAttempt[conceptInfoType] = conceptMember;
                }
            }
        }

        private CodeAnalysisError CreateAnalysisError(DslParseSyntaxException e)
        {
            var lineChr = textDocument.GetLineChr(e.Position);
            return new CodeAnalysisError() {LineChr = lineChr, Message = e.SimpleMessage};
        }

        private void OnUpdateContext(ITokenReader iTokenReader, Stack<IConceptInfo> context, bool isOpening)
        {
            var tokenReader = (TokenReader)iTokenReader;
            var lastToken = result.Tokens[tokenReader.PositionInTokenList - 1];
            var contextPos = lastToken.PositionInDslScript + lastToken.Value.Length;
            if (contextPos <= targetPos)
                result.ConceptContext = context.Reverse().ToList();
        }

        private void OnKeyword(ITokenReader iTokenReader, string keyword)
        {
            var tokenReader = (TokenReader)iTokenReader;
            if (tokenReader.PositionInTokenList >= result.Tokens.Count) return;

            var lastToken = result.Tokens[tokenReader.PositionInTokenList];
            if (keyword == null && tokenReader.PositionInTokenList > 0)
                lastToken = result.Tokens[tokenReader.PositionInTokenList - 1];

            if (lastToken.PositionInDslScript <= targetPos)
            {
                if (keyword != null)
                {
                    result.KeywordToken = lastToken;
                    result.MemberDebug = new Dictionary<string, List<string>>();
                    result.ValidConcepts = new List<IConceptInfo>();
                }
                else
                {
                    result.KeywordToken = null;
                }
            }
            else if (result.NextKeywordToken == null)
            {
                result.NextKeywordToken = lastToken;
            }
        }

        // Tokenizer just skips comments, so we are unable to detect whether typing is done inside a comment 
        // Therefore we need to reparse and detect all comment tokens
        private List<Token> ParseCommentTokens()
        {
            var script = textDocument.DslScripts.Single();
            var commentTokens = new List<Token>();
            try
            {
                var scriptPosition = 0;
                while (true)
                {
                    TokenizerInternals.SkipWhitespaces(script.Script, ref scriptPosition);
                    if (scriptPosition >= textDocument.Text.Length)
                        break;

                    var startPosition = scriptPosition;
                    var token = TokenizerInternals.GetNextToken_ValueType(script, ref scriptPosition, _ => "");
                    token.DslScript = script;
                    token.PositionInDslScript = startPosition;

                    if (token.Type == TokenType.Comment)
                        commentTokens.Add(token);
                }
            }
            catch
            {
                // we will ignore all errors as any relevant ones are capture by CreateTokenizerWithCapturedErrors()
            }

            return commentTokens;
        }

        // Due to unusual way the tokenizer works, if we capture errors during initial call to GetToken(),
        // valid tokens will be returned without error in subsequent calls
        private (Tokenizer tokenizer, List<CodeAnalysisError> capturedErrors) CreateTokenizerWithCapturedErrors()
        {
            var capturedErrors = new List<CodeAnalysisError>();
            var safeTokenizer = new Tokenizer(textDocument, new FilesUtility(rhetosLogProvider));
            try
            {
                safeTokenizer.GetTokens();
            }
            catch (DslParseSyntaxException e)
            {
                var lineChr = textDocument.GetLineChr(e.Position);
                capturedErrors.Add(new CodeAnalysisError() { LineChr = lineChr, Message = e.SimpleMessage });
            }
            catch (Exception e)
            {
                capturedErrors.Add(new CodeAnalysisError() { Message = e.Message });
            }
            return (safeTokenizer, capturedErrors);
        }
    }
}
