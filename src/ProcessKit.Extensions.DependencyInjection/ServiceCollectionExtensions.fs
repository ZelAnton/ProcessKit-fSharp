namespace ProcessKit.Extensions.DependencyInjection

open System.Runtime.CompilerServices
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open Microsoft.Extensions.Logging
open ProcessKit

/// An `IProcessRunner` that attaches `logger` to every command it runs (unless the command already
/// carries one), so DI-resolved runs emit ProcessKit's lifecycle events. argv/env are never logged
/// — see `Command.WithLogger`.
type internal LoggingRunner(inner: IProcessRunner, logger: ILogger) =

    let withLogger (command: Command) =
        if command.Config.Logger.IsNone then
            command.WithLogger logger
        else
            command

    interface IProcessRunner with
        member _.CaptureStringAsync(command, cancellationToken) =
            inner.CaptureStringAsync(withLogger command, cancellationToken)

        member _.CaptureBytesAsync(command, cancellationToken) =
            inner.CaptureBytesAsync(withLogger command, cancellationToken)

        member _.SpawnAsync(command, cancellationToken) =
            inner.SpawnAsync(withLogger command, cancellationToken)

/// `IServiceCollection.AddProcessKit()` extensions.
[<Extension>]
type ServiceCollectionExtensions =

    /// Register `IProcessRunner` (a singleton `JobRunner`). When the container also has an
    /// `ILoggerFactory`, the runner is wrapped so every run it drives emits ProcessKit's structured
    /// lifecycle events under the `ProcessKit` category (argv/env never logged). A pre-existing
    /// `IProcessRunner` registration is left untouched.
    [<Extension>]
    static member AddProcessKit(services: IServiceCollection) : IServiceCollection =
        services.TryAddSingleton<IProcessRunner>(fun provider ->
            let inner = JobRunner() :> IProcessRunner

            match provider.GetService<ILoggerFactory>() with
            | null -> inner
            | factory -> LoggingRunner(inner, factory.CreateLogger "ProcessKit") :> IProcessRunner)

        services
