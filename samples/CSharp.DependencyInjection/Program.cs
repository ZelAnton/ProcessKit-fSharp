using System;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcessKit;
using ProcessKit.Extensions.DependencyInjection;
using ProcessKit.Testing;

var services = new ServiceCollection();

services.AddSingleton<IProcessRunner>(
    new ScriptedRunner()
        .On(["git", "status"], Reply.Ok("clean\n"))
        .On(["git", "rev-parse", "HEAD"], Reply.Ok("abc123\n"))
);

services.AddProcessKit(options =>
{
    options.DefaultTimeout = TimeSpan.FromSeconds(30);
});

services.AddProcessKitClient("git", "git");

await using var provider = services.BuildServiceProvider();

var runner = provider.GetRequiredService<IProcessRunner>();
var git = provider.GetRequiredKeyedService<CliClient>("git");
var options = provider.GetRequiredService<IOptions<ProcessKitOptions>>().Value;

Console.WriteLine("== Dependency injection ==");
Console.WriteLine($"default timeout: {options.DefaultTimeout}");

Console.WriteLine(await runner.RunAsync(new Command("git").Args(["rev-parse", "HEAD"]), CancellationToken.None) switch
{
    { IsOk: true, ResultValue: var sha } => $"injected IProcessRunner returned: {sha}",
    { IsOk: false, ErrorValue: var err } => $"runner error: {err.Message}",
});

Console.WriteLine(await git.RunAsync(["status"]) switch
{
    { IsOk: true, ResultValue: var status } => $"keyed CliClient returned: {status}",
    { IsOk: false, ErrorValue: var err } => $"client error: {err.Message}",
});
