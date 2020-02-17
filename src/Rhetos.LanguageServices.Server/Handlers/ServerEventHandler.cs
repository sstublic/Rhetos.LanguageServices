﻿using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Rhetos.LanguageServices.Server.Services;

namespace Rhetos.LanguageServices.Server.Handlers
{
    public class ServerEventHandler
    {
        private readonly RhetosAppContext rhetosContext;
        private readonly ILogger<ServerEventHandler> log;

        public ServerEventHandler(RhetosAppContext rhetosContext, ILogger<ServerEventHandler> log)
        {
            this.rhetosContext = rhetosContext;
            this.log = log;
        }

        public Task InitializeRhetosContext(string rootPath)
        {
            log.LogInformation($"Initializing RhetosContext with rootPath='{rootPath}'.");
            var initializeTask = Task.Run(() => rhetosContext.InitializeFromAppPath(rootPath))
                .ContinueWith(result =>
                {
                    var status = result.Status == TaskStatus.RanToCompletion
                        ? "OK"
                        : result.Exception?.Flatten().ToString();
                    log.LogInformation($"Initialize complete with status: {status}.");
                });

            return initializeTask;
        }
    }
}
