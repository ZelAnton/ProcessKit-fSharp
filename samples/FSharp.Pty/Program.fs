namespace ProcessKit.Samples.FSharpPty

open System
open System.Text
open ProcessKit

module Program =

    let private promptCommand () =
        if OperatingSystem.IsWindows() then
            Ok(
                (Command.create "powershell.exe")
                    .Args(
                        [ "-NoProfile"
                          "-Command"
                          "$null = Read-Host -Prompt 'Password' -AsSecureString; Write-Output OK" ]
                    )
            )
        elif OperatingSystem.IsLinux() then
            Ok(
                Command.create "/bin/sh"
                |> Command.args [ "-c"; "printf 'Password: '; IFS= read -r password; printf 'OK\\n'" ]
            )
        else
            Error "This sample needs Windows ConPTY or Linux openpty plus setsid --ctty."

    let private streamMergedOutput (running: RunningProcess) =
        task {
            let enumerator = running.StdoutLinesAsync().GetAsyncEnumerator()

            try
                let mutable hasMore = true

                while hasMore do
                    let! moved = enumerator.MoveNextAsync().AsTask()

                    if moved then
                        Console.WriteLine enumerator.Current
                    else
                        hasMore <- false
            finally
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()
        }

    [<EntryPoint>]
    let main _ =
        task {
            match promptCommand () with
            | Error message ->
                Console.Error.WriteLine message
                return 1
            | Ok baseCommand ->
                let command =
                    baseCommand
                        .Pty({ PtyConfig.Default with Echo = false })
                        .KeepStdinOpen()
                        .Timeout(TimeSpan.FromSeconds 30.0)

                match! command.StartAsync() with
                | Error error ->
                    Console.Error.WriteLine error.Message
                    return 1
                | Ok running ->
                    use running = running
                    let outputTask = streamMergedOutput running

                    match running.TakeStdin() with
                    | None ->
                        Console.Error.WriteLine "PTY stdin was not available."
                        return 1
                    | Some stdin ->
                        if OperatingSystem.IsWindows() then
                            do! stdin.WriteAsync(Encoding.UTF8.GetBytes "sample-password\r")
                        else
                            do! stdin.WriteLineAsync "sample-password"

                        do! stdin.FlushAsync()
                        do! stdin.FinishAsync()
                        do! outputTask

                        match! running.FinishAsync() with
                        | Ok finished ->
                            match finished.Outcome with
                            | Outcome.Exited 0 -> return 0
                            | outcome ->
                                Console.Error.WriteLine $"Prompt exited with {outcome}."
                                return 1
                        | Error error ->
                            Console.Error.WriteLine error.Message
                            return 1
        }
        |> fun task -> task.GetAwaiter().GetResult()
