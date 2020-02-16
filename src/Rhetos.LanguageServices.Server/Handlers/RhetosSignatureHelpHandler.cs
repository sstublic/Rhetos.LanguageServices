﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Rhetos.Dsl;
using Rhetos.LanguageServices.Server.Parsing;
using Rhetos.LanguageServices.Server.Services;
using Rhetos.LanguageServices.Server.Tools;

namespace Rhetos.LanguageServices.Server.Handlers
{
    public class RhetosSignatureHelpHandler : SignatureHelpHandler
    {
        private static readonly SignatureHelpRegistrationOptions registrationOptions = new SignatureHelpRegistrationOptions()
        {
            DocumentSelector = TextDocumentHandler.RhetosDocumentSelector,
            TriggerCharacters = new Container<string>(".", " ", ";", "{")
        };
        private readonly RhetosWorkspace rhetosWorkspace;
        private readonly ILogger<RhetosSignatureHelpHandler> log;
        private readonly ConceptQueries conceptQueries;

        public RhetosSignatureHelpHandler(RhetosWorkspace rhetosWorkspace, ConceptQueries conceptQueries, ILogger<RhetosSignatureHelpHandler> log) : base(registrationOptions)
        {
            this.rhetosWorkspace = rhetosWorkspace;
            this.log = log;
            this.conceptQueries = conceptQueries;
        }

        public override Task<SignatureHelp> Handle(SignatureHelpParams request, CancellationToken cancellationToken)
        {
            log.LogInformation($"SignatureHelp requested.");
            var rhetosDocument = rhetosWorkspace.GetRhetosDocument(request.TextDocument.Uri);
            if (rhetosDocument == null)
                return Task.FromResult<SignatureHelp>(null);

            var signatures = rhetosDocument.GetSignatureHelpAtPosition(request.Position.ToLineChr());
            if (signatures.signatures == null)
                return Task.FromResult<SignatureHelp>(null);

            var position = rhetosDocument.TextDocument.ShowPosition(request.Position.ToLineChr());
            log.LogInformation($"Signature at position:\n{position}\n");

            ParameterInformation FromRhetosParameter(ConceptMember conceptMember) => new ParameterInformation()
            {
                Documentation = "",
                Label = new ParameterInformationLabel(ConceptInfoType.ConceptMemberDescription(conceptMember))
            };

            SignatureInformation FromRhetosSignature(RhetosSignature rhetosSignature) => new SignatureInformation()
            {
                Documentation = rhetosSignature.Documentation,
                Label = rhetosSignature.Signature,
                Parameters = new Container<ParameterInformation>(rhetosSignature.Parameters.Select(FromRhetosParameter))
            };

            log.LogInformation($"SignatureHelp NOT null. ActiveSig = {signatures.activeSignature}, ActiveParam= {signatures.activeParameter}");
            if (signatures.activeSignature != null)
            {
                var tmp = signatures.signatures[signatures.activeSignature.Value];
                signatures.signatures[signatures.activeSignature.Value] = signatures.signatures[0];
                signatures.signatures[0] = tmp;
                signatures.activeSignature = 0;
            }

            var signatureHelp = new SignatureHelp()
            {
                Signatures = new Container<SignatureInformation>(signatures.signatures.Select(FromRhetosSignature)),
                ActiveSignature = signatures.activeSignature ?? 100,
                ActiveParameter = signatures.activeParameter ?? 100
            };

            return Task.FromResult(signatureHelp);
        }

        /*
        public override Task<SignatureHelp> Handle(SignatureHelpParams request, CancellationToken cancellationToken)
        {
            var rhetosDocument = rhetosWorkspace.GetRhetosDocument(request.TextDocument.Uri);
            if (rhetosDocument == null)
                return Task.FromResult<SignatureHelp>(null);

            // debug signature
            var analysisResult = rhetosDocument.GetAnalysis(request.Position.ToLineChr());
            //log.LogInformation($"Member info: {JsonConvert.SerializeObject(analysisResult.MemberDebug, Formatting.Indented)}");

            var keyword = analysisResult.KeywordToken?.Value;

            Func<IConceptInfo, int> NonNullMemberCount = info => 
                ConceptMembers.Get(info).Count(member => member.GetValue(info) != null);

            var bestMatch = analysisResult.ValidConcepts.OrderByDescending(NonNullMemberCount).FirstOrDefault();

            {
                if (bestMatch != null)
                {
                    var members = ConceptMembers.Get(bestMatch);
                    var membersDesc = string.Join(", ", members.Select(a => $"{a.Name}:'{a.GetValue(bestMatch)}'"));
                    log.LogInformation($"BestMatch ==> {bestMatch.GetType().Name}: " + membersDesc);
                }
            }
            //log.LogInformation($"Current keyword: '{keyword}' at {request.Position.ToLineChr()}.");
            //log.LogInformation("\n" + rhetosDocument.TextDocument.ShowPosition(request.Position.ToLineChr()));
            var signatures = conceptQueries.GetSignaturesWithDocumentation(keyword);

            if (signatures == null)
                return Task.FromResult<SignatureHelp>(null);

            var signatureInfos = new List<SignatureInformation>();
            foreach (var signature in signatures)
            {
                var members = ConceptMembers.Get(signature.ConceptInfoType);
                var parameters = members
                    .Where(member => member.IsParsable)
                    .Select(member => new ParameterInformation() {Label = new ParameterInformationLabel(ConceptInfoType.ConceptMemberDescription(member))});

                var signatureInfo = new SignatureInformation()
                {
                    Documentation = new StringOrMarkupContent(signature.Documentation),
                    Parameters = new Container<ParameterInformation>(parameters),
                    Label = signature.Signature
                };
                signatureInfos.Add(signatureInfo);
            }

            if (!signatureInfos.Any())
                return Task.FromResult<SignatureHelp>(null);

            var signatureHelp = new SignatureHelp()
            {
                ActiveParameter = 0,
                ActiveSignature = 0,
                Signatures = new Container<SignatureInformation>(signatureInfos)
            };

            if (bestMatch != null)
            {
                var signatureIndex = signatures.FindIndex(sig => sig.ConceptInfoType == bestMatch.GetType());
                log.LogInformation($"SigIndex: {signatureIndex}");
                if (signatureIndex != -1)
                {
                    signatureHelp.ActiveSignature = signatureIndex;
                    var conceptInfoInstance = analysisResult.ValidConcepts.Single(concept => concept.GetType() == signatures[signatureIndex].ConceptInfoType);
                    var paramIndex = NonNullMemberCount(conceptInfoInstance);
                    log.LogInformation($"ParamIndex: {paramIndex}");
                    if (paramIndex < signatureInfos[signatureIndex].Parameters.Count())
                        signatureHelp.ActiveParameter = paramIndex;
                }
            }

            return Task.FromResult(signatureHelp);
        }
        */
    }
}
