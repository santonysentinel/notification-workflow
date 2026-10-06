using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NotificationWorkflowService.Service;

namespace NotificationWorkflowService
{
    public class StepWorker : BackgroundService
    {
        private readonly ILogger<StepWorker> _logger;
        private readonly WorkFlowInitiatorService _workflowInitiatorService;
        private readonly IConfiguration _configuration;

        public StepWorker(ILogger<StepWorker> logger, WorkFlowInitiatorService workflowInitiatorService, IConfiguration configuration)
        {
            _logger = logger;
            _workflowInitiatorService = workflowInitiatorService;
            _configuration = configuration;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            string? platform = _configuration["Platform"];
            if (string.IsNullOrWhiteSpace(platform))
            {
                throw new InvalidOperationException("Platform must be configured to start the step worker.");
            }

            _logger.LogInformation("Starting step worker for platform {Platform}", platform);
            try
            {
                await _workflowInitiatorService.startParseSteps(platform, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host shutdown cancels the parser's delays.
            }
        }
    }
}