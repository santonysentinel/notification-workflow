using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using NotificationWorkflowService.Service;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NotificationWorkflowService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly WorkFlowInitiatorService _workflowInitiatorService;
        private readonly IConfiguration _configuration;
        
        public Worker(ILogger<Worker> logger, WorkFlowInitiatorService workflowInitiatorService, IConfiguration configuration)
        {
            _logger = logger;
            _workflowInitiatorService = workflowInitiatorService;
            _configuration = configuration;
        }

        /// <summary>
        /// The StartAsync.
        /// </summary>
        /// <param name="cancellationToken">The cancellationToken<see cref="CancellationToken"/>.</param>
        /// <returns>The <see cref="Task"/>.</returns>
        public override Task StartAsync(CancellationToken cancellationToken)
        {
            return base.StartAsync(cancellationToken);
        }

        /// <summary>
        /// The StopAsync.
        /// </summary>
        /// <param name="cancellationToken">The cancellationToken<see cref="CancellationToken"/>.</param>
        /// <returns>The <see cref="Task"/>.</returns>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("The service has stopped procedures...");
            await base.StopAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            string? platform = _configuration["Platform"];
            if (string.IsNullOrWhiteSpace(platform))
            {
                throw new InvalidOperationException("Platform must be configured to start the workflow initiator.");
            }

            _logger.LogInformation("Starting workflow initiator for platform {Platform}", platform);
            try
            {
                await _workflowInitiatorService.startParse(platform, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host shutdown cancels the parser's delays.
            }
        }
    }
}
