using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = FunctionsApplication.CreateBuilder(args);
// Add Logging
builder.Services.AddLogging(logging =>
{
    logging.AddConsole(); // Logs to console (visible in Log Stream)
    logging.SetMinimumLevel(LogLevel.Information); // Adjust as needed
});

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();
builder.Build().Run();
