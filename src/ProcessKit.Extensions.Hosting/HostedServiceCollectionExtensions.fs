namespace ProcessKit.Extensions.Hosting

open System
open System.Runtime.CompilerServices
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open Microsoft.Extensions.Options
open ProcessKit

/// `IServiceCollection.AddProcessKitHostedProcess()` extensions.
[<Extension>]
type HostedServiceCollectionExtensions =

    /// Register one supervised child process as an `IHostedService`.
    [<Extension>]
    static member AddProcessKitHostedProcess
        (services: IServiceCollection, name: string, command: Command, configureSupervisor: Func<Supervisor, Supervisor>) : IServiceCollection =
        ArgumentNullException.ThrowIfNull services
        ArgumentNullException.ThrowIfNull name
        ArgumentNullException.ThrowIfNull command
        ArgumentNullException.ThrowIfNull configureSupervisor

        services.AddOptions() |> ignore

        services.TryAddKeyedSingleton<HostedProcessService>(
            name,
            Func<IServiceProvider, obj | null, HostedProcessService>(fun provider _key ->
                let runner =
                    match provider.GetService<IProcessRunner>() with
                    | null -> JobRunner() :> IProcessRunner
                    | existing -> existing

                let logger =
                    match provider.GetService<ILoggerFactory>() with
                    | null -> NullLogger.Instance :> ILogger
                    | factory -> factory.CreateLogger("ProcessKit.Extensions.Hosting")

                let options =
                    provider.GetRequiredService<IOptionsMonitor<HostedProcessOptions>>().Get name

                new HostedProcessService(name, command, configureSupervisor, runner, options, logger))
        )

        services.AddSingleton<IHostedService>(
            Func<IServiceProvider, IHostedService>(fun provider ->
                provider.GetRequiredKeyedService<HostedProcessService> name :> IHostedService)
        )
        |> ignore

        services

    /// Register one supervised child process as an `IHostedService`, with the default supervisor settings.
    [<Extension>]
    static member AddProcessKitHostedProcess
        (services: IServiceCollection, name: string, command: Command)
        : IServiceCollection =
        HostedServiceCollectionExtensions.AddProcessKitHostedProcess(
            services,
            name,
            command,
            Func<Supervisor, Supervisor>(id)
        )

    /// Configure host-shutdown options for a named ProcessKit hosted process.
    [<Extension>]
    static member ConfigureProcessKitHostedProcess
        (services: IServiceCollection, name: string, configure: Action<HostedProcessOptions>)
        : IServiceCollection =
        ArgumentNullException.ThrowIfNull services
        ArgumentNullException.ThrowIfNull name
        ArgumentNullException.ThrowIfNull configure

        services.Configure(name, configure) |> ignore
        services

    /// Opt-in health check for a named ProcessKit hosted process: registers `HostedProcessHealthCheck`
    /// as a keyed `IHealthCheck` (keyed by `name`, same key as `AddProcessKitHostedProcess`), reporting
    /// Healthy/Degraded/Unhealthy from the hosted process's supervision state. Never registered unless
    /// you call this — the base `AddProcessKitHostedProcess` registration is unaffected either way.
    /// This package depends only on `Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions`, so
    /// this method cannot call `AddHealthChecks()` on your behalf; see `HostedProcessHealthCheck`'s doc
    /// comment for how to wire the registered keyed check into your own health-checks pipeline.
    [<Extension>]
    static member AddProcessKitHostedProcessHealthCheck
        (services: IServiceCollection, name: string)
        : IServiceCollection =
        ArgumentNullException.ThrowIfNull services
        ArgumentNullException.ThrowIfNull name

        services.TryAddKeyedSingleton<HostedProcessHealthCheck>(
            name,
            Func<IServiceProvider, obj | null, HostedProcessHealthCheck>(fun provider _key ->
                HostedProcessHealthCheck(provider.GetRequiredKeyedService<HostedProcessService> name))
        )

        services
