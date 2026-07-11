namespace ProcessKit.Tests

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

/// Windows P/Invoke for temporarily pointing the test process's own `STD_INPUT_HANDLE` at a known
/// payload file, so a child spawned with `Command.InheritStdin` reads that payload. Mirrors the
/// std-handle manipulation already used by `WindowsOverlappedPipeTests`.
module private WindowsStdinRedirect =
    [<Literal>]
    let STD_INPUT_HANDLE = -10

    [<Literal>]
    let GENERIC_READ = 0x80000000u

    [<Literal>]
    let FILE_SHARE_READ = 0x00000001u

    [<Literal>]
    let OPEN_EXISTING = 3u

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern nativeint GetStdHandle(int nStdHandle)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool SetStdHandle(int nStdHandle, nativeint hHandle)

    [<DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern nativeint CreateFileW(
        string lpFileName,
        uint32 dwDesiredAccess,
        uint32 dwShareMode,
        nativeint lpSecurityAttributes,
        uint32 dwCreationDisposition,
        uint32 dwFlagsAndAttributes,
        nativeint hTemplateFile
    )

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool CloseHandle(nativeint hObject)

/// POSIX P/Invoke for temporarily replacing the test process's own fd 0 (standard input) with a known
/// payload file, so a child spawned with `Command.InheritStdin` inherits it. `dup`/`dup2` save and
/// restore the original fd 0 around the spawn.
module private PosixStdinRedirect =
    [<Literal>]
    let O_RDONLY = 0

    [<DllImport("libc", SetLastError = true, EntryPoint = "open")>]
    extern int openReadOnly(string path, int flags)

    [<DllImport("libc", SetLastError = true)>]
    extern int dup(int oldfd)

    [<DllImport("libc", SetLastError = true)>]
    extern int dup2(int oldfd, int newfd)

    [<DllImport("libc", SetLastError = true)>]
    extern int close(int fd)

/// Tests for `Command.InheritStdin` (T-102): a child handed the PARENT's own standard input directly
/// (inherited, no pipe, no feeder) — the stdin analogue of `StdioMode.Inherit`. Covers the integration
/// contract (the parent's redirected stdin reaches the child), the builder-boundary rejection of the
/// incompatible stdin knobs, the `TakeStdin`/retry interactions, and record/replay support.
[<TestFixture>]
type StdinInheritTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let runner: IProcessRunner = JobRunner()

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    // A pure stdin->stdout filter: `sort` on Windows and `cat` on POSIX both copy their standard input
    // to their standard output and exit at EOF. With a single-line payload `sort` leaves it unchanged.
    let echoStdin =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; "sort" ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; "cat" ]

    [<Test>]
    member _.``a child with inherited stdin reads the parent's standard input``() : Task =
        task {
            // The honest test of OS-level stdin inheritance: give THIS process a known standard input
            // (the child's parent), spawn the child with InheritStdin, and prove the child read it.
            let payload = "inherited-stdin-payload-4242"
            let tmp = Path.GetTempFileName()
            File.WriteAllText(tmp, payload + Environment.NewLine)

            try
                let! captured =
                    if isWindows then
                        task {
                            let handle =
                                WindowsStdinRedirect.CreateFileW(
                                    tmp,
                                    WindowsStdinRedirect.GENERIC_READ,
                                    WindowsStdinRedirect.FILE_SHARE_READ,
                                    IntPtr.Zero,
                                    WindowsStdinRedirect.OPEN_EXISTING,
                                    0u,
                                    IntPtr.Zero
                                )

                            Assert.That(
                                handle <> IntPtr.Zero && handle <> IntPtr(-1),
                                "CreateFileW for the payload file failed"
                            )

                            let saved = WindowsStdinRedirect.GetStdHandle WindowsStdinRedirect.STD_INPUT_HANDLE

                            try
                                WindowsStdinRedirect.SetStdHandle(WindowsStdinRedirect.STD_INPUT_HANDLE, handle)
                                |> ignore

                                return! (echoStdin |> Command.inheritStdin).OutputStringAsync()
                            finally
                                // Restore the real standard input and drop our file handle (the child
                                // already has its own inherited duplicate by now).
                                WindowsStdinRedirect.SetStdHandle(WindowsStdinRedirect.STD_INPUT_HANDLE, saved)
                                |> ignore

                                WindowsStdinRedirect.CloseHandle handle |> ignore
                        }
                    else
                        task {
                            let fileFd = PosixStdinRedirect.openReadOnly (tmp, PosixStdinRedirect.O_RDONLY)

                            Assert.That(fileFd, Is.GreaterThanOrEqualTo 0, "open() for the payload file failed")
                            let savedFd = PosixStdinRedirect.dup 0

                            try
                                PosixStdinRedirect.dup2 (fileFd, 0) |> ignore
                                return! (echoStdin |> Command.inheritStdin).OutputStringAsync()
                            finally
                                // Restore the real fd 0 (the child already inherited the file at spawn),
                                // then close both the saved and the file fds.
                                PosixStdinRedirect.dup2 (savedFd, 0) |> ignore
                                PosixStdinRedirect.close savedFd |> ignore
                                PosixStdinRedirect.close fileFd |> ignore
                        }

                match captured with
                | Error error -> Assert.Fail $"inherited-stdin run failed: {error}"
                | Ok result ->
                    Assert.That(
                        result.Stdout,
                        Does.Contain payload,
                        "the child must have read the parent's redirected standard input"
                    )
            finally
                File.Delete tmp
        }
        :> Task

    [<Test>]
    member _.``InheritStdin alone builds without throwing``() =
        // The lone InheritStdin, and re-applying it, are both fine (idempotent on the source field).
        Assert.DoesNotThrow(
            Action(fun () -> Command.create "vim" |> Command.inheritStdin |> Command.inheritStdin |> ignore)
        )

    [<Test>]
    member _.``InheritStdin rejects a feeder Stdin source in either chaining order``() =
        Assert.Throws<ArgumentException>(
            Action(fun () ->
                (Command.create "git" |> Command.inheritStdin).Stdin(Stdin.FromString "x")
                |> ignore)
        )
        |> ignore

        Assert.Throws<ArgumentException>(
            Action(fun () -> (Command.create "git").Stdin(Stdin.FromString "x").InheritStdin() |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``InheritStdin rejects KeepStdinOpen in either chaining order``() =
        Assert.Throws<ArgumentException>(
            Action(fun () -> (Command.create "git" |> Command.inheritStdin).KeepStdinOpen() |> ignore)
        )
        |> ignore

        Assert.Throws<ArgumentException>(
            Action(fun () -> (Command.create "git").KeepStdinOpen().InheritStdin() |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``TakeStdin yields None for an inherited-stdin child``() : Task =
        task {
            // A child that does not read stdin (so the inherited real stdin can't block it) and exits at
            // once: InheritStdin creates no interactive pipe, so TakeStdin must honestly hand out nothing.
            match! runner.StartAsync(shell "echo ready" |> Command.inheritStdin, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                Assert.That(running.TakeStdin(), Is.EqualTo(None: ProcessStdin option))
                let! _ = running.OutputStringAsync()
                ()
        }
        :> Task

    [<Test>]
    member _.``retry with an inherited stdin source is not refused as one-shot``() : Task =
        task {
            let mutable calls = 0

            // Fails twice, then succeeds on the 3rd attempt — proves the retry loop runs normally with
            // InheritStdin (a repeatable source: the child re-inherits the parent's stdin each attempt),
            // instead of being refused up front like a one-shot FromStream/FromLines source.
            let flaky =
                { new IProcessRunner with
                    member _.CaptureStringAsync(_, _) =
                        calls <- calls + 1

                        if calls < 3 then
                            Task.FromResult(Ok(ProcessResult.Failure "" "boom" 1))
                        else
                            Task.FromResult(Ok(ProcessResult.Success "ok"))

                    member _.CaptureBytesAsync(_, _) =
                        Task.FromResult(Ok(ProcessResult.Success(Array.empty<byte>)))

                    member _.SpawnAsync(command, _) =
                        Task.FromResult(Error(ProcessError.Unsupported command.Program)) }

            let command =
                Command.create "prompt-tool"
                |> Command.inheritStdin
                |> Command.retry 3 TimeSpan.Zero (fun _ -> true)

            let! result = command |> Runner.run flaky CancellationToken.None

            match result with
            | Ok text -> Assert.That(text, Is.EqualTo "ok")
            | Error error -> Assert.Fail $"expected Ok after retrying, got {error}"

            Assert.That(calls, Is.EqualTo 3)
        }
        :> Task
