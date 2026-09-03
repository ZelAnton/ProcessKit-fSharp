namespace ProcessKit.Tests

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

/// POSIX-only: the event-driven replacement for `Native.Posix.waitPosix`'s old `Task.Run` + blocking
/// `waitpid` (a shared SIGCHLD registration + pid -> `TaskCompletionSource` registry — see
/// `Native.Posix.fs`). Verifies the observable contract holds under this native change: no fd leak under
/// load, and the thread-pool no longer parking one thread per live POSIX child. The fixture is explicitly
/// non-parallel because its narrow fault-injection seams belong to the process-wide reaper.
[<TestFixture>]
[<NonParallelizable>]
type PosixEventDrivenWaitTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux
    let isMacOs = RuntimeInformation.IsOSPlatform OSPlatform.OSX

    let shell (script: string) =
        Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    // Race `work` against a deadline so a regression that strands a `TaskCompletionSource` fails this
    // test in `deadlineMs` instead of hanging the whole run.
    let withDeadline (deadlineMs: int) (work: Task<'T>) =
        task {
            let! winner = Task.WhenAny((work :> Task), Task.Delay deadlineMs)
            Assert.That(obj.ReferenceEquals(winner, work), Is.True, "timed out waiting for the task to complete")
            return! work
        }

    let waitUntil (deadlineMs: int) (condition: unit -> bool) =
        let deadline = DateTime.UtcNow.AddMilliseconds(float deadlineMs)
        let mutable satisfied = condition ()

        while not satisfied && DateTime.UtcNow < deadline do
            Thread.Sleep 1
            satisfied <- condition ()

        satisfied

    let processIsZombie (pid: int) =
        try
            let stat = File.ReadAllText $"/proc/{pid}/stat"
            let closeParen = stat.LastIndexOf ')'

            closeParen >= 0
            && stat.Substring(closeParen + 1).Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)[0] = "Z"
        with
        | :? IOException
        | :? UnauthorizedAccessException -> false

    // Spawn a short-lived child directly through `Native.Posix.spawnPosix` (bypassing the containment/verb
    // layer entirely) and call `Native.Posix.waitPosix` on its pid TWICE — the exact double-registration
    // scenario a caller could hit before this handle's own single wait has settled. Returns both
    // outcomes so a caller can assert they agree.
    let spawnAndDoubleWait () =
        task {
            match Native.Posix.spawnPosix (shell "true") with
            | Error e -> return Error e
            | Ok spawned ->
                let first = Native.Posix.waitPosix spawned.Handle
                let second = Native.Posix.waitPosix spawned.Handle
                let! firstOutcome = withDeadline 5000 first
                let! secondOutcome = withDeadline 5000 second
                spawned.Stdout |> Option.iter (fun s -> s.Dispose())
                spawned.Stderr |> Option.iter (fun s -> s.Dispose())
                spawned.Stdin |> Option.iter (fun s -> s.Dispose())
                return Ok(firstOutcome, secondOutcome)
        }

    // Spawn a short-lived child through `Native.Posix.spawnPosix` and reap it through `waiter`, returning
    // its decoded `Outcome`. Bounded by a deadline so a stranded wait fails fast instead of hanging.
    let waitOutcomeVia (waiter: nativeint -> Task<Outcome>) (script: string) =
        task {
            match Native.Posix.spawnPosix (shell script) with
            | Error e ->
                Assert.Fail $"spawn failed: {e.Message}"
                return Outcome.Unobserved "unreachable"
            | Ok spawned ->
                let! outcome = withDeadline 5000 (waiter spawned.Handle)
                spawned.Stdout |> Option.iter (fun s -> s.Dispose())
                spawned.Stderr |> Option.iter (fun s -> s.Dispose())
                spawned.Stdin |> Option.iter (fun s -> s.Dispose())
                return outcome
        }

    // Assert that a `spawnAndDoubleWait` result is BOTH a clean exit AND agreed on by the two waiters —
    // the idempotency contract: two `waitPosix` calls on one pid must resolve to the SAME real outcome,
    // never one to the real status and the other to `Unobserved (ECHILD race)`.
    let assertCleanAgreement (result: Result<Outcome * Outcome, ProcessError>) =
        match result with
        | Error e -> Assert.Fail $"spawn failed: {e.Message}"
        | Ok(firstOutcome, secondOutcome) ->
            Assert.That(secondOutcome, Is.EqualTo firstOutcome, "the two waitPosix calls disagreed on the outcome")

            match firstOutcome with
            | Outcome.Exited 0 -> ()
            | other -> Assert.Fail $"expected a clean exit, got {other}"

    [<Test>]
    member _.``a shared reaper descriptor is not published until its worker starts successfully``() =
        let initializationLock = obj ()
        let mutable descriptor = -1
        let mutable createCount = 0
        let mutable closeCount = 0
        let mutable firstStarterThreadId = -1
        let mutable contenderReadIntercepted = 0
        use firstStartEntered = new ManualResetEventSlim(false)
        use releaseFirstStart = new ManualResetEventSlim(false)
        use contenderReadEntered = new ManualResetEventSlim(false)
        use releaseContenderRead = new ManualResetEventSlim(false)
        use contenderReadReturned = new ManualResetEventSlim(false)

        let readDescriptor () =
            let starterThread = Volatile.Read(&firstStarterThreadId)

            if
                firstStartEntered.IsSet
                && Thread.CurrentThread.ManagedThreadId <> starterThread
                && Interlocked.CompareExchange(&contenderReadIntercepted, 1, 0) = 0
            then
                contenderReadEntered.Set()
                releaseContenderRead.Wait()
                let value = Volatile.Read(&descriptor)
                contenderReadReturned.Set()
                value
            else
                Volatile.Read(&descriptor)

        let publishDescriptor value = Volatile.Write(&descriptor, value)

        let createDescriptor () =
            100 + Interlocked.Increment(&createCount)

        let closeDescriptor _ =
            Interlocked.Increment(&closeCount) |> ignore

        let startWorker value =
            if value = 101 then
                Volatile.Write(&firstStarterThreadId, Thread.CurrentThread.ManagedThreadId)
                firstStartEntered.Set()
                releaseFirstStart.Wait()
                invalidOp "injected worker start failure"

        let ensureDescriptor () =
            Native.Posix.ensureReaperDescriptorForTests
                readDescriptor
                publishDescriptor
                initializationLock
                createDescriptor
                closeDescriptor
                startWorker

        let first =
            Task.Run(fun () ->
                try
                    ensureDescriptor ()
                with :? InvalidOperationException ->
                    -1)

        let mutable contender: Task<int> option = None

        try
            Assert.That(firstStartEntered.Wait 5000, Is.True, "the failing worker start did not reach its gate")

            let concurrent = Task.Run(fun () -> ensureDescriptor ())
            contender <- Some concurrent

            Assert.That(
                contenderReadEntered.Wait 5000,
                Is.True,
                "the concurrent initialization did not reach the descriptor read"
            )

            Assert.That(
                Volatile.Read(&descriptor),
                Is.EqualTo(-1),
                "a descriptor whose worker had not started was visible to a concurrent handoff"
            )

            releaseContenderRead.Set()

            Assert.That(contenderReadReturned.Wait 5000, Is.True, "the concurrent descriptor read did not return")

            Assert.That(
                concurrent.IsCompleted,
                Is.False,
                "the concurrent initialization bypassed the in-progress failing startup"
            )

            releaseFirstStart.Set()
            let firstResult = first.GetAwaiter().GetResult()
            let concurrentResult = concurrent.GetAwaiter().GetResult()

            Assert.That(firstResult, Is.EqualTo(-1), "the injected startup failure was not observed")
            Assert.That(concurrentResult, Is.EqualTo(102), "the concurrent caller did not recover with a new worker")
            Assert.That(Volatile.Read(&descriptor), Is.EqualTo 102)
            Assert.That(createCount, Is.EqualTo 2)
            Assert.That(closeCount, Is.EqualTo 1, "the unpublished failed descriptor was not closed exactly once")
        finally
            releaseContenderRead.Set()
            releaseFirstStart.Set()

            match contender with
            | Some concurrent when not concurrent.IsCompleted -> concurrent.Wait 5000 |> ignore
            | _ -> ()

    [<Test>]
    member _.``a readable Linux pidfd transfers once when its replacement thread cannot start``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only: exercises consumed pidfd readiness on the shared epoll worker"

            if not Native.Posix.pidfdActive then
                Assert.Ignore "Linux pidfd support is unavailable on this host"

            match Native.Posix.spawnPosix (shell "read _; exit 37" |> Command.keepStdinOpen) with
            | Error error -> Assert.Fail $"spawn failed: {error.Message}"
            | Ok spawned ->
                let pid = int spawned.Handle
                let mutable pidfdCalls = 0
                let mutable nonBlockingCalls = 0
                let mutable blockingCalls = 0
                let mutable failedStarts = 0
                let mutable completed = false
                use pidfdProbe = new ManualResetEventSlim(false)
                use blockingProbe = new ManualResetEventSlim(false)
                use parked = new ManualResetEventSlim(false)

                try
                    Native.Posix.blockingReapParkedForTests <-
                        Some(fun candidatePid ->
                            if candidatePid = pid then
                                parked.Set())

                    Native.Posix.blockingReapThreadStartFailureForTests <-
                        Some(fun candidatePid ->
                            if candidatePid = pid && pidfdProbe.IsSet then
                                Interlocked.Increment(&failedStarts) |> ignore
                                true
                            else
                                false)

                    Native.Posix.exitWaitFaultForTests <-
                        Some(fun operation candidatePid ->
                            if candidatePid <> pid then
                                None
                            else
                                match operation with
                                | Native.Posix.ExitWaitOperationForTests.PidfdWaitId ->
                                    Interlocked.Increment(&pidfdCalls) |> ignore
                                    pidfdProbe.Set()
                                    Some Native.Posix.transientExitWaitErrnoForTests
                                | Native.Posix.ExitWaitOperationForTests.NonBlockingWaitPid ->
                                    Interlocked.Increment(&nonBlockingCalls) |> ignore
                                    pidfdProbe.Wait 5000 |> ignore
                                    blockingProbe.Wait 5000 |> ignore
                                    Some Native.Posix.transientExitWaitErrnoForTests
                                | Native.Posix.ExitWaitOperationForTests.BlockingWaitPid ->
                                    blockingProbe.Set()

                                    if Interlocked.Increment(&blockingCalls) <= 3 then
                                        Some Native.Posix.transientExitWaitErrnoForTests
                                    else
                                        None
                                | Native.Posix.ExitWaitOperationForTests.KqueueWaitPid -> None)

                    let outcomeTask = Native.Posix.waitPosix spawned.Handle
                    Assert.That(outcomeTask.IsCompleted, Is.False, "the live child completed during pidfd handoff")

                    match spawned.Stdin with
                    | Some input ->
                        input.WriteByte(byte '\n')
                        input.Flush()
                    | None -> Assert.Fail "spawn did not retain the stdin pipe needed to release the child"

                    Assert.That(pidfdProbe.Wait 5000, Is.True, "the readable pidfd did not reach its reap probe")
                    let! outcome = withDeadline 5000 outcomeTask
                    completed <- true

                    Assert.That(outcome, Is.EqualTo(Outcome.Exited 37))
                    Assert.That(pidfdCalls, Is.EqualTo 1, "the level-ready pidfd was dispatched more than once")
                    Assert.That(failedStarts, Is.EqualTo 1, "the pidfd path retried replacement thread startup")

                    Assert.That(
                        blockingCalls,
                        Is.EqualTo 4,
                        "the retained epoll worker did not survive three temporary blocking failures"
                    )

                    Assert.That(nonBlockingCalls, Is.LessThanOrEqualTo 1)
                    Assert.That(parked.IsSet, Is.False, "the pidfd owner waited for a synthetic signal generation")
                    Native.Posix.exitWaitFaultForTests <- None

                    Assert.That(
                        Native.Posix.reapLeader pid,
                        Is.EqualTo LeaderReap.Gone,
                        "the pidfd-owned child retained a second reapable status"
                    )
                finally
                    Native.Posix.blockingReapParkedForTests <- None
                    Native.Posix.blockingReapThreadStartFailureForTests <- None
                    Native.Posix.exitWaitFaultForTests <- None
                    spawned.Stdout |> Option.iter (fun stream -> stream.Dispose())
                    spawned.Stderr |> Option.iter (fun stream -> stream.Dispose())
                    spawned.Stdin |> Option.iter (fun stream -> stream.Dispose())

                    if not completed then
                        Native.Posix.killProcess pid
                        Native.Posix.reapLeader pid |> ignore
        }
        :> Task

    [<Test>]
    member _.``one SIGCHLD permits a bounded post-event EAGAIN retry and retains one owner``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: exercises the shared SIGCHLD fallback"

            match Native.Posix.spawnPosix (shell "read _; exit 23" |> Command.keepStdinOpen) with
            | Error error -> Assert.Fail $"spawn failed: {error.Message}"
            | Ok spawned ->
                let pid = int spawned.Handle
                let mutable nonBlockingCalls = 0
                let mutable blockingCalls = 0
                let mutable holdEagain = 1
                let mutable childReleased = 0
                let mutable completed = false
                use parked = new ManualResetEventSlim(false)
                use exitSignalScanned = new ManualResetEventSlim(false)
                use postEventEagain = new ManualResetEventSlim(false)

                try
                    Native.Posix.blockingReapParkedForTests <-
                        Some(fun candidatePid ->
                            if candidatePid = pid then
                                parked.Set())

                    Native.Posix.exitWaitFaultForTests <-
                        Some(fun operation candidatePid ->
                            if candidatePid <> pid then
                                None
                            else
                                match operation with
                                | Native.Posix.ExitWaitOperationForTests.PidfdWaitId -> None
                                | Native.Posix.ExitWaitOperationForTests.BlockingWaitPid ->
                                    let call = Interlocked.Increment(&blockingCalls)

                                    if call <= 2 then
                                        Some Native.Posix.transientExitWaitErrnoForTests
                                    elif call = 3 then
                                        // The SIGCHLD callback pulses the blocking owner before scanning
                                        // pending pids. Wait for that same callback's targeted probe so
                                        // this is deterministically the first retry after the real child
                                        // exit, then clear EAGAIN without generating a second event.
                                        exitSignalScanned.Wait 5000 |> ignore
                                        postEventEagain.Set()
                                        Volatile.Write(&holdEagain, 0)
                                        Some Native.Posix.transientExitWaitErrnoForTests
                                    elif Volatile.Read(&holdEagain) <> 0 then
                                        Some Native.Posix.transientExitWaitErrnoForTests
                                    else
                                        None
                                | Native.Posix.ExitWaitOperationForTests.NonBlockingWaitPid ->
                                    Interlocked.Increment(&nonBlockingCalls) |> ignore

                                    if Volatile.Read(&childReleased) <> 0 then
                                        exitSignalScanned.Set()

                                    // Keep the portable scan from consuming the status: this test is
                                    // specifically proving the dedicated owner's bounded follow-up.
                                    Some Native.Posix.transientExitWaitErrnoForTests
                                | Native.Posix.ExitWaitOperationForTests.KqueueWaitPid -> None)

                    let first = Native.Posix.waitPosixViaSigchldForTests spawned.Handle
                    let second = Native.Posix.waitPosixViaSigchldForTests spawned.Handle

                    Assert.That(parked.Wait 5000, Is.True, "the repeated EAGAIN path did not reach its event gate")

                    Assert.That(
                        blockingCalls,
                        Is.EqualTo 2,
                        "the blocking owner made another native probe while EAGAIN remained active"
                    )

                    Assert.That(first.IsCompleted, Is.False, "the unreaped child wait completed before retry release")
                    Assert.That(second.IsCompleted, Is.False, "the shared waiter completed before retry release")

                    Volatile.Write(&childReleased, 1)

                    match spawned.Stdin with
                    | Some input ->
                        input.WriteByte(byte '\n')
                        input.Flush()
                    | None -> Assert.Fail "spawn did not retain the stdin pipe needed to release the child"

                    Assert.That(
                        exitSignalScanned.Wait 5000,
                        Is.True,
                        "the child's real SIGCHLD did not trigger the targeted portable scan"
                    )

                    Assert.That(
                        postEventEagain.Wait 5000,
                        Is.True,
                        "the first blocking retry after the real SIGCHLD did not retain EAGAIN"
                    )

                    let! firstOutcome = withDeadline 5000 first
                    let! secondOutcome = withDeadline 5000 second
                    completed <- true

                    Assert.That(firstOutcome, Is.EqualTo(Outcome.Exited 23))
                    Assert.That(secondOutcome, Is.EqualTo firstOutcome, "duplicate waiters did not share the one reap")

                    Assert.That(
                        blockingCalls,
                        Is.EqualTo 4,
                        "the owner did not make exactly one bounded follow-up after the post-event EAGAIN"
                    )

                    Assert.That(nonBlockingCalls, Is.GreaterThanOrEqualTo 2)

                    Native.Posix.exitWaitFaultForTests <- None

                    Assert.That(
                        Native.Posix.reapLeader pid,
                        Is.EqualTo LeaderReap.Gone,
                        "the child still had a second reapable status after both waiters completed"
                    )
                finally
                    Volatile.Write(&holdEagain, 0)
                    Native.Posix.notifyExitSignalForTests ()
                    Native.Posix.blockingReapParkedForTests <- None
                    Native.Posix.exitWaitFaultForTests <- None
                    spawned.Stdout |> Option.iter (fun stream -> stream.Dispose())
                    spawned.Stderr |> Option.iter (fun stream -> stream.Dispose())
                    spawned.Stdin |> Option.iter (fun stream -> stream.Dispose())

                    if not completed then
                        Native.Posix.killProcess pid
                        Native.Posix.reapLeader pid |> ignore
        }
        :> Task

    [<Test>]
    member _.``a pre-handoff SIGCHLD gives an already-exited detached child a bounded recovery probe``() =
        if not isLinux then
            Assert.Ignore "Linux-only: /proc makes the consumed pre-handoff SIGCHLD deterministic"

        let mutable pid = 0
        let mutable nonBlockingCalls = 0
        let mutable blockingCalls = 0
        let mutable signalConsumedBeforeHandoff = false
        let mutable completed = false
        use parked = new ManualResetEventSlim(false)

        try
            Native.Posix.detachedReaperUseFastPathForTests <- Some false

            Native.Posix.blockingReapParkedForTests <-
                Some(fun candidatePid ->
                    if candidatePid = pid then
                        parked.Set())

            Native.Posix.exitWaitFaultForTests <-
                Some(fun operation candidatePid ->
                    if candidatePid <> pid then
                        None
                    else
                        match operation with
                        | Native.Posix.ExitWaitOperationForTests.NonBlockingWaitPid ->
                            Interlocked.Increment(&nonBlockingCalls) |> ignore
                            Some Native.Posix.transientExitWaitErrnoForTests
                        | Native.Posix.ExitWaitOperationForTests.BlockingWaitPid ->
                            if Interlocked.Increment(&blockingCalls) <= 3 then
                                Some Native.Posix.transientExitWaitErrnoForTests
                            else
                                None
                        | Native.Posix.ExitWaitOperationForTests.KqueueWaitPid
                        | Native.Posix.ExitWaitOperationForTests.PidfdWaitId -> None)

            Native.Posix.detachedReaperHandoffForTests <-
                Some(fun candidatePid ->
                    pid <- candidatePid
                    let generationBeforeExit = Native.Posix.exitSignalGenerationForTests ()
                    Native.Posix.killProcess candidatePid

                    let becameZombie = waitUntil 5000 (fun () -> processIsZombie candidatePid)

                    signalConsumedBeforeHandoff <-
                        becameZombie
                        && waitUntil 5000 (fun () ->
                            Native.Posix.exitSignalGenerationForTests () > generationBeforeExit)

                    Ok())

            match Native.Posix.spawnDetachedPosix (shell "sleep 60") with
            | Error error -> Assert.Fail $"detached spawn failed: {error.Message}"
            | Ok spawned ->
                pid <- spawned.Pid

                Assert.That(
                    signalConsumedBeforeHandoff,
                    Is.True,
                    "the child's real SIGCHLD was not consumed before its pid entered the wait registry"
                )

                Assert.That(
                    waitUntil 5000 (fun () -> not (File.Exists $"/proc/{pid}/stat")),
                    Is.True,
                    "the already-exited detached child was not eventually reaped"
                )

                Assert.That(nonBlockingCalls, Is.EqualTo 1, "initial handoff did not make exactly one eager probe")

                Assert.That(
                    blockingCalls,
                    Is.EqualTo 4,
                    "the consumed signal did not survive three temporary failures before the real reap"
                )

                Assert.That(parked.IsSet, Is.False, "the recovery owner parked on the already-consumed signal")
                Native.Posix.exitWaitFaultForTests <- None

                Assert.That(
                    Native.Posix.reapLeader pid,
                    Is.EqualTo LeaderReap.Gone,
                    "the detached child retained a second reapable status after recovery"
                )

                completed <- true
        finally
            Native.Posix.detachedReaperHandoffForTests <- None
            Native.Posix.detachedReaperUseFastPathForTests <- None
            Native.Posix.blockingReapParkedForTests <- None
            Native.Posix.exitWaitFaultForTests <- None

            if pid <> 0 && not completed then
                Native.Posix.killProcess pid
                Native.Posix.reapLeader pid |> ignore
                Native.Posix.notifyExitSignalForTests ()

    [<Test>]
    member _.``a failed Linux fast-path registration retains a pre-handoff exit event``() =
        if not isLinux then
            Assert.Ignore "Linux-only: exercises the pidfd registration-failure fallback"

        if not Native.Posix.pidfdActive then
            Assert.Ignore "Linux pidfd support is unavailable on this host"

        let mutable pid = 0
        let mutable registrationFailures = 0
        let mutable nonBlockingCalls = 0
        let mutable blockingCalls = 0
        let mutable signalConsumedBeforeHandoff = false
        let mutable completed = false
        use parked = new ManualResetEventSlim(false)

        try
            Native.Posix.detachedReaperUseFastPathForTests <- Some true

            Native.Posix.pidfdRegistrationFailureForTests <-
                Some(fun candidatePid ->
                    if candidatePid = pid then
                        Interlocked.Increment(&registrationFailures) |> ignore
                        true
                    else
                        false)

            Native.Posix.blockingReapParkedForTests <-
                Some(fun candidatePid ->
                    if candidatePid = pid then
                        parked.Set())

            Native.Posix.exitWaitFaultForTests <-
                Some(fun operation candidatePid ->
                    if candidatePid <> pid then
                        None
                    else
                        match operation with
                        | Native.Posix.ExitWaitOperationForTests.NonBlockingWaitPid ->
                            Interlocked.Increment(&nonBlockingCalls) |> ignore
                            Some Native.Posix.transientExitWaitErrnoForTests
                        | Native.Posix.ExitWaitOperationForTests.BlockingWaitPid ->
                            if Interlocked.Increment(&blockingCalls) <= 3 then
                                Some Native.Posix.transientExitWaitErrnoForTests
                            else
                                None
                        | Native.Posix.ExitWaitOperationForTests.KqueueWaitPid
                        | Native.Posix.ExitWaitOperationForTests.PidfdWaitId -> None)

            Native.Posix.detachedReaperHandoffForTests <-
                Some(fun candidatePid ->
                    pid <- candidatePid
                    let generationBeforeExit = Native.Posix.exitSignalGenerationForTests ()
                    Native.Posix.killProcess candidatePid

                    let becameZombie = waitUntil 5000 (fun () -> processIsZombie candidatePid)

                    signalConsumedBeforeHandoff <-
                        becameZombie
                        && waitUntil 5000 (fun () ->
                            Native.Posix.exitSignalGenerationForTests () > generationBeforeExit)

                    Ok())

            match Native.Posix.spawnDetachedPosix (shell "sleep 60") with
            | Error error -> Assert.Fail $"detached spawn failed: {error.Message}"
            | Ok spawned ->
                pid <- spawned.Pid

                Assert.That(
                    signalConsumedBeforeHandoff,
                    Is.True,
                    "the child's real SIGCHLD was not observed before its fast-path registration"
                )

                Assert.That(
                    waitUntil 5000 (fun () -> not (File.Exists $"/proc/{pid}/stat")),
                    Is.True,
                    "the fast-path registration fallback did not reap the already-exited child"
                )

                Assert.That(registrationFailures, Is.EqualTo 1, "the per-child pidfd registration did not fail once")

                Assert.That(
                    blockingCalls,
                    Is.EqualTo 4,
                    "the pre-spawn event boundary did not survive three temporary waits before the real reap"
                )

                Assert.That(
                    nonBlockingCalls,
                    Is.LessThanOrEqualTo 1,
                    "the already-consumed exit caused repeated portable reap probes"
                )

                Assert.That(parked.IsSet, Is.False, "the fallback parked on the already-consumed exit event")
                Native.Posix.exitWaitFaultForTests <- None

                Assert.That(
                    Native.Posix.reapLeader pid,
                    Is.EqualTo LeaderReap.Gone,
                    "the detached child retained a second reapable status after fast-path recovery"
                )

                completed <- true
        finally
            Native.Posix.detachedReaperHandoffForTests <- None
            Native.Posix.detachedReaperUseFastPathForTests <- None
            Native.Posix.pidfdRegistrationFailureForTests <- None
            Native.Posix.blockingReapParkedForTests <- None
            Native.Posix.exitWaitFaultForTests <- None

            if pid <> 0 && not completed then
                Native.Posix.killProcess pid
                Native.Posix.reapLeader pid |> ignore
                Native.Posix.notifyExitSignalForTests ()

    [<Test>]
    member _.``a consumed SIGCHLD callback retains the child when its replacement thread cannot start``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only: /proc attributes the injected callback probe to the exited child"

            match Native.Posix.spawnPosix (shell "read _; exit 31" |> Command.keepStdinOpen) with
            | Error error -> Assert.Fail $"spawn failed: {error.Message}"
            | Ok spawned ->
                let pid = int spawned.Handle
                let mutable childReleased = 0
                let mutable nonBlockingCalls = 0
                let mutable blockingCalls = 0
                let mutable failedStarts = 0
                let mutable completed = false
                use callbackProbe = new ManualResetEventSlim(false)
                use parked = new ManualResetEventSlim(false)

                try
                    Native.Posix.blockingReapParkedForTests <-
                        Some(fun candidatePid ->
                            if candidatePid = pid then
                                parked.Set())

                    Native.Posix.blockingReapThreadStartFailureForTests <-
                        Some(fun candidatePid ->
                            if candidatePid = pid && callbackProbe.IsSet then
                                Interlocked.Increment(&failedStarts) |> ignore
                                true
                            else
                                false)

                    Native.Posix.exitWaitFaultForTests <-
                        Some(fun operation candidatePid ->
                            if candidatePid <> pid then
                                None
                            else
                                match operation with
                                | Native.Posix.ExitWaitOperationForTests.NonBlockingWaitPid ->
                                    Interlocked.Increment(&nonBlockingCalls) |> ignore

                                    if Volatile.Read(&childReleased) <> 0 && processIsZombie candidatePid then
                                        callbackProbe.Set()
                                        Some Native.Posix.transientExitWaitErrnoForTests
                                    else
                                        None
                                | Native.Posix.ExitWaitOperationForTests.BlockingWaitPid ->
                                    if Interlocked.Increment(&blockingCalls) <= 3 then
                                        Some Native.Posix.transientExitWaitErrnoForTests
                                    else
                                        None
                                | Native.Posix.ExitWaitOperationForTests.KqueueWaitPid
                                | Native.Posix.ExitWaitOperationForTests.PidfdWaitId -> None)

                    let first = Native.Posix.waitPosixViaSigchldForTests spawned.Handle
                    let second = Native.Posix.waitPosixViaSigchldForTests spawned.Handle

                    Assert.That(first.IsCompleted, Is.False, "the live child completed during initial handoff")
                    Assert.That(second.IsCompleted, Is.False, "the shared waiter completed before child exit")
                    Assert.That(nonBlockingCalls, Is.EqualTo 1, "handoff did not make exactly one live-child probe")

                    Volatile.Write(&childReleased, 1)

                    match spawned.Stdin with
                    | Some input ->
                        input.WriteByte(byte '\n')
                        input.Flush()
                    | None -> Assert.Fail "spawn did not retain the stdin pipe needed to release the child"

                    Assert.That(
                        callbackProbe.Wait 5000,
                        Is.True,
                        "the child's real SIGCHLD did not reach the injected temporary callback probe"
                    )

                    let! firstOutcome = withDeadline 5000 first
                    let! secondOutcome = withDeadline 5000 second
                    completed <- true

                    Assert.That(firstOutcome, Is.EqualTo(Outcome.Exited 31))
                    Assert.That(secondOutcome, Is.EqualTo firstOutcome, "duplicate waiters did not share the one reap")
                    Assert.That(failedStarts, Is.EqualTo 1, "the callback did not observe one failed replacement start")

                    Assert.That(
                        blockingCalls,
                        Is.EqualTo 4,
                        "the consumed-event credit did not survive three temporary waits before the real reap"
                    )

                    Assert.That(parked.IsSet, Is.False, "the callback parked on the SIGCHLD it had already consumed")
                    Assert.That(nonBlockingCalls, Is.EqualTo 2, "the exit callback did not make one targeted probe")
                    Native.Posix.exitWaitFaultForTests <- None

                    Assert.That(
                        Native.Posix.reapLeader pid,
                        Is.EqualTo LeaderReap.Gone,
                        "the child retained a second reapable status after both waiters completed"
                    )
                finally
                    Native.Posix.notifyExitSignalForTests ()
                    Native.Posix.blockingReapParkedForTests <- None
                    Native.Posix.blockingReapThreadStartFailureForTests <- None
                    Native.Posix.exitWaitFaultForTests <- None
                    spawned.Stdout |> Option.iter (fun stream -> stream.Dispose())
                    spawned.Stderr |> Option.iter (fun stream -> stream.Dispose())
                    spawned.Stdin |> Option.iter (fun stream -> stream.Dispose())

                    if not completed then
                        Native.Posix.killProcess pid
                        Native.Posix.reapLeader pid |> ignore
        }
        :> Task

    [<Test>]
    member _.``a consumed macOS kqueue event transfers once when its replacement thread cannot start``() : Task =
        task {
            if not isMacOs then
                Assert.Ignore "macOS-only: exercises failed replacement ownership after a consumed EV_ONESHOT event"

            match Native.Posix.spawnPosix (shell "sleep 0.2; exit 29") with
            | Error error -> Assert.Fail $"spawn failed: {error.Message}"
            | Ok spawned ->
                let pid = int spawned.Handle
                let mutable kqueueCalls = 0
                let mutable armCalls = 0
                let mutable nonBlockingCalls = 0
                let mutable blockingCalls = 0
                let mutable failedStarts = 0
                let mutable completed = false
                use kqueueProbe = new ManualResetEventSlim(false)
                use blockingProbe = new ManualResetEventSlim(false)
                use parked = new ManualResetEventSlim(false)

                try
                    Native.Posix.blockingReapParkedForTests <-
                        Some(fun candidatePid ->
                            if candidatePid = pid then
                                parked.Set())

                    Native.Posix.blockingReapThreadStartFailureForTests <-
                        Some(fun candidatePid ->
                            if candidatePid = pid && kqueueProbe.IsSet then
                                Interlocked.Increment(&failedStarts) |> ignore
                                true
                            else
                                false)

                    Native.Posix.kqueueRegistrationFailureForTests <-
                        Some(fun candidatePid ->
                            if candidatePid = pid then
                                Interlocked.Increment(&armCalls) |> ignore

                            false)

                    Native.Posix.exitWaitFaultForTests <-
                        Some(fun operation candidatePid ->
                            if candidatePid <> pid then
                                None
                            else
                                match operation with
                                | Native.Posix.ExitWaitOperationForTests.KqueueWaitPid ->
                                    if Interlocked.Increment(&kqueueCalls) = 2 then
                                        kqueueProbe.Set()
                                        Some Native.Posix.transientExitWaitErrnoForTests
                                    else
                                        None
                                | Native.Posix.ExitWaitOperationForTests.NonBlockingWaitPid ->
                                    Interlocked.Increment(&nonBlockingCalls) |> ignore
                                    kqueueProbe.Wait 5000 |> ignore
                                    blockingProbe.Wait 5000 |> ignore
                                    Some Native.Posix.transientExitWaitErrnoForTests
                                | Native.Posix.ExitWaitOperationForTests.BlockingWaitPid ->
                                    blockingProbe.Set()

                                    if Interlocked.Increment(&blockingCalls) <= 3 then
                                        Some Native.Posix.transientExitWaitErrnoForTests
                                    else
                                        None
                                | _ -> None)

                    let! outcome = withDeadline 5000 (Native.Posix.waitPosix spawned.Handle)
                    completed <- true

                    Assert.That(outcome, Is.EqualTo(Outcome.Exited 29))

                    Assert.That(
                        kqueueCalls,
                        Is.EqualTo 2,
                        "the consumed one-shot event was not followed by exactly one failed reap probe"
                    )

                    Assert.That(
                        armCalls,
                        Is.EqualTo 1,
                        "the consumed one-shot event was rearmed instead of transferred to the live worker"
                    )

                    Assert.That(failedStarts, Is.EqualTo 1, "the kqueue path retried replacement thread startup")

                    Assert.That(
                        blockingCalls,
                        Is.EqualTo 4,
                        "the retained kqueue worker did not survive three temporary blocking failures"
                    )

                    Assert.That(nonBlockingCalls, Is.LessThanOrEqualTo 1)
                    Assert.That(parked.IsSet, Is.False, "the kqueue owner waited for a synthetic signal generation")
                    Assert.That(Native.Posix.kqueueActive (), Is.True)
                finally
                    Native.Posix.blockingReapParkedForTests <- None
                    Native.Posix.blockingReapThreadStartFailureForTests <- None
                    Native.Posix.kqueueRegistrationFailureForTests <- None
                    Native.Posix.exitWaitFaultForTests <- None
                    spawned.Stdout |> Option.iter (fun stream -> stream.Dispose())
                    spawned.Stderr |> Option.iter (fun stream -> stream.Dispose())
                    spawned.Stdin |> Option.iter (fun stream -> stream.Dispose())

                    if not completed then
                        Native.Posix.killProcess pid
                        Native.Posix.reapLeader pid |> ignore
        }
        :> Task

    [<Test>]
    member _.``a failed macOS kqueue registration transfers that child to one blocking wait``() : Task =
        task {
            if not isMacOs then
                Assert.Ignore "macOS-only: exercises the EVFILT_PROC registration-failure fallback"

            match Native.Posix.spawnPosix (shell "sleep 0.1; exit 19") with
            | Error error -> Assert.Fail $"spawn failed: {error.Message}"
            | Ok spawned ->
                let pid = int spawned.Handle
                let mutable blockingCalls = 0
                let mutable completed = false

                try
                    Native.Posix.kqueueRegistrationFailureForTests <- Some((=) pid)

                    Native.Posix.exitWaitFaultForTests <-
                        Some(fun operation candidatePid ->
                            if candidatePid <> pid then
                                None
                            else
                                match operation with
                                | Native.Posix.ExitWaitOperationForTests.BlockingWaitPid ->
                                    Interlocked.Increment(&blockingCalls) |> ignore
                                    None
                                | Native.Posix.ExitWaitOperationForTests.NonBlockingWaitPid ->
                                    Some Native.Posix.transientExitWaitErrnoForTests
                                | _ -> None)

                    let! outcome = withDeadline 5000 (Native.Posix.waitPosix spawned.Handle)
                    completed <- true
                    Assert.That(outcome, Is.EqualTo(Outcome.Exited 19))
                    Assert.That(blockingCalls, Is.EqualTo 1, "the failed registration did not use one blocking wait")
                    Assert.That(Native.Posix.kqueueActive (), Is.True)
                finally
                    Native.Posix.kqueueRegistrationFailureForTests <- None
                    Native.Posix.exitWaitFaultForTests <- None
                    spawned.Stdout |> Option.iter (fun stream -> stream.Dispose())
                    spawned.Stderr |> Option.iter (fun stream -> stream.Dispose())
                    spawned.Stdin |> Option.iter (fun stream -> stream.Dispose())

                    if not completed then
                        Native.Posix.killProcess pid
                        Native.Posix.reapLeader pid |> ignore
        }
        :> Task

    [<Test>]
    member _.``waitPosix is idempotent for a repeated pid and does not leak a descriptor``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: exercises Native.Posix.waitPosix directly"

            let! firstResult = spawnAndDoubleWait ()
            assertCleanAgreement firstResult

            if isLinux then
                // Warm up (JIT, the lazy shared SIGCHLD registration) before the baseline, then repeat
                // the double-registration scenario under load. Every iteration re-asserts idempotency, so
                // the loop doubles as a determinism stress test for the concurrent double-registration
                // race (not just an fd probe): a flaky ECHILD race would surface as a disagreement here.
                // Separately, any per-spawn fd left open (stdio pipes, or anything the losing side of the
                // registration race failed to release) shows up as fd growth proportional to the count.
                for _ in 1..5 do
                    let! warmup = spawnAndDoubleWait ()
                    assertCleanAgreement warmup

                let fdCount () =
                    Directory.GetFileSystemEntries("/proc/self/fd").Length

                let baseline = fdCount ()

                for _ in 1..50 do
                    let! loaded = spawnAndDoubleWait ()
                    assertCleanAgreement loaded

                let after = fdCount ()

                Assert.That(
                    after,
                    Is.LessThan(baseline + 20),
                    $"open fd count grew from {baseline} to {after} after 50 duplicate-registration \
                      spawns — looks like an fd leak"
                )
        }
        :> Task

    [<Test>]
    member _.``no fd leak after many piped spawns``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: exercises the event-driven waitPosix replacement"

            if not isLinux then
                Assert.Ignore "open fd count is observable portably via /proc on Linux only"

            let echo = shell "echo warmup"

            // Warm up (JIT, the lazy shared SIGCHLD registration, first-spawn one-offs) before
            // establishing the baseline, so the load below is measured against a settled steady state.
            for _ in 1..5 do
                match! echo.RunAsync() with
                | Ok _ -> ()
                | Error error -> Assert.Fail $"{error.Message}"

            let fdCount () =
                Directory.GetFileSystemEntries("/proc/self/fd").Length

            let baseline = fdCount ()

            for _ in 1..200 do
                match! echo.RunAsync() with
                | Ok _ -> ()
                | Error error -> Assert.Fail $"{error.Message}"

            let after = fdCount ()

            // A per-spawn leak of even one fd would show up as growth proportional to the 200
            // runs; a generous absolute slack (`+20`) absorbs incidental steady-state noise without
            // masking a real leak.
            Assert.That(
                after,
                Is.LessThan(baseline + 20),
                $"open fd count grew from {baseline} to {after} after 200 piped spawns — looks like a leak"
            )
        }
        :> Task

    [<Test>]
    member _.``reaping a POSIX child does not park a thread-pool thread per concurrent child``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: exercises the event-driven waitPosix replacement"

            let concurrency = 100
            let baselineThreadPoolCount = ThreadPool.ThreadCount

            // Each child sleeps briefly so all `concurrency` of them are alive together at the
            // sampling point below.
            let sleeper = shell "sleep 0.3"
            let runs = [ for _ in 1..concurrency -> sleeper.RunAsync() ]

            // Sample mid-flight. The old, unconditionally-blocking `waitPosix` parked one dedicated
            // thread-pool thread per concurrent child (one `Task.Run` each, blocked in `waitpid`); the
            // event-driven replacement shares one SIGCHLD registration process-wide, so thread-pool
            // growth here should be far below `concurrency`, not track it 1:1.
            do! Task.Delay 150
            let midFlightThreadPoolCount = ThreadPool.ThreadCount

            let! results = Task.WhenAll runs

            for result in results do
                match result with
                | Ok _ -> ()
                | Error error -> Assert.Fail $"{error.Message}"

            Assert.That(
                midFlightThreadPoolCount,
                Is.LessThan(baselineThreadPoolCount + concurrency / 2),
                $"thread-pool grew from {baselineThreadPoolCount} to {midFlightThreadPoolCount} threads \
                  with {concurrency} concurrent POSIX children in flight — looks like one thread parked \
                  per child"
            )
        }
        :> Task

    [<Test>]
    member _.``exit code, clean exit, and signal all decode correctly through the event-driven wait``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: exercises the event-driven waitPosix replacement"

            match! (shell "exit 0").RunAsync() with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"clean exit: {error.Message}"

            match! Runner.exitCode (JobRunner()) CancellationToken.None (shell "exit 42") with
            | Ok 42 -> ()
            | other -> Assert.Fail $"expected exit code 42, got {other}"

            match! (shell "kill -TERM $$").RunAsync() with
            | Error(ProcessError.Signalled _) -> ()
            | other -> Assert.Fail $"expected a Signalled outcome, got {other}"
        }
        :> Task

    [<Test>]
    member _.``the SIGCHLD fallback wait decodes exit, clean exit, and signal correctly``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: exercises the shared-SIGCHLD fallback path directly"

            // `waitPosixViaSigchldForTests` forces the fallback regardless of pidfd support, so this
            // covers the fallback's `waitpid`/`decodeWaitStatus` decoding even on a pidfd-capable host
            // (where `waitPosix` itself would otherwise take the pidfd path and never touch it).
            let via = Native.Posix.waitPosixViaSigchldForTests

            match! waitOutcomeVia via "exit 0" with
            | Outcome.Exited 0 -> ()
            | other -> Assert.Fail $"clean exit via fallback: expected Exited 0, got {other}"

            match! waitOutcomeVia via "exit 7" with
            | Outcome.Exited 7 -> ()
            | other -> Assert.Fail $"exit code via fallback: expected Exited 7, got {other}"

            match! waitOutcomeVia via "kill -TERM $$" with
            | Outcome.Signalled(Some _) -> ()
            | other -> Assert.Fail $"signal via fallback: expected Signalled, got {other}"
        }
        :> Task

    [<Test>]
    member _.``the active wait mechanism and the SIGCHLD fallback agree on a child's outcome``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: cross-checks the two Native.Posix wait mechanisms"

            // The pidfd fast path is Linux-only — it must never be selected on macOS/other POSIX. (On an
            // old Linux kernel it is simply off, which is also fine; we only assert the negative.)
            if not isLinux then
                Assert.That(Native.Posix.pidfdActive, Is.False, "the pidfd fast path must never be selected off Linux")

            // For each shape (clean exit / non-zero exit / signal), the active mechanism (`waitPosix` —
            // the pidfd `waitid(P_PIDFD)` decode where supported) and the forced SIGCHLD fallback
            // (`waitpid`/`decodeWaitStatus`) must agree, so `decodeSiginfo` matches the status-word decode.
            for script in [ "exit 0"; "exit 7"; "kill -TERM $$" ] do
                let! viaActive = waitOutcomeVia Native.Posix.waitPosix script
                let! viaFallback = waitOutcomeVia Native.Posix.waitPosixViaSigchldForTests script

                Assert.That(
                    viaActive,
                    Is.EqualTo viaFallback,
                    $"the pidfd path and the SIGCHLD fallback disagreed for `{script}` \
                      (pidfdActive={Native.Posix.pidfdActive})"
                )

            if isMacOs then
                Assert.That(Native.Posix.kqueueActive (), Is.True, "macOS did not initialize the kqueue reaper")
        }
        :> Task

    [<Test>]
    member _.``macOS shared kqueue reaps a burst of exits without stranding waits``() : Task =
        task {
            if not isMacOs then
                Assert.Ignore "macOS-only: exercises EVFILT_PROC NOTE_EXIT"

            let runs = [ for _ in 1..200 -> (shell "exit 0").RunAsync() ]
            let! results = Task.WhenAll runs

            for result in results do
                match result with
                | Ok _ -> ()
                | Error error -> Assert.Fail error.Message

            Assert.That(Native.Posix.kqueueActive (), Is.True)
        }
        :> Task

    [<Test>]
    member _.``waitPosix resolves to the real outcome when reapLeader wins the reap race``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX-only: exercises the reformulated ECHILD race in tryReapPending/reapLeader"

            match Native.Posix.spawnPosix (shell "exit 0") with
            | Error e -> Assert.Fail $"spawn failed: {e.Message}"
            | Ok spawned ->
                let pid = int spawned.Handle
                let waitTask = Native.Posix.waitPosix spawned.Handle

                // Race a direct `reapLeader` call — the exact "some concurrent caller already reaped
                // this pid" scenario `tryReapPending`'s ECHILD branch exists for (in real use, this is
                // a group's own teardown racing a run's own wait) — against `waitPosix`'s own
                // SIGCHLD-driven reap. Whichever side actually wins the `waitpid` race, the wait must
                // still resolve to the REAL decoded status promptly: never a fabricated clean exit
                // (the old behaviour), and never a hang (the removed blocking spin's replacement must
                // not silently swallow a result either).
                let reapTask = Task.Run(fun () -> Native.Posix.reapLeader pid)

                let! outcome = withDeadline 5000 waitTask
                // The racing reap's own verdict (it may or may not be the side that won the `waitpid`)
                // is not what this test is about — the shared wait's resolved status is.
                let! _ = reapTask

                spawned.Stdout |> Option.iter (fun s -> s.Dispose())
                spawned.Stderr |> Option.iter (fun s -> s.Dispose())
                spawned.Stdin |> Option.iter (fun s -> s.Dispose())

                match outcome with
                | Outcome.Exited 0 -> ()
                | other -> Assert.Fail $"expected a clean exit despite the concurrent reap race, got {other}"
        }
        :> Task
