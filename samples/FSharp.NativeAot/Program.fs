namespace ProcessKit.Samples.NativeAot

open System
open Microsoft.Extensions.DependencyInjection
open ProcessKit
open ProcessKit.Extensions.DependencyInjection

/// A minimal NativeAOT/trimmed consumer of ProcessKit. Published and run by the `aot-smoke` CI job to
/// validate that the packages' trim/AOT declarations hold in a real ahead-of-time-compiled image: it
/// spawns a child, captures a non-zero exit as an honest result (not a raised error), and runs a child
/// inside a kill-on-dispose `ProcessGroup`. Deliberately avoids the reflection-backed F# `printf`/`%A`
/// family so the smoke stays a clean canary for ProcessKit's own trim/AOT behaviour. Exits 0 on success
/// and non-zero if any check fails, so the CI run step gates on it.
module Program =

    let mutable private failures = 0

    let private log (message: string) = Console.Out.WriteLine message

    let private fail (message: string) =
        Console.Error.WriteLine("FAIL: " + message)
        failures <- failures + 1

    let private check (condition: bool) (message: string) =
        if condition then log ("  ok: " + message) else fail message

    let private mechanismName (mechanism: Mechanism) : string =
        match mechanism with
        | Mechanism.JobObject -> "JobObject (Windows Job Object)"
        | Mechanism.CgroupV2 -> "CgroupV2 (Linux cgroup v2)"
        | Mechanism.ProcessGroup -> "ProcessGroup (POSIX process group)"

    // Spawn a child and capture its stdout, requiring a clean exit.
    let private basicCapture () =
        task {
            log "spawn + capture: dotnet --version"

            match! (Command.create "dotnet" |> Command.arg "--version").OutputStringAsync() with
            | Ok result ->
                check result.IsSuccess "dotnet --version exits successfully"
                check (not (String.IsNullOrWhiteSpace result.Stdout)) "captured non-empty stdout"
                log ("  captured version: " + result.Stdout.Trim())
            | Error error -> fail ("dotnet --version errored: " + error.Message)
        }

    // A non-zero exit is DATA (surfaced in the ProcessResult), never a raised error — the honest-result
    // contract must survive AOT compilation.
    let private honestNonZero () =
        task {
            log "honest non-zero capture: unknown option"

            match! (Command.create "dotnet" |> Command.arg "--definitely-not-a-real-option").OutputStringAsync() with
            | Ok result -> check (not result.IsSuccess) "non-zero exit captured as data, not raised as an error"
            | Error error -> fail ("honest-result capture unexpectedly errored: " + error.Message)
        }

    // Run a child inside a kill-on-dispose ProcessGroup — exercises the platform containment P/Invoke
    // (Windows Job Object struct marshalling / POSIX process group / cgroup) under the native image.
    let private containment () =
        task {
            log "containment: run a child inside a ProcessGroup"

            match ProcessGroup.Create() with
            | Error error -> fail ("ProcessGroup.Create failed: " + error.Message)
            | Ok group ->
                use group = group
                log ("  mechanism: " + mechanismName group.Mechanism)
                let runner = group :> IProcessRunner

                match! runner.OutputStringAsync(Command.create "dotnet" |> Command.arg "--version") with
                | Ok result -> check result.IsSuccess "child ran to completion inside the ProcessGroup"
                | Error error -> fail ("run through ProcessGroup errored: " + error.Message)
        }

    // Resolve an IProcessRunner from a DI container (AddProcessKit) and run through it — validates the
    // ProcessKit.Extensions.DependencyInjection factory-registration path under the native image.
    let private dependencyInjection () =
        task {
            log "dependency injection: resolve IProcessRunner and run"

            use provider = ServiceCollection().AddProcessKit().BuildServiceProvider()
            let runner = provider.GetRequiredService<IProcessRunner>()

            match! runner.OutputStringAsync(Command.create "dotnet" |> Command.arg "--version") with
            | Ok result -> check result.IsSuccess "DI-resolved runner ran a child to completion"
            | Error error -> fail ("DI-resolved run errored: " + error.Message)
        }

    [<EntryPoint>]
    let main _ =
        task {
            log "ProcessKit NativeAOT smoke"
            do! basicCapture ()
            do! honestNonZero ()
            do! containment ()
            do! dependencyInjection ()

            if failures = 0 then
                log "all NativeAOT smoke checks passed"
                return 0
            else
                Console.Error.WriteLine(string failures + " NativeAOT smoke check(s) failed")
                return 1
        }
        |> fun t -> t.GetAwaiter().GetResult()
