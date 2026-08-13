namespace ProcessKit.Tests

open System
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Text.RegularExpressions
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

    // A child that comfortably outlives the short deadline/cancellation the T-329 precedence tests below
    // fire at it, so the run really is decided by the timeout/cancellation rather than by its own exit.
    let sleeper () =
        if isWindows then
            shell "ping 127.0.0.1 -n 10 >NUL"
        else
            shell "sleep 8"

    [<Test>]
    member _.``public verb extensions reject null command and runner arguments eagerly``() =
        let nullCommand = Unchecked.defaultof<Command>
        let nullRunner = Unchecked.defaultof<IProcessRunner>

        Assert.Throws<ArgumentNullException>(Action(fun () -> CommandVerbs.RunAsync(nullCommand) |> ignore))
        |> ignore

        Assert.Throws<ArgumentNullException>(
            Action(fun () -> ProcessRunnerExtensions.RunAsync(runner, nullCommand) |> ignore)
        )
        |> ignore

        Assert.Throws<ArgumentNullException>(
            Action(fun () -> ProcessRunnerExtensions.RunAsync(nullRunner, Command.create "svc") |> ignore)
        )
        |> ignore

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
            elif (Native.Posix.trustedHelperPathForTests "setpriv").IsNone then
                // The drop runs through `setpriv`, which is loaded ONLY from a trusted system directory and
                // never from `PATH` (T-317). A host that keeps util-linux elsewhere is refused by design, so
                // gate on the library's own resolution — an `Exec.which` gate would turn that deliberate,
                // correct refusal into a failing test.
                Assert.Ignore
                    "The privilege drop needs the util-linux 'setpriv' helper in a trusted system directory (/usr/bin, /bin, /usr/sbin, /sbin)."
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
            elif (Native.Posix.trustedHelperPathForTests "setpriv").IsNone then
                // Same trusted-directory precondition as the uid/gid drop above: the helper is never taken
                // from `PATH`, so "reachable on PATH" is not the rule this test may gate on.
                Assert.Ignore
                    "Setting supplementary groups needs the util-linux 'setpriv' helper in a trusted system directory (/usr/bin, /bin, /usr/sbin, /sbin)."
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

    // ---- WindowsRestrictedToken / WindowsIntegrityLevel (Windows token hardening, mirror-image) ---
    //
    // The Windows counterpart of the Unix drop above, and verified the same way: the CHILD reads its own
    // token and reports it back, so the assertion is about what the OS actually gave the child rather
    // than about the call ProcessKit made. `whoami` prints locale-independent identifiers — privilege
    // constant names (`SeShutdownPrivilege`, …) and the raw integrity SID (`S-1-16-4096`) — so the
    // assertions do not depend on the host's display language.

    /// Every privilege constant named in a `whoami /priv` report, whatever its enabled/disabled state.
    static member private PrivilegeNames(output: string) =
        Regex.Matches(output, @"Se[A-Za-z]+Privilege")
        |> Seq.map (fun regexMatch -> regexMatch.Value)
        |> Set.ofSeq

    [<Test>]
    member _.``WindowsRestrictedToken leaves the child no privilege beyond SeChangeNotifyPrivilege``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Restricted tokens are Windows-only; the POSIX behaviour is the Unsupported gate below."
            else
                // `whoami /priv` makes the child enumerate its OWN token's privileges.
                let probe = Command.create "whoami" |> Command.args [ "/priv" ]

                match! runner.RunAsync probe with
                | Error error ->
                    // No `whoami` on this host (a trimmed image): the contract is unobservable here, so skip
                    // explicitly rather than pass vacuously.
                    Assert.Ignore $"whoami is unavailable on this host ({error.Message}); cannot read the child's token"
                | Ok baseline ->
                    let baselinePrivileges = VerbTests.PrivilegeNames baseline

                    let baselineBeyondChangeNotify =
                        Set.remove "SeChangeNotifyPrivilege" baselinePrivileges

                    if Set.isEmpty baselineBeyondChangeNotify then
                        // Nothing to take away — the account already holds only the privilege
                        // DISABLE_MAX_PRIVILEGE keeps, so the drop cannot be observed.
                        Assert.Ignore
                            "this account holds no privilege beyond SeChangeNotifyPrivilege, so the restricted token has nothing observable to remove"
                    else
                        match! runner.RunAsync(probe |> Command.windowsRestrictedToken) with
                        | Error error -> Assert.Fail $"a restricted-token spawn should succeed, got {error}"
                        | Ok restricted ->
                            let restrictedBeyondChangeNotify =
                                VerbTests.PrivilegeNames restricted |> Set.remove "SeChangeNotifyPrivilege"

                            let message =
                                $"DISABLE_MAX_PRIVILEGE must leave the child no privilege beyond SeChangeNotifyPrivilege, but it reported {restrictedBeyondChangeNotify}"

                            Assert.That(restrictedBeyondChangeNotify, Is.Empty, message)
        }
        :> Task

    [<Test>]
    member _.``WindowsIntegrityLevel runs the child at the requested (lower) integrity level``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Integrity levels are Windows-only; the POSIX behaviour is the Unsupported gate below."
            else
                // `whoami /groups` prints the token's mandatory label row, including its raw SID —
                // `S-1-16-4096` is Low, `S-1-16-8192` Medium, `S-1-16-12288` High.
                let probe = Command.create "whoami" |> Command.args [ "/groups" ]
                let lowIntegritySid = "S-1-16-4096"

                match! runner.RunAsync probe with
                | Error error ->
                    Assert.Ignore $"whoami is unavailable on this host ({error.Message}); cannot read the child's token"
                | Ok baseline ->
                    // Contrast first: an ordinary child does NOT run at low integrity, so the assertion
                    // below cannot pass by accident on a host that was already low.
                    let contrast =
                        $"the baseline child should not already be at low integrity, but reported {lowIntegritySid}"

                    Assert.That(baseline, Does.Not.Contain lowIntegritySid, contrast)

                    let lowered = probe |> Command.windowsIntegrityLevel WindowsIntegrityLevel.Low

                    match! runner.RunAsync lowered with
                    | Error error -> Assert.Fail $"a low-integrity spawn should succeed, got {error}"
                    | Ok output ->
                        let message =
                            $"the child's own token should carry the Low mandatory label ({lowIntegritySid})"

                        Assert.That(output, Does.Contain lowIntegritySid, message)
        }
        :> Task

    [<Test>]
    member _.``the Windows token knobs compose - a restricted, low-integrity child reports both``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows token hardening is Windows-only; see the POSIX Unsupported gate below."
            else
                // Both knobs land on ONE token: the restricted copy is what gets relabelled, so the child
                // must report the lowered integrity AND the stripped privileges together.
                let probe =
                    Command.create "whoami"
                    |> Command.args [ "/all" ]
                    |> Command.windowsRestrictedToken
                    |> Command.windowsIntegrityLevel WindowsIntegrityLevel.Low

                match! runner.RunAsync probe with
                | Error error ->
                    Assert.Ignore $"whoami is unavailable on this host ({error.Message}); cannot read the child's token"
                | Ok output ->
                    let beyondChangeNotify =
                        VerbTests.PrivilegeNames output |> Set.remove "SeChangeNotifyPrivilege"

                    let integrityMessage = "the composed token should carry the Low mandatory label"
                    Assert.That(output, Does.Contain "S-1-16-4096", integrityMessage)

                    let privilegeMessage =
                        $"the composed token should also be privilege-stripped, but reported {beyondChangeNotify}"

                    Assert.That(beyondChangeNotify, Is.Empty, privilegeMessage)
        }
        :> Task

    [<Test>]
    member _.``the Windows token knobs are honestly Unsupported on POSIX (no silent no-op)``() : Task =
        task {
            if isWindows then
                Assert.Ignore
                    "The POSIX Unsupported gate is POSIX-only; Windows applies the hardening (observed above)."
            else
                // The exact mirror image of the Uid/Gid/Groups/Setsid/Umask gates above: a hardening
                // request the platform cannot honour fails the spawn instead of being dropped in silence.
                let restricted = shell "echo hi" |> Command.windowsRestrictedToken

                match! runner.RunAsync restricted with
                | Error(ProcessError.Unsupported _) -> ()
                | other -> Assert.Fail $"expected Unsupported for WindowsRestrictedToken on POSIX, got {other}"

                let lowered =
                    shell "echo hi" |> Command.windowsIntegrityLevel WindowsIntegrityLevel.Low

                match! runner.RunAsync lowered with
                | Error(ProcessError.Unsupported _) -> Assert.Pass()
                | other -> Assert.Fail $"expected Unsupported for WindowsIntegrityLevel on POSIX, got {other}"
        }
        :> Task

    // --- T-329: a stdin source that fails only AFTER the child exits ------------------------------
    //
    // The background feeder stashes a genuine source failure when its feed finishes. A Result-producing
    // verb used to PEEK at that stash exactly once, the instant the child's exit and the output drains
    // were observed: a slow `FromStream`/`FromLines`/`FromAsyncLines` source that had not concluded yet
    // read as "no failure", the verb returned a spurious success, and teardown then stopped the feeder
    // and destroyed the evidence. These tests pin the bounded final observation that closes the race —
    // and, just as importantly, what it must NOT change: a louder failure still wins (and pays nothing
    // for the window), a stopped feed is still not an error, and a hung source can never hold a verb open.

    [<Test>]
    member _.``a stdin source that fails only after the child exits surfaces as ProcessError.Stdin``() : Task =
        task {
            // The source is released at the exact instant the verb opens its observation window — i.e.
            // strictly after the child exited and the pumps drained, which is precisely where the old
            // single peek saw nothing. The failure is genuine (the child was fed truncated input), so it
            // must become the run's result instead of a silent success.
            let source = DelayedStdinAsyncLines FailWhenReleased

            use _observation =
                new StdinFinalObservationScope((fun () -> source.Release()), None)

            let command = shell "exit 0" |> Command.stdin (Stdin.FromAsyncLines source)

            match! command.OutputStringAsync() with
            | Error(ProcessError.Stdin _) -> ()
            | Error other -> Assert.Fail $"expected ProcessError.Stdin, got {other.Message}"
            | Ok _ -> Assert.Fail "a source failing after the child exited must not pass through as a success"
        }
        :> Task

    [<Test>]
    member _.``a stdin source that ends cleanly after the child exits is not turned into a failure``() : Task =
        task {
            // The mirror image: the window opens on a still-running source, but that source concludes
            // WITHOUT failing. Waiting for it must invent no error — and the routine broken pipe from a
            // child that never read its input must stay the non-failure it has always been.
            let source = DelayedStdinAsyncLines EndWhenReleased

            use observation = new StdinFinalObservationScope((fun () -> source.Release()), None)

            let command = shell "exit 0" |> Command.stdin (Stdin.FromAsyncLines source)

            match! command.OutputStringAsync() with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"a clean delayed source must not fail the run, got {error.Message}"

            // Proves the assertion above is about a source observed through the window, not about a feed
            // that had already finished by the time the verb first looked at it.
            Assert.That(observation.Windows, Is.EqualTo 1, "the verb never opened a bounded observation window")
        }
        :> Task

    [<Test>]
    member _.``the bounded observation stops a hung stdin source instead of waiting for it``() : Task =
        task {
            // At the seam itself, with no verb or teardown around it to muddy the picture: a source still
            // parked in its own read when the budget runs out is STOPPED — its lifecycle token cancels, it
            // unwinds, and the user's enumerator is disposed rather than leaked — and reported as no
            // failure, because a cancelled feed is not the caller's error.
            let source = DelayedStdinAsyncLines FailWhenReleased // never released: it stays parked

            use _observation =
                new StdinFinalObservationScope(ignore, Some(TimeSpan.FromMilliseconds 200.0))

            use pipe = new MemoryStream()

            let feeder =
                Pump.feedStdinSource (Some(pipe :> Stream)) (Some(Stdin.FromAsyncLines source)) true

            do! source.Parked.WaitAsync(TimeSpan.FromSeconds 30.0)
            let! fault = feeder.ObserveFaultAsync()

            Assert.That(fault.IsNone, Is.True, "a source stopped at the budget must not be reported as a failure")
            do! source.Cancelled.WaitAsync(TimeSpan.FromSeconds 30.0)
            do! source.Disposed.WaitAsync(TimeSpan.FromSeconds 30.0)
            let! _ = feeder.Task.WaitAsync(TimeSpan.FromSeconds 30.0)
            ()
        }
        :> Task

    [<Test>]
    member _.``a hung stdin source cannot hold a verb open past the bounded budget``() : Task =
        task {
            // End to end: the source parks forever, so without a bound the verb could never return. The
            // window closes on the budget, the run reports its honest success, and the source is stopped.
            let source = DelayedStdinAsyncLines FailWhenReleased // never released

            use _observation =
                new StdinFinalObservationScope(ignore, Some(TimeSpan.FromMilliseconds 200.0))

            let command = shell "exit 0" |> Command.stdin (Stdin.FromAsyncLines source)
            let run = command.OutputStringAsync()
            let! completed = Task.WhenAny(run :> Task, Task.Delay(TimeSpan.FromSeconds 30.0))

            Assert.That(
                obj.ReferenceEquals(completed, (run :> Task)),
                Is.True,
                "the verb waited on a hung stdin source instead of bounding the observation"
            )

            match! run with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"a stopped hung source must not fail the run, got {error.Message}"

            do! source.Cancelled.WaitAsync(TimeSpan.FromSeconds 30.0)
        }
        :> Task

    [<Test>]
    member _.``a non-zero exit still wins over a stdin source that fails after exit``() : Task =
        task {
            // Precedence is unchanged, and is decided BEFORE the window: an unaccepted exit is the realer
            // failure, so the outcome passes through as data and no observation window opens at all.
            let source = DelayedStdinAsyncLines FailWhenReleased

            use observation = new StdinFinalObservationScope((fun () -> source.Release()), None)

            let command = shell "exit 7" |> Command.stdin (Stdin.FromAsyncLines source)

            match! command.OutputStringAsync() with
            | Ok result ->
                match result.Outcome with
                | Outcome.Exited 7 -> ()
                | other -> Assert.Fail $"expected exit 7 to pass through, got {other}"
            | Error(ProcessError.Stdin _) ->
                Assert.Fail "a non-zero exit must win over the delayed stdin failure, not surface ProcessError.Stdin"
            | Error other -> Assert.Fail $"unexpected error: {other.Message}"

            Assert.That(
                observation.Windows,
                Is.EqualTo 0,
                "a failing run must not pay for the bounded observation window at all"
            )
        }
        :> Task

    [<Test>]
    member _.``a timeout still wins over a stdin source that fails after exit``() : Task =
        task {
            // The same precedence rule for the deadline: `TimedOut` is not an accepted outcome, so the
            // stdin failure is neither surfaced nor waited for.
            let source = DelayedStdinAsyncLines FailWhenReleased

            use observation = new StdinFinalObservationScope((fun () -> source.Release()), None)

            let command =
                sleeper ()
                |> Command.stdin (Stdin.FromAsyncLines source)
                |> Command.timeout (TimeSpan.FromMilliseconds 400.0)

            match! command.OutputStringAsync() with
            | Ok result ->
                match result.Outcome with
                | Outcome.TimedOut -> ()
                | other -> Assert.Fail $"expected TimedOut to pass through, got {other}"
            | Error(ProcessError.Stdin _) ->
                Assert.Fail "a timeout must win over the delayed stdin failure, not surface ProcessError.Stdin"
            | Error other -> Assert.Fail $"unexpected error: {other.Message}"

            Assert.That(
                observation.Windows,
                Is.EqualTo 0,
                "a timed-out run must not pay for the bounded observation window at all"
            )
        }
        :> Task

    [<Test>]
    member _.``cancellation still wins over a stdin source that fails after exit``() : Task =
        task {
            // And for cancellation, decided outside the outcome classification entirely: the killed run is
            // not an accepted outcome, so no window opens and the verb reports `Cancelled`.
            let source = DelayedStdinAsyncLines FailWhenReleased

            use observation = new StdinFinalObservationScope((fun () -> source.Release()), None)

            use cts = new CancellationTokenSource()
            let command = sleeper () |> Command.stdin (Stdin.FromAsyncLines source)
            cts.CancelAfter(TimeSpan.FromMilliseconds 400.0)

            match! runner.OutputStringAsync(command, cts.Token) with
            | Error(ProcessError.Cancelled _) -> ()
            | Error(ProcessError.Stdin _) ->
                Assert.Fail "cancellation must win over the delayed stdin failure, not surface ProcessError.Stdin"
            | Error other -> Assert.Fail $"expected Cancelled, got {other.Message}"
            | Ok result -> Assert.Fail $"expected Cancelled, got a result with outcome {result.Outcome}"

            Assert.That(
                observation.Windows,
                Is.EqualTo 0,
                "a cancelled run must not pay for the bounded observation window at all"
            )
        }
        :> Task
