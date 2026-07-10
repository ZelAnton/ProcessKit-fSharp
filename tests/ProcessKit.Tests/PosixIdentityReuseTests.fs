namespace ProcessKit.Tests

open System
open System.Collections.Generic
open System.Runtime.InteropServices
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
/// delivery seams (`readProcessIdentityForTests`, `processGroupAliveForTests`,
/// `groupDeliveryObserverForTests`) so a recycled number can be simulated deterministically rather than
/// racing a real OS pid recycle. The seams are set and reset in a `finally`; the fixture runs
/// sequentially (no `[Parallelizable]`), so they never race a concurrent probe. The synthetic pgid
/// numbers are far above any real pid, so the delivery primitives' own (still real) `killpg`/`waitpid`
/// calls are harmless `ESRCH`/`ECHILD` no-ops.
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
          WindowsCtrlGroup = false }

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

            backend.Suspend() |> ignore

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
