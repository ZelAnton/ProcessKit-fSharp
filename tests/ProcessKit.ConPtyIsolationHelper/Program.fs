namespace ProcessKit.ConPtyIsolationHelper

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open ProcessKit

module Program =

    [<Literal>]
    let private CTRL_C_EVENT = 0u

    [<UnmanagedFunctionPointer(CallingConvention.Winapi)>]
    type private Handler = delegate of uint32 -> bool

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private SetConsoleCtrlHandler(Handler handlerRoutine, bool add)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private GenerateConsoleCtrlEvent(uint32 ctrlEvent, uint32 processGroupId)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private FreeConsole()

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private AllocConsole()

    let private run (completionMarker: string) (statusMarker: string) =
        task {
            let handler = Handler(fun _ -> true)
            let report (status: string) = File.WriteAllText(statusMarker, status)
            let mutable handlerInstalled = false

            if not (FreeConsole()) then
                return 76
            elif not (AllocConsole()) then
                return 78
            elif not (SetConsoleCtrlHandler(handler, true)) then
                return 71
            else
                handlerInstalled <- true
                report "starting-child"

                try
                    let childScript =
                        "Start-Sleep -Seconds 2; "
                        + "[System.IO.File]::WriteAllText($env:PK_CONPTY_COMPLETE, 'complete'); exit 23"

                    let command =
                        Command.create "powershell.exe"
                        |> Command.args [ "-NoLogo"; "-NoProfile"; "-NonInteractive"; "-Command"; childScript ]
                        |> Command.env "PK_CONPTY_COMPLETE" completionMarker
                        |> Command.pty
                        |> Command.keepStdinOpen
                        |> Command.timeout (TimeSpan.FromSeconds 30.0)

                    let runner: IProcessRunner = JobRunner()

                    match! runner.StartAsync(command, CancellationToken.None) with
                    | Error(ProcessError.Unsupported message) when message.Contains "1809" -> return 77
                    | Error _ -> return 72
                    | Ok running ->
                        report "child-started"

                        let! exitCode =
                            task {
                                // Give PowerShell time to enter Start-Sleep. The child's completion marker,
                                // not this delay, proves that it survived the later broadcast into the
                                // private console to which it belonged when it was spawned.
                                do! Task.Delay 500

                                match running.TakeStdin() with
                                | None -> return 73
                                | Some stdin ->
                                    do! stdin.WriteAsync([| 0x03uy |])
                                    do! stdin.FlushAsync()
                                    report "ctrl-c-input-sent"

                                    if not (GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0u)) then
                                        return 74
                                    else
                                        report "ctrl-c-broadcast-sent"

                                        match! running.WaitAsync() with
                                        | Outcome.Exited 23 when File.Exists completionMarker ->
                                            report "child-completed"
                                            return 0
                                        | _ -> return 75
                            }

                        report $"disposing-after-{exitCode}"
                        do! (running :> IAsyncDisposable).DisposeAsync().AsTask()
                        report $"disposed-after-{exitCode}"
                        return exitCode
                finally
                    if handlerInstalled then
                        SetConsoleCtrlHandler(handler, false) |> ignore

                    GC.KeepAlive handler
        }

    [<EntryPoint>]
    let main args =
        if args.Length <> 2 then
            70
        else
            run args[0] args[1] |> fun task -> task.GetAwaiter().GetResult()
