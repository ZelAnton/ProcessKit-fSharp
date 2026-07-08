namespace ProcessKit.Samples.FSharpCatalog

open System
open System.Threading
open System.Threading.Tasks
open ProcessKit
open ProcessKit.Testing

module Program =

    let private printHeader text =
        printfn ""
        printfn "== %s ==" text

    let private printError (error: ProcessError) = printfn "error: %s" error.Message

    let private basicRunAndCapture () =
        task {
            printHeader "Basic run and honest capture"

            match! (Command.create "dotnet" |> Command.arg "--version").RunAsync() with
            | Ok version -> printfn "dotnet version: %s" version
            | Error error -> printError error

            let failing =
                Command.create "dotnet" |> Command.arg "--definitely-not-a-real-option"

            match! failing.OutputStringAsync() with
            | Ok result ->
                printfn
                    "captured non-zero exit as data: success=%b code=%A stderr=%s"
                    result.IsSuccess
                    result.Code
                    (result.Stderr.Trim())
            | Error error -> printError error
        }

    let private streamingWithReadiness () =
        task {
            printHeader "Streaming with readiness probe"

            match! (Command.create "dotnet" |> Command.arg "--info").StartAsync() with
            | Error error -> printError error
            | Ok running ->
                use running = running

                match! running.WaitForLineAsync((fun line -> line.Contains "Host:"), TimeSpan.FromSeconds 10.0) with
                | Ok line -> printfn "ready line: %s" line
                | Error error -> printError error

                match! running.FinishAsync() with
                | Ok finished -> printfn "finished: %A" finished.Outcome
                | Error error -> printError error
        }

    let private shellFreePipeline () =
        task {
            printHeader "Shell-free pipeline"

            let pipeline =
                (Command.create "dotnet" |> Command.arg "--info").Pipe(Command.create "sort")

            match! pipeline.OutputStringAsync() with
            | Ok output ->
                let firstLines =
                    output.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    |> Seq.map (fun line -> line.Trim())
                    |> Seq.filter (String.IsNullOrWhiteSpace >> not)
                    |> Seq.truncate 5
                    |> String.concat " | "

                printfn "sorted first lines: %s" firstLines
            | Error error -> printError error
        }

    let private supervisionWithScriptedRunner () =
        task {
            printHeader "Supervisor restart/backoff"

            let mutable calls = 0

            let runner =
                (ScriptedRunner())
                    .When(
                        (fun _ ->
                            calls <- calls + 1
                            calls <= 2),
                        Reply.Fail(1, "transient crash")
                    )
                    .Fallback(Reply.Ok "ready")
                :> IProcessRunner

            let supervisor =
                (Supervisor.create (Command.create "worker"))
                    .WithRunner(runner)
                    .Restart(RestartPolicy.OnCrash)
                    .Backoff(TimeSpan.FromMilliseconds 10.0, 1.0)
                    .MaxRestarts(3)

            match! supervisor.RunAsync() with
            | Ok outcome ->
                printfn "stopped after %i restarts: %A" outcome.Restarts outcome.Stopped
                printfn "final stdout: %s" (outcome.FinalResult.Stdout.Trim())
            | Error error -> printError error
        }

    let private testDoubleSeam () =
        task {
            printHeader "Test double seam"

            let runner: IProcessRunner =
                (ScriptedRunner()).On([ "git"; "rev-parse"; "HEAD" ], Reply.Ok "abc123\n")

            match!
                runner.RunAsync(Command.create "git" |> Command.args [ "rev-parse"; "HEAD" ], CancellationToken.None)
            with
            | Ok sha -> printfn "stubbed git HEAD: %s" sha
            | Error error -> printError error
        }

    [<EntryPoint>]
    let main _ =
        task {
            do! basicRunAndCapture ()
            do! streamingWithReadiness ()
            do! shellFreePipeline ()
            do! supervisionWithScriptedRunner ()
            do! testDoubleSeam ()
            return 0
        }
        |> fun t -> t.GetAwaiter().GetResult()
