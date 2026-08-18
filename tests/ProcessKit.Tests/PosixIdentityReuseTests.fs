namespace ProcessKit.Tests

open System
open System.Collections.Generic
open System.IO
open System.Runtime.InteropServices
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

/// Regression tests for the POSIX pgid/pid-reuse hardening (T-084): each tracked process group is bound
/// to its leader's start-time identity token, captured at `Track` and re-checked on every probe, so a
/// pgid/pid number recycled by an unrelated process — with no intervening `ESRCH` — is detected and
/// never signalled/suspended/killed (the wrong-target-kill window that breaks the kill-on-drop tree
/// guarantee). A matching or unreadable identity degrades to the prior by-number liveness behavior, so
/// no platform loses coverage.
///
/// The `/proc/<pid>/stat` field-22 parser is unit-tested directly with synthetic lines. The
/// reuse/non-regression behavior is driven through `Native.Posix`'s process-wide identity/liveness/
/// delivery seams (`readProcessIdentityForTests`, `processGroupAliveForTests`, `processAliveForTests`,
/// `groupDeliveryObserverForTests`, `leaderPidDeliveryObserverForTests`) so a recycled number — or the
/// pre-`setsid()` pty window — can be simulated deterministically rather than racing a real OS pid
/// recycle. The seams are set and reset in a `finally`; the fixture runs sequentially (no
/// `[Parallelizable]`), so they never race a concurrent probe. The synthetic pgid numbers are far above
/// any real pid, so the delivery primitives' own (still real) `kill`/`killpg`/`waitpid` calls are
/// harmless `ESRCH`/`ECHILD` no-ops.
///
/// That last point holds for a TRACKED pgid, whose sweep ignores a `TargetGone` verdict, and stops
/// holding for a pid adopted by NUMBER, whose ledger treats the same verdict as proof the process is
/// gone and prunes the entry on it. The bare-pid tests therefore add one more seam
/// (`adoptedPidKillForTests`, through `runWithAdoptionSeams`) to pin what that exact-pid `kill` reports;
/// see the comment on the T-371 section below.
[<TestFixture>]
type PosixIdentityReuseTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux
    let isMac = RuntimeInformation.IsOSPlatform OSPlatform.OSX

    // A `Spawned` for pgid `n` — only `Handle` matters to `ProcessGroupBackend`.
    let spawnedFor (pgid: int) : Native.Common.Spawned =
        { Native.Common.Spawned.Handle = nativeint pgid
          Stdout = None
          Stderr = None
          Stdin = None
          ExtraFds = []
          WindowsCtrlGroup = false
          PtyControl = None }

    // Drive a `ProcessGroupBackend` against deterministic identity/liveness/delivery seams. `current`
    // maps a pgid to the identity a probe reads NOW — mutate it between track and signal to simulate a
    // recycled number (a live pgid whose current token differs from the one captured at track). Every
    // tracked pgid is treated alive by number; `delivered` records each pgid a signal/kill path actually
    // reached. Seams are always reset in the `finally`.
    let runWithReuseSeams (body: Dictionary<int, uint64 option> -> ResizeArray<int> -> unit) =
        let current = Dictionary<int, uint64 option>()
        let delivered = ResizeArray<int>()

        Native.Posix.processGroupAliveForTests <- Some(fun _ -> true)

        Native.Posix.readProcessIdentityForTests <-
            Some(fun pid ->
                match current.TryGetValue pid with
                | true, token -> token
                | false, _ -> None)

        Native.Posix.groupDeliveryObserverForTests <- Some(fun pid -> delivered.Add pid)

        try
            body current delivered
        finally
            Native.Posix.processGroupAliveForTests <- None
            Native.Posix.readProcessIdentityForTests <- None
            Native.Posix.groupDeliveryObserverForTests <- None

    // Drive a `ProcessGroupBackend` through the PRE-`setsid()` pty window deterministically. The GROUP
    // probe answers ESRCH for every id — no process group carries that number yet, which is exactly what
    // `killpg(pid, 0)` reports between `posix_spawn` returning and the `setsid --ctty` helper's own
    // `setsid()` — while the exact-pid probe (`live`) and the identity reader (`current`) are driven per
    // id by the test. `delivered` records every id a control path reached at all; `direct` records only
    // the ones reached by the exact-pid route, so "was the target reached" and "by which route" stay
    // separate questions. Seams are always reset in the `finally`.
    let runWithPreSetsidSeams
        (body: Dictionary<int, uint64 option> -> HashSet<int> -> ResizeArray<int> -> ResizeArray<int> -> unit)
        =
        let current = Dictionary<int, uint64 option>()
        let live = HashSet<int>()
        let delivered = ResizeArray<int>()
        let direct = ResizeArray<int>()

        Native.Posix.processGroupAliveForTests <- Some(fun _ -> false)
        Native.Posix.processAliveForTests <- Some(fun pid -> live.Contains pid)

        Native.Posix.readProcessIdentityForTests <-
            Some(fun pid ->
                match current.TryGetValue pid with
                | true, token -> token
                | false, _ -> None)

        Native.Posix.groupDeliveryObserverForTests <- Some(fun pid -> delivered.Add pid)
        Native.Posix.leaderPidDeliveryObserverForTests <- Some(fun pid -> direct.Add pid)

        try
            body current live delivered direct
        finally
            Native.Posix.processGroupAliveForTests <- None
            Native.Posix.processAliveForTests <- None
            Native.Posix.readProcessIdentityForTests <- None
            Native.Posix.groupDeliveryObserverForTests <- None
            Native.Posix.leaderPidDeliveryObserverForTests <- None

    // Drive the BARE-PID adoption paths (`AdoptByPid`) deterministically: the same identity/liveness/
    // delivery-observer seams as `runWithReuseSeams`, plus the one an adopted FOREIGN pid additionally
    // needs — the exact-pid `kill` that path runs for its signalability gate (`kill(pid, 0)`) and for
    // every delivery.
    //
    // Without that last seam a synthetic number is not merely imprecise, it is untestable: its real
    // `kill` always answers `ESRCH`, which refuses the adoption outright at the gate and — for an entry
    // that did get in — makes the ledger legitimately PRUNE it on its first delivery, so nothing after
    // that first call is exercised at all. `EPERM` (another user's process) cannot be produced on a build
    // host by any pid the test is allowed to signal, either. `outcomes` maps a pid to what its exact-pid
    // `kill` reports; an unmapped pid answers `TargetGone`, the honest "nothing is at this number".
    // Seams are always reset in the `finally`.
    let runWithAdoptionSeams
        (body:
            Dictionary<int, uint64 option> -> Dictionary<int, Native.Common.SignalDelivery> -> ResizeArray<int> -> unit)
        =
        let current = Dictionary<int, uint64 option>()
        let outcomes = Dictionary<int, Native.Common.SignalDelivery>()
        let delivered = ResizeArray<int>()

        Native.Posix.processGroupAliveForTests <- Some(fun _ -> true)

        Native.Posix.readProcessIdentityForTests <-
            Some(fun pid ->
                match current.TryGetValue pid with
                | true, token -> token
                | false, _ -> None)

        Native.Posix.groupDeliveryObserverForTests <- Some(fun pid -> delivered.Add pid)

        Native.Posix.adoptedPidKillForTests <-
            Some(fun pid _signalNum ->
                match outcomes.TryGetValue pid with
                | true, outcome -> outcome
                | false, _ -> Native.Common.SignalDelivery.TargetGone)

        try
            body current outcomes delivered
        finally
            Native.Posix.processGroupAliveForTests <- None
            Native.Posix.readProcessIdentityForTests <- None
            Native.Posix.groupDeliveryObserverForTests <- None
            Native.Posix.adoptedPidKillForTests <- None

    // Adopt `pid` into `backend` and fail the test if the mechanism refuses — a precondition, never the
    // assertion under test, so a refusal reads as "the setup did not hold" rather than as a control-path
    // result.
    let adoptOrFail (backend: IContainmentBackend) (pid: int) =
        match backend.AdoptByPid pid with
        | Ok() -> ()
        | Error error -> Assert.Fail $"AdoptByPid {pid} failed: {error.Message}"

    // How many deliveries an observer recorded for `id`. Both observers fire once per native delivery
    // ATTEMPT, so the count is what tells a single delivery apart from the exact-leader hard kill's two
    // halves (the pid itself, then the `killpg` sweep behind it).
    let deliveries (observed: ResizeArray<int>) (id: int) : int =
        observed |> Seq.filter ((=) id) |> Seq.length

    [<Test>]
    member _.``liveness seam keeps permission-denied groups tracked and prunes missing groups``() =
        if isWindows then
            Assert.Ignore "the POSIX process-group liveness probe is POSIX-only"

        // The hook keeps its existing bool signature: true models killpg(..., 0) failing with EPERM
        // (the group exists but is not signalable), while false models ESRCH (the group is gone).
        let permissionDeniedPgid = 2_000_000_201
        let missingPgid = 2_000_000_202

        Native.Posix.processGroupAliveForTests <- Some(fun pgid -> pgid = permissionDeniedPgid)

        try
            Assert.That(
                Native.Posix.processGroupStillTracked permissionDeniedPgid None,
                Is.True,
                "EPERM must keep an existing process group tracked"
            )

            Assert.That(
                Native.Posix.processGroupStillTracked missingPgid None,
                Is.False,
                "ESRCH must prune a missing process group"
            )
        finally
            Native.Posix.processGroupAliveForTests <- None

    // ---- Etape 1: the /proc/<pid>/stat field-22 parser ----

    [<Test>]
    member _.``parseLinuxStartTime reads field 22 past a comm holding spaces and parens``() =
        // /proc/<pid>/stat is "pid (comm) state ppid ... starttime(22) ...". `comm` can hold spaces and
        // ')', so parsing must start after the LAST ')'. Here comm = "weird ) proc" and starttime is the
        // marker 8675309 — proving the parser is not fooled by the embedded ')'/spaces.
        let afterComm = "S 1 100 100 0 -1 4194304 100 0 0 0 5 5 0 0 20 0 1 0 8675309 123456"
        let statLine = $"4242 (weird ) proc) {afterComm}"

        match Native.Posix.parseLinuxStartTime statLine with
        | Some value -> Assert.That(value, Is.EqualTo 8675309UL)
        | None -> Assert.Fail "expected the starttime field to parse"

    [<Test>]
    member _.``parseLinuxStartTime reads a plain single-word comm``() =
        let statLine =
            "1234 (bash) S 1 1234 1234 0 -1 4194304 500 0 0 0 10 5 0 0 20 0 1 0 5551212 999"

        match Native.Posix.parseLinuxStartTime statLine with
        | Some value -> Assert.That(value, Is.EqualTo 5551212UL)
        | None -> Assert.Fail "expected the starttime field to parse"

    [<Test>]
    member _.``parseLinuxStartTime returns None for a malformed or truncated stat line``() =
        // No ')' at all, and a well-formed prefix that is too short to reach field 22 — both are
        // unreadable identities, so the choke defers to the by-number liveness verdict.
        Assert.That(Native.Posix.parseLinuxStartTime("not a stat line at all").IsNone, Is.True)
        Assert.That(Native.Posix.parseLinuxStartTime("123 (short) S 1 2 3").IsNone, Is.True)
        // Field 22 present but non-numeric.
        let bad = "1 (x) S 1 1 1 0 -1 0 0 0 0 0 0 0 0 0 20 0 1 0 notanumber 0"
        Assert.That(Native.Posix.parseLinuxStartTime(bad).IsNone, Is.True)

    [<Test>]
    member _.``readProcessIdentity yields a stable token for a live process (Some on Linux and macOS)``() =
        if isWindows then
            Assert.Ignore "start-time identity is read on POSIX only (Linux /proc, macOS proc_pidinfo)"

        // Our own (definitely live) process must yield a token that is identical across two reads. On a
        // POSIX with a reader (Linux/macOS) it is `Some`; on any other POSIX it may be `None` (no reader)
        // — still stable, and the choke degrades cleanly.
        let first = Native.Posix.readProcessIdentity (Environment.ProcessId)
        let second = Native.Posix.readProcessIdentity (Environment.ProcessId)

        match first, second with
        | Some a, Some b -> Assert.That(b, Is.EqualTo a, "the start-time token must be stable across reads")
        | None, None -> ()
        | _ -> Assert.Fail "the start-time token was not stable across reads"

        if isLinux || isMac then
            Assert.That(first.IsSome, Is.True, "a live process must yield a readable start-time token on Linux/macOS")

    // ---- Etapes 2-4: the reuse gate on the process-group backend ----

    [<Test>]
    member _.``a recycled pgid is pruned and never signalled while a matching one still is (group)``() =
        if isWindows then
            Assert.Ignore "the POSIX process-group backend and its identity gate are POSIX-only"

        runWithReuseSeams (fun current delivered ->
            // Two tracked pgids in one group. Both capture a known, distinct identity at track time.
            let pgidKept = 2_000_000_001
            let pgidRecycled = 2_000_000_002
            current[pgidKept] <- Some 111UL
            current[pgidRecycled] <- Some 222UL

            let backend = ProcessGroupBackend() :> IContainmentBackend
            backend.Track(spawnedFor pgidKept) |> ignore
            backend.Track(spawnedFor pgidRecycled) |> ignore

            // Recycle the second pgid number: still "alive", but now a DIFFERENT process (its current
            // identity changed). The first keeps its captured identity.
            current[pgidRecycled] <- Some 999UL

            match backend.Signal Signal.Term with
            | Ok() -> ()
            | Error e -> Assert.Fail $"Signal failed: {e.Message}"

            Assert.That(delivered, Does.Contain pgidKept, "the matching-identity pgid must still be signalled")

            Assert.That(
                delivered,
                Does.Not.Contain pgidRecycled,
                "the recycled pgid must never be signalled (wrong-target kill)"
            )

            // The recycled pgid is pruned from tracking; the matching one remains a member.
            match backend.Members() with
            | Ok members ->
                Assert.That(members, Does.Contain pgidKept, "the matching-identity pgid must remain tracked")
                Assert.That(members, Does.Not.Contain pgidRecycled, "the recycled pgid must be pruned from tracking")
            | Error e -> Assert.Fail $"Members failed: {e.Message}")

    [<Test>]
    member _.``a recycled pgid is never suspended while a matching one still is``() =
        if isWindows then
            Assert.Ignore "the POSIX process-group backend and its identity gate are POSIX-only"

        runWithReuseSeams (fun current delivered ->
            let pgidKept = 2_000_000_003
            let pgidRecycled = 2_000_000_004
            current[pgidKept] <- Some 333UL
            current[pgidRecycled] <- Some 444UL

            let backend = ProcessGroupBackend() :> IContainmentBackend
            backend.Track(spawnedFor pgidKept) |> ignore
            backend.Track(spawnedFor pgidRecycled) |> ignore
            current[pgidRecycled] <- Some 555UL

            // The synthetic but matching pgid reaches the real killpg and returns ESRCH. `Suspend`
            // must classify that concurrent exit as a best-effort success while still pruning the
            // recycled pgid without attempting delivery.
            match backend.Suspend() with
            | Ok() -> ()
            | Error e -> Assert.Fail $"Suspend failed: {e.Message}"

            match backend.Resume() with
            | Ok() -> ()
            | Error e -> Assert.Fail $"Resume failed: {e.Message}"

            Assert.That(delivered, Does.Contain pgidKept, "the matching-identity pgid must still be suspended")
            Assert.That(delivered, Does.Not.Contain pgidRecycled, "a recycled pgid must never be suspended"))

    [<Test>]
    member _.``a recycled pid is never hard-killed while a matching one still is (solo child)``() =
        if isWindows then
            Assert.Ignore "the POSIX process-group backend and its identity gate are POSIX-only"

        runWithReuseSeams (fun current delivered ->
            let pgidKept = 2_000_000_005
            let pgidRecycled = 2_000_000_006
            current[pgidKept] <- Some 500UL
            current[pgidRecycled] <- Some 600UL

            let backend = ProcessGroupBackend() :> IContainmentBackend
            backend.Track(spawnedFor pgidKept) |> ignore
            backend.Track(spawnedFor pgidRecycled) |> ignore

            // Recycle the solo pid number.
            current[pgidRecycled] <- Some 700UL

            backend.KillChild(spawnedFor pgidRecycled) // must NOT killpg — the number is a stranger now
            backend.KillChild(spawnedFor pgidKept) // must killpg — still ours

            Assert.That(delivered, Does.Contain pgidKept, "a matching solo pgid must still be hard-killed")

            Assert.That(
                delivered,
                Does.Not.Contain pgidRecycled,
                "a recycled solo pid must never be hard-killed (wrong-target kill)"
            ))

    [<Test>]
    member _.``teardown never SIGKILLs a recycled pgid but still reaps and SIGKILLs a matching one``() =
        if isWindows then
            Assert.Ignore "the POSIX process-group backend and its identity gate are POSIX-only"

        runWithReuseSeams (fun current delivered ->
            let pgidKept = 2_000_000_007
            let pgidRecycled = 2_000_000_008
            current[pgidKept] <- Some 800UL
            current[pgidRecycled] <- Some 900UL

            let backend = ProcessGroupBackend() :> IContainmentBackend
            backend.Track(spawnedFor pgidKept) |> ignore
            backend.Track(spawnedFor pgidRecycled) |> ignore

            // Recycle one pgid number before the one-shot teardown drain.
            current[pgidRecycled] <- Some 1000UL

            backend.HardRelease()

            Assert.That(delivered, Does.Contain pgidKept, "teardown must still SIGKILL a matching pgid")

            Assert.That(
                delivered,
                Does.Not.Contain pgidRecycled,
                "teardown must never SIGKILL a pgid recycled since it was tracked"
            ))

    // ---- non-regression: coverage is never lost when the identity is unreadable or matching ----

    [<Test>]
    member _.``an unreadable identity token degrades to the by-number liveness verdict (non-regression)``() =
        if isWindows then
            Assert.Ignore "the POSIX process-group backend and its identity gate are POSIX-only"

        runWithReuseSeams (fun _current delivered ->
            // No identity reader wired up (the token is `None` everywhere — a BSD-like platform). The
            // pgid must still be signalled purely on its by-number liveness, so no platform loses cover.
            let pgid = 2_000_000_009

            let backend = ProcessGroupBackend() :> IContainmentBackend
            backend.Track(spawnedFor pgid) |> ignore

            match backend.Signal Signal.Term with
            | Ok() -> ()
            | Error e -> Assert.Fail $"Signal failed: {e.Message}"

            Assert.That(
                delivered,
                Does.Contain pgid,
                "a pgid with no readable identity must still be signalled by number"
            )

            match backend.Members() with
            | Ok members -> Assert.That(members, Does.Contain pgid, "it must remain tracked")
            | Error e -> Assert.Fail $"Members failed: {e.Message}")

    [<Test>]
    member _.``a group whose leader was reaped but whose descendants hold the pgid is still signalled``() =
        if isWindows then
            Assert.Ignore "the POSIX process-group backend and its identity gate are POSIX-only"

        runWithReuseSeams (fun current delivered ->
            // A real token is captured at track; the leader is later reaped so its identity read now
            // returns `None` (no /proc entry), but descendants keep the pgid alive by number. The choke
            // must DEFER to the liveness verdict — the group is still ours and must still be signalled,
            // not pruned as if recycled.
            let pgid = 2_000_000_030
            current[pgid] <- Some 4242UL

            let backend = ProcessGroupBackend() :> IContainmentBackend
            backend.Track(spawnedFor pgid) |> ignore

            // Leader reaped: its identity is now unreadable, but the pgid stays alive (descendants).
            current[pgid] <- None

            match backend.Signal Signal.Term with
            | Ok() -> ()
            | Error e -> Assert.Fail $"Signal failed: {e.Message}"

            Assert.That(
                delivered,
                Does.Contain pgid,
                "a leader-reaped-but-descendants-hold pgid must still be signalled, not pruned as recycled"
            ))

    // ---- the cgroup backend's per-child kill now funnels through the same identity choke ----

    [<Test>]
    member _.``the cgroup backend's KillChild never SIGKILLs a recycled pid but still kills a matching one``() =
        if isWindows then
            Assert.Ignore "the cgroup backend is Linux-only; its KillChild identity gate is POSIX-only"

        // `CgroupBackend.KillChild` used to be a raw `kill(pid, SIGKILL)` with no identity gate — after a
        // reap the pid could be recycled and the SIGKILL land on a stranger (the T-084 hole). It now
        // funnels through the same start-time-identity choke (`processGroupStillTracked`) as the POSIX
        // backend. Drive it against the deterministic identity/liveness/delivery seams. `Track` writes the
        // pid into `cgroup.procs`, so back it with a writable temp file (a plain file the libc open/write
        // accept) — the migrate confirmation then returns `Ok` and the identity token is captured.
        let cgroupDir =
            Path.Combine(Path.GetTempPath(), $"pk-cgroup-killchild-{Guid.NewGuid():N}")

        Directory.CreateDirectory cgroupDir |> ignore
        File.WriteAllText(Path.Combine(cgroupDir, "cgroup.procs"), "")

        try
            runWithReuseSeams (fun current delivered ->
                let pidKept = 2_000_000_101
                let pidRecycled = 2_000_000_102
                current[pidKept] <- Some 1_100UL
                current[pidRecycled] <- Some 1_200UL

                let backend = CgroupBackend cgroupDir :> IContainmentBackend

                match backend.Track(spawnedFor pidKept) with
                | Ok() -> ()
                | Error e -> Assert.Fail $"Track(kept) failed: {e.Message}"

                match backend.Track(spawnedFor pidRecycled) with
                | Ok() -> ()
                | Error e -> Assert.Fail $"Track(recycled) failed: {e.Message}"

                // Recycle the second pid number: still "alive" by number, but now a DIFFERENT process (its
                // current identity changed). The first keeps its captured identity.
                current[pidRecycled] <- Some 9_999UL

                backend.KillChild(spawnedFor pidRecycled) // a stranger now — must NOT be SIGKILLed
                backend.KillChild(spawnedFor pidKept) // still ours — must be SIGKILLed

                Assert.That(delivered, Does.Contain pidKept, "a matching cgroup child must still be hard-killed")

                Assert.That(
                    delivered,
                    Does.Not.Contain pidRecycled,
                    "a recycled cgroup pid must never be hard-killed (wrong-target kill)"
                ))
        finally
            try
                Directory.Delete(cgroupDir, true)
            with _ ->
                // best-effort temp cleanup; a leftover temp dir must not fail the test.
                ()

    // ---- T-371: bare-pid adoption (`ProcessGroup.AdoptByPid`) on the process-group mechanism ----
    //
    // A foreign process adopted by NUMBER is tracked against the start-time anchor captured when it was
    // adopted, and that anchor is re-read before every probe, signal, suspend/resume and teardown kill —
    // never once at adoption and then trusted. Three things separate it from a tracked child of ours: the
    // delivery is always to the EXACT pid (its process group belongs to whoever started it, so `killpg`
    // there would sweep strangers), an unreadable identity is fail-CLOSED (an adopted number has no
    // unreaped-child slot holding it, so a bare number is never evidence of anything), and the adoption
    // itself is refused unless this process can actually SIGNAL the target — an anchor proves identity,
    // never control.
    //
    // They run on `runWithAdoptionSeams`, which additionally pins the verdict of that exact-pid `kill`.
    // A synthetic number's real `kill` answers ESRCH, which is a legitimate "gone" here: it refuses the
    // adoption at the gate, and for an entry already in the ledger it prunes on the first delivery — so
    // with the real syscall in the loop nothing past that first call is exercised at all, and the
    // permission verdict cannot be produced on a build host by any pid a test may signal.

    [<Test>]
    member _.``an adopted pid is listed, signalled and killed with the group while its anchor matches``() =
        if isWindows then
            Assert.Ignore "the POSIX process-group backend and its identity gate are POSIX-only"

        runWithAdoptionSeams (fun current outcomes delivered ->
            let adoptedPid = 2_000_000_401
            current[adoptedPid] <- Some 4_010UL
            // A live, signalable target: its exact-pid kill passes the adoption gate AND reports a real
            // delivery, so the ledger entry survives each operation exactly as a live process's would.
            // The next test drives the opposite verdict deliberately.
            outcomes[adoptedPid] <- Native.Common.SignalDelivery.Delivered

            let backend = ProcessGroupBackend() :> IContainmentBackend
            adoptOrFail backend adoptedPid

            match backend.Members() with
            | Ok members -> Assert.That(members, Does.Contain adoptedPid, "an adopted pid is a member of the group")
            | Error e -> Assert.Fail $"Members failed: {e.Message}"

            match backend.Stats() with
            | Ok stats -> Assert.That(stats.ActiveProcessCount, Is.EqualTo 1, "and it counts as an active member")
            | Error e -> Assert.Fail $"Stats failed: {e.Message}"

            match backend.Signal Signal.Term with
            | Ok() -> ()
            | Error e -> Assert.Fail $"Signal failed: {e.Message}"

            Assert.That(delivered, Does.Contain adoptedPid, "the adopted process must receive the group's signal")

            match backend.Members() with
            | Ok members -> Assert.That(members, Does.Contain adoptedPid, "a delivered signal leaves it in the group")
            | Error e -> Assert.Fail $"Members failed: {e.Message}"

            delivered.Clear()

            match backend.Suspend() with
            | Ok() -> ()
            | Error e -> Assert.Fail $"Suspend failed: {e.Message}"

            match backend.Resume() with
            | Ok() -> ()
            | Error e -> Assert.Fail $"Resume failed: {e.Message}"

            Assert.That(deliveries delivered adoptedPid, Is.EqualTo 2, "suspend and resume both reach it")

            delivered.Clear()
            backend.KillTree() |> ignore
            Assert.That(delivered, Does.Contain adoptedPid, "the whole-tree hard kill reaches it")

            delivered.Clear()
            backend.HardRelease()

            Assert.That(delivered, Does.Contain adoptedPid, "and so does teardown")

            match backend.Members() with
            | Ok members -> Assert.That(members, Is.Empty, "teardown drains the adopted ledger too")
            | Error e -> Assert.Fail $"Members failed: {e.Message}")

    [<Test>]
    member _.``an adopted pid that exits before a delivery lands is dropped from the group, not retried``() =
        if isWindows then
            Assert.Ignore "the POSIX process-group backend and its identity gate are POSIX-only"

        // The other half of the ledger's contract, and the reason the test above has to pin a DELIVERED
        // verdict explicitly. An adopted foreign process is nobody's tracked child, so an `ESRCH` from its
        // exact-pid kill proves nothing of ours is at that number any more: the entry is pruned on that
        // same verdict — no second probe to disagree with it — and no later operation revisits the number.
        // A tracked pgid deliberately does the opposite (its sweep ignores `TargetGone`), because a group
        // that answers ESRCH may still be a live pre-`setsid()` child of ours; nothing here can be.
        runWithAdoptionSeams (fun current outcomes delivered ->
            let adoptedPid = 2_000_000_408
            current[adoptedPid] <- Some 4_080UL
            outcomes[adoptedPid] <- Native.Common.SignalDelivery.Delivered

            let backend = ProcessGroupBackend() :> IContainmentBackend
            adoptOrFail backend adoptedPid

            // It exits between the anchor re-read and the kill: the anchor still matched, so the delivery
            // was attempted, and the kernel answers that no such process exists.
            outcomes[adoptedPid] <- Native.Common.SignalDelivery.TargetGone

            match backend.Signal Signal.Term with
            | Ok() -> ()
            | Error e -> Assert.Fail $"Signal failed: {e.Message}"

            Assert.That(deliveries delivered adoptedPid, Is.EqualTo 1, "the delivery was attempted exactly once")

            match backend.Members() with
            | Ok members -> Assert.That(members, Is.Empty, "and that same verdict pruned the entry")
            | Error e -> Assert.Fail $"Members failed: {e.Message}"

            match backend.Suspend() with
            | Ok() -> ()
            | Error e -> Assert.Fail $"Suspend failed: {e.Message}"

            backend.KillTree() |> ignore
            backend.HardRelease()

            Assert.That(
                deliveries delivered adoptedPid,
                Is.EqualTo 1,
                "nothing revisits a number the group has already dropped"
            ))

    [<Test>]
    member _.``a pid recycled after it was adopted by number is never signalled, suspended or killed``() =
        if isWindows then
            Assert.Ignore "the POSIX process-group backend and its identity gate are POSIX-only"

        // The regression this feature could most easily introduce: adopting by NUMBER and then acting on
        // the number. Here the adoption captures a real anchor and the number then changes hands — every
        // later path must treat it as gone, exactly as a recycled tracked pgid is treated.
        runWithAdoptionSeams (fun current outcomes delivered ->
            let kept = 2_000_000_402
            let recycled = 2_000_000_403
            current[kept] <- Some 4_020UL
            current[recycled] <- Some 4_030UL
            outcomes[kept] <- Native.Common.SignalDelivery.Delivered
            outcomes[recycled] <- Native.Common.SignalDelivery.Delivered

            let backend = ProcessGroupBackend() :> IContainmentBackend
            adoptOrFail backend kept
            adoptOrFail backend recycled

            // A stranger now holds the second number (its start-time token differs from the captured one).
            // Its exact-pid kill would still SUCCEED — which is precisely the danger: nothing but the
            // anchor stands between this group's teardown and a process it never adopted.
            current[recycled] <- Some 9_999UL

            match backend.Members() with
            | Ok members ->
                Assert.That(members, Does.Contain kept, "the matching-anchor pid is still a member")
                Assert.That(members, Does.Not.Contain recycled, "a recycled number is not a member of this group")
            | Error e -> Assert.Fail $"Members failed: {e.Message}"

            match backend.Signal Signal.Term with
            | Ok() -> ()
            | Error e -> Assert.Fail $"Signal failed: {e.Message}"

            match backend.Suspend() with
            | Ok() -> ()
            | Error e -> Assert.Fail $"Suspend failed: {e.Message}"

            backend.KillTree() |> ignore
            backend.HardRelease()

            Assert.That(delivered, Does.Contain kept, "the adopted process whose anchor still matches is reached")

            Assert.That(
                delivered,
                Does.Not.Contain recycled,
                "a number recycled since it was adopted must never be signalled, suspended or SIGKILLed"
            ))

    [<Test>]
    member _.``an adopted pid whose identity becomes unreadable receives nothing (fail-closed)``() =
        if isWindows then
            Assert.Ignore "the POSIX process-group backend and its identity gate are POSIX-only"

        // Deliberately STRICTER than the tracked-pgid choke, and the asymmetry is the point. A tracked
        // pgid whose token cannot be read falls back to the by-number liveness verdict, because an
        // unreaped child of ours holds its number. An adopted foreign process is not our child: nothing
        // holds its number, so with no readable identity there is no evidence left and nothing is
        // delivered — losing containment of a live process rather than risking a stranger's kill.
        runWithAdoptionSeams (fun current outcomes delivered ->
            let adoptedPid = 2_000_000_404
            current[adoptedPid] <- Some 4_040UL
            // The kill itself would succeed throughout: the anchor alone is what stops the delivery.
            outcomes[adoptedPid] <- Native.Common.SignalDelivery.Delivered

            let backend = ProcessGroupBackend() :> IContainmentBackend
            adoptOrFail backend adoptedPid

            // The identity read now fails (a hidepid /proc, a denied read, or the process is gone).
            current[adoptedPid] <- None

            match backend.Signal Signal.Term with
            | Ok() -> ()
            | Error e -> Assert.Fail $"Signal failed: {e.Message}"

            backend.KillTree() |> ignore
            backend.HardRelease()

            Assert.That(delivered, Is.Empty, "with no readable identity there is no evidence, so nothing is delivered")

            match backend.Members() with
            | Ok members -> Assert.That(members, Is.Empty, "and it is no longer reported as a member")
            | Error e -> Assert.Fail $"Members failed: {e.Message}")

    [<Test>]
    member _.``a graceful stop reaches an adopted pid with both its soft signal and its escalation``() =
        if isWindows then
            Assert.Ignore "the POSIX process-group backend and its identity gate are POSIX-only"

        // The off-lock poll works from a snapshot of the adopted ledger (K-086) and re-reads the anchor in
        // every phase, so both the soft signal and the escalation must land — deterministically, because
        // the seams keep the process "alive" for the whole grace.
        runWithAdoptionSeams (fun current outcomes delivered ->
            let adoptedPid = 2_000_000_405
            current[adoptedPid] <- Some 4_050UL
            outcomes[adoptedPid] <- Native.Common.SignalDelivery.Delivered

            let backend = ProcessGroupBackend() :> IContainmentBackend
            adoptOrFail backend adoptedPid

            let graceful =
                backend.GracefulKillTree Signal.Term (TimeSpan.FromMilliseconds 150.0)

            Assert.That(graceful.Wait(TimeSpan.FromSeconds 30.0), Is.True, "the graceful stop must finish")

            Assert.That(
                deliveries delivered adoptedPid,
                Is.EqualTo 2,
                "an adopted process gets the soft signal and then the SIGKILL escalation"
            ))

    [<Test>]
    member _.``a pid with no readable identity is refused rather than adopted by number``() =
        if isWindows then
            Assert.Ignore "the POSIX process-group backend and its identity gate are POSIX-only"

        runWithAdoptionSeams (fun _current _outcomes delivered ->
            // The reader exists on this host (the seam is installed) but answers `None` for this pid: it
            // is already gone, or its /proc entry is unreadable. A per-process refusal, not a platform one
            // — and it is reached BEFORE the signalability probe, so an unreadable identity is reported as
            // such rather than as whatever `kill(pid, 0)` would have said about the same number.
            let unreadable = 2_000_000_406

            let backend = ProcessGroupBackend() :> IContainmentBackend

            match backend.AdoptByPid unreadable with
            | Error(ProcessError.Adopt(pid, detail)) ->
                Assert.That(pid, Is.EqualTo unreadable)
                Assert.That(detail, Does.Contain "identity")
            | other -> Assert.Fail $"expected a typed Adopt failure for an unreadable identity, got {other}"

            match backend.Members() with
            | Ok members -> Assert.That(members, Is.Empty, "a refused adoption tracks nothing")
            | Error e -> Assert.Fail $"Members failed: {e.Message}"

            backend.HardRelease()
            Assert.That(delivered, Is.Empty, "and teardown has nothing to deliver to"))

    [<Test>]
    member _.``a pid this process may not signal is refused rather than adopted (permission denied)``() =
        if isWindows then
            Assert.Ignore "the POSIX process-group backend and its identity gate are POSIX-only"

        // Reading the anchor proves only that the process can be IDENTIFIED. On Linux `/proc/<pid>/stat`
        // is world-readable, so another user's (or a system) process yields a perfectly good anchor while
        // every `kill` to it fails EPERM — and the teardown kill is fire-and-forget, so accepting such a
        // pid would report containment of a process this group can neither signal nor SIGKILL: a silent
        // downgrade of the kill-on-dispose guarantee, and the one case where this mechanism used to differ
        // from the Windows (denied `OpenProcess`) and cgroup v2 (denied `cgroup.procs` write) refusals.
        // The verdict comes from the seam because no build host can be asked to point a test at a real
        // process it must never actually signal.
        runWithAdoptionSeams (fun current outcomes delivered ->
            let foreign = 2_000_000_409
            current[foreign] <- Some 4_090UL

            outcomes[foreign] <-
                Native.Common.SignalDelivery.DeliveryFailed(Native.Posix.EPERM, "Operation not permitted")

            let backend = ProcessGroupBackend() :> IContainmentBackend

            match backend.AdoptByPid foreign with
            | Error(ProcessError.Adopt(pid, detail)) ->
                Assert.That(pid, Is.EqualTo foreign)
                Assert.That(detail, Does.Contain "may not signal", $"the refusal must name the cause; got: {detail}")
            | other -> Assert.Fail $"expected a typed Adopt refusal for a pid this process may not signal, got {other}"

            match backend.Members() with
            | Ok members -> Assert.That(members, Is.Empty, "a refused adoption tracks nothing")
            | Error e -> Assert.Fail $"Members failed: {e.Message}"

            // The neighbouring verdict is not collapsed into it: a readable identity whose number nothing
            // lives at any more is refused too, and says something different about why.
            let vanished = 2_000_000_410
            current[vanished] <- Some 4_100UL

            match backend.AdoptByPid vanished with
            | Error(ProcessError.Adopt(_, detail)) ->
                Assert.That(detail, Does.Contain "nothing is at this pid", $"got: {detail}")
            | other -> Assert.Fail $"expected a typed Adopt refusal for a pid that names nothing, got {other}"

            backend.HardRelease()
            Assert.That(delivered, Is.Empty, "and neither refusal left anything in the group to deliver to"))

    [<Test>]
    member _.``a platform with no start-time reader refuses bare-pid adoption as Unsupported``() =
        if isWindows then
            Assert.Ignore "the POSIX process-group backend and its identity gate are POSIX-only"

        // The BSD branch, driven from any POSIX build host: with no reader there is no anchor to take for
        // ANY pid, so the refusal is the whole-platform `Unsupported` rather than a per-process `Adopt`
        // failure — and never a downgrade to tracking the bare number.
        Native.Posix.processIdentityReaderAvailableForTests <- Some(fun () -> false)

        try
            let backend = ProcessGroupBackend() :> IContainmentBackend

            match backend.AdoptByPid 2_000_000_407 with
            | Error(ProcessError.Unsupported detail) -> Assert.That(detail, Does.Contain "start-time identity anchor")
            | other -> Assert.Fail $"expected Unsupported on a host with no identity reader, got {other}"

            match backend.Members() with
            | Ok members -> Assert.That(members, Is.Empty, "a refused adoption must track nothing")
            | Error e -> Assert.Fail $"Members failed: {e.Message}"
        finally
            Native.Posix.processIdentityReaderAvailableForTests <- None

    // ---- T-359: the pre-`setsid()` pty window — a live leader whose process GROUP does not exist yet ----
    //
    // A `Command.Pty` child is spawned with neither `POSIX_SPAWN_SETPGROUP` nor `POSIX_SPAWN_SETSID`,
    // because its `setsid --ctty` helper must call `setsid()` itself after `exec`. Until that call lands,
    // the child is alive but is NOT the leader of a group whose number equals its pid, so `killpg(pid, 0)`
    // answers ESRCH. Reading that as "the target is gone" dropped a live child out of the container and
    // skipped its kill/signal entirely; the choke now probes the exact pid too and, while that pid is
    // still our identity-matched child, keeps the record AND delivers straight to it.

    [<Test>]
    member _.``a group ESRCH over a live identity-matched pid is a leader-pid target, not a gone one``() =
        if isWindows then
            Assert.Ignore "the POSIX liveness + identity choke is POSIX-only"

        runWithPreSetsidSeams (fun current live _delivered _direct ->
            let pid = 2_000_000_301
            current[pid] <- Some 7_001UL
            live.Add pid |> ignore

            Assert.That(
                Native.Posix.trackedTarget pid (Some 7_001UL),
                Is.EqualTo Native.Posix.TrackedTarget.LeaderPid,
                "a live, identity-matched child whose group does not exist yet must route to its exact pid"
            )

            Assert.That(
                Native.Posix.processGroupStillTracked pid (Some 7_001UL),
                Is.True,
                "a group ESRCH alone must not drop a live child of ours from tracking"
            ))

    [<Test>]
    member _.``a target gone by both the group and the pid probe is gone``() =
        if isWindows then
            Assert.Ignore "the POSIX liveness + identity choke is POSIX-only"

        runWithPreSetsidSeams (fun current _live _delivered _direct ->
            // The pid is absent from `live`, so the exact-pid probe answers ESRCH as well.
            let pid = 2_000_000_302
            current[pid] <- Some 7_002UL

            Assert.That(
                Native.Posix.trackedTarget pid (Some 7_002UL),
                Is.EqualTo Native.Posix.TrackedTarget.Gone,
                "ESRCH from BOTH probes is the only verdict that means the target really left"
            )

            Assert.That(Native.Posix.processGroupStillTracked pid (Some 7_002UL), Is.False))

    [<Test>]
    member _.``a live pid whose identity does not match the captured one is gone, never a leader-pid target``() =
        if isWindows then
            Assert.Ignore "the POSIX liveness + identity choke is POSIX-only"

        runWithPreSetsidSeams (fun current live _delivered _direct ->
            // A stranger recycled the number after our leader was reaped: the pid probes alive, but its
            // start-time token is not the one captured at track time. The exact-pid route must never
            // deliver here — that would be the wrong-target kill T-084 closes.
            let recycled = 2_000_000_303
            current[recycled] <- Some 9_999UL
            live.Add recycled |> ignore

            Assert.That(
                Native.Posix.trackedTarget recycled (Some 7_003UL),
                Is.EqualTo Native.Posix.TrackedTarget.Gone,
                "a recycled number must never become an exact-pid delivery target"
            )

            // The same strictness for an identity that cannot be read on either side: a bare pid NUMBER
            // is never enough evidence to signal, so the exact-pid route is simply not taken (the
            // previous behaviour — nothing delivered, the record pruned — is kept).
            let unreadable = 2_000_000_304
            live.Add unreadable |> ignore

            Assert.That(
                Native.Posix.trackedTarget unreadable (Some 7_004UL),
                Is.EqualTo Native.Posix.TrackedTarget.Gone,
                "an unreadable current identity is not a match, so the exact-pid route is not taken"
            )

            let noCapturedToken = 2_000_000_305
            current[noCapturedToken] <- Some 7_005UL
            live.Add noCapturedToken |> ignore

            Assert.That(
                Native.Posix.trackedTarget noCapturedToken None,
                Is.EqualTo Native.Posix.TrackedTarget.Gone,
                "with no captured token there is nothing to match the live pid against"
            ))

    [<Test>]
    member _.``a signal in the pre-setsid window is delivered to the exact pid and keeps the child tracked``() =
        if isWindows then
            Assert.Ignore "the POSIX process-group backend and its identity gate are POSIX-only"

        runWithPreSetsidSeams (fun current live delivered direct ->
            let pid = 2_000_000_310
            current[pid] <- Some 7_100UL
            live.Add pid |> ignore

            let backend = ProcessGroupBackend() :> IContainmentBackend
            backend.Track(spawnedFor pid) |> ignore

            match backend.Signal Signal.Term with
            | Ok() -> ()
            | Error e -> Assert.Fail $"Signal failed: {e.Message}"

            Assert.That(delivered, Does.Contain pid, "the live child must actually receive the signal")

            Assert.That(
                direct,
                Does.Contain pid,
                "it must be delivered to the exact pid — killpg would reach nothing before setsid()"
            )

            match backend.Members() with
            | Ok members -> Assert.That(members, Does.Contain pid, "a group ESRCH alone must not untrack a live child")
            | Error e -> Assert.Fail $"Members failed: {e.Message}")

    [<Test>]
    member _.``a child gone by both probes receives nothing and is pruned from tracking``() =
        if isWindows then
            Assert.Ignore "the POSIX process-group backend and its identity gate are POSIX-only"

        runWithPreSetsidSeams (fun current live delivered _direct ->
            let gone = 2_000_000_311
            let alive = 2_000_000_312
            current[gone] <- Some 7_110UL
            current[alive] <- Some 7_111UL
            // Only the second pid answers the exact-pid probe; the first is gone by both.
            live.Add alive |> ignore

            let backend = ProcessGroupBackend() :> IContainmentBackend
            backend.Track(spawnedFor gone) |> ignore
            backend.Track(spawnedFor alive) |> ignore

            match backend.Signal Signal.Term with
            | Ok() -> ()
            | Error e -> Assert.Fail $"Signal failed: {e.Message}"

            Assert.That(delivered, Does.Not.Contain gone, "a target gone by both probes must receive nothing")
            Assert.That(delivered, Does.Contain alive)

            match backend.Members() with
            | Ok members ->
                Assert.That(members, Does.Not.Contain gone, "group ESRCH plus pid ESRCH must prune the record")
                Assert.That(members, Does.Contain alive, "the live child must stay tracked")
            | Error e -> Assert.Fail $"Members failed: {e.Message}")

    [<Test>]
    member _.``a recycled number is never signalled through the exact-pid route``() =
        if isWindows then
            Assert.Ignore "the POSIX process-group backend and its identity gate are POSIX-only"

        runWithPreSetsidSeams (fun current live delivered direct ->
            let recycled = 2_000_000_313
            current[recycled] <- Some 7_120UL
            live.Add recycled |> ignore

            let backend = ProcessGroupBackend() :> IContainmentBackend
            backend.Track(spawnedFor recycled) |> ignore

            // Our leader was reaped and a stranger took the number: it still probes alive by pid, but its
            // identity changed. Every control path must treat it as gone.
            current[recycled] <- Some 8_888UL

            match backend.Signal Signal.Term with
            | Ok() -> ()
            | Error e -> Assert.Fail $"Signal failed: {e.Message}"

            backend.KillChild(spawnedFor recycled)
            backend.KillTree() |> ignore

            Assert.That(delivered, Is.Empty, "a recycled number must never be signalled or killed")
            Assert.That(direct, Is.Empty, "least of all through the exact-pid route")

            match backend.Members() with
            | Ok members -> Assert.That(members, Does.Not.Contain recycled, "and it must be pruned from tracking")
            | Error e -> Assert.Fail $"Members failed: {e.Message}")

    [<Test>]
    member _.``every control path reaches a freshly spawned leader whose group does not exist yet``() =
        if isWindows then
            Assert.Ignore "the POSIX process-group backend and its identity gate are POSIX-only"

        // Kill, kill-tree, per-child signal, suspend/resume, and the teardown drain must ALL reach the
        // child in this window — an immediate `Kill`/`Stop`/`Dispose`/`Signal` right after a pty spawn
        // used to skip its delivery entirely and leave the child running.
        runWithPreSetsidSeams (fun current live delivered direct ->
            let pid = 2_000_000_320
            current[pid] <- Some 7_200UL
            live.Add pid |> ignore

            let backend = ProcessGroupBackend() :> IContainmentBackend
            backend.Track(spawnedFor pid) |> ignore

            backend.KillChild(spawnedFor pid)
            Assert.That(direct, Does.Contain pid, "KillChild must SIGKILL the exact pid")

            direct.Clear()
            backend.KillTree() |> ignore
            Assert.That(direct, Does.Contain pid, "KillTree must SIGKILL the exact pid")

            direct.Clear()

            match backend.SignalChild(spawnedFor pid, Signal.Term) with
            | Ok() -> ()
            | Error e -> Assert.Fail $"SignalChild failed: {e.Message}"

            Assert.That(direct, Does.Contain pid, "a per-child signal must reach the exact pid")

            direct.Clear()

            match backend.Suspend() with
            | Ok() -> ()
            | Error e -> Assert.Fail $"Suspend failed: {e.Message}"

            match backend.Resume() with
            | Ok() -> ()
            | Error e -> Assert.Fail $"Resume failed: {e.Message}"

            Assert.That(direct, Does.Contain pid, "Suspend/Resume must reach the exact pid")

            // `Release` is the reap-side bookkeeping: an empty GROUP is not proof the child is gone, so
            // the live child stays tracked and teardown still owns its kill.
            backend.Release(spawnedFor pid)

            match backend.Members() with
            | Ok members -> Assert.That(members, Does.Contain pid, "Release must not drop a live child")
            | Error e -> Assert.Fail $"Members failed: {e.Message}"

            direct.Clear()
            delivered.Clear()
            backend.HardRelease()

            Assert.That(direct, Does.Contain pid, "the teardown drain must SIGKILL the exact pid")

            match backend.Members() with
            | Ok members -> Assert.That(members, Is.Empty, "teardown drains the tracking set")
            | Error e -> Assert.Fail $"Members failed: {e.Message}")

    [<Test>]
    member _.``once the group exists the whole tree is signalled through it, not the leader pid``() =
        if isWindows then
            Assert.Ignore "the POSIX process-group backend and its identity gate are POSIX-only"

        // The post-`setsid()` regression: with a live process group, delivery goes to the GROUP exactly as
        // before, so a descendant the leader backgrounded is still reached. The exact-pid route is only
        // ever the fallback for the window in which no such group exists.
        runWithPreSetsidSeams (fun current live delivered direct ->
            let pid = 2_000_000_330
            current[pid] <- Some 7_300UL
            live.Add pid |> ignore

            let backend = ProcessGroupBackend() :> IContainmentBackend
            backend.Track(spawnedFor pid) |> ignore

            // The helper's `setsid()` has now run: the group exists (pgid == pid).
            Native.Posix.processGroupAliveForTests <- Some(fun _ -> true)

            match backend.Signal Signal.Term with
            | Ok() -> ()
            | Error e -> Assert.Fail $"Signal failed: {e.Message}"

            backend.KillTree() |> ignore

            Assert.That(delivered, Does.Contain pid, "the group must still receive the signal and the kill")

            Assert.That(
                direct,
                Is.Empty,
                "with a live group, delivery must go through killpg so the whole tree is reached"
            ))

    [<Test>]
    member _.``a hard kill in the pre-setsid window sweeps the group as well as the exact pid``() =
        if isWindows then
            Assert.Ignore "the POSIX process-group backend and its identity gate are POSIX-only"

        // The verdict is probed BEFORE the delivery, and `LeaderPid` is by definition the answer from the
        // instant in which the child is about to call `setsid()`: preemption between the probe and the
        // delivery is enough for it to become the leader of group `pid` and fork a subtree under it (a
        // pty-wrapped shell does exactly that). A kill delivered to the pid alone would take the leader
        // and leave that subtree running — with teardown's drain erasing the tracking record right
        // behind it, so nothing would ever come back for it. The exact-leader route therefore delivers
        // BOTH halves: the pid, then a `killpg` sweep behind it.
        runWithPreSetsidSeams (fun current live delivered direct ->
            let pid = 2_000_000_340
            current[pid] <- Some 7_400UL
            live.Add pid |> ignore

            let backend = ProcessGroupBackend() :> IContainmentBackend
            backend.Track(spawnedFor pid) |> ignore

            backend.KillChild(spawnedFor pid)

            Assert.That(deliveries direct pid, Is.EqualTo 1, "the exact-pid route must be taken, exactly once")

            Assert.That(
                deliveries delivered pid,
                Is.EqualTo 2,
                "a hard kill here must reach the exact pid AND sweep the group the child may have created since the probe"
            )

            // Non-regression on the ordinary route: once the group exists there is nothing to sweep
            // twice, so a kill is one delivery to the group and never touches the exact-pid route.
            delivered.Clear()
            direct.Clear()
            Native.Posix.processGroupAliveForTests <- Some(fun _ -> true)

            backend.KillChild(spawnedFor pid)

            Assert.That(deliveries delivered pid, Is.EqualTo 1, "a live group is killed by one killpg, as before")
            Assert.That(direct, Is.Empty, "and never through the exact-pid route"))

    [<Test>]
    member _.``the graceful stop reaches a pre-setsid child with both its soft signal and its escalation``() =
        if isWindows then
            Assert.Ignore "the POSIX process-group backend and its identity gate are POSIX-only"

        // `GracefulKillTree` re-routes all three of its phases through the same verdict — the soft-signal
        // sweep, the liveness question driving the poll, and the final SIGKILL sweep — so the pre-setsid
        // window has to hold for a `Stop`, not just for a `Kill`. Deterministic, not timing-dependent:
        // the seams keep the child alive for the whole grace, so the poll ALWAYS runs to its escalation;
        // the grace only bounds how long that takes.
        runWithPreSetsidSeams (fun current live delivered direct ->
            let pid = 2_000_000_350
            current[pid] <- Some 7_500UL
            live.Add pid |> ignore

            let backend = ProcessGroupBackend() :> IContainmentBackend
            backend.Track(spawnedFor pid) |> ignore

            let graceful =
                backend.GracefulKillTree Signal.Term (TimeSpan.FromMilliseconds 150.0)

            Assert.That(
                graceful.Wait(TimeSpan.FromSeconds 30.0),
                Is.True,
                "a graceful stop over a live pre-setsid child must still finish within its own grace"
            )

            // Two exact-pid deliveries: the soft signal, then the escalation. The second one is also the
            // proof that the poll did NOT read the empty process group as "no children left" and skip its
            // force-kill — that phase only runs while the liveness question still answers alive.
            Assert.That(
                deliveries direct pid,
                Is.EqualTo 2,
                "the soft signal and the escalation must BOTH reach the exact pid of a live pre-setsid child"
            )

            // Four delivery attempts in all: the soft phase tries the group and falls back to the pid,
            // and the escalation kills the pid and sweeps the group behind it.
            Assert.That(
                deliveries delivered pid,
                Is.EqualTo 4,
                "the soft signal is delivered once (group, then pid on its ESRCH); the escalation delivers both halves"
            )

            match backend.Members() with
            | Ok members -> Assert.That(members, Does.Contain pid, "a graceful stop must not drop a live child")
            | Error e -> Assert.Fail $"Members failed: {e.Message}")

    [<Test>]
    member _.``a graceful stop over a child gone by both probes delivers nothing and does not sit out the grace``() =
        if isWindows then
            Assert.Ignore "the POSIX process-group backend and its identity gate are POSIX-only"

        // The negative half of the same path: nothing of ours is at that number, so no phase delivers
        // anything — and the poll must fall straight through rather than waiting out a grace that has
        // nothing to wait for. The grace is deliberately far longer than the bound asserted below, so a
        // regression that polls it out fails the assertion instead of merely running slowly.
        runWithPreSetsidSeams (fun current _live delivered direct ->
            let gone = 2_000_000_360
            current[gone] <- Some 7_600UL

            let backend = ProcessGroupBackend() :> IContainmentBackend
            backend.Track(spawnedFor gone) |> ignore

            let graceful = backend.GracefulKillTree Signal.Term (TimeSpan.FromSeconds 10.0)

            Assert.That(
                graceful.Wait(TimeSpan.FromSeconds 5.0),
                Is.True,
                "a graceful stop with nothing alive must fall through its poll, not wait out the whole grace"
            )

            Assert.That(delivered, Is.Empty, "a target gone by both probes must receive neither signal nor kill")
            Assert.That(direct, Is.Empty, "least of all through the exact-pid route"))

    [<Test>]
    member _.``a live pty child whose process group probes gone is still killed by the group teardown``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "the POSIX pty ctty helper (and this window) is Linux-only"
            else
                // The same window, end to end on a REAL child: only the group probe is overridden — to
                // answer ESRCH for this one pid, exactly as `killpg(pid, 0)` does before the pty helper's
                // `setsid()` — while the child, its `/proc` start-time identity, and the kill itself are
                // all real. Before this fix the tree teardown read that ESRCH as "already gone", skipped
                // the kill, and left the live child behind.
                use group =
                    match ProcessGroup.Create() with
                    | Ok created -> created
                    | Error error -> failwith $"ProcessGroup.Create failed: {error}"

                if group.Mechanism <> Mechanism.ProcessGroup then
                    // A limit-free group falls back to the pgid mechanism; anything else (a delegated
                    // cgroup v2 container) kills through its own primitive and does not exercise the
                    // `killpg`-versus-pid routing this test is about.
                    Assert.Ignore $"this window belongs to the pgid mechanism, not {group.Mechanism}"

                let command =
                    (Command.create "/bin/cat").Pty({ Cols = 80; Rows = 24; Echo = false })

                match! group.StartAsync command with
                | Error(ProcessError.Unsupported message) -> Assert.Ignore $"host lacks a PTY: {message}"
                | Error error -> Assert.Fail $"pty spawn failed: {error}"
                | Ok running ->
                    match running.Pid with
                    | None -> Assert.Fail "a POSIX pty child must report its pid"
                    | Some pid ->
                        Native.Posix.processGroupAliveForTests <- Some(fun id -> id <> pid)

                        try
                            match group.KillAll() with
                            | Ok() -> ()
                            | Error error -> Assert.Fail $"KillAll failed: {error}"

                            let wait = running.WaitAsync()

                            let! finished = Task.WhenAny [| (wait :> Task); Task.Delay(TimeSpan.FromSeconds 20.0) |]

                            Assert.That(
                                obj.ReferenceEquals(finished, wait),
                                Is.True,
                                "a live child whose group probes gone must still be killed, not left running"
                            )

                            let! outcome = wait

                            match outcome with
                            | Outcome.Signalled _ -> ()
                            | other -> Assert.Fail $"expected the child to be SIGKILLed, got {other}"
                        finally
                            Native.Posix.processGroupAliveForTests <- None
        }
        :> Task
