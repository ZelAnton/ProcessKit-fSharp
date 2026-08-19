namespace ProcessKit.Tests

open System
open System.Diagnostics
open System.Runtime.InteropServices
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Native

/// `ProcessLookup.processInfo` / `processIsAlive` (T-385) — the standalone, identity-safe lookup and
/// reuse-safe liveness query for a pid the caller holds OUTSIDE any `ProcessGroup`. A group is used here
/// only to obtain a real, live external process to query BY BARE PID — `ProcessLookup` itself never sees
/// or needs the group.
[<TestFixture>]
type ProcessLookupTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    // A pid far above any the OS will assign in the test window — never a real process, so it is the
    // deterministic "already gone" pid (no reliance on a just-reaped pid the OS might recycle).
    let vanishedPid = 0x7FFFFFF0

    let sleeper =
        if isWindows then
            Command.create "ping"
            |> Command.args [ "-n"; "4"; "127.0.0.1" ]
            |> Command.stdout StdioMode.Null
        else
            Command.create "sleep" |> Command.args [ "3" ]

    let create () =
        match ProcessGroup.Create() with
        | Ok group -> group
        | Error error -> failwith $"ProcessGroup.Create failed: {error}"

    [<Test>]
    member _.``processInfo reports the identity of a live external process by bare pid``() : Task =
        task {
            use group = create ()

            match! group.StartAsync sleeper with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match running.Pid with
                | None -> Assert.Fail "expected a pid"
                | Some pid ->
                    match ProcessLookup.processInfo pid with
                    | Ok(Some info) ->
                        Assert.That(info.Pid, Is.EqualTo pid)
                        Assert.That(info.StartTime.IsSome, Is.True, "expected a start time on this platform")
                    | Ok None -> Assert.Fail "expected the live external process to be found"
                    | Error error -> Assert.Fail $"{error}"

                    running.Kill()
                    let! _ = running.WaitAsync()
                    ()
        }
        :> Task

    [<Test>]
    member _.``processInfo reports a vanished pid as an honest None, never fabricated``() =
        match ProcessLookup.processInfo vanishedPid with
        | Ok None -> ()
        | other -> Assert.Fail $"expected Ok None for a vanished pid, got {other}"

    [<Test>]
    member _.``processInfo reports pid 0 and a negative pid as an honest None, never an OS-dependent crash``() =
        match ProcessLookup.processInfo 0 with
        | Ok None -> ()
        | other -> Assert.Fail $"expected Ok None for pid 0, got {other}"

        match ProcessLookup.processInfo -1 with
        | Ok None -> ()
        | other -> Assert.Fail $"expected Ok None for a negative pid, got {other}"

    [<Test>]
    member _.``processInfo reports this process's own identity, an entirely ordinary target``() =
        let currentPid = Process.GetCurrentProcess().Id

        match ProcessLookup.processInfo currentPid with
        | Ok(Some info) -> Assert.That(info.Pid, Is.EqualTo currentPid)
        | other -> Assert.Fail $"expected Ok(Some _) for this process's own pid, got {other}"

    [<Test>]
    member _.``processInfo never exposes the process's command line or environment``() : Task =
        task {
            use group = create ()

            let secret = "PROCESSKITSECRET9f83c2a1b7"

            let child =
                if isWindows then
                    Command.create "cmd.exe"
                    |> Command.args [ "/c"; $"ping -n 6 127.0.0.1 >nul & rem {secret}" ]
                else
                    Command.create "/bin/sh" |> Command.args [ "-c"; $"sleep 6 # {secret}" ]
                |> Command.env "PROCESSKIT_TEST_SECRET" secret

            match! group.StartAsync child with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match running.Pid with
                | None -> Assert.Fail "expected a pid"
                | Some pid ->
                    match ProcessLookup.processInfo pid with
                    | Ok(Some info) ->
                        match info.ExeName with
                        | Some name ->
                            Assert.That(
                                name.Contains(secret, StringComparison.OrdinalIgnoreCase),
                                Is.False,
                                "argv/env leaked into ExeName"
                            )
                        | None -> ()
                    | Ok None -> Assert.Fail "expected the live external process to be found"
                    | Error error -> Assert.Fail $"{error}"

                    running.Kill()
                    let! _ = running.WaitAsync()
                    ()
        }
        :> Task

    [<Test>]
    member _.``processIsAlive reports true for a live external process against its own saved start time``() : Task =
        task {
            use group = create ()

            match! group.StartAsync sleeper with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match running.Pid with
                | None -> Assert.Fail "expected a pid"
                | Some pid ->
                    let savedStartTime =
                        match ProcessLookup.processInfo pid with
                        | Ok(Some info) -> info.StartTime
                        | other -> failwith $"expected Ok(Some _), got {other}"

                    match ProcessLookup.processIsAlive pid savedStartTime with
                    | Ok true -> ()
                    | other -> Assert.Fail $"expected Ok true for the still-live original process, got {other}"

                    running.Kill()
                    let! _ = running.WaitAsync()
                    ()
        }
        :> Task

    [<Test>]
    member _.``processIsAlive reports false once the pid names no process at all``() =
        match ProcessLookup.processIsAlive vanishedPid None with
        | Ok false -> ()
        | other -> Assert.Fail $"expected Ok false for a vanished pid, got {other}"

    [<Test>]
    member _.``processIsAlive reports false for a pid whose saved identity token no longer matches (the pid-reuse regression)``
        ()
        =
        // Models pid reuse without racing a real OS recycle: `currentPid` really is alive, but the saved
        // token names a different instant — exactly the shape a stale token has once the ORIGINAL process
        // at that number has exited and a stranger now holds it. This must never read as "alive".
        let currentPid = Process.GetCurrentProcess().Id
        let staleStartTime = Some(DateTime.Now.AddDays -1.0)

        match ProcessLookup.processIsAlive currentPid staleStartTime with
        | Ok false -> ()
        | other -> Assert.Fail $"expected Ok false for a mismatched identity token, got {other}"

    [<Test>]
    member _.``processIsAlive degrades to bare-pid liveness when no identity token was saved``() =
        let currentPid = Process.GetCurrentProcess().Id

        match ProcessLookup.processIsAlive currentPid None with
        | Ok true -> ()
        | other -> Assert.Fail $"expected Ok true (no token to prove a recycle), got {other}"

    [<Test>]
    member _.``processIsAlive never reports a guessed "alive" when a saved token cannot be verified against an unreadable current start time (R-01 regression)``
        ()
        =
        // R-01: `processIsAlive` must not fall back to bare-pid liveness when the CALLER supplied a
        // saved token but the live process's CURRENT start time could not be read — that is exactly the
        // reuse false-positive this API exists to prevent (a pid recycled by a stranger reads the same
        // way). Models a reader-bearing platform (Linux/macOS) whose per-pid read failed for this one
        // pid: the typed answer must be `ProcessError.Io`, never `Ok true`.
        if isWindows then
            Assert.Ignore "POSIX-only: exercises the POSIX processInfoForTests / reader-availability seams"
        else
            let originalInfo = Posix.processInfoForTests
            let originalAvailable = Posix.processIdentityReaderAvailableForTests

            try
                Posix.processIdentityReaderAvailableForTests <- Some(fun () -> true)
                Posix.processInfoForTests <- Some(fun pid -> Ok(Some(MemberInfo(pid, Some 1, Some "child", None))))

                match ProcessLookup.processIsAlive 4242 (Some(DateTime.Now.AddMinutes -5.0)) with
                | Error(ProcessError.Io _) -> ()
                | other -> Assert.Fail $"expected Error(ProcessError.Io _) — never a guessed \"alive\" — got {other}"
            finally
                Posix.processInfoForTests <- originalInfo
                Posix.processIdentityReaderAvailableForTests <- originalAvailable

    [<Test>]
    member _.``processIsAlive reports typed Unsupported, never a guessed "alive", when this platform has no start-time reader at all``
        ()
        =
        // Same false-positive guard as above, but for a platform with NO reader at all (a BSD other than
        // macOS): a saved token can never be verified there, so the honest answer is `Unsupported`, never
        // a silently degraded `Ok true`.
        if isWindows then
            Assert.Ignore "POSIX-only: models a reader-less POSIX host"
        else
            let originalInfo = Posix.processInfoForTests
            let originalAvailable = Posix.processIdentityReaderAvailableForTests

            try
                Posix.processIdentityReaderAvailableForTests <- Some(fun () -> false)
                Posix.processInfoForTests <- Some(fun pid -> Ok(Some(MemberInfo(pid, None, None, None))))

                match ProcessLookup.processIsAlive 4242 (Some(DateTime.Now.AddMinutes -5.0)) with
                | Error(ProcessError.Unsupported _) -> ()
                | other -> Assert.Fail $"expected Error(ProcessError.Unsupported _), got {other}"
            finally
                Posix.processInfoForTests <- originalInfo
                Posix.processIdentityReaderAvailableForTests <- originalAvailable

    [<Test>]
    member _.``processInfo reports a POSIX zombie the same as a live process — the documented divergence with Windows (R-02)``
        ()
        =
        // R-02: a real /proc/<pid>/stat for a zombie (state 'Z') parses exactly like a live process's —
        // none of the POSIX readers examine the state field — so this seam-level simulation pins exactly
        // what the real Linux/macOS/bare-BSD backends produce for a zombie pid: `Ok(Some _)`, unlike
        // Windows, where the equivalent state is `Ok None`. See the XML doc on `ProcessLookup.processInfo`.
        if isWindows then
            Assert.Ignore "POSIX-only: documents the zombie-vs-live divergence Windows does not exhibit"
        else
            let original = Posix.processInfoForTests

            try
                Posix.processInfoForTests <-
                    Some(fun pid -> Ok(Some(MemberInfo(pid, Some 1, Some "zombie-child", None))))

                match Native.Posix.processInfo 4242 with
                | Ok(Some _) -> ()
                | other -> Assert.Fail $"expected Ok(Some _) for a zombie pid, got {other}"
            finally
                Posix.processInfoForTests <- original

    [<Test>]
    member _.``processInfo's StartTime is stable across independent reads and survives a round-trip through a string (R-01)``
        ()
        : Task =
        task {
            use group = create ()

            match! group.StartAsync sleeper with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match running.Pid with
                | None -> Assert.Fail "expected a pid"
                | Some pid ->
                    let first =
                        match ProcessLookup.processInfo pid with
                        | Ok(Some info) -> info.StartTime
                        | other -> failwith $"expected Ok(Some _), got {other}"

                    let second =
                        match ProcessLookup.processInfo pid with
                        | Ok(Some info) -> info.StartTime
                        | other -> failwith $"expected Ok(Some _), got {other}"

                    Assert.That(
                        second,
                        Is.EqualTo first,
                        "StartTime changed between two independent reads of the same live process"
                    )

                    match first with
                    | Some value ->
                        let roundTripped =
                            DateTime.Parse(
                                value.ToString("o"),
                                Globalization.CultureInfo.InvariantCulture,
                                Globalization.DateTimeStyles.RoundtripKind
                            )

                        Assert.That(
                            roundTripped,
                            Is.EqualTo value,
                            "StartTime did not survive a string round-trip — unsafe to persist across runs"
                        )
                    | None -> Assert.Ignore "no start time reported on this platform"

                    running.Kill()
                    let! _ = running.WaitAsync()
                    ()
        }
        :> Task

    [<Test>]
    member _.``Windows processInfo reports a typed error, never a crash, when OpenProcess is denied``() =
        if not isWindows then
            Assert.Ignore "Windows-only: exercises the OpenProcess-refused branch through its test seam"
        else
            let original = Windows.openMemberProcessForTests

            try
                // ERROR_ACCESS_DENIED (5): the process exists but this caller may not query it (a
                // protected/higher-integrity process, or another user's) — the exact case that must never
                // be folded into "gone".
                Windows.openMemberProcessForTests <- Some(fun _ _ -> Error 5)

                match Native.Windows.processInfo 4 with
                | Error(ProcessError.Io _) -> ()
                | other -> Assert.Fail $"expected Error(ProcessError.Io _), got {other}"
            finally
                Windows.openMemberProcessForTests <- original

    [<Test>]
    member _.``POSIX processInfo reports a typed error, never a crash, for a permission-denied pid``() =
        if isWindows then
            Assert.Ignore "POSIX-only: exercises the POSIX process-info seam"
        else
            let original = Posix.processInfoForTests

            try
                // Models EACCES (a Linux `hidepid` mount) / EPERM (macOS): the process may well exist, so
                // this must surface as a typed error, never a false "gone".
                Posix.processInfoForTests <- Some(fun _ -> Error "simulated permission denial")

                match Native.Posix.processInfo 4242 with
                | Error(ProcessError.Io message) -> Assert.That(message, Does.Contain "simulated")
                | other -> Assert.Fail $"expected Error(ProcessError.Io _), got {other}"
            finally
                Posix.processInfoForTests <- original
