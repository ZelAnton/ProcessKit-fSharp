namespace ProcessKit.Tests

open System
open System.Runtime.InteropServices
open System.Threading.Tasks
open NUnit.Framework
// `CollectionAssert` is the classic-model assertion this suite uses wherever a SEQUENCE is compared:
// `Assert.That(list, Is.EqualTo [ ... ])` cannot resolve its overload from F# (`'T seq` vs `'T` are both
// candidates for a list), which is the FS0041 a build-free review would miss (K-039).
open NUnit.Framework.Legacy
open ProcessKit

/// Synthetic reaper facts and a recording `ReaperOps`, so the FreeBSD `procctl(2)` containment layer —
/// whose four syscalls only exist on FreeBSD — is exercised deterministically from ANY build host.
///
/// This is the same injected-dependency seam the rest of the containment layer already uses for
/// platform-shaped logic (`GracefulTeardown.pollUsing`, `CgroupMemberStats.sample`,
/// `MechanismSelection.chooseUsing`, `CapabilityProbe.snapshot`). What it can and cannot prove is worth
/// stating plainly: everything ABOVE the syscalls — membership, root pruning, the zombie sweep's selection
/// rule, the `EPERM` discrimination, the listing's growth/truncation loop, the backend's verb wiring — is
/// covered here on every platform. What it cannot cover is the kernel's own behaviour behind those four
/// calls; that is what the FreeBSD VM path in `.github/workflows/ci.yml` exists for.
module private ReaperFacts =

    /// `REAPER_PIDINFO_VALID` / `REAPER_PIDINFO_ZOMBIE`, restated here rather than read from the library.
    /// The restatement is the point: a test that imported the production constant would agree with it even
    /// if both were wrong, whereas these are transcribed independently from `<sys/procctl.h>`.
    [<Literal>]
    let Valid = 0x0000_0001

    [<Literal>]
    let Zombie = 0x0000_0008

    /// One live descendant: `pid`, belonging to the subtree rooted at `subtree`.
    let live (pid: int) (subtree: int) : Native.FreeBsd.PidInfo =
        { Pid = pid
          Subtree = subtree
          Flags = Valid }

    /// One descendant that has exited and awaits a `wait(2)`.
    let zombie (pid: int) (subtree: int) : Native.FreeBsd.PidInfo =
        { Pid = pid
          Subtree = subtree
          Flags = Valid ||| Zombie }

    /// A slot the kernel never filled — what terminates a `PROC_REAP_GETPIDS` prefix.
    let unfilled: Native.FreeBsd.PidInfo = { Pid = 0; Subtree = 0; Flags = 0 }

    let listing (entries: Native.FreeBsd.PidInfo list) : Native.FreeBsd.Listing =
        { Entries = entries; Truncated = false }

    let truncated (entries: Native.FreeBsd.PidInfo list) : Native.FreeBsd.Listing =
        { Entries = entries; Truncated = true }

/// A recording stand-in for the three reaper syscalls. The kernel's answers are data the test sets; every
/// delivery and every `waitpid` is recorded, so a verb's REACH (which subtrees, which signal, how many
/// times) is assertable rather than inferred.
type private FakeReaper() =
    let deliveries = ResizeArray<int * int>()
    let reaped = ResizeArray<int>()

    /// What the next `PROC_REAP_GETPIDS` answers.
    member val Tree: Result<Native.FreeBsd.Listing, string> = Ok Native.FreeBsd.Listing.Empty with get, set

    /// What a `PROC_REAP_KILL` at one root answers, by root pid and signal number.
    member val Reply: int -> int -> Native.FreeBsd.SubtreeDelivery =
        (fun _ _ -> Native.FreeBsd.SubtreeDelivery.Delivered) with get, set

    /// Every `(root, signal)` a delivery aimed at, in order.
    member _.Deliveries = List.ofSeq deliveries

    /// Every pid the stray-zombie sweep `waitpid`ed.
    member _.Reaped = List.ofSeq reaped

    member this.Ops: Native.FreeBsd.ReaperOps =
        { Descendants = fun () -> this.Tree
          SignalSubtree =
            fun root signalNum ->
                deliveries.Add(root, signalNum)
                this.Reply root signalNum
          ReapZombie = fun pid -> reaped.Add pid }

/// The `procctl(2)` reaper ABI: the struct sizes and field offsets this port hands the kernel, and the
/// decoding of a `PROC_REAP_GETPIDS` answer out of them.
///
/// Getting one of these wrong hands the kernel a wrongly-sized buffer or reads the wrong word back, which
/// on FreeBSD is memory corruption rather than a clean failure — and no other test on any platform would
/// notice. So the layout is pinned here against numbers transcribed from `<sys/procctl.h>` directly.
[<TestFixture>]
type ReaperAbiTests() =

    [<Test>]
    member _.``the reaper struct layout matches sys procctl h``() =
        // struct procctl_reaper_status: u_int rs_flags, rs_children, rs_descendants; pid_t rs_reaper,
        // rs_pid; u_int rs_pad0[15]  ->  5 * 4 + 60 = 80.
        Assert.That(Native.FreeBsd.ReaperStatusSize, Is.EqualTo 80)
        Assert.That(Native.FreeBsd.ReaperStatusFlagsOffset, Is.EqualTo 0)
        Assert.That(Native.FreeBsd.ReaperStatusDescendantsOffset, Is.EqualTo 8)
        Assert.That(Native.FreeBsd.ReaperStatusReaperOffset, Is.EqualTo 12)

        // struct procctl_reaper_pidinfo: pid_t pi_pid, pi_subtree; u_int pi_flags; u_int pi_pad0[15]
        //   ->  3 * 4 + 60 = 72.
        Assert.That(Native.FreeBsd.PidInfoSize, Is.EqualTo 72)

        // struct procctl_reaper_pids: u_int rp_count; u_int rp_pad0[15]; struct ... *rp_pids
        //   ->  the pointer sits at 4 + 60 = 64, naturally aligned on both 32- and 64-bit.
        Assert.That(Native.FreeBsd.ReaperPidsPointerOffset, Is.EqualTo 64)

        // struct procctl_reaper_kill: int rk_sig; u_int rk_flags; pid_t rk_subtree; u_int rk_killed;
        // pid_t rk_fpid; u_int rk_pad0[15]  ->  5 * 4 + 60 = 80, with rk_fpid at 16.
        Assert.That(Native.FreeBsd.ReaperKillSize, Is.EqualTo 80)
        Assert.That(Native.FreeBsd.ReaperKillKilledOffset, Is.EqualTo 12)
        Assert.That(Native.FreeBsd.ReaperKillFirstFailingPidOffset, Is.EqualTo 16)

    [<Test>]
    member _.``a successful PROC_REAP_KILL with rk_fpid identifies a partial delivery``() =
        match Native.FreeBsd.classifySuccessfulKill 3 404 with
        | Native.FreeBsd.SubtreeDelivery.PartialDeliveryFailed(killed, firstFailingPid) ->
            Assert.That(killed, Is.EqualTo 3)
            Assert.That(firstFailingPid, Is.EqualTo 404)
        | outcome -> Assert.Fail $"expected a partial delivery, got {outcome}"

        Assert.That(
            Native.FreeBsd.classifySuccessfulKill 3 -1,
            Is.EqualTo Native.FreeBsd.SubtreeDelivery.Delivered,
            "rk_fpid = -1 is FreeBSD's full-delivery sentinel"
        )

        Assert.That(
            Native.FreeBsd.classifySuccessfulKill 0 404,
            Is.EqualTo Native.FreeBsd.SubtreeDelivery.Delivered,
            "without a positive rk_killed count there is no partial-delivery answer"
        )

    [<Test>]
    member _.``a GETPIDS buffer decodes at the kernel's offsets and its prefix ends at the first unfilled slot``() =
        let capacity = 4
        let size = capacity * Native.FreeBsd.PidInfoSize
        let buffer = Marshal.AllocHGlobal size

        try
            // The buffer is zeroed exactly as the production allocation does it: `PROC_REAP_GETPIDS`
            // reports no element count, so the slots it does NOT write are the terminator.
            Marshal.Copy(Array.zeroCreate<byte> size, 0, buffer, size)

            let write (index: int) (pid: int) (subtree: int) (flags: int) =
                let slot = buffer + nativeint (index * Native.FreeBsd.PidInfoSize)
                Marshal.WriteInt32(slot, 0, pid)
                Marshal.WriteInt32(slot, 4, subtree)
                Marshal.WriteInt32(slot, 8, flags)

            write 0 101 101 ReaperFacts.Valid
            write 1 202 101 (ReaperFacts.Valid ||| ReaperFacts.Zombie)
            // Slot 2 is left unfilled; slot 3 carries a value the kernel would never have written past the
            // terminator, so a decoder that ignored the VALID bit would visibly report it.
            write 3 909 909 ReaperFacts.Valid

            let first = Native.FreeBsd.readPidInfoAt buffer 0
            Assert.That(first.Pid, Is.EqualTo 101)
            Assert.That(first.Subtree, Is.EqualTo 101)
            Assert.That(first.IsValid, Is.True)
            Assert.That(first.IsZombie, Is.False)
            Assert.That(first.IsOwnFork, Is.True, "pi_pid == pi_subtree means this process forked it itself")

            let second = Native.FreeBsd.readPidInfoAt buffer 1
            Assert.That(second.Pid, Is.EqualTo 202)
            Assert.That(second.IsZombie, Is.True)
            Assert.That(second.IsOwnFork, Is.False, "a deeper descendant carries its root's pid, not its own")

            let prefix = Native.FreeBsd.readFilledPrefix buffer capacity

            CollectionAssert.AreEqual(
                [| 101; 202 |],
                prefix |> List.map (fun entry -> entry.Pid),
                "the prefix must end at the first slot without REAPER_PIDINFO_VALID, not run to the buffer's end"
            )
        finally
            Marshal.FreeHGlobal buffer

/// The pure containment logic over a reaper listing: who is a member, which corpses are ours to collect,
/// when a subtree root may be forgotten, how a partial listing is handled, and how a delivery failure is
/// classified. None of it touches a syscall, so all of it runs on every platform.
[<TestFixture>]
type FreeBsdReaperTreeTests() =

    let rootSet (pids: int list) = Set.ofList pids

    [<Test>]
    member _.``membership is the whole subtree - setsid escapees included, zombies and other groups excluded``() =
        // Root 101 is a child this group started; 202 is its grandchild (a `setsid` escapee would look
        // exactly like this — the kernel's subtree tag survives leaving the process group). 303 belongs to
        // ANOTHER group's root, and 404 is a corpse.
        let entries =
            [ ReaperFacts.live 101 101
              ReaperFacts.live 202 101
              ReaperFacts.live 303 300
              ReaperFacts.zombie 404 101 ]

        let members = Native.FreeBsd.membersOf (rootSet [ 101 ]) entries

        CollectionAssert.AreEqual([| 101; 202 |], members)

        Assert.That(
            members,
            Does.Not.Contain 303,
            "one group must never enumerate another group's subtree, even inside one process-wide reaper"
        )

        Assert.That(members, Does.Not.Contain 404, "a zombie has exited; reporting it would claim a dead tree is up")

    [<Test>]
    member _.``the zombie sweep collects re-parented corpses only, never a process this one forked``() =
        // 101 is our own fork (its subtree is itself) and is owned by whoever started it — `waitpid`ing it
        // here would steal the exit status its owner is waiting for (K-016). 202 reached us only by being
        // re-parented when its parent died, so nothing but this process will ever reap it.
        let fake = FakeReaper()

        let listing =
            ReaperFacts.listing
                [ ReaperFacts.zombie 101 101
                  ReaperFacts.zombie 202 101
                  ReaperFacts.live 303 101 ]

        Native.FreeBsd.sweepStrayZombies fake.Ops listing

        CollectionAssert.AreEqual([| 202 |], fake.Reaped)

    [<Test>]
    member _.``the drain waits only for processes BELOW a root, never for the root itself``() =
        let roots = rootSet [ 101 ]

        Assert.That(
            Native.FreeBsd.hasLiveDescendant roots [ ReaperFacts.live 101 101 ],
            Is.False,
            "a root is owned by a run verb that reaps it; waiting for it would be waiting on someone else's wait"
        )

        Assert.That(
            Native.FreeBsd.hasLiveDescendant roots [ ReaperFacts.live 101 101; ReaperFacts.live 202 101 ],
            Is.True
        )

        Assert.That(
            Native.FreeBsd.hasLiveDescendant roots [ ReaperFacts.live 101 101; ReaperFacts.zombie 202 101 ],
            Is.False,
            "a corpse below the root is not something to keep waiting for - the sweep has already collected it"
        )

    [<Test>]
    member _.``a root is forgotten only when the kernel positively reports its subtree empty``() =
        let stale: Native.FreeBsd.Root = { Pid = 101; Seq = 0UL }
        let populated: Native.FreeBsd.Root = { Pid = 202; Seq = 1UL }
        let roots = [ stale; populated ]

        // 202 still has a descendant; 101 names nothing in the listing.
        let kept =
            Native.FreeBsd.pruneRoots (ReaperFacts.listing [ ReaperFacts.live 303 202 ]) 5UL roots

        CollectionAssert.AreEqual([| populated |], kept)

    [<Test>]
    member _.``a root recorded after the listing was taken survives the prune it could not appear in``() =
        // `since` is the stamp read BEFORE the listing. A root stamped at or after it was recorded by a
        // concurrent spawn the listing cannot possibly contain, so pruning it would drop a brand-new
        // child's subtree and silently narrow teardown to what `killpg` reaches.
        let concurrent: Native.FreeBsd.Root = { Pid = 101; Seq = 7UL }

        CollectionAssert.AreEqual(
            [| concurrent |],
            Native.FreeBsd.pruneRoots (ReaperFacts.listing []) 7UL [ concurrent ]
        )

        Assert.That(
            Native.FreeBsd.pruneRoots (ReaperFacts.listing []) 8UL [ concurrent ],
            Is.Empty,
            "a root stamped BEFORE the mark really was covered by the listing, so an empty answer releases it"
        )

    [<Test>]
    member _.``a truncated listing prunes nothing - its silence is not evidence``() =
        let root: Native.FreeBsd.Root = { Pid = 101; Seq = 0UL }

        CollectionAssert.AreEqual(
            [| root |],
            Native.FreeBsd.pruneRoots (ReaperFacts.truncated []) 5UL [ root ],
            "treating a buffer the kernel overflowed as 'the subtree is empty' would drop live subtrees precisely when the tree is forking fastest"
        )

    [<Test>]
    member _.``the listing skips the enumeration entirely when the kernel reports no descendants``() =
        let mutable fills = 0

        let result =
            Native.FreeBsd.listUsing (fun () -> Ok 0) (fun _ ->
                fills <- fills + 1
                Ok [])

        match result with
        | Ok listing ->
            Assert.That(listing.Entries, Is.Empty)
            Assert.That(listing.Truncated, Is.False, "a childless process's listing is complete by construction")
        | Error message -> Assert.Fail $"expected an empty listing, got {message}"

        Assert.That(fills, Is.EqualTo 0, "a childless process must not pay for a buffer or a second syscall")

    [<Test>]
    member _.``a short prefix proves the listing complete, an exactly-full buffer grows and re-reads``() =
        let requested = ResizeArray<int>()

        // The first read fills its buffer exactly (so it may have been truncated); the second comes back
        // short, which PROVES the answer is complete.
        let result =
            Native.FreeBsd.listUsing (fun () -> Ok 1) (fun capacity ->
                requested.Add capacity

                if requested.Count = 1 then
                    Ok(List.replicate capacity (ReaperFacts.live 101 101))
                else
                    Ok [ ReaperFacts.live 101 101 ])

        match result with
        | Ok listing ->
            Assert.That(listing.Truncated, Is.False)
            Assert.That(listing.Entries |> List.length, Is.EqualTo 1)
        | Error message -> Assert.Fail $"expected a complete listing, got {message}"

        Assert.That(requested.Count, Is.EqualTo 2)

        Assert.That(
            requested[1],
            Is.EqualTo(requested[0] * 2),
            "an exactly-full buffer doubles rather than guessing at the size"
        )

    [<Test>]
    member _.``a tree that outgrows every attempt is reported TRUNCATED rather than as a complete answer``() =
        let mutable attempts = 0

        let result =
            Native.FreeBsd.listUsing (fun () -> Ok 1) (fun capacity ->
                attempts <- attempts + 1
                Ok(List.replicate capacity (ReaperFacts.live 101 101)))

        match result with
        | Ok listing ->
            Assert.That(
                listing.Truncated,
                Is.True,
                "under-reporting is safe; passing a partial list off as complete is not"
            )

            Assert.That(attempts, Is.EqualTo Native.FreeBsd.GetPidsGrowAttempts)
        | Error message -> Assert.Fail $"expected a truncated listing, got {message}"

    [<Test>]
    member _.``an unreadable status or enumeration propagates rather than reporting an empty tree``() =
        match Native.FreeBsd.listUsing (fun () -> Error "status failed") (fun _ -> Ok []) with
        | Ok _ -> Assert.Fail "an unreadable status must not become an empty tree"
        | Error message -> Assert.That(message, Is.EqualTo "status failed")

        match Native.FreeBsd.listUsing (fun () -> Ok 4) (fun _ -> Error "getpids failed") with
        | Ok _ -> Assert.Fail "an unreadable enumeration must not become an empty tree"
        | Error message -> Assert.That(message, Is.EqualTo "getpids failed")

    [<Test>]
    member _.``a delivery sweep visits every root even after one of them fails``() =
        let fake = FakeReaper()

        fake.Reply <-
            fun root _ ->
                if root = 202 then
                    Native.FreeBsd.SubtreeDelivery.DeliveryFailed(22, "invalid argument", 0)
                else
                    Native.FreeBsd.SubtreeDelivery.Delivered

        let roots: Native.FreeBsd.Root list =
            [ { Pid = 101; Seq = 0UL }; { Pid = 202; Seq = 1UL }; { Pid = 303; Seq = 2UL } ]

        let outcome = Native.FreeBsd.deliverToRoots fake.Ops 15 roots

        CollectionAssert.AreEqual(
            [| (101, 15); (202, 15); (303, 15) |],
            fake.Deliveries,
            "one failing subtree must never leave another unsignalled"
        )

        Assert.That(
            outcome.Failure.IsSome,
            Is.True,
            "EINVAL is a malformed REQUEST and is wrong whatever the target's state"
        )

        Assert.That(outcome.AnyDelivered, Is.True)

    [<Test>]
    member _.``a partial subtree delivery is retained by the sweep while other roots still receive the signal``() =
        let fake = FakeReaper()

        fake.Reply <-
            fun root _ ->
                if root = 202 then
                    Native.FreeBsd.SubtreeDelivery.PartialDeliveryFailed(2, 404)
                else
                    Native.FreeBsd.SubtreeDelivery.Delivered

        let roots: Native.FreeBsd.Root list =
            [ { Pid = 101; Seq = 0UL }; { Pid = 202; Seq = 1UL }; { Pid = 303; Seq = 2UL } ]

        let outcome = Native.FreeBsd.deliverToRoots fake.Ops 15 roots

        CollectionAssert.AreEqual([| (101, 15); (202, 15); (303, 15) |], fake.Deliveries)

        match outcome.Failure with
        | Some(Native.FreeBsd.DeliveryFailure.PartialFailure(killed, firstFailingPid)) ->
            Assert.That(killed, Is.EqualTo 2)
            Assert.That(firstFailingPid, Is.EqualTo 404)
        | failure -> Assert.Fail $"expected the partial failure to survive the sweep, got {failure}"

        Assert.That(outcome.AnyDelivered, Is.True)

    [<Test>]
    member _.``an ESRCH is a success that releases the root, not a failure``() =
        let fake = FakeReaper()
        fake.Reply <- fun _ _ -> Native.FreeBsd.SubtreeDelivery.SubtreeGone

        let root: Native.FreeBsd.Root = { Pid = 101; Seq = 0UL }
        let outcome = Native.FreeBsd.deliverToRoots fake.Ops 9 [ root ]

        Assert.That(outcome.Failure, Is.EqualTo None)
        CollectionAssert.AreEqual([| root |], outcome.Drained)

        Assert.That(
            outcome.AnyDelivered,
            Is.False,
            "nothing received the signal, which is what tells a vacuous sweep from a delivered one"
        )

    [<Test>]
    member _.``an EPERM is surfaced only against a positively live member, and swallowed otherwise``() =
        let fake = FakeReaper()
        fake.Reply <- fun _ _ -> Native.FreeBsd.SubtreeDelivery.DeliveryFailed(1, "operation not permitted", 202)
        let roots: Native.FreeBsd.Root list = [ { Pid = 101; Seq = 0UL } ]

        // The refusing member is live and non-zombie: a genuine containment gap (a uid-changed child), and
        // the one case worth failing the verb for.
        fake.Tree <- Ok(ReaperFacts.listing [ ReaperFacts.live 101 101; ReaperFacts.live 202 101 ])
        Assert.That((Native.FreeBsd.deliverToRoots fake.Ops 15 roots).Failure.IsSome, Is.True)

        // The same EPERM against a corpse is the harmless "the target was already dead" case every unix
        // backend here keeps as a best-effort success.
        fake.Tree <- Ok(ReaperFacts.listing [ ReaperFacts.live 101 101; ReaperFacts.zombie 202 101 ])
        Assert.That((Native.FreeBsd.deliverToRoots fake.Ops 15 roots).Failure, Is.EqualTo None)

        // And when liveness cannot be established at all, the fail-safe direction is to swallow it rather
        // than fail an ordinary teardown spuriously.
        fake.Tree <- Error "the tree could not be read"
        Assert.That((Native.FreeBsd.deliverToRoots fake.Ops 15 roots).Failure, Is.EqualTo None)

    [<Test>]
    member _.``the post-kill drain ends as soon as nothing lives below the roots, and is bounded when something does``
        ()
        =
        let mutable slept = 0

        // Nothing below the root: the drain must not sleep at all.
        Native.FreeBsd.drainDeadUsing
            (fun () -> fun () -> TimeSpan.Zero)
            (fun _ -> slept <- slept + 1)
            (fun () -> Ok(ReaperFacts.listing [ ReaperFacts.live 101 101 ]))
            (fun () -> Set.ofList [ 101 ])
            (TimeSpan.FromMilliseconds 100.0)

        Assert.That(slept, Is.EqualTo 0)

        // A descendant that never dies: the drain gives up at the budget instead of blocking teardown.
        let mutable elapsed = TimeSpan.Zero
        slept <- 0

        Native.FreeBsd.drainDeadUsing
            (fun () -> fun () -> elapsed)
            (fun _ ->
                slept <- slept + 1
                elapsed <- elapsed + TimeSpan.FromMilliseconds 40.0)
            (fun () -> Ok(ReaperFacts.listing [ ReaperFacts.live 101 101; ReaperFacts.live 202 101 ]))
            (fun () -> Set.ofList [ 101 ])
            (TimeSpan.FromMilliseconds 100.0)

        Assert.That(slept, Is.EqualTo 3, "40ms per poll against a 100ms budget: three polls, then the deadline")

        // An unreadable listing ends the drain rather than spinning on a question the kernel will not answer.
        slept <- 0

        Native.FreeBsd.drainDeadUsing
            (fun () -> fun () -> TimeSpan.Zero)
            (fun _ -> slept <- slept + 1)
            (fun () -> Error "unreadable")
            (fun () -> Set.ofList [ 101 ])
            (TimeSpan.FromMilliseconds 100.0)

        Assert.That(slept, Is.EqualTo 0)

    [<Test>]
    member _.``the BSD signal table - macOS and every BSD, FreeBSD included - is not the Linux one``() =
        // The regression this pins: the four numbers below used to be selected by "is this macOS", which
        // silently gave FreeBSD the LINUX table — so a whole-tree Suspend there delivered 19, which on the
        // BSD table is SIGCONT, i.e. the exact opposite of the requested operation.
        Assert.That(Native.Posix.platformSuspendSignal true, Is.EqualTo 17, "SIGSTOP on the BSD table")
        Assert.That(Native.Posix.platformResumeSignal true, Is.EqualTo 19, "SIGCONT on the BSD table")
        Assert.That(Native.Posix.platformSignalNumber true Signal.Usr1, Is.EqualTo 30)
        Assert.That(Native.Posix.platformSignalNumber true Signal.Usr2, Is.EqualTo 31)

        Assert.That(Native.Posix.platformSuspendSignal false, Is.EqualTo 19, "SIGSTOP on the Linux table")
        Assert.That(Native.Posix.platformResumeSignal false, Is.EqualTo 18, "SIGCONT on the Linux table")
        Assert.That(Native.Posix.platformSignalNumber false Signal.Usr1, Is.EqualTo 10)
        Assert.That(Native.Posix.platformSignalNumber false Signal.Usr2, Is.EqualTo 12)

        // Every other curated signal shares its number across both tables, which is why only four rows are
        // platform-dependent.
        for signal in
            [ Signal.Term
              Signal.Kill
              Signal.Int
              Signal.Hup
              Signal.Quit
              Signal.Other 42 ] do
            Assert.That(
                Native.Posix.platformSignalNumber true signal,
                Is.EqualTo(Native.Posix.platformSignalNumber false signal),
                $"{signal} must not diverge between the two tables"
            )

/// `ProcessReaperBackend` driven through the synthetic reaper: the verb wiring, the reach of each
/// delivery, and the honest refusals — all without a FreeBSD kernel.
///
/// Every instance here keeps the POSIX layer's own ledger EMPTY (roots are recorded through the seam that
/// is the second half of `Track`), so no libc primitive is ever reached and the fixture runs on Windows,
/// Linux and macOS alike. The POSIX layer's own behaviour is already covered by `PosixIdentityReuseTests`;
/// what is under test here is the reaper half and the composition.
[<TestFixture>]
type FreeBsdReaperBackendTests() =

    let backendWith (fake: FakeReaper) (roots: int list) =
        let backend =
            ProcessReaperBackend(ProcessGroupBackend(), fake.Ops, ResourceLimits.None)

        for root in roots do
            backend.RecordRootForTests root

        backend

    let contained (backend: ProcessReaperBackend) = backend :> IContainmentBackend

    [<Test>]
    member _.``the mechanism is reported as ProcessReaper, never as the process group underneath it``() =
        let fake = FakeReaper()
        Assert.That((contained (backendWith fake [])).Mechanism, Is.EqualTo Mechanism.ProcessReaper)

    [<Test>]
    member _.``Members reports this group's whole subtree and nothing else``() =
        let fake = FakeReaper()

        fake.Tree <-
            Ok(
                ReaperFacts.listing
                    [ ReaperFacts.live 101 101
                      ReaperFacts.live 202 101 // a grandchild - a `setsid` escapee looks exactly like this
                      ReaperFacts.zombie 303 101
                      ReaperFacts.live 404 400 ] // another group's subtree, inside the same process reaper
            )

        match (contained (backendWith fake [ 101 ])).Members() with
        | Ok members -> CollectionAssert.AreEqual([| 101; 202 |], members)
        | Error error -> Assert.Fail $"Members failed: {error.Message}"

    [<Test>]
    member _.``an unreadable tree is an honest Io error, never a fabricated empty group``() =
        let fake = FakeReaper()
        fake.Tree <- Error "procctl refused"
        let backend = contained (backendWith fake [ 101 ])

        match backend.Members() with
        | Ok members -> Assert.Fail $"expected a typed failure, got {members.Length} members"
        | Error error ->
            Assert.That(error.Message, Does.Contain "PROC_REAP_GETPIDS")
            Assert.That(error.Message, Does.Contain "procctl refused")

        match backend.Stats() with
        | Ok _ -> Assert.Fail "an unreadable tree must not be reported as a zero-process group"
        | Error _ -> ()

    [<Test>]
    member _.``a soft stop goes through the reaper ONCE per subtree, never doubled with the process group``() =
        let fake = FakeReaper()
        fake.Tree <- Ok(ReaperFacts.listing [ ReaperFacts.live 101 101; ReaperFacts.live 202 202 ])
        let backend = contained (backendWith fake [ 101; 202 ])

        match backend.Signal Signal.Term with
        | Ok() -> ()
        | Error error -> Assert.Fail $"Signal failed: {error.Message}"

        let term = Native.Posix.signalNumber Signal.Term

        CollectionAssert.AreEqual(
            [| (101, term); (202, term) |],
            fake.Deliveries,
            "an observable signal delivered twice would make a child that reads the second as 'force quit' skip its own graceful path"
        )

    [<Test>]
    member _.``a liveness probe is refused before any delivery, on an empty group too``() =
        let fake = FakeReaper()
        let backend = contained (backendWith fake [ 101 ])

        match backend.Signal(Signal.Other 0) with
        | Ok() -> Assert.Fail "signal 0 delivers nothing and must never be reported as a delivered signal"
        | Error error -> Assert.That(error.Message, Does.Contain "liveness probe")

        match backend.Signal(Signal.Other -1) with
        | Ok() -> Assert.Fail "a negative number is not a signal at all"
        | Error _ -> ()

        Assert.That(fake.Deliveries, Is.Empty, "the refusal must come before the kernel is asked anything")

    [<Test>]
    member _.``Suspend and Resume deliver this platform's SIGSTOP and SIGCONT to every subtree``() =
        let fake = FakeReaper()
        // The kernel must still know the subtree: every delivery prunes first, and a root the tree no
        // longer names is released rather than signalled.
        fake.Tree <- Ok(ReaperFacts.listing [ ReaperFacts.live 101 101 ])
        let backend = contained (backendWith fake [ 101 ])

        Assert.That(
            Native.Posix.suspendSignalNumber,
            Is.Not.EqualTo Native.Posix.resumeSignalNumber,
            "freeze and thaw must not resolve to the same number on any table"
        )

        backend.Suspend() |> ignore
        backend.Resume() |> ignore

        CollectionAssert.AreEqual(
            [| (101, Native.Posix.suspendSignalNumber)
               (101, Native.Posix.resumeSignalNumber) |],
            fake.Deliveries
        )

    [<Test>]
    member _.``a subtree the kernel answers ESRCH for is released there and then``() =
        let fake = FakeReaper()
        fake.Tree <- Ok(ReaperFacts.listing [ ReaperFacts.live 101 101; ReaperFacts.live 202 202 ])

        fake.Reply <-
            fun root _ ->
                if root = 202 then
                    Native.FreeBsd.SubtreeDelivery.SubtreeGone
                else
                    Native.FreeBsd.SubtreeDelivery.Delivered

        let backend = backendWith fake [ 101; 202 ]
        (contained backend).Signal Signal.Term |> ignore

        CollectionAssert.AreEqual(
            [| 101 |],
            backend.Roots |> List.map (fun root -> root.Pid),
            "an ESRCH is the kernel's own positive answer that the subtree drained - the one thing that releases a root"
        )

    [<Test>]
    member _.``a Release does NOT drop the subtree root the escapee is still reachable through``() =
        let fake = FakeReaper()
        // The kernel still knows a descendant under root 101 (a `setsid` escapee), so the root must stay
        // even though the POSIX layer has finished with the child it was recorded for.
        fake.Tree <- Ok(ReaperFacts.listing [ ReaperFacts.live 202 101 ])
        let backend = backendWith fake [ 101 ]

        // `Release` delegates to the POSIX layer, whose liveness probe is a real `killpg` — a libc call
        // that does not exist on a Windows build host. Drive it through the same process-wide liveness
        // seam `PosixIdentityReuseTests` uses, so this stays a cross-platform test of the REAPER's
        // behaviour rather than one that only runs on POSIX. The seam is reset in the `finally`.
        Native.Posix.processGroupAliveForTests <- Some(fun _ -> false)
        Native.Posix.processAliveForTests <- Some(fun _ -> false)

        try
            (contained backend).Release
                { Native.Common.Spawned.Handle = nativeint 101
                  Stdout = None
                  Stderr = None
                  Stdin = None
                  ExtraFds = []
                  WindowsCtrlGroup = false
                  PtyControl = None }
        finally
            Native.Posix.processGroupAliveForTests <- None
            Native.Posix.processAliveForTests <- None

        CollectionAssert.AreEqual([| 101 |], backend.Roots |> List.map (fun root -> root.Pid))

    [<Test>]
    member _.``a per-run signal reaches that child's whole SUBTREE, exactly once``() =
        let fake = FakeReaper()
        let backend = contained (backendWith fake [ 101 ])

        let spawned: Native.Common.Spawned =
            { Handle = nativeint 101
              Stdout = None
              Stderr = None
              Stdin = None
              ExtraFds = []
              WindowsCtrlGroup = false
              PtyControl = None }

        let term = Native.Posix.signalNumber Signal.Term

        match backend.SignalChild(spawned, Signal.Term) with
        | Ok() -> ()
        | Error error -> Assert.Fail $"SignalChild failed: {error.Message}"

        CollectionAssert.AreEqual(
            [| (101, term) |],
            fake.Deliveries,
            "the reaper reaches this run's escapees where killpg would not, and an observable signal must not also go through the POSIX layer"
        )

    [<Test>]
    member _.``a partial per-run signal is a typed delivery error``() =
        let fake = FakeReaper()
        fake.Tree <- Ok(ReaperFacts.listing [ ReaperFacts.live 101 101; ReaperFacts.live 202 101 ])
        fake.Reply <- fun _ _ -> Native.FreeBsd.SubtreeDelivery.PartialDeliveryFailed(1, 202)
        let backend = contained (backendWith fake [ 101 ])

        let spawned: Native.Common.Spawned =
            { Handle = nativeint 101
              Stdout = None
              Stderr = None
              Stdin = None
              ExtraFds = []
              WindowsCtrlGroup = false
              PtyControl = None }

        match backend.SignalChild(spawned, Signal.Term) with
        | Ok() -> Assert.Fail "a successful but partial PROC_REAP_KILL must not look like Ok"
        | Error error ->
            Assert.That(error.Message, Does.Contain "partial delivery")
            Assert.That(error.Message, Does.Contain "202")

    [<Test>]
    member _.``partial whole-tree delivery is surfaced by every result-returning control verb``() =
        let operations: (string * (IContainmentBackend -> Result<unit, ProcessError>)) list =
            [ "Signal", fun backend -> backend.Signal Signal.Term
              "Suspend", fun backend -> backend.Suspend()
              "Resume", fun backend -> backend.Resume()
              "KillTree", fun backend -> backend.KillTree()
              "Signal.Kill", fun backend -> backend.Signal Signal.Kill ]

        for name, operation in operations do
            let fake = FakeReaper()
            fake.Tree <- Ok(ReaperFacts.listing [ ReaperFacts.live 101 101 ])
            fake.Reply <- fun _ _ -> Native.FreeBsd.SubtreeDelivery.PartialDeliveryFailed(1, 202)
            let backend = contained (backendWith fake [ 101 ])

            match operation backend with
            | Ok() -> Assert.Fail "partial response hid a failure"
            | Error error ->
                Assert.That(error.Message, Does.Contain "partial delivery", name)
                Assert.That(error.Message, Does.Contain "202", name)

    [<Test>]
    member _.``a per-run signal that only a corpse refused stays a best-effort success``() =
        let fake = FakeReaper()
        // EPERM naming a member the tree reports as a zombie: the harmless "already dead" case, which
        // every unix backend here keeps as a success rather than failing an ordinary teardown.
        fake.Tree <- Ok(ReaperFacts.listing [ ReaperFacts.zombie 202 101 ])
        fake.Reply <- fun _ _ -> Native.FreeBsd.SubtreeDelivery.DeliveryFailed(1, "operation not permitted", 202)

        let backend = contained (backendWith fake [ 101 ])

        let spawned: Native.Common.Spawned =
            { Handle = nativeint 101
              Stdout = None
              Stderr = None
              Stdin = None
              ExtraFds = []
              WindowsCtrlGroup = false
              PtyControl = None }

        match backend.SignalChild(spawned, Signal.Term) with
        | Ok() -> ()
        | Error error -> Assert.Fail $"a zombie's EPERM must not fail the verb: {error.Message}"

        // ...while the same EPERM against a positively live member IS the containment gap worth surfacing.
        fake.Tree <- Ok(ReaperFacts.listing [ ReaperFacts.live 202 101 ])

        match backend.SignalChild(spawned, Signal.Term) with
        | Ok() -> Assert.Fail "a live member refusing the signal is a real containment gap, not a success"
        | Error error -> Assert.That(error.Message, Does.Contain "PROC_REAP_KILL")

    [<Test>]
    member _.``the soft-stop scope is the whole tree, unconditionally``() =
        let fake = FakeReaper()
        Assert.That((contained (backendWith fake [])).SoftStopScope(), Is.EqualTo SoftStopScope.WholeTree)

    [<Test>]
    member _.``whole-tree resource limits stay honestly refused - never an rlimit surrogate``() =
        let fake = FakeReaper()
        let backend = contained (backendWith fake [ 101 ])

        // An empty set is a trivially satisfiable no-op, exactly as on the process group.
        match backend.UpdateLimits ResourceLimits.None with
        | Ok() -> ()
        | Error error -> Assert.Fail $"an empty limit set is trivially satisfiable: {error.Message}"

        match backend.UpdateLimits(ResourceLimits.None.WithMemoryMax(64L * 1024L * 1024L)) with
        | Ok() -> Assert.Fail "a memory cap nothing here can enforce must never be reported as applied"
        | Error(ProcessError.ResourceLimit detail) ->
            Assert.That(detail, Does.Contain "reaper")
            Assert.That(detail, Does.Contain "accounts for nothing")
        | Error other -> Assert.Fail $"expected ProcessError.ResourceLimit, got {other.Message}"

        match backend.UpdateLimits(ResourceLimits.None.WithOomGroupKill()) with
        | Error(ProcessError.Unsupported _) -> ()
        | other -> Assert.Fail $"whole-tree OOM kill has no counterpart here at all: {other}"

    [<Test>]
    member _.``limit evidence is Unknown on every axis - there is no evidence apparatus to read``() =
        let fake = FakeReaper()
        let capped = CappedAxes.None.Record(ResourceLimits.None.WithMemoryMax(1L))
        let evidence = (contained (backendWith fake [])).LimitEvidence capped

        Assert.That(evidence.Memory, Is.EqualTo LimitVerdict.Unknown)
        Assert.That(evidence.Processes, Is.EqualTo LimitVerdict.Unknown)
        Assert.That(evidence.Cpu, Is.EqualTo LimitVerdict.Unknown)

    [<Test>]
    member _.``Stats reports the whole tree's live process count and no invented measurements``() =
        let fake = FakeReaper()

        fake.Tree <-
            Ok(
                ReaperFacts.listing
                    [ ReaperFacts.live 101 101
                      ReaperFacts.live 202 101
                      ReaperFacts.zombie 303 101 ]
            )

        match (contained (backendWith fake [ 101 ])).Stats() with
        | Ok stats ->
            Assert.That(stats.ActiveProcessCount, Is.EqualTo 2)
            Assert.That(stats.TotalCpuTime, Is.EqualTo None)
            Assert.That(stats.PeakMemoryBytes, Is.EqualTo None)
            Assert.That(stats.PeakProcessCount, Is.EqualTo None)
        | Error error -> Assert.Fail $"Stats failed: {error.Message}"

    [<Test>]
    member _.``teardown SIGKILLs the tree, collects the corpses it re-parented, and only then drops its roots``
        ()
        : Task =
        task {
            let fake = FakeReaper()

            // One live root, one grandchild that has already exited and was re-parented onto this process:
            // nothing but this process will ever `wait` for it, and after teardown there is no later sweep.
            fake.Tree <- Ok(ReaperFacts.listing [ ReaperFacts.live 101 101; ReaperFacts.zombie 202 101 ])

            let backend = backendWith fake [ 101 ]
            (contained backend).HardRelease()

            let kill = Native.Posix.signalNumber Signal.Kill
            Assert.That(fake.Deliveries, Does.Contain((101, kill)))
            Assert.That(fake.Reaped, Does.Contain 202, "an orphan the reaper inherited is this process's to collect")
            Assert.That(backend.Roots, Is.Empty)
        }

    [<Test>]
    member _.``a graceful stop that drains within the grace never escalates``() : Task =
        task {
            let fake = FakeReaper()
            fake.Tree <- Ok(ReaperFacts.listing [])
            let backend = contained (backendWith fake [ 101 ])

            let! outcome = backend.GracefulKillTree Signal.Term (TimeSpan.FromMilliseconds 50.0)

            Assert.That(outcome.Drained, Is.True)
            Assert.That(outcome.Escalated, Is.False)
            Assert.That(outcome.Soft, Is.EqualTo SoftDelivery.Sent)

            Assert.That(
                fake.Deliveries
                |> List.filter (fun (_, signalNum) -> signalNum = Native.Posix.signalNumber Signal.Kill),
                Is.Empty,
                "a tree that drained on the soft signal must never be handed an unearned hard kill"
            )
        }

    [<Test>]
    member _.``a tree that outlasts the grace is escalated with a whole-subtree SIGKILL``() : Task =
        task {
            let fake = FakeReaper()
            fake.Tree <- Ok(ReaperFacts.listing [ ReaperFacts.live 101 101 ])
            let backend = contained (backendWith fake [ 101 ])

            let! outcome = backend.GracefulKillTree Signal.Term (TimeSpan.FromMilliseconds 20.0)

            Assert.That(outcome.Drained, Is.False)
            Assert.That(outcome.Escalated, Is.True)
            Assert.That(fake.Deliveries, Does.Contain((101, Native.Posix.signalNumber Signal.Kill)))
        }

    [<Test>]
    member _.``a syscall-level soft failure reports Failed only when nothing received the signal``() : Task =
        task {
            // One subtree returns a syscall-level refusal (a live member's EPERM), the other receives it.
            // This mechanism keeps the established "failed for every target" reading for that errno path.
            let fake = FakeReaper()

            fake.Tree <- Ok(ReaperFacts.listing [ ReaperFacts.live 101 101; ReaperFacts.live 202 202 ])

            fake.Reply <-
                fun root _ ->
                    if root = 101 then
                        Native.FreeBsd.SubtreeDelivery.DeliveryFailed(1, "operation not permitted", 101)
                    else
                        Native.FreeBsd.SubtreeDelivery.Delivered

            let backend = contained (backendWith fake [ 101; 202 ])
            let! outcome = backend.GracefulKillTree Signal.Term TimeSpan.Zero

            Assert.That(outcome.Soft, Is.EqualTo SoftDelivery.Sent)
        }

    [<Test>]
    member _.``a soft phase reports a kernel partial delivery as Failed``() : Task =
        task {
            let fake = FakeReaper()

            fake.Tree <-
                Ok(ReaperFacts.listing [ ReaperFacts.live 101 101; ReaperFacts.live 202 202; ReaperFacts.live 303 303 ])

            fake.Reply <-
                fun root _ ->
                    if root = 101 then
                        Native.FreeBsd.SubtreeDelivery.DeliveryFailed(22, "invalid argument", 101)
                    elif root = 202 then
                        Native.FreeBsd.SubtreeDelivery.PartialDeliveryFailed(1, 404)
                    else
                        Native.FreeBsd.SubtreeDelivery.Delivered

            let backend = contained (backendWith fake [ 101; 202; 303 ])
            let! outcome = backend.GracefulKillTree Signal.Term TimeSpan.Zero

            Assert.That(
                outcome.Soft,
                Is.EqualTo SoftDelivery.Failed,
                "rk_fpid must survive an earlier syscall failure and a later successful root"
            )
        }
