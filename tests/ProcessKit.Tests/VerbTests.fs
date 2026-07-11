namespace ProcessKit.Tests

open System
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

/// Effective-uid probe for the root-gated privilege-drop tests below. Only ever called on POSIX (the
/// tests guard with `isWindows` first), so the `libc` entry point is never resolved on Windows.
module private NativePrivilege =
    [<DllImport("libc")>]
    extern int geteuid()

[<TestFixture>]
type VerbTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let runner: IProcessRunner = JobRunner()

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    let threeLines =
        if isWindows then
            shell "echo line1&echo line2&echo line3"
        else
            shell "echo line1; echo line2; echo line3"

    [<Test>]
    member _.``StdoutTee copies raw output to the sink as well as capturing it``() : Task =
        task {
            use sink = new MemoryStream()
            let command = shell "echo teed" |> Command.stdoutTee sink

            match! runner.StartAsync(command, CancellationToken.None) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match! running.OutputStringAsync() with
                | Ok result ->
                    let teed = Encoding.UTF8.GetString(sink.ToArray())
                    Assert.That(teed, Does.Contain "teed")
                    Assert.That(result.Stdout, Does.Contain "teed")
                | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``parse converts trimmed stdout to a typed value``() : Task =
        task {
            match! Runner.parse runner CancellationToken.None (fun s -> int (s.Trim())) (shell "echo 42") with
            | Ok value -> Assert.That(value, Is.EqualTo 42)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``tryParse surfaces a parser failure as Parse``() : Task =
        task {
            let parser (_: string) : Result<int, string> = Error "bad value"

            match! Runner.tryParse runner CancellationToken.None parser (shell "echo x") with
            | Error(ProcessError.Parse _) -> Assert.Pass()
            | other -> Assert.Fail $"expected Parse, got {other}"
        }
        :> Task

    [<Test>]
    member _.``firstLine returns the first matching line``() : Task =
        task {
            match! Runner.firstLine runner CancellationToken.None (fun line -> line.Contains "line2") threeLines with
            | Ok(Some line) -> Assert.That(line, Does.Contain "line2")
            | Ok None -> Assert.Fail "expected a matching line"
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Command.ExitCodeAsync returns the process exit code``() : Task =
        task {
            match! (shell "exit 7").ExitCodeAsync() with
            | Ok code -> Assert.That(code, Is.EqualTo 7)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Command.ProbeAsync reads exit 0/1 as true/false``() : Task =
        task {
            match! (shell "exit 0").ProbeAsync() with
            | Ok value -> Assert.That(value, Is.True)
            | Error error -> Assert.Fail $"{error}"

            match! (shell "exit 1").ProbeAsync() with
            | Ok value -> Assert.That(value, Is.False)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Command.RunUnitAsync succeeds on a zero exit and is cancellable``() : Task =
        task {
            match! (shell "echo hi").RunUnitAsync(CancellationToken.None) with
            | Ok() -> Assert.Pass()
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Command.Parse/TryParse/FirstLine are reachable on the default runner (token omitted and passed)``
        ()
        : Task =
        task {
            // Parse — cancellation token omitted, then passed.
            match! (shell "echo 42").ParseAsync(fun s -> int (s.Trim())) with
            | Ok value -> Assert.That(value, Is.EqualTo 42)
            | Error error -> Assert.Fail $"parse: {error}"

            match! (shell "echo 42").ParseAsync((fun s -> int (s.Trim())), CancellationToken.None) with
            | Ok value -> Assert.That(value, Is.EqualTo 42)
            | Error error -> Assert.Fail $"parse(ct): {error}"

            // TryParse — the C#-friendly TryParser delegate (BCL try-parse shape), token omitted then passed.
            let tryInt =
                TryParser(fun (s: string) (v: byref<int>) -> System.Int32.TryParse(s.Trim(), &v))

            match! (shell "echo 42").TryParseAsync tryInt with
            | Ok value -> Assert.That(value, Is.EqualTo 42)
            | Error error -> Assert.Fail $"tryParse: {error}"

            match! (shell "echo 42").TryParseAsync(tryInt, CancellationToken.None) with
            | Ok value -> Assert.That(value, Is.EqualTo 42)
            | Error error -> Assert.Fail $"tryParse(ct): {error}"

            // A parser that rejects the output becomes ProcessError.Parse.
            let tryFail = TryParser(fun (_: string) (_: byref<int>) -> false)

            match! (shell "echo 42").TryParseAsync tryFail with
            | Error(ProcessError.Parse _) -> ()
            | other -> Assert.Fail $"expected a Parse error, got {other}"

            // A parser that *throws* (rather than returning false) is also surfaced as ProcessError.Parse.
            let tryThrow = TryParser(fun (_: string) (_: byref<int>) -> failwith "boom")

            match! (shell "echo 42").TryParseAsync tryThrow with
            | Error(ProcessError.Parse _) -> ()
            | other -> Assert.Fail $"expected a Parse error from a throwing parser, got {other}"

            // FirstLine — cancellation token omitted, then passed.
            match! threeLines.FirstLineAsync(fun line -> line.Contains "line2") with
            | Ok(Some line) -> Assert.That(line, Does.Contain "line2")
            | Ok None -> Assert.Fail "expected a matching line"
            | Error error -> Assert.Fail $"firstLine: {error}"

            match! threeLines.FirstLineAsync((fun line -> line.Contains "line2"), CancellationToken.None) with
            | Ok(Some line) -> Assert.That(line, Does.Contain "line2")
            | Ok None -> Assert.Fail "expected a matching line"
            | Error error -> Assert.Fail $"firstLine(ct): {error}"
        }
        :> Task

    // ---- Priority (observable, platform-guarded) ----------------------------------------------

    [<Test>]
    member _.``Priority sets the child's Windows priority class (observed on the live process)``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "The Windows priority class is observable only on Windows."
            else
                // BelowNormal (a lower priority) needs no privilege; the creation-flag path sets it on the
                // spawned leader, which the tree inherits. Observed directly on the live leader process.
                let sleeper =
                    shell "ping -n 4 127.0.0.1 >nul" |> Command.priority Priority.BelowNormal

                match! runner.StartAsync(sleeper, CancellationToken.None) with
                | Error error -> Assert.Fail $"{error}"
                | Ok running ->
                    match running.Pid with
                    | None ->
                        running.Kill()
                        let! _ = running.WaitAsync()
                        Assert.Fail "expected a pid"
                    | Some pid ->
                        use proc = System.Diagnostics.Process.GetProcessById pid
                        let observed = proc.PriorityClass
                        running.Kill()
                        let! _ = running.WaitAsync()

                        Assert.That(observed, Is.EqualTo System.Diagnostics.ProcessPriorityClass.BelowNormal)
        }
        :> Task

    [<Test>]
    member _.``Priority sets the child's Unix nice value (observed via proc)``() : Task =
        task {
            if isWindows then
                Assert.Ignore "The Unix nice value is observed via /proc, below."
            elif not (RuntimeInformation.IsOSPlatform OSPlatform.Linux) then
                Assert.Ignore "nice introspection via /proc is Linux-only (macOS has no /proc)."
            else
                // BelowNormal maps to nice 10 — raising the nice never needs privilege — applied to the
                // spawned leader via setpriority. Read the leader's own nice from /proc so there is no
                // fork window in play (the leader's nice is set synchronously before StartAsync returns).
                let sleeper = shell "sleep 3" |> Command.priority Priority.BelowNormal

                match! runner.StartAsync(sleeper, CancellationToken.None) with
                | Error error -> Assert.Fail $"{error}"
                | Ok running ->
                    match running.Pid with
                    | None ->
                        running.Kill()
                        let! _ = running.WaitAsync()
                        Assert.Fail "expected a pid"
                    | Some pid ->
                        let stat = File.ReadAllText $"/proc/{pid}/stat"
                        running.Kill()
                        let! _ = running.WaitAsync()

                        // Fields after the final ')': state ppid ... priority nice ...; nice is the 17th
                        // (splitting after the last ')' side-steps a comm that itself contains parens/spaces).
                        let afterComm = stat.Substring(stat.LastIndexOf(')') + 1)

                        let fields =
                            afterComm.Split([| ' '; '\t'; '\n' |], System.StringSplitOptions.RemoveEmptyEntries)

                        Assert.That(int fields[16], Is.EqualTo 10)
        }
        :> Task

    // ---- Umask (observable, platform-guarded) -------------------------------------------------

    [<Test>]
    member _.``Umask restricts the permissions of files the child creates (observed on Unix)``() : Task =
        task {
            if isWindows then
                Assert.Ignore
                    "umask is a Unix file-mode creation mask; the Windows behaviour is the Unsupported gate below."
            else
                // umask 0o077 masks off every group/other bit, so a file `touch` creates at the default
                // 0o666 lands at 0o600. CI's ambient umask is the usual 0o022 (which would leave 0o644,
                // i.e. group/other read), so an observed 0o600 proves the requested mask actually applied.
                let path =
                    Path.Combine(Path.GetTempPath(), "processkit-umask-" + System.Guid.NewGuid().ToString("N"))

                try
                    let command = shell $"touch '{path}'" |> Command.umask 0o077

                    match! runner.RunUnitAsync command with
                    | Error error -> Assert.Fail $"{error}"
                    | Ok() ->
                        Assert.That(File.Exists path, Is.True, "the child should have created the file")
                        let mode = File.GetUnixFileMode path

                        let groupOther =
                            UnixFileMode.GroupRead
                            ||| UnixFileMode.GroupWrite
                            ||| UnixFileMode.GroupExecute
                            ||| UnixFileMode.OtherRead
                            ||| UnixFileMode.OtherWrite
                            ||| UnixFileMode.OtherExecute

                        Assert.That(
                            mode &&& groupOther,
                            Is.EqualTo UnixFileMode.None,
                            "umask 0o077 must clear all group/other bits"
                        )

                        Assert.That(
                            mode.HasFlag UnixFileMode.UserRead && mode.HasFlag UnixFileMode.UserWrite,
                            Is.True,
                            "the owner should still keep read/write"
                        )
                finally
                    if File.Exists path then
                        File.Delete path
        }
        :> Task

    [<Test>]
    member _.``Umask is honestly Unsupported on Windows (no silent drop)``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "The umask Unsupported gate is Windows-only; Unix applies the mask (observed above)."
            else
                // Windows has no umask equivalent, so a requested mask fails the spawn honestly rather
                // than being silently ignored — gated before any spawn work.
                let command = shell "echo hi" |> Command.umask 0o022

                match! runner.RunAsync command with
                | Error(ProcessError.Unsupported _) -> Assert.Pass()
                | other -> Assert.Fail $"expected Unsupported for umask on Windows, got {other}"
        }
        :> Task

    // ---- Setsid / Uid / Gid (privilege drop & session detach, platform-guarded) ---------------

    [<Test>]
    member _.``Setsid detaches into a new session yet the group still contains it (Unix)``() : Task =
        task {
            if isWindows then
                Assert.Ignore "setsid is Unix-only; the Windows behaviour is the Unsupported gate below."
            else
                match ProcessGroup.Create() with
                | Error error -> Assert.Fail $"ProcessGroup.Create failed: {error}"
                | Ok group ->
                    // THE setsid x process-group coordination regression: with POSIX_SPAWN_SETPGROUP still
                    // set alongside the session detach, the spawn would fail EPERM — so a successful spawn
                    // is itself the guard. `setsid` alone stays on the posix_spawn path (POSIX_SPAWN_SETSID).
                    let detached = Command.create "sleep" |> Command.args [ "30" ] |> Command.setsid

                    match! group.StartAsync detached with
                    | Error error ->
                        (group :> IDisposable).Dispose()

                        Assert.Fail
                            $"a setsid child failed to spawn (EPERM would mean the setsid/pgroup coordination broke): {error}"
                    | Ok running ->
                        match running.Pid with
                        | None ->
                            running.Kill()
                            let! _ = running.WaitAsync()
                            (group :> IDisposable).Dispose()
                            Assert.Fail "expected a pid for the setsid child"
                        | Some _ ->
                            // setsid makes the child its own process-group leader (pgid == pid), so the
                            // kill-on-drop killpg teardown still reaches it: dropping the group must reap it.
                            (group :> IDisposable).Dispose()
                            let wait = running.WaitAsync() :> Task
                            let! winner = Task.WhenAny(wait, Task.Delay 10000)

                            Assert.That(
                                Object.ReferenceEquals(winner, wait),
                                Is.True,
                                "the setsid child outlived the group drop — containment broke"
                            )
        }
        :> Task

    [<Test>]
    member _.``Setsid is honestly Unsupported on Windows (no silent drop)``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "The setsid Unsupported gate is Windows-only; Unix detaches the session (observed above)."
            else
                let command = shell "echo hi" |> Command.setsid

                match! runner.RunAsync command with
                | Error(ProcessError.Unsupported _) -> Assert.Pass()
                | other -> Assert.Fail $"expected Unsupported for setsid on Windows, got {other}"
        }
        :> Task

    [<Test>]
    member _.``Uid and Gid drop the child's privileges when run as root (Unix)``() : Task =
        task {
            if isWindows then
                Assert.Ignore "Privilege drop is Unix-only; the Windows behaviour is the Unsupported gate below."
            elif NativePrivilege.geteuid () <> 0 then
                // Dropping to another uid/gid needs privilege; without root setuid/setgid would EPERM, so
                // the drop is not exercisable here. Skipped explicitly (never a silent always-pass).
                Assert.Ignore "Dropping to another uid/gid requires root; skipping as an unprivileged user."
            else
                // As root, drop to uid/gid 1 and have the child report its own euid via `id -u`. A correct
                // fork + setgid + setuid before exec makes it print "1". Uid 1 exists on every POSIX system,
                // and this also exercises the fork path's own PATH resolution + execve of `id`.
                let dropped = Command.create "id" |> Command.args [ "-u" ] |> Command.user 1 1

                match! runner.RunAsync dropped with
                | Ok out -> Assert.That(out.Trim(), Is.EqualTo "1", "the child should report the dropped uid")
                | Error error -> Assert.Fail $"privilege drop as root should succeed, got {error}"
        }
        :> Task

    [<Test>]
    member _.``Uid drop by an unprivileged user fails honestly, never a silent no-drop (Unix)``() : Task =
        task {
            if isWindows then
                Assert.Ignore "Privilege drop is Unix-only; the Windows behaviour is the Unsupported gate above."
            elif NativePrivilege.geteuid () = 0 then
                // Running as root the drop would actually succeed; this test covers the UNPRIVILEGED
                // rejection (the case exercised on a non-root CI runner). Skipped explicitly as root.
                Assert.Ignore "This checks the unprivileged rejection; as root the drop would succeed instead."
            else
                // A non-root caller cannot change to a different uid, so the spawn must fail honestly with
                // ProcessError.Spawn (the up-front privilege pre-check) rather than silently running the
                // child under the parent's uid. Target a uid guaranteed different from the current one.
                let target = NativePrivilege.geteuid () + 1
                let cmd = shell "echo hi" |> Command.uid target

                match! runner.RunAsync cmd with
                | Error(ProcessError.Spawn _) -> Assert.Pass()
                | other -> Assert.Fail $"expected a Spawn error for an unprivileged uid drop, got {other}"
        }
        :> Task

    [<Test>]
    member _.``Uid and Gid are honestly Unsupported on Windows (no silent drop)``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "The uid/gid Unsupported gate is Windows-only; Unix applies the drop (observed above)."
            else
                let withUid = shell "echo hi" |> Command.uid 1000

                match! runner.RunAsync withUid with
                | Error(ProcessError.Unsupported _) -> ()
                | other -> Assert.Fail $"expected Unsupported for uid on Windows, got {other}"

                let withGid = shell "echo hi" |> Command.gid 1000

                match! runner.RunAsync withGid with
                | Error(ProcessError.Unsupported _) -> Assert.Pass()
                | other -> Assert.Fail $"expected Unsupported for gid on Windows, got {other}"
        }
        :> Task

    // ---- Groups (supplementary-group privilege drop, platform-guarded) -------------------------

    [<Test>]
    member _.``Groups restores the target user's supplementary groups on the dropped child (Unix, root)``() : Task =
        task {
            if isWindows then
                Assert.Ignore
                    "Supplementary-group drop is Unix-only; the Windows behaviour is the Unsupported gate below."
            elif NativePrivilege.geteuid () <> 0 then
                // setgroups needs privilege (CAP_SETGID); without root the setpriv --groups step would
                // EPERM, so the real membership change is not exercisable here. Skipped explicitly.
                Assert.Ignore
                    "Setting supplementary groups requires root (CAP_SETGID); skipping as an unprivileged user."
            else
                // As root, drop to uid/gid 1 AND set two supplementary groups, then have the child report
                // its full group set via `id -G`. setpriv applies the numeric gids verbatim (no /etc/group
                // lookup needed), so arbitrary high gids exercise the mechanism without depending on the
                // host's group database. A correct `setpriv --reuid=1 --regid=1 --groups=4242,4243` makes
                // the child's supplementary set exactly {4242, 4243}.
                let dropped =
                    Command.create "id"
                    |> Command.args [ "-G" ]
                    |> Command.user 1 1
                    |> Command.groups [ 4242; 4243 ]

                match! runner.RunAsync dropped with
                | Ok out ->
                    let reported = out.Trim().Split(' ')

                    Assert.That(reported, Does.Contain "4242", "the child should carry the first supplementary group")
                    Assert.That(reported, Does.Contain "4243", "the child should carry the second supplementary group")
                | Error error -> Assert.Fail $"a groups drop as root should succeed, got {error}"
        }
        :> Task

    [<Test>]
    member _.``Groups without a uid or gid drop fails honestly, never a silent no-op (Unix)``() : Task =
        task {
            if isWindows then
                Assert.Ignore "Groups is Unix-only; the Windows behaviour is the Unsupported gate below."
            else
                // Supplementary groups ride the setpriv privilege-drop helper, which is engaged only by a
                // Uid/Gid drop. Requested WITHOUT one, the option would otherwise be silently ignored — so
                // it must be refused up front with ProcessError.Spawn rather than run the child with its
                // groups left untouched. (Independent of privilege, so it runs as any user.)
                let orphanGroups = shell "echo hi" |> Command.groups [ 4242 ]

                match! runner.RunAsync orphanGroups with
                | Error(ProcessError.Spawn _) -> Assert.Pass()
                | other -> Assert.Fail $"expected a Spawn error for Groups without a uid/gid drop, got {other}"
        }
        :> Task

    [<Test>]
    member _.``Groups is honestly Unsupported on Windows (no silent drop)``() : Task =
        task {
            if not isWindows then
                Assert.Ignore
                    "The groups Unsupported gate is Windows-only; Unix applies it via setpriv (observed above)."
            else
                let withGroups = shell "echo hi" |> Command.groups [ 1000 ]

                match! runner.RunAsync withGroups with
                | Error(ProcessError.Unsupported _) -> Assert.Pass()
                | other -> Assert.Fail $"expected Unsupported for groups on Windows, got {other}"
        }
        :> Task
