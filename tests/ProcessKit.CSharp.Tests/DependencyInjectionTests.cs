using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
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
    private sealed class SingleValueConfiguration(string key, string? value) : IConfiguration
    {
        private readonly IConfigurationSection section = new SingleValueConfigurationSection(key, value);

        public string? this[string configurationKey]
        {
            get => configurationKey == key ? value : null;
            set => throw new NotSupportedException();
        }

        public IEnumerable<IConfigurationSection> GetChildren() => [section];

        public IChangeToken GetReloadToken() => NullChangeToken.Singleton;

        public IConfigurationSection GetSection(string configurationKey) =>
            configurationKey == key ? section : new SingleValueConfigurationSection(configurationKey, null);
    }

    private sealed class SingleValueConfigurationSection(string key, string? value) : IConfigurationSection
    {
        public string Key => key;

        public string Path => key;

        public string? Value
        {
            get => value;
            set => throw new NotSupportedException();
        }

        public string? this[string configurationKey]
        {
            get => null;
            set => throw new NotSupportedException();
        }

        public IEnumerable<IConfigurationSection> GetChildren() => [];

        public IChangeToken GetReloadToken() => NullChangeToken.Singleton;

        public IConfigurationSection GetSection(string configurationKey) =>
            new SingleValueConfigurationSection(configurationKey, null);
    }

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

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(" /app")]
    [TestCase("/app ")]
    public void ProcessKitOptions_DefaultWorkingDirectory_rejects_invalid_values(string value)
    {
        var options = new ProcessKitOptions();

        var exception = Assert.Throws<ArgumentException>(() => options.DefaultWorkingDirectory = value);

        Assert.That(exception!.ParamName, Is.EqualTo(nameof(ProcessKitOptions.DefaultWorkingDirectory)));
        Assert.That(exception.Message, Does.Contain(nameof(ProcessKitOptions.DefaultWorkingDirectory)));
    }

    [Test]
    public void ProcessKitOptions_DefaultWorkingDirectory_accepts_null_and_a_valid_path()
    {
        var options = new ProcessKitOptions { DefaultWorkingDirectory = null };

        Assert.That(options.DefaultWorkingDirectory, Is.Null);

        options.DefaultWorkingDirectory = "/app";

        Assert.That(options.DefaultWorkingDirectory, Is.EqualTo("/app"));

        options.DefaultWorkingDirectory = null;

        Assert.That(options.DefaultWorkingDirectory, Is.Null);
    }

    [Test]
    public void AddProcessKit_with_configuration_rejects_an_invalid_default_working_directory()
    {
        var services = new ServiceCollection();
        services.AddProcessKit(new SingleValueConfiguration(nameof(ProcessKitOptions.DefaultWorkingDirectory), ""));

        using var provider = services.BuildServiceProvider();

        var thrown = Assert.Throws<TargetInvocationException>(
            () => { _ = provider.GetRequiredService<IOptions<ProcessKitOptions>>().Value; });

        Assert.That(thrown?.InnerException, Is.TypeOf<ArgumentException>());
    }
}
