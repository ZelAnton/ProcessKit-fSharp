using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProcessKit;
using ProcessKit.Extensions.Hosting;

if (args.Contains("--child", StringComparer.Ordinal))
{
    var childBuilder = Host.CreateApplicationBuilder(args);
    childBuilder.Services.AddHostedService<ChildWorker>();
    await childBuilder.Build().RunAsync();
    return;
}

var executable = Environment.ProcessPath
    ?? throw new InvalidOperationException("The current executable path is unavailable.");

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Information);

var childCommand = new Command(executable)
    .Arg("--child")
    .StopSignal(Signal.Term)
    .WindowsCtrlSignals()
    .Stdout(StdioMode.Inherit)
    .Stderr(StdioMode.Inherit);

builder.Services.AddProcessKitHostedProcess(
    "worker",
    childCommand,
    supervisor => supervisor
        .Restart(RestartPolicy.OnCrash)
        .Backoff(TimeSpan.FromSeconds(1), 2.0)
        .MaxBackoff(TimeSpan.FromSeconds(10))
        .StormPause(TimeSpan.FromSeconds(30)));

builder.Services.ConfigureProcessKitHostedProcess("worker", options =>
{
    options.ShutdownGracePeriod = TimeSpan.FromSeconds(5);
});

builder.Services.AddProcessKitHostedProcessHealthCheck("worker");
builder.Services.AddHostedService<HealthReporter>();

await builder.Build().RunAsync();

internal sealed class ChildWorker(ILogger<ChildWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Supervised child started with pid {Pid}.", Environment.ProcessId);

        while (!stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Supervised child heartbeat at {Timestamp}.", DateTimeOffset.UtcNow);
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}

internal sealed class HealthReporter(
    [FromKeyedServices("worker")] HostedProcessService process,
    [FromKeyedServices("worker")] HostedProcessHealthCheck healthCheck,
    ILogger<HealthReporter> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await ((IHealthCheck)healthCheck)
                .CheckHealthAsync(new HealthCheckContext(), stoppingToken);

            logger.LogInformation(
                "Worker health is {Status}; supervision active={Active}, restarts={Restarts}, storm pause={StormPause}.",
                result.Status,
                process.IsSupervisionActive,
                process.RestartCount,
                process.IsStormPaused);

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
