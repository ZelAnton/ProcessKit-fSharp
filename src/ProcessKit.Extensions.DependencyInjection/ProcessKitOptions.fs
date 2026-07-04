namespace ProcessKit.Extensions.DependencyInjection

open System

/// Default settings applied to every command run through the DI-resolved `IProcessRunner`, configured
/// with `AddProcessKit(Action&lt;ProcessKitOptions&gt;)` or bound from an `IConfiguration` section.
///
/// Each default is applied **only when the command does not set it itself** — a per-command `Timeout` or
/// working directory always wins over the default here. Every property is optional (a `null` / no-value
/// leaves the corresponding behaviour unchanged), so an unconfigured `AddProcessKit()` behaves exactly as
/// before. These are the defaults a *primitive* runner can apply; for verb-layer defaults (retry) and
/// richer per-tool defaults (encoding, ok-codes, environment), register a named client with
/// `AddProcessKitClient` — its template carries them into the command before a verb runs.
[<Sealed>]
type ProcessKitOptions() =

    /// A default run timeout applied when a command sets none. `null` (the default) means no timeout.
    member val DefaultTimeout: Nullable<TimeSpan> = Nullable() with get, set

    /// A default working directory applied when a command sets none. `null` (the default) means the
    /// process inherits the current directory.
    member val DefaultWorkingDirectory: string | null = null with get, set
