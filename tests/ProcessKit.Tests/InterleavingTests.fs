namespace ProcessKit.Tests

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics.Metrics
open System.IO
open System.Net
open System.Runtime.ExceptionServices
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

// Randomized interleaving search for RunningProcess/ProcessGroup lifecycle races. The most productive
// class of past defects here has been lifecycle races (StopAsync x Dispose, verb x teardown, TakeStdin
// x feeder, probe x exit — see CHANGELOG.md). The existing suites cover those pointwise (regression
// tests for a specific race) and under load (StressTests.fs, `[<Category("Stress")>]` — soak/leak),
// but neither *searches* for new interleavings. This harness generates, from a logged numeric seed, a
// random sequence of CONCURRENT public lifecycle operations against a real child, then checks the
// invariants that every interleaving must uphold. The generated plan is a pure function of the seed
// (so a failure replays from its logged seed); the concurrency *timing* still varies run to run, which
// is what turns a fixed seed set into an ongoing search rather than a fixed set of cases.

/// One of the three child-process shapes the harness drives, so the generated interleavings race a
/// child that is already gone, one still comfortably alive, and one flooding its pipes.
type private ChildProfile =
    /// Exits almost immediately — most ops race an already-terminated (or terminating) child.
    | ShortLived
    /// Sleeps ~1s without exiting — ops race a child that is reliably still alive.
    | LongLived
    /// Writes well over one OS pipe buffer of output before exiting — exercises the streaming/draining
    /// path (and backpressure) racing teardown.
    | Flooding

/// A public `RunningProcess` lifecycle operation the generator can schedule. Kept as pure data (an
/// equatable DU) so a plan is reproducible and comparable; the closure that actually performs the op
/// is built at execution time (`Harness.runRpOp`).
type private RpOp =
    | Kill
    | StopFast
    | StopGrace
    | DisposeHandle
    | Wait
    | OutputString
    | OutputBytes
    | Profile
    | Finish
    | StdoutLines
    | OutputEvents
    | TakeStdinWrite
    | WaitForLine
    | WaitForCustom
    | WaitForPort

/// A public `ProcessGroup` lifecycle operation the generator can schedule.
type private GroupOp =
    | GSignal of Signal
    | GSuspend
    | GResume
    | GMembers
    | GKillAll
    | GShutdown
    | GDisposeGroup

/// A reproducible `RunningProcess` interleaving: a child profile plus a sequence of concurrency
/// "waves". Each wave's ops are fired together (`Task.WhenAll`); the waves run in order.
type private RpPlan =
    { Seed: int
      Profile: ChildProfile
      Waves: RpOp list list }

/// A reproducible `ProcessGroup` interleaving: a child profile, how many children to start into the
/// group, and the concurrency waves of group control ops.
type private GroupPlan =
    { Seed: int
      Profile: ChildProfile
      ChildCount: int
      Waves: GroupOp list list }

module private Harness =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux

    /// A substring of `RunningProcess`'s documented claim-gate message
    /// ("this RunningProcess has already been consumed by another verb"). Two consuming verbs racing on
    /// one handle is a *documented* outcome — the loser is refused with this `InvalidOperationException`
    /// (or the `ProcessError.Unsupported` Result form) — so the generator is free to draw two consuming
    /// verbs into one wave: the harness treats "already consumed" as a normal, expected result rather
    /// than a surprise.
    let private alreadyConsumedNeedle = "already been consumed"

    let private shell (script: string) : Command =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    /// The command for a profile. `KeepStdinOpen` on every profile so `TakeStdin` can hand back an
    /// interactive stdin (with no source it is available immediately), exercising the stdin-writer path;
    /// a profile that does not read stdin simply never drains it, which teardown closes.
    let commandFor (profile: ChildProfile) : Command =
        let script =
            match profile with
            | ShortLived -> "echo interleave-tick"
            | LongLived -> if isWindows then "ping -n 2 127.0.0.1 >NUL" else "sleep 1"
            | Flooding ->
                if isWindows then
                    "for /l %i in (1,1,1500) do @echo flood line %i has some padding to enlarge the record"
                else
                    "i=0; while [ $i -lt 1500 ]; do echo \"flood line $i has some padding to enlarge the record\"; i=$((i+1)); done"

        shell script |> Command.keepStdinOpen

    /// True when `pid` is present in `/proc` in the zombie ('Z') state — it exited but was never reaped.
    /// Absence of the `/proc` entry means it WAS reaped (the success case), so that returns `false`.
    /// The same technique `StressTests.fs` uses for its leak assertion (Linux only).
    let isZombie (pid: int) : bool =
        let statPath = $"/proc/{pid}/stat"

        if not (File.Exists statPath) then
            false // reaped: no /proc entry
        else
            try
                let stat = File.ReadAllText statPath
                // "/proc/<pid>/stat" is "pid (comm) state ...": comm may hold spaces and parens, so
                // locate the state field just past the final ')'.
                let closeParen = stat.LastIndexOf ')'
                closeParen >= 0 && closeParen + 2 < stat.Length && stat.[closeParen + 2] = 'Z'
            with :? IOException ->
                // the entry vanished between the existence check and the read - reaped.
                false

    /// Subscribe a `MeterListener` to ProcessKit's meter and collect every `processkit.runs.active`
    /// delta (the individual +1/-1 measurements). Summing them to zero after a run proves the in-flight
    /// telemetry mark returned to baseline — the same technique `LoggingTests.fs` uses. Tests run
    /// sequentially (this fixture is not `[<Parallelizable>]`), so a listener scoped tightly around one
    /// run sees only that run's measurements.
    let listenRunsActive () : MeterListener * ConcurrentQueue<int64> =
        let deltas = ConcurrentQueue<int64>()
        let listener = new MeterListener()

        listener.InstrumentPublished <-
            (fun instrument l ->
                if instrument.Meter.Name = ProcessKitDiagnostics.MeterName then
                    l.EnableMeasurementEvents instrument)

        listener.SetMeasurementEventCallback<int64>(
            MeasurementCallback<int64>(fun instrument value _tags _state ->
                if instrument.Name = "processkit.runs.active" then
                    deltas.Enqueue value)
        )

        listener.Start()
        listener, deltas

    /// Is `ex` a *documented*, non-bug outcome of racing lifecycle ops, or a genuine surprise the
    /// harness must flag? Deliberately narrow, so the harness stays a real search and not a
    /// swallow-everything false green:
    ///   - `OperationCanceledException` — an op raced a `DisposeAsync`/cancellation that tore it down.
    ///   - `ProcessException` — the typed `ProcessError` surfaced through a streaming enumeration (the
    ///     library's documented "never a raw exception escapes the `IAsyncEnumerable`" contract).
    ///   - the claim-gate `InvalidOperationException` — two consuming verbs raced one handle.
    /// Anything else (a leaked `ObjectDisposedException`, a `NullReferenceException`, an
    /// `InvalidOperationException` with a *different* message, …) is an unexpected invariant breach.
    let classifyExpected (ex: exn) : bool =
        match ex with
        | :? OperationCanceledException -> true
        | :? ProcessException -> true
        | :? InvalidOperationException as ioe when ioe.Message.Contains alreadyConsumedNeedle -> true
        | _ -> false

    /// Write one short line to the taken interactive stdin, then close it. Broken-pipe / disposed
    /// faults are the documented outcome of writing to a child whose stdin end a racing op already
    /// closed, so they are tolerated *here* (scoped to this op) rather than widening the global
    /// classifier to every `IOException`.
    let private writeStdinGuarded (stdin: ProcessStdin) : Task =
        task {
            try
                do! stdin.WriteLineAsync("interleave-ping", CancellationToken.None)
                do! stdin.FinishAsync()
            with
            | :? IOException ->
                // Broken pipe: the child's stdin end closed because it exited or was killed by a racing op.
                ()
            | :? ObjectDisposedException ->
                // A racing DisposeAsync tore the stdin pipe down mid-write.
                ()
            | :? OperationCanceledException ->
                // The write raced a disposal that cancelled the shared token.
                ()
        }
        :> Task

    /// Enumerate a streaming verb up to `cap` items, then dispose the enumerator. Capping (rather than
    /// draining to completion) is deliberate — stopping mid-stream while the child still produces output
    /// is exactly the streaming-verb x teardown race. Disposes the enumerator on both the normal and the
    /// faulting path, re-raising the fault (via `ExceptionDispatchInfo`, since `reraise` is unavailable
    /// in a `task` CE) so the op-level classifier handles it uniformly.
    let private drainStream (source: IAsyncEnumerable<'T>) (cap: int) : Task =
        task {
            let enumerator = source.GetAsyncEnumerator()

            try
                let mutable pulled = 0
                let mutable go = true

                while go && pulled < cap do
                    let! has = enumerator.MoveNextAsync()

                    if has then pulled <- pulled + 1 else go <- false
            with ex ->
                do! enumerator.DisposeAsync()
                ExceptionDispatchInfo.Throw ex

            do! enumerator.DisposeAsync()
        }
        :> Task

    /// Perform one `RunningProcess` op. Total by construction: a documented racing outcome (see
    /// `classifyExpected`) is expected and ignored; any other fault is recorded — with its seed and op,
    /// so the failing plan is replayable — rather than thrown, so a concurrent `Task.WhenAll` wave
    /// surfaces EVERY surprise instead of only whichever one it observed first.
    let runRpOp (rp: RunningProcess) (unexpected: ConcurrentQueue<string>) (seed: int) (kind: RpOp) : Task =
        let probeTimeout = TimeSpan.FromMilliseconds 150.0
        let ct = CancellationToken.None

        task {
            try
                match kind with
                | Kill -> rp.Kill()
                | StopFast ->
                    let! _ = rp.StopAsync TimeSpan.Zero
                    ()
                | StopGrace ->
                    let! _ = rp.StopAsync(TimeSpan.FromMilliseconds 150.0)
                    ()
                | DisposeHandle -> do! (rp :> IAsyncDisposable).DisposeAsync()
                | Wait ->
                    let! _ = rp.WaitAsync()
                    ()
                | OutputString ->
                    let! _ = rp.OutputStringAsync()
                    ()
                | OutputBytes ->
                    let! _ = rp.OutputBytesAsync()
                    ()
                | Profile ->
                    let! _ = rp.ProfileAsync()
                    ()
                | Finish ->
                    let! _ = rp.FinishAsync()
                    ()
                | StdoutLines -> do! drainStream (rp.StdoutLinesAsync()) 50
                | OutputEvents -> do! drainStream (rp.OutputEventsAsync()) 50
                | TakeStdinWrite ->
                    match rp.TakeStdin() with
                    | Some stdin -> do! writeStdinGuarded stdin
                    | None -> ()
                | WaitForLine ->
                    let! _ = rp.WaitForLineAsync((fun (_: string) -> true), probeTimeout, ct)
                    ()
                | WaitForCustom ->
                    let! _ = rp.WaitForAsync((fun () -> Task.FromResult true), probeTimeout, ct)
                    ()
                | WaitForPort ->
                    let! _ = rp.WaitForPortAsync(IPEndPoint(IPAddress.Loopback, 65533), probeTimeout, ct)
                    ()
            with ex ->
                if not (classifyExpected ex) then
                    unexpected.Enqueue($"seed={seed} op={kind} -> {ex.GetType().Name}: {ex.Message}")
        }
        :> Task

    /// Perform one `ProcessGroup` op. Total, exactly like `runRpOp`. Every control verb funnels through
    /// the group's release gate and returns a typed `ProcessError` (never an `ObjectDisposedException`)
    /// once the group is torn down, so a `GDisposeGroup` drawn into a wave alongside `Signal`/`Suspend`/…
    /// is a sound race: the losers just observe `Unsupported` Results, which are data here.
    let runGroupOp (group: ProcessGroup) (unexpected: ConcurrentQueue<string>) (seed: int) (kind: GroupOp) : Task =
        task {
            try
                match kind with
                | GSignal signalValue -> group.Signal signalValue |> ignore
                | GSuspend -> group.Suspend() |> ignore
                | GResume -> group.Resume() |> ignore
                | GMembers -> group.Members() |> ignore
                | GKillAll -> group.KillAll() |> ignore
                | GShutdown -> do! group.ShutdownAsync(TimeSpan.FromMilliseconds 150.0)
                | GDisposeGroup -> do! (group :> IAsyncDisposable).DisposeAsync()
            with ex ->
                if not (classifyExpected ex) then
                    unexpected.Enqueue($"seed={seed} groupOp={kind} -> {ex.GetType().Name}: {ex.Message}")
        }
        :> Task

    let private allProfiles = [| ShortLived; LongLived; Flooding |]

    let private allRpOps =
        [| Kill
           StopFast
           StopGrace
           DisposeHandle
           Wait
           OutputString
           OutputBytes
           Profile
           Finish
           StdoutLines
           OutputEvents
           TakeStdinWrite
           WaitForLine
           WaitForCustom
           WaitForPort |]

    let private allSignals =
        [| Signal.Term; Signal.Kill; Signal.Int; Signal.Hup; Signal.Other 30 |]

    /// Generate a `RunningProcess` plan purely from `seed`: a single `Random(seed)` drives every choice
    /// in a fixed order, so the same seed always yields the identical plan (the determinism the replay
    /// contract needs).
    let generateRpPlan (seed: int) : RpPlan =
        let rng = Random(seed)
        let profile = allProfiles.[rng.Next allProfiles.Length]
        let waveCount = rng.Next(2, 6)

        let waves =
            [ for _ in 1..waveCount ->
                  let waveSize = rng.Next(1, 4)
                  [ for _ in 1..waveSize -> allRpOps.[rng.Next allRpOps.Length] ] ]

        { Seed = seed
          Profile = profile
          Waves = waves }

    /// Generate a `ProcessGroup` plan purely from `seed`, same determinism contract as `generateRpPlan`.
    let generateGroupPlan (seed: int) : GroupPlan =
        let rng = Random(seed)
        let profile = allProfiles.[rng.Next allProfiles.Length]
        let childCount = rng.Next(1, 3)
        let waveCount = rng.Next(2, 5)

        let pickGroupOp () =
            match rng.Next 7 with
            | 0 -> GSignal allSignals.[rng.Next allSignals.Length]
            | 1 -> GSuspend
            | 2 -> GResume
            | 3 -> GMembers
            | 4 -> GKillAll
            | 5 -> GShutdown
            | _ -> GDisposeGroup

        let waves =
            [ for _ in 1..waveCount ->
                  let waveSize = rng.Next(1, 4)
                  [ for _ in 1..waveSize -> pickGroupOp () ] ]

        { Seed = seed
          Profile = profile
          ChildCount = childCount
          Waves = waves }

    /// Run an `RpPlan` and return (unexpected-fault messages, observed child pids). The handle is ALWAYS
    /// disposed afterwards — that guaranteed teardown clears the run's `runs.active` mark and reaps the
    /// tree even when the plan drew no explicit Kill/Stop/Dispose.
    let executeRpPlan (plan: RpPlan) : Task<string list * int list> =
        task {
            let unexpected = ConcurrentQueue<string>()
            let pids = ResizeArray<int>()
            let runner: IProcessRunner = JobRunner()

            match! runner.StartAsync(commandFor plan.Profile, CancellationToken.None) with
            | Error err -> unexpected.Enqueue($"seed={plan.Seed} StartAsync failed: {err.Message}")
            | Ok rp ->
                rp.Pid |> Option.iter pids.Add
                let mutable fault: exn option = None

                try
                    for wave in plan.Waves do
                        let opTasks = wave |> List.map (runRpOp rp unexpected plan.Seed)
                        do! Task.WhenAll opTasks
                with ex ->
                    // The op closures are total, so this should never fire; capture it anyway so the
                    // guaranteed dispose below still runs before any fault propagates.
                    fault <- Some ex

                do! (rp :> IAsyncDisposable).DisposeAsync()

                match fault with
                | Some ex -> ExceptionDispatchInfo.Throw ex
                | None -> ()

            return List.ofSeq unexpected, List.ofSeq pids
        }

    /// Run a `GroupPlan` and return (unexpected-fault messages, observed child pids). Every started
    /// handle is disposed (each clears its own run's `runs.active` mark) and then the group itself
    /// (kill-on-drop reaps whatever the plan left alive).
    let executeGroupPlan (plan: GroupPlan) : Task<string list * int list> =
        task {
            let unexpected = ConcurrentQueue<string>()
            let pids = ResizeArray<int>()

            match ProcessGroup.Create() with
            | Error err -> unexpected.Enqueue($"seed={plan.Seed} ProcessGroup.Create failed: {err.Message}")
            | Ok group ->
                let handles = ResizeArray<RunningProcess>()
                let mutable fault: exn option = None

                try
                    for _ in 1 .. plan.ChildCount do
                        match! group.StartAsync(commandFor plan.Profile, CancellationToken.None) with
                        | Error err -> unexpected.Enqueue($"seed={plan.Seed} group StartAsync failed: {err.Message}")
                        | Ok rp ->
                            rp.Pid |> Option.iter pids.Add
                            handles.Add rp

                    for wave in plan.Waves do
                        let opTasks = wave |> List.map (runGroupOp group unexpected plan.Seed)
                        do! Task.WhenAll opTasks
                with ex ->
                    fault <- Some ex

                for rp in handles do
                    do! (rp :> IAsyncDisposable).DisposeAsync()

                do! (group :> IAsyncDisposable).DisposeAsync()

                match fault with
                | Some ex -> ExceptionDispatchInfo.Throw ex
                | None -> ()

            return List.ofSeq unexpected, List.ofSeq pids
        }

/// Randomized lifecycle interleaving search over `RunningProcess`/`ProcessGroup`. Each `[<TestCase>]`
/// drives one seed's generated plan of CONCURRENT public operations against a real child, then asserts
/// the interleaving upheld every invariant: no unexpected exceptions surfaced (only the documented
/// typed/claim-gate outcomes), no child was left a zombie, no task fault went unobserved, and the
/// `processkit.runs.active` telemetry returned to zero. The seed is in each case name (and in every
/// failure message), so a red case replays deterministically.
///
/// Its own category (`Interleaving`) keeps it — like `Stress` — off the ordinary PR/push `test` job
/// (which filters `Category!=Stress&Category!=Interleaving`); it runs instead on the scheduled/
/// `workflow_dispatch` `interleaving` job in `.github/workflows/ci.yml`. Not `[<Parallelizable>]`: the
/// per-run `MeterListener` telemetry check assumes it observes only its own run's measurements.
[<TestFixture>]
[<Category("Interleaving")>]
type InterleavingTests() =

    // Fixed, varied plan seeds — a fixed set makes a failure reproducible run to run (re-run the seed),
    // while the concurrency timing still varies each execution, so the search keeps exploring new
    // interleavings of the same plans.
    static member RpSeeds: int[] = [| for i in 0..31 -> 100_003 + i * 97_003 |]

    static member GroupSeeds: int[] = [| for i in 0..19 -> 500_009 + i * 89_017 |]

    [<Test>]
    [<TestCaseSource("RpSeeds")>]
    member _.``RunningProcess lifecycle survives a randomized concurrent interleaving``(seed: int) : Task =
        task {
            TestContext.WriteLine $"interleaving RunningProcess plan seed={seed}"
            let plan = Harness.generateRpPlan seed
            let listener, deltas = Harness.listenRunsActive ()
            let unobserved = ConcurrentQueue<exn>()

            let handler =
                EventHandler<UnobservedTaskExceptionEventArgs>(fun _ args ->
                    unobserved.Enqueue(args.Exception :> exn)
                    args.SetObserved())

            TaskScheduler.UnobservedTaskException.AddHandler handler

            try
                let! unexpected, pids = Harness.executeRpPlan plan

                // A GC/finalizer pass gives a faulted, never-awaited task's finalizer the chance to raise
                // UnobservedTaskException before we read `unobserved` (the StressTests technique).
                GC.Collect()
                GC.WaitForPendingFinalizers()
                GC.Collect()

                Assert.That(unexpected, Is.Empty, $"seed={seed} produced unexpected exception(s): %A{unexpected}")

                let unobservedMessages = unobserved |> Seq.map (fun e -> e.Message) |> List.ofSeq

                Assert.That(
                    unobservedMessages,
                    Is.Empty,
                    $"seed={seed} left unobserved task exception(s): %A{unobservedMessages}"
                )

                Assert.That(
                    deltas |> Seq.sum,
                    Is.EqualTo 0L,
                    $"seed={seed} left processkit.runs.active non-zero (its +1/-1 deltas did not sum to zero)"
                )

                if Harness.isLinux then
                    for pid in pids do
                        Assert.That(
                            Harness.isZombie pid,
                            Is.False,
                            $"seed={seed} left child pid {pid} as a zombie (the tree was not reaped)"
                        )
            finally
                TaskScheduler.UnobservedTaskException.RemoveHandler handler
                listener.Dispose()
        }
        :> Task

    [<Test>]
    [<TestCaseSource("GroupSeeds")>]
    member _.``ProcessGroup lifecycle survives a randomized concurrent interleaving``(seed: int) : Task =
        task {
            TestContext.WriteLine $"interleaving ProcessGroup plan seed={seed}"
            let plan = Harness.generateGroupPlan seed
            let listener, deltas = Harness.listenRunsActive ()
            let unobserved = ConcurrentQueue<exn>()

            let handler =
                EventHandler<UnobservedTaskExceptionEventArgs>(fun _ args ->
                    unobserved.Enqueue(args.Exception :> exn)
                    args.SetObserved())

            TaskScheduler.UnobservedTaskException.AddHandler handler

            try
                let! unexpected, pids = Harness.executeGroupPlan plan

                GC.Collect()
                GC.WaitForPendingFinalizers()
                GC.Collect()

                Assert.That(unexpected, Is.Empty, $"seed={seed} produced unexpected exception(s): %A{unexpected}")

                let unobservedMessages = unobserved |> Seq.map (fun e -> e.Message) |> List.ofSeq

                Assert.That(
                    unobservedMessages,
                    Is.Empty,
                    $"seed={seed} left unobserved task exception(s): %A{unobservedMessages}"
                )

                Assert.That(
                    deltas |> Seq.sum,
                    Is.EqualTo 0L,
                    $"seed={seed} left processkit.runs.active non-zero (its +1/-1 deltas did not sum to zero)"
                )

                if Harness.isLinux then
                    for pid in pids do
                        Assert.That(
                            Harness.isZombie pid,
                            Is.False,
                            $"seed={seed} left group child pid {pid} as a zombie (the tree was not reaped)"
                        )
            finally
                TaskScheduler.UnobservedTaskException.RemoveHandler handler
                listener.Dispose()
        }
        :> Task

    [<Test>]
    member _.``the same seed regenerates an identical operation plan (deterministic replay)``() =
        // Same seed -> identical generated sequence, for both generators — this is what makes a failure
        // logged with its seed replayable to the exact plan (the concurrency timing still varies, so the
        // search keeps finding new interleavings; only the plan is reproduced).
        for seed in InterleavingTests.RpSeeds do
            Assert.That(
                Harness.generateRpPlan seed = Harness.generateRpPlan seed,
                Is.True,
                $"RunningProcess plan for seed={seed} was not reproducible"
            )

        for seed in InterleavingTests.GroupSeeds do
            Assert.That(
                Harness.generateGroupPlan seed = Harness.generateGroupPlan seed,
                Is.True,
                $"ProcessGroup plan for seed={seed} was not reproducible"
            )

        // Sanity: the generator actually varies with the seed. A constant generator would satisfy the
        // determinism checks above trivially, so prove distinct seeds do not all collapse to one plan.
        let distinctRpPlans =
            InterleavingTests.RpSeeds
            |> Array.map Harness.generateRpPlan
            |> Array.distinct
            |> Array.length

        Assert.That(distinctRpPlans, Is.GreaterThan 1, "the generator does not vary with its seed")
