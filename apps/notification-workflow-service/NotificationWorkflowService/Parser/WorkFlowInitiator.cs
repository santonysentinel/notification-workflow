namespace ActiveAlarmsParser
{
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Logging;
    using NotificationWorkflowService.Parser;
    using NotificationWorkflowService.Repository;
    using NotificationWorkflowService.Service.Notes;
    using NotificationSender = ActiveAlarmsParser.Service.NotificationService.NotificationService;

    /// <summary>
    /// Compatibility entry point for the shared initial workflow implementation.
    /// The supplied logger retains the WorkFlowInitiator category.
    /// </summary>
    internal class WorkFlowInitiator : WorkFlowCommon
    {
        public WorkFlowInitiator(ILogger<WorkFlowInitiator> logger, IConfiguration configuration, NotificationSender notificationService)
            : this(logger, configuration, notificationService, new Repository(configuration))
        {
        }

        public WorkFlowInitiator(ILogger<WorkFlowInitiator> logger, IConfiguration configuration, NotificationSender notificationService, IRepository repository)
            : base((ILogger)logger, configuration, notificationService, repository)
        {
        }

        public WorkFlowInitiator(ILogger<WorkFlowInitiator> logger, IConfiguration configuration, NotificationSender notificationService, IRepository repository, INoteService noteService)
            : base((ILogger)logger, configuration, notificationService, repository, noteService)
        {
        }
    }
}
