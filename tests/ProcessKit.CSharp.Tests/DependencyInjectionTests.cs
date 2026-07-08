using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using ProcessKit;
using ProcessKit.Extensions.DependencyInjection;
using ProcessKit.Testing;

namespace ProcessKit.CSharp.Tests;

/// Covers `ProcessKit.Extensions.DependencyInjection` (`docs/dependency-injection.md`): registering
/// `IProcessRunner` / keyed `CliClient`s via `AddProcessKit`/`AddProcessKitClient`, and binding
/// `ProcessKitOptions` defaults. Registers a `ScriptedRunner` before `AddProcessKit` (its `TryAdd`
/// backs off) so the whole container round trip stays hermetic — no real subprocess spawns here.
[TestFixture]
public class DependencyInjectionTests
{
    [Test]
    public async Task AddProcessKit_registers_an_IProcessRunner_a_consumer_can_inject()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProcessRunner>(new ScriptedRunner().On(["probe"], Reply.Ok("hermetic\n")));
        services.AddProcessKit();

        await using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<IProcessRunner>();

        var result = await runner.RunAsync(new Command("probe"), CancellationToken.None);

        Assert.That(result.IsOk, Is.True);
        Assert.That(result.ResultValue, Is.EqualTo("hermetic"));
    }

    [Test]
    public async Task AddProcessKitClient_registers_a_keyed_CliClient_backed_by_the_container_runner()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProcessRunner>(new ScriptedRunner().On(["git", "status"], Reply.Ok("clean\n")));
        services.AddProcessKit();
        services.AddProcessKitClient("git", "git");

        await using var provider = services.BuildServiceProvider();
        var git = provider.GetRequiredKeyedService<CliClient>("git");

        var result = await git.RunAsync(["status"]);

        Assert.That(result.IsOk, Is.True);
        Assert.That(result.ResultValue, Is.EqualTo("clean"));
    }

    [Test]
    public void AddProcessKit_with_a_configure_action_binds_ProcessKitOptions_defaults()
    {
        var services = new ServiceCollection();

        services.AddProcessKit(options =>
        {
            options.DefaultTimeout = TimeSpan.FromSeconds(30);
            options.DefaultWorkingDirectory = "/app";
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ProcessKitOptions>>().Value;

        Assert.That(options.DefaultTimeout, Is.EqualTo(TimeSpan.FromSeconds(30)));
        Assert.That(options.DefaultWorkingDirectory, Is.EqualTo("/app"));
    }
}
