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

module private Registration =

    /// True when `services` already carries a keyed `HostedProcessService` registered under `name` —
    /// the marker `AddProcessKitHostedProcess` uses to reject a duplicate name rather than silently
    /// dropping the second call's `command`/`configureSupervisor` (see that method's doc comment).
    /// Reading `descriptor.ServiceKey` is only valid on a keyed descriptor, so `IsKeyedService` is
    /// checked first (the `&&` short-circuits).
    let hasHostedProcess (services: IServiceCollection) (name: string) : bool =
        services
        |> Seq.exists (fun descriptor ->
            descriptor.IsKeyedService
            && descriptor.ServiceType = typeof<HostedProcessService>
            && (match descriptor.ServiceKey with
                | :? string as existing -> String.Equals(existing, name, StringComparison.Ordinal)
                | _ -> false))

/// `IServiceCollection.AddProcessKitHostedProcess()` extensions.
[<Extension>]
type HostedServiceCollectionExtensions =

    /// Register one supervised child process as an `IHostedService`.
    ///
    /// The `name` must be unique across `AddProcessKitHostedProcess` calls on this `IServiceCollection`.
    /// Registering a second process under a name already in use throws `InvalidOperationException`
    /// rather than silently taking the first registration and dropping this call's `command`/
    /// `configureSupervisor` (which the underlying `TryAddKeyedSingleton` would otherwise do), and
    /// rather than adding a second `IHostedService` that resolves to the very same keyed instance —
    /// so the host would `StartAsync`/`StopAsync` one service twice. A differing second registration
    /// under the same name is a configuration mistake, so it is surfaced honestly instead of downgraded
    /// to a partial no-op; register the second process under a different name. This mirrors
    /// `AddProcessKitClient`, which rejects a duplicate client name the same way. (`AddProcessKit` /
    /// `AddProcessKitGroup` are unnamed idempotent registrations and keep their first-wins `TryAdd`
    /// semantics — repeating them is benign, not a mistake.)
    [<Extension>]
    static member AddProcessKitHostedProcess
        (services: IServiceCollection, name: string, command: Command, configureSupervisor: Func<Supervisor, Supervisor>) : IServiceCollection =
        ArgumentNullException.ThrowIfNull services
        ArgumentNullException.ThrowIfNull name
        ArgumentNullException.ThrowIfNull command
        ArgumentNullException.ThrowIfNull configureSupervisor

        if Registration.hasHostedProcess services name then
            raise (
                InvalidOperationException
                    $"A ProcessKit hosted process named '{name}' is already registered. Each AddProcessKitHostedProcess name must be unique; register the second process under a different name."
            )

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
