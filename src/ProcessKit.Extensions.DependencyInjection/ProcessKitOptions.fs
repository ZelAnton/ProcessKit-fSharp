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
    let mutable defaultTimeout: Nullable<TimeSpan> = Nullable()

    /// A default run timeout applied when a command sets none. `null` (the default) means no timeout.
    /// A negative value is rejected at assignment (`ArgumentOutOfRangeException`) — validated here,
    /// at the options boundary, so a misconfiguration surfaces at setup/binding time rather than as an
    /// exception escaping a `Result`-returning verb when the default is later applied to a command.
    member _.DefaultTimeout
        with get () = defaultTimeout
        and set (value: Nullable<TimeSpan>) =
            if value.HasValue then
                ArgumentOutOfRangeException.ThrowIfLessThan(value.Value, TimeSpan.Zero)

            defaultTimeout <- value

    /// A default working directory applied when a command sets none. `null` (the default) means the
    /// process inherits the current directory.
    member val DefaultWorkingDirectory: string | null = null with get, set
