using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NotificationWorkflowService.Repository;
using Serilog;
using Xunit;

namespace NotificationWorkflowService.Tests;

[CollectionDefinition("Console logging", DisableParallelization = true)]
public sealed class ConsoleLoggingCollection;

[Collection("Console logging")]
public sealed class LoggingConfigurationTests
{
    [Fact]
    public void ProductionCodeDoesNotWriteToConsoleOrChangeConsoleColors()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string root = Path.Combine(directory.FullName, "NotificationWorkflowService");
            if (!File.Exists(Path.Combine(root, "NotificationWorkflowService.csproj"))) continue;
            var sources = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(file => !Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar)
                    .Any(part => part is "bin" or "obj")).ToArray();
            Assert.NotEmpty(sources);
            foreach (string file in sources)
                Assert.DoesNotMatch(@"\bConsole\s*\.|\bConsoleColor\b", File.ReadAllText(file));
            return;
        }
        throw new DirectoryNotFoundException("Console logging checks require the workflow service sources.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsoleOutputExistsAndConfiguredConsoleIsNotDuplicated(bool configuredConsole)
    {
        var settings = new Dictionary<string, string?>();
        if (configuredConsole)
        {
            settings["Serilog:Using:0"] = "Serilog.Sinks.Console";
            settings["Serilog:WriteTo:0:Name"] = "Console";
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            var builder = new LoggerConfiguration();
            ActiveAlarmsParser.Program.ConfigureLogging(configuration, builder);
            using (var logger = builder.CreateLogger())
                logger.Error("database-output-test");
            Assert.Equal(1, output.ToString().Split("database-output-test").Length - 1);
        }
        finally { Console.SetOut(original); }
    }

    [Fact]
    public async Task DatabaseFailureReachesConsoleThroughMicrosoftLogging()
    {
        var configuration = new ConfigurationBuilder().Build();
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            var builder = new LoggerConfiguration();
            ActiveAlarmsParser.Program.ConfigureLogging(configuration, builder);
            using var serilog = builder.CreateLogger();
            using var factory = LoggerFactory.Create(logging => logging.AddSerilog(serilog));
            var logger = factory.CreateLogger("DatabaseTest");
            var failure = NotificationHistoryRetryTests.CreateSqlException(2812);
            await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() =>
                DatabaseDiagnostics.ExecuteAsync<int>(logger, "AutoCallDB", "dbo.example", default,
                    () => Task.FromException<int>(failure)));
            string text = output.ToString();
            Assert.Contains("AutoCallDB", text);
            Assert.Contains("dbo.example", text);
            Assert.Contains("2812", text);
            Assert.DoesNotContain("deadlock test message", text);
        }
        finally { Console.SetOut(original); }
    }
}
