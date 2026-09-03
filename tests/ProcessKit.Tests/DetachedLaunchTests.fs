namespace ProcessKit.Tests

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

/// `Command.LaunchDetached` / `Exec.detach` (T-225) — the library's single, deliberate opt-out from the
/// whole-tree kill-on-dispose guarantee. Covers the three things that make it honest: the child really is
/// outside every container (it is in no `ProcessGroup`'s membership and survives the disposal that reaps a
/// contained sibling), the returned `DetachedProcess` is a pid + start-time snapshot with no
/// consuming/streaming/kill member to pretend otherwise, and every builder knob a detached launch cannot
/// honour comes back as a typed `ProcessError.Unsupported` naming it rather than being silently ignored.
///
/// Every test that launches a detached child MUST kill it in a `finally`: by design nothing else can —
/// there is no group, no handle and no teardown, so a leaked child would outlive the whole test run.
[<TestFixture>]
type DetachedLaunchTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux

    // A single-process sleeper that spawns NO grandchildren (K-069/F-05: a `cmd.exe /c` wrapper would add
    // a transient child to the group membership this test asserts on). Windows `ping` sends its first
    // packet immediately and one per second after, so `-n (seconds + 1)` lives roughly `seconds`; its
    // output goes to the null device so no pipe or console is involved.
    let sleeper (seconds: int) =
        if isWindows then
            Command.create "ping"
            |> Command.args [ "-n"; string (seconds + 1); "127.0.0.1" ]
            |> Command.stdout StdioMode.Null
        else
            Command.create "sleep" |> Command.args [ string seconds ]

    let create () =
        match ProcessGroup.Create() with
        | Ok group -> group
        | Error error -> failwith $"ProcessGroup.Create failed: {error}"

    /// Whether `pid` still names a live process. Used only on children this test knows are running (or
    /// that a container has just been told to kill), never as a general liveness oracle.
    let isAlive (pid: int) =
        try
            use proc = Process.GetProcessById pid
            not proc.HasExited
        with :? ArgumentException ->
            // `GetProcessById` throws `ArgumentException` for a pid that is not running — exactly the
            // "gone" answer this predicate reports, not an error to propagate.
            false

    /// Kill a detached child. Mandatory cleanup: nothing in the library owns one, so a test that leaves it
    /// running leaks a process past the whole run.
    let killQuietly (pid: int) =
        try
            use proc = Process.GetProcessById pid
            proc.Kill()
        with _ ->
            // Best-effort test cleanup. The child may already have exited (`ArgumentException`) or be
            // exiting concurrently (`InvalidOperationException`/`Win32Exception`); in every case there is
            // nothing left to kill, and a cleanup failure must never fail the assertion under test.
            ()

    /// Poll until `predicate` holds or the deadline passes; returns whether it held.
    let waitUntil (timeout: TimeSpan) (predicate: unit -> bool) =
        let deadline = DateTime.UtcNow + timeout
        let mutable ok = predicate ()

        while not ok && DateTime.UtcNow < deadline do
            Thread.Sleep 50
            ok <- predicate ()

        ok

    // A reaped Linux child has no /proc entry; a zombie still has one. The state is included only in the
    // failure message so a regression distinguishes a live child from an unreaped zombie.
    let procState (pid: int) : string option =
        let path = $"/proc/{pid}/stat"

        if not (File.Exists path) then
            None
        else
            try
                let stat = File.ReadAllText path
                let closeParen = stat.LastIndexOf ')'

                if closeParen >= 0 then
                    let fields =
                        stat.Substring(closeParen + 1).Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)

                    if fields.Length > 0 then Some fields[0] else Some "?"
                else
                    Some "?"
            with _ ->
                // The process can disappear between the existence check and the read; treat that as reaped.
                None

    let tempFile () =
        Path.Combine(Path.GetTempPath(), $"pk-detached-{Guid.NewGuid():N}.log")

    /// The file's current contents, or `""` while it does not exist yet / is momentarily unreadable.
    /// Opened share-compatible so a read never trips a Windows sharing violation against the detached
    /// child's own write handle.
    let readIfPresent (path: string) =
        try
            use fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
            use reader = new StreamReader(fs)
            reader.ReadToEnd()
        with
        | :? FileNotFoundException
        | :? IOException ->
            // The detached child has not created (or not yet flushed) the file: an expected step of the
            // poll below, not a failure — the caller simply tries again until its deadline.
            ""

    let deleteQuietly (path: string) =
        try
            File.Delete path
        with _ ->
            // Best-effort test cleanup — a leftover temp file is harmless and must never fail a test.
            ()

    [<Test>]
    member _.``a detached child is in no group's membership and survives the disposal that reaps a contained one``
        ()
        : Task =
        task {
            use group = create ()
            let mutable detachedPid = 0

            try
                match! group.StartAsync(sleeper 3) with
                | Error error -> Assert.Fail $"{error}"
                | Ok contained ->
                    match (sleeper 60).LaunchDetached() with
                    | Error error -> Assert.Fail $"{error}"
                    | Ok detached ->
                        detachedPid <- detached.Pid

                        let members =
                            match group.Members() with
                            | Ok pids -> Set.ofSeq pids
                            | Error error -> failwith $"Members failed: {error}"

                        match contained.Pid with
                        | Some pid ->
                            let missing: string = "the contained child is missing from the group's membership"
                            Assert.That(members.Contains pid, Is.True, missing)
                        | None -> Assert.Fail "expected a pid for the contained child"

                        // The whole point: the detached child was never placed in the container.
                        let escaped: string =
                            "a detached child must not be a member of any ProcessGroup — it was assigned to the container"

                        Assert.That(members.Contains detached.Pid, Is.False, escaped)

                        // Kill-on-dispose reaps the contained tree...
                        (group :> IDisposable).Dispose()

                        // ...and cannot reach the detached child, immediately or later.
                        let survivedDispose: string =
                            "the detached child was killed by a ProcessGroup disposal it is not contained by"

                        Assert.That(isAlive detached.Pid, Is.True, survivedDispose)

                        match contained.Pid with
                        | Some pid ->
                            let containedDied: string =
                                "the contained child outlived its group's disposal (kill-on-dispose regressed)"

                            Assert.That(
                                waitUntil (TimeSpan.FromSeconds 10.0) (fun () -> not (isAlive pid)),
                                Is.True,
                                containedDied
                            )
                        | None -> ()

                        let stillAlive: string =
                            "the detached child died alongside the contained tree instead of outliving it"

                        Assert.That(isAlive detached.Pid, Is.True, stillAlive)
            finally
                if detachedPid <> 0 then
                    killQuietly detachedPid
        }
        :> Task

    [<Test>]
    member _.``the detached descriptor is a pid + start-time identity with no lifetime members``() =
        match (sleeper 60).LaunchDetached() with
        | Error error -> Assert.Fail $"{error}"
        | Ok detached ->
            try
                Assert.That(detached.Pid, Is.GreaterThan 0)
                Assert.That(detached.Program, Is.EqualTo((sleeper 60).Program))

                let identity: string =
                    "expected an OS-reported start time — the pid-reuse disambiguator — on this platform"

                Assert.That(detached.StartTime.IsSome, Is.True, identity)

                match detached.StartTime with
                | Some started -> Assert.That(started, Is.LessThanOrEqualTo(DateTime.Now.AddMinutes 1.0))
                | None -> ()

                let described: string = "ToString should name the pid for diagnostics"
                Assert.That(detached.ToString().Contains(string detached.Pid), Is.True, described)

                // The descriptor deliberately owns nothing: no disposal semantics, and no member that
                // would imply the containment this verb opted out of.
                let descriptor = typeof<DetachedProcess>

                let noDispose: string =
                    "a detached descriptor must not be disposable — it owns nothing"

                Assert.That(typeof<IDisposable>.IsAssignableFrom descriptor, Is.False, noDispose)
                Assert.That(typeof<IAsyncDisposable>.IsAssignableFrom descriptor, Is.False, noDispose)

                let names =
                    descriptor.GetMembers(BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.Static)
                    |> Array.map (fun m -> m.Name)
                    |> Set.ofArray

                for forbidden in
                    [ "Kill"
                      "KillAsync"
                      "StopAsync"
                      "WaitAsync"
                      "Dispose"
                      "DisposeAsync"
                      "ExitTask"
                      "TakeStdin"
                      "StdoutLinesAsync"
                      "OutputEventsAsync" ] do
                    let leaked: string =
                        $"'{forbidden}' would imply containment/consumption a detached launch cannot provide"

                    Assert.That(names.Contains forbidden, Is.False, leaked)
            finally
                killQuietly detached.Pid

    [<Test>]
    member _.``repeated short-lived detached children are reaped while the owner remains alive``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only: confirms detached leaders leave no zombie /proc entries"

            let seen = System.Collections.Generic.HashSet<int>()

            try
                for index in 1..32 do
                    let command = Command.create "/bin/sh" |> Command.args [ "-c"; "exit 0" ]

                    let pid =
                        match command.LaunchDetached() with
                        | Ok detached ->
                            Assert.That(seen.Add detached.Pid, Is.True, $"detached pid was reused at iteration {index}")
                            detached.Pid
                        | Error error ->
                            Assert.Fail $"short-lived detached spawn {index} failed: {error}"
                            0

                    let reaped =
                        waitUntil (TimeSpan.FromSeconds 2.0) (fun () -> procState pid |> Option.isNone)

                    let state = procState pid |> Option.defaultValue "gone"

                    Assert.That(
                        reaped,
                        Is.True,
                        $"detached child {pid} remained in /proc after exit (state {state}); the live parent did not reap it"
                    )
            finally
                // A failure must not leave a live child behind while still giving the reaper the normal chance
                // to consume successful exits. `killQuietly` is a no-op for an already-reaped child.
                for pid in seen do
                    killQuietly pid
        }

    [<Test>]
    member _.``a live detached POSIX child is not polled and is reaped after its exit event``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only: /proc makes the detached child lifecycle directly observable"

            let counts = System.Collections.Generic.Dictionary<int, int>()
            let countsLock = obj ()

            let countFor pid =
                lock countsLock (fun () ->
                    match counts.TryGetValue pid with
                    | true, count -> count
                    | false, _ -> 0)

            let observe pid =
                lock countsLock (fun () ->
                    let current =
                        match counts.TryGetValue pid with
                        | true, count -> count
                        | false, _ -> 0

                    counts[pid] <- current + 1)

            let runCase (useFastPath: bool) =
                task {
                    let mutable pid = 0

                    try
                        Native.Posix.detachedReaperUseFastPathForTests <- Some useFastPath
                        Native.Posix.exitWaitProbeForTests <- Some observe

                        match (sleeper 60).LaunchDetached() with
                        | Error error -> Assert.Fail $"detached launch failed: {error}"
                        | Ok detached -> pid <- detached.Pid

                        // Two settled samples, each far beyond the removed 10-ms interval. The portable
                        // SIGCHLD branch makes one eager registration probe; pidfd makes none. Neither may
                        // make another native reap attempt until an actual child-exit notification arrives.
                        do! Task.Delay 120
                        let firstIdleCount = countFor pid
                        do! Task.Delay 120
                        let secondIdleCount = countFor pid

                        Assert.That(
                            secondIdleCount,
                            Is.EqualTo firstIdleCount,
                            $"the live detached child was probed again without an exit event (fastPath={useFastPath})"
                        )

                        if useFastPath && Native.Posix.pidfdActive then
                            Assert.That(
                                firstIdleCount,
                                Is.EqualTo 0,
                                "the pidfd fast path probed waitid before the pidfd reported exit"
                            )
                        elif not useFastPath then
                            Assert.That(
                                firstIdleCount,
                                Is.EqualTo 1,
                                "the forced SIGCHLD fallback did not perform exactly its one eager registration probe"
                            )

                        killQuietly pid

                        let reaped =
                            waitUntil (TimeSpan.FromSeconds 5.0) (fun () -> procState pid |> Option.isNone)

                        Assert.That(reaped, Is.True, "the detached child was not reaped after its exit event")
                        Assert.That(countFor pid, Is.GreaterThan secondIdleCount, "exit did not trigger a final reap")
                    finally
                        if pid <> 0 && (procState pid |> Option.isSome) then
                            killQuietly pid

                            waitUntil (TimeSpan.FromSeconds 5.0) (fun () -> procState pid |> Option.isNone)
                            |> ignore

                        Native.Posix.exitWaitProbeForTests <- None
                        Native.Posix.detachedReaperUseFastPathForTests <- None
                }

            // Exercise the host-selected Linux path and then the same handoff through the portable
            // SIGCHLD fallback, without changing the public detached-launch surface.
            do! runCase true
            do! runCase false
        }
        :> Task

    [<Test>]
    member _.``a detached child runs and writes its redirected output with no parent involvement``() =
        let path = tempFile ()

        let writer =
            if isWindows then
                Command.create "cmd.exe" |> Command.args [ "/c"; "echo DETACHED-OK" ]
            else
                Command.create "/bin/sh" |> Command.args [ "-c"; "echo DETACHED-OK" ]

        try
            match (writer |> Command.stdoutToFile path false).LaunchDetached() with
            | Error error -> Assert.Fail $"{error}"
            | Ok detached ->
                Assert.That(detached.Pid, Is.GreaterThan 0)

                // Nothing here waits on, pumps, or reaps the child — the file is the only evidence, and it
                // appears purely because the child was handed the file as its own stdout at spawn.
                let wrote =
                    waitUntil (TimeSpan.FromSeconds 20.0) (fun () -> (readIfPresent path).Contains "DETACHED-OK")

                let ranDetached: string =
                    "the detached child never wrote its redirected stdout (it did not run, or its stdio was miswired)"

                Assert.That(wrote, Is.True, ranDetached)
        finally
            deleteQuietly path

    [<Test>]
    member _.``every builder knob a detached launch cannot honour is refused with a typed error``() =
        // A program that does not exist: were a knob silently accepted instead of refused, the launch would
        // fail with `NotFound` (spawning nothing) and the assertion below reports exactly that.
        let probe () =
            Command.create "processkit-detached-refusal-probe"

        let cases: (string * Command) list =
            [ "Pty", (probe ()).Pty()
              "KillOnParentDeath", (probe ()).KillOnParentDeath()
              "Timeout", (probe ()).Timeout(TimeSpan.FromSeconds 5.0)
              "TimeoutGrace", (probe ()).TimeoutGrace(TimeSpan.FromSeconds 1.0)
              "IdleTimeout", (probe ()).IdleTimeout(TimeSpan.FromSeconds 5.0)
              "CancelOn", (probe ()).CancelOn(CancellationToken.None)
              "CancelGrace", (probe ()).CancelGrace(TimeSpan.FromSeconds 1.0)
              "Stdin", (probe ()).Stdin(Stdin.FromString "input")
              "KeepStdinOpen", (probe ()).KeepStdinOpen()
              "OnStdoutLine", (probe ()).OnStdoutLine(Action<string>(fun _ -> ()))
              "OnStderrLine", (probe ()).OnStderrLine(Action<string>(fun _ -> ()))
              "StdoutTee", (probe ()).StdoutTee(new MemoryStream())
              "StderrTee", (probe ()).StderrTee(new MemoryStream())
              "StreamBuffer", (probe ()).StreamBuffer(StreamBufferPolicy.Bounded 8)
              "Retry", (probe ()).Retry(3, TimeSpan.FromMilliseconds 1.0, Func<ProcessError, bool>(fun _ -> true)) ]

        for knob, command in cases do
            match command.LaunchDetached() with
            | Ok detached ->
                killQuietly detached.Pid
                Assert.Fail $"{knob} was silently accepted by a detached launch instead of refused"
            | Error(ProcessError.Unsupported operation) ->
                let named: string = $"the refusal must name the offending knob (got: {operation})"
                Assert.That(operation.Contains knob, Is.True, named)
            | Error other -> Assert.Fail $"{knob} must be refused with a typed Unsupported, got: {other}"

    [<Test>]
    member _.``the knobs a detached launch can honour are not refused``() =
        // The contrast to the refusal table: these reach the real spawn (and fail only because the probe
        // program does not exist), so none of them is over-rejected.
        let probe () =
            Command.create "processkit-detached-refusal-probe"

        let accepted: (string * Command) list =
            [ "InheritStdin", (probe ()).InheritStdin()
              "CreateNoWindow", (probe ()).CreateNoWindow()
              "MergeStderr", (probe ()).MergeStderr()
              "Stdout(Null)", (probe ()).Stdout(StdioMode.Null)
              "OkCodes", (probe ()).OkCodes [ 0; 3 ]
              // `RetryNever` opts a command carrying an inherited `Retry` default back out, so the launch
              // stops being refused for it — the documented escape hatch for a CliClient template.
              "RetryNever",
              (probe ()).Retry(3, TimeSpan.FromMilliseconds 1.0, Func<ProcessError, bool>(fun _ -> true)).RetryNever() ]

        for knob, command in accepted do
            match command.LaunchDetached() with
            | Ok detached ->
                killQuietly detached.Pid
                Assert.Fail $"{knob}: the probe program should not exist"
            | Error(ProcessError.Unsupported operation) ->
                Assert.Fail $"{knob} must be honoured by a detached launch, but was refused: {operation}"
            | Error _ ->
                // NotFound (the expected shape) or a host-specific spawn failure: either way the knob
                // itself was accepted, which is what this test asserts.
                ()

    [<Test>]
    member _.``Exec.detach launches through the same opt-out and reports a missing program honestly``() =
        match Exec.detach "processkit-missing-detached-program" [] with
        | Ok detached ->
            killQuietly detached.Pid
            Assert.Fail "a missing program must not report a successful detached launch"
        | Error error ->
            let honest: string =
                $"expected a typed NotFound for a missing program, got: {error}"

            Assert.That(ProcessError.isNotFound error, Is.True, honest)

        let command = sleeper 60

        match Exec.detach command.Program (List.ofSeq command.Arguments) with
        | Error error -> Assert.Fail $"{error}"
        | Ok detached ->
            try
                Assert.That(isAlive detached.Pid, Is.True, "the Exec.detach one-liner did not leave a live child")
            finally
                killQuietly detached.Pid
