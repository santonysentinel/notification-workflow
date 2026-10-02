using NotificationWorkflowService;
using NotificationWorkflowService.Parser;
using NotificationWorkflowService.Service;
using NotificationWorkflowService.Repository;
using NotificationSender = ActiveAlarmsParser.Service.NotificationService.NotificationService;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System;
using System.IO;

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
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                //.AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
                .AddEnvironmentVariables();


            Log.Logger = new LoggerConfiguration()
                    .ReadFrom.Configuration(builder.Build())
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
                if (string.Equals(workerMode, "Normal", StringComparison.OrdinalIgnoreCase))
                {
                    services.AddHostedService<Worker>();
                }
                else if (string.Equals(workerMode, "Step", StringComparison.OrdinalIgnoreCase))
                {
                    services.AddHostedService<StepWorker>();
                }
                else
                {
                    throw new InvalidOperationException("WorkerMode must be 'Normal' or 'Step'.");
                }
                services.AddTransient<NotificationSender>();
                services.AddTransient<IRepository, Repository>();
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
            .UseSerilog();
        }
    }

}