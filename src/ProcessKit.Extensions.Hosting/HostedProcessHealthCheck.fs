namespace ProcessKit.Extensions.Hosting

// FS3261 fires on the `box`ed values below (`box service.Name` / `box restarts` / ...): the checked
// nullness feature can't see that `box` of a `string`/`int`/`bool` value never actually produces
// `null`, so it types the boxed result as nullable `obj` and then flags the mismatch against
// `HealthCheckResult`'s non-nullable `IReadOnlyDictionary<string, obj>` parameter. A false positive —
// same rationale as the `FS3265` suppression in `Native.Windows.fs`.
#nowarn "3261"

open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.Extensions.Diagnostics.HealthChecks

/// `IHealthCheck` reporting the observable supervision state of one named `HostedProcessService` —
/// see `HostedServiceCollectionExtensions.AddProcessKitHostedProcessHealthCheck` to register one.
///
/// **Dependency placement (decided here, not a separate package).** This type — and the
/// registration extension alongside it — lives in this same `ProcessKit.Extensions.Hosting`
/// package rather than a new `ProcessKit.Extensions.HealthChecks` package: the only additional
/// dependency it needs, `Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions`
/// (`IHealthCheck`/`HealthCheckResult`/`HealthCheckRegistration` — types only, no health-check
/// runtime), is exactly as lightweight as the `Microsoft.Extensions.Hosting.Abstractions` this
/// package already depends on. A separate package would only add another assembly to publish and
/// version in lockstep for no real dependency-weight saving, since a consumer who never calls
/// `AddProcessKitHostedProcessHealthCheck` still pays only for the Abstractions-only reference.
///
/// **Only the Abstractions package — deliberately.** The full `Microsoft.Extensions.Diagnostics.HealthChecks`
/// package (which supplies `AddHealthChecks()` / `IHealthChecksBuilder` / the concrete
/// `HealthCheckService` that actually polls registered checks) is intentionally *not* referenced
/// here, so this package never forces that heavier dependency onto a consumer who does not use
/// health checks at all. The consequence: `AddProcessKitHostedProcessHealthCheck` cannot call
/// `AddHealthChecks()` on your behalf — it only registers this `IHealthCheck` as a keyed service.
/// Wire it into your own health-check pipeline (already referenced transitively via the ASP.NET
/// Core shared framework in a web host; add the `Microsoft.Extensions.Diagnostics.HealthChecks`
/// package explicitly in a Worker Service) with `HealthCheckRegistration`'s factory overload, keyed
/// by the same name:
///
/// ```csharp
/// services.AddProcessKitHostedProcess("worker", command);
/// services.AddProcessKitHostedProcessHealthCheck("worker");
///
/// services.AddHealthChecks().Add(
///     new HealthCheckRegistration(
///         "worker",
///         sp => sp.GetRequiredKeyedService<HostedProcessHealthCheck>("worker"),
///         failureStatus: null,
///         tags: null));
/// ```
[<Sealed>]
type HostedProcessHealthCheck(service: HostedProcessService) =

    interface IHealthCheck with
        /// Maps `HostedProcessService`'s observable state to a `HealthCheckResult`:
        /// - **Healthy** — supervision is active and not currently storm-paused: the child is
        ///   running, or restarting within the configured `RestartPolicy`.
        /// - **Degraded** — supervision is active but currently paused by the failure-storm guard
        ///   (`Supervisor.StormPause`): restarts are being throttled because failures are
        ///   clustering.
        /// - **Unhealthy** — supervision is not active: it has not been started yet, or it has
        ///   ended (an error, a crashed/permanent-failure stop, an exhausted restart budget, or a
        ///   stop-predicate match) — the hosted child is not running.
        member _.CheckHealthAsync(_context, _cancellationToken) =
            let isActive = service.IsSupervisionActive
            let stormPaused = service.IsStormPaused
            let restarts = service.RestartCount

            let data: IReadOnlyDictionary<string, obj> =
                readOnlyDict
                    [ "name", box service.Name
                      "restarts", box restarts
                      "isSupervisionActive", box isActive
                      "isStormPaused", box stormPaused ]

            let result =
                if isActive && stormPaused then
                    HealthCheckResult.Degraded(
                        $"ProcessKit hosted process '{service.Name}' is storm-paused: restarts are being throttled ({restarts} restart(s) so far).",
                        null,
                        data
                    )
                elif isActive then
                    HealthCheckResult.Healthy(
                        $"ProcessKit hosted process '{service.Name}' is running ({restarts} restart(s) so far).",
                        data
                    )
                else
                    let reason =
                        match service.LastOutcome with
                        | None -> "it has not started yet"
                        | Some(Ok outcome) -> $"supervision ended ({outcome.Stopped})"
                        | Some(Error error) -> $"supervision ended with an error: {error.Message}"

                    HealthCheckResult.Unhealthy(
                        $"ProcessKit hosted process '{service.Name}' is not running: {reason}.",
                        null,
                        data
                    )

            Task.FromResult result
