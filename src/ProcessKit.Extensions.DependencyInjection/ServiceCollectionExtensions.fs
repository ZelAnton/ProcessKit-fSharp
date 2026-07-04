namespace ProcessKit.Extensions.DependencyInjection

open System
open System.Runtime.CompilerServices
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open ProcessKit

/// An `IProcessRunner` that attaches `logger` to every command it runs (unless the command already
/// carries one), so DI-resolved runs emit ProcessKit's lifecycle events. argv/env are never logged
/// — see `Command.Logger`.
type internal LoggingRunner(inner: IProcessRunner, logger: ILogger) =

    let withLogger (command: Command) =
        if command.Config.Logger.IsNone then
            command.Logger logger
        else
            command

    interface IProcessRunner with
        member _.CaptureStringAsync(command, cancellationToken) =
            inner.CaptureStringAsync(withLogger command, cancellationToken)

        member _.CaptureBytesAsync(command, cancellationToken) =
            inner.CaptureBytesAsync(withLogger command, cancellationToken)

        member _.SpawnAsync(command, cancellationToken) =
            inner.SpawnAsync(withLogger command, cancellationToken)

/// An `IProcessRunner` that fills in the configured `ProcessKitOptions` defaults on every command —
/// **only where the command has not set that field itself**, so a per-command value always wins. A
/// no-op when no defaults are configured (an unconfigured `AddProcessKit()` runs commands unchanged).
///
/// Only defaults a *primitive* runner can honour live here (timeout, working directory) — both are read
/// on the spawn/primitive path, so stamping them onto the forwarded command takes effect. Verb-layer
/// policy (retry) is deliberately NOT here: the retry loop runs one layer up (`Runner.withRetry`) and
/// reads the command before this decorator sees it, so a retry stamped here would never fire — retry
/// defaults belong on a `CliClient` template (`AddProcessKitClient`), where they precede the verb.
type internal DefaultsRunner(inner: IProcessRunner, options: ProcessKitOptions) =

    let applyDefaults (command: Command) : Command =
        let mutable c = command

        if command.Config.Timeout.IsNone && options.DefaultTimeout.HasValue then
            c <- c.Timeout options.DefaultTimeout.Value

        if command.Config.WorkingDirectory.IsNone then
            match options.DefaultWorkingDirectory with
            | null -> ()
            | dir -> c <- c.CurrentDir dir

        c

    interface IProcessRunner with
        member _.CaptureStringAsync(command, cancellationToken) =
            inner.CaptureStringAsync(applyDefaults command, cancellationToken)

        member _.CaptureBytesAsync(command, cancellationToken) =
            inner.CaptureBytesAsync(applyDefaults command, cancellationToken)

        member _.SpawnAsync(command, cancellationToken) =
            inner.SpawnAsync(applyDefaults command, cancellationToken)

module internal DiInternals =

    /// Wrap a base runner with the configured defaults, then (when the container has an `ILoggerFactory`)
    /// the lifecycle-logging decorator — the shared build path for the default and shared-group runners.
    let buildRunner (provider: IServiceProvider) (inner: IProcessRunner) : IProcessRunner =
        let options = provider.GetRequiredService<IOptions<ProcessKitOptions>>().Value
        let withDefaults = DefaultsRunner(inner, options) :> IProcessRunner

        match provider.GetService<ILoggerFactory>() with
        | null -> withDefaults
        | factory -> LoggingRunner(withDefaults, factory.CreateLogger "ProcessKit") :> IProcessRunner

/// `IServiceCollection.AddProcessKit()` / `AddProcessKitClient()` / `AddProcessKitGroup()` extensions.
[<Extension>]
type ServiceCollectionExtensions =

    /// Register `IProcessRunner` (a singleton `JobRunner`). When the container also has an
    /// `ILoggerFactory`, the runner is wrapped so every run it drives emits ProcessKit's structured
    /// lifecycle events under the `ProcessKit` category (argv/env never logged); configured
    /// `ProcessKitOptions` defaults are applied to each command. A pre-existing `IProcessRunner`
    /// registration is left untouched.
    [<Extension>]
    static member AddProcessKit(services: IServiceCollection) : IServiceCollection =
        services.AddOptions() |> ignore

        services.TryAddSingleton<IProcessRunner>(fun provider ->
            DiInternals.buildRunner provider (JobRunner() :> IProcessRunner))

        services

    /// Register the runner (see `AddProcessKit`) and configure the `ProcessKitOptions` defaults
    /// (default timeout, working directory) applied to every DI-resolved run.
    [<Extension>]
    static member AddProcessKit
        (services: IServiceCollection, configure: Action<ProcessKitOptions>)
        : IServiceCollection =
        ArgumentNullException.ThrowIfNull configure
        services.Configure configure |> ignore
        ServiceCollectionExtensions.AddProcessKit services

    /// Register the runner (see `AddProcessKit`) and bind the `ProcessKitOptions` defaults from a
    /// configuration section (e.g. `configuration.GetSection("ProcessKit")`).
    [<Extension>]
    static member AddProcessKit(services: IServiceCollection, configuration: IConfiguration) : IServiceCollection =
        ArgumentNullException.ThrowIfNull configuration
        services.Configure<ProcessKitOptions> configuration |> ignore
        ServiceCollectionExtensions.AddProcessKit services

    /// Register a **keyed** `CliClient` for one external tool under `name`, so an app injects it by role
    /// (`[FromKeyedServices("git")] CliClient git`). The client runs through the container's registered
    /// `IProcessRunner` when present (so it is logger-aware and honours a shared group / test runner),
    /// otherwise a default `JobRunner`. `configure` applies shared defaults via the `CliClient` builder
    /// (`WithDefaults` — timeout, working directory, environment, encoding, ok-codes, …).
    [<Extension>]
    static member AddProcessKitClient
        (services: IServiceCollection, name: string, program: string, configure: Func<CliClient, CliClient>)
        : IServiceCollection =
        ArgumentNullException.ThrowIfNull name
        ArgumentNullException.ThrowIfNull program
        ArgumentNullException.ThrowIfNull configure

        services.TryAddKeyedSingleton<CliClient>(
            name,
            Func<IServiceProvider, obj | null, CliClient>(fun provider _key ->
                let runner =
                    match provider.GetService<IProcessRunner>() with
                    | null -> JobRunner() :> IProcessRunner
                    | existing -> existing

                configure.Invoke(CliClient(program).WithRunner runner))
        )

        services

    /// Register a keyed `CliClient` for `program` under `name`, with no extra defaults (see the
    /// `configure` overload).
    [<Extension>]
    static member AddProcessKitClient
        (services: IServiceCollection, name: string, program: string)
        : IServiceCollection =
        ServiceCollectionExtensions.AddProcessKitClient(services, name, program, Func<CliClient, CliClient>(id))

    /// Register `IProcessRunner` backed by a **shared, container-managed `ProcessGroup`** — every run
    /// goes into one kill-on-dispose container whose lifetime is the provider's, so disposing the
    /// provider reaps the whole tree. The `ProcessGroup` is also registered directly (inject it for tree
    /// control). Logger-aware and options-applying like `AddProcessKit`. Call this **instead of**
    /// `AddProcessKit` when you want a shared group; both use `TryAdd`, so the first wins.
    [<Extension>]
    static member AddProcessKitGroup(services: IServiceCollection) : IServiceCollection =
        services.AddOptions() |> ignore

        services.TryAddSingleton<ProcessGroup>(fun _provider ->
            match ProcessGroup.Create() with
            | Ok group -> group
            | Error error -> raise (ProcessException error))

        services.TryAddSingleton<IProcessRunner>(fun provider ->
            DiInternals.buildRunner provider (provider.GetRequiredService<ProcessGroup>() :> IProcessRunner))

        services
