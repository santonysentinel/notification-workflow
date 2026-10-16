using NotificationWorkflowService;
using NotificationWorkflowService.Parser;
using NotificationWorkflowService.Service;
using NotificationWorkflowService.Repository;
using NotificationWorkflowService.Service.Notes;
using NotificationWorkflowService.Service.AutoCall;
using NotificationWorkflowService.Service.Transcription;
using NotificationSender = ActiveAlarmsParser.Service.NotificationService.NotificationService;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System;

namespace ActiveAlarmsParser
{
    class Program
    {
        /// <summary>
        /// The Main.
        /// </summary>
        /// <param name="args">The args<see cref="string[]"/>.</param>
        public static void Main(string[] args)
        {
            Log.Logger = new LoggerConfiguration()
                    .WriteTo.Console()
                    .CreateLogger();

            try
            {
                Log.Information("Starting up the service");
                CreateHostBuilder(args).Build().Run();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "There was a problem starting the serivce");
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        /// <summary>
        /// The CreateHostBuilder.
        /// </summary>
        /// <param name="args">The args<see cref="string[]"/>.</param>
        /// <returns>The <see cref="IHostBuilder"/>.</returns>
        public static IHostBuilder CreateHostBuilder(string[] args)
        {
            return Host.CreateDefaultBuilder(args)
            .ConfigureServices((hostContext, services) =>
            {
                string workerMode = hostContext.Configuration["WorkerMode"] ?? "Normal";
                switch (workerMode.ToUpperInvariant())
                {
                    case "NORMAL":
                        services.AddHostedService<Worker>();
                        break;
                    case "STEP":
                        services.AddHostedService<StepWorker>();
                        break;
                    case "AUTOCALL":
                        services.AddHostedService<AutoCallWorker>();
                        break;
                    case "TRANSCRIPTION":
                        services.AddHostedService<TranscriptionWorker>();
                        break;
                    default:
                        throw new InvalidOperationException("WorkerMode must be 'Normal', 'Step', 'AutoCall' or 'Transcription'.");
                }
                services.AddTransient<NotificationSender>();
                services.AddTransient<IRepository, Repository>();
                services.AddNoteServices();
                services.AddAutoCallContacts();
                services.AddTranscriptionClients();
                services.AddSingleton<NotificationWorkflowService.Repository.INotificationRepository, NotificationWorkflowService.Repository.NotificationRepository>();
                services.AddTransient<WorkFlowCommon>();
                services.AddTransient<Func<WorkFlowCommon>>(provider => () => provider.GetRequiredService<WorkFlowCommon>());
                services.AddTransient<WorkFlowInitiator>();
                services.AddTransient<WorkFlowInitiatorService>();
               
                //services.AddSingleton<IDataRouter, DataRouter>();
                
                //Register the HttpClient factory
                services.AddHttpClient();
                ActiveAlarmsParser.Service.NotificationService.NotificationHttp.RegisterClients(services);
            })
            .UseSerilog((context, configuration) => ConfigureLogging(context.Configuration, configuration));
        }

        internal static void ConfigureLogging(IConfiguration settings, LoggerConfiguration logger)
        {
            logger.ReadFrom.Configuration(settings);
            if (!settings.GetSection("Serilog:WriteTo").GetChildren().Any())
                logger.WriteTo.Console();
        }
    }

}