namespace ProcessKit.Tests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing

/// Shared soak-test spine for `StressTests` below: snapshot a small set of process-wide resources
/// (thread-pool occupancy, managed memory, and — on Windows — kernel handles), run a load, force a
/// real GC pass, then assert every resource returned to its pre-load baseline within a generous
/// tolerance. Also subscribes to `TaskScheduler.UnobservedTaskException` for the duration of the run,
/// so a task fault nothing ever awaited fails the test instead of vanishing silently.
module private StressHarness =

    /// Managed heap growth tolerated across a stress run (JIT, GC segment growth, and incidental
    /// allocator slack all show up here even with zero real leak) — generous on purpose: this guards
    /// against an actual leak, not byte-for-byte equality.
    let private managedMemorySlackBytes = 64L * 1024L * 1024L

    /// Busy thread-pool threads tolerated across a run — background timers, the GC's own worker
    /// threads, and steady-state pool churn all wander by a few threads with zero real leak.
    let private threadSlack = 64

    /// Windows kernel handle-count tolerance — the same generous absolute-slack rationale as
    /// `WindowsOverlappedPipeTests`' own handle-leak checks (steady-state GC/JIT/thread-pool noise).
    let private handleSlack = 500

    type private ResourceSnapshot =
        { BusyThreads: int
          ManagedBytes: int64
          HandleCount: int option }

    let private settle () =
        // A GC/finalizer pass BEFORE the "before" snapshot keeps a prior test's garbage out of this
        // run's baseline; the same pass after the load is what lets a real leak show up as growth
        // instead of being masked by an uncollected baseline.
        GC.Collect()
        GC.WaitForPendingFinalizers()
        GC.Collect()

    let private snapshot () : ResourceSnapshot =
        settle ()

        let mutable maxWorker = 0
        let mutable maxIo = 0
        ThreadPool.GetMaxThreads(&maxWorker, &maxIo)

        let mutable availWorker = 0
        let mutable availIo = 0
        ThreadPool.GetAvailableThreads(&availWorker, &availIo)

        let handleCount =
            if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
                use proc = Process.GetCurrentProcess()
                proc.Refresh()
                Some proc.HandleCount
            else
                None

        { BusyThreads = (maxWorker - availWorker) + (maxIo - availIo)
          ManagedBytes = GC.GetTotalMemory true
          HandleCount = handleCount }

    /// Runs `action`, then asserts the resources it touched were returned to their pre-load baseline
    /// (within tolerance) and that no task's exception went unobserved during the run.
    let runAndAssertNoLeak (action: unit -> Task) : Task =
        task {
            let unobserved = ResizeArray<exn>()
            let sync = obj ()

            let handler =
                EventHandler<UnobservedTaskExceptionEventArgs>(fun _ args ->
                    lock sync (fun () -> unobserved.Add(args.Exception :> exn))
                    args.SetObserved())

            TaskScheduler.UnobservedTaskException.AddHandler handler

            try
                let before = snapshot ()

                do! action ()

                // `snapshot` opens with a GC/finalizer pass — this is what gives a faulted, never-
                // awaited task's finalizer the chance to raise `UnobservedTaskException` before the
                // assertion below reads `unobserved`.
                let after = snapshot ()

                Assert.That(
                    unobserved,
                    Is.Empty,
                    sprintf "unobserved task exception(s) surfaced during the stress run: %A" (List.ofSeq unobserved)
                )

                Assert.That(
                    after.ManagedBytes,
                    Is.LessThan(before.ManagedBytes + managedMemorySlackBytes),
                    $"managed memory grew from {before.ManagedBytes} to {after.ManagedBytes} bytes across the run"
                )

                Assert.That(
                    after.BusyThreads,
                    Is.LessThanOrEqualTo(before.BusyThreads + threadSlack),
                    $"busy thread-pool threads grew from {before.BusyThreads} to {after.BusyThreads}"
                )

                match before.HandleCount, after.HandleCount with
                | Some baseline, Some current ->
                    Assert.That(
                        current,
                        Is.LessThan(baseline + handleSlack),
                        $"Windows handle count grew from {baseline} to {current}"
                    )
                | _ -> ()
            finally
                TaskScheduler.UnobservedTaskException.RemoveHandler handler
        }
        :> Task

    /// True when `pid` is present in `/proc` and sitting in the zombie ('Z') state — i.e. it exited
    /// but its parent never reaped it. Absence of the /proc entry means it WAS reaped (the success
    /// case), so this returns `false` rather than treating "gone" as suspicious.
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

/// Stress/soak coverage: the library's core promise is that the process tree, its handles, and its
/// tasks never outlive their scope — but the functional tests elsewhere in this project exercise that
/// promise one process at a time. This fixture instead drives hundreds of concurrent runs and asserts
/// every tracked resource returns to its baseline afterward, so a regression in the shared teardown/
/// reap path (the source of most past fixes — see CHANGELOG.md) shows up under load rather than going
/// unnoticed. Excluded from the ordinary CI `test` job (`Category!=Stress`) and run instead by a
/// separate scheduled/`workflow_dispatch` stage — see `.github/workflows/ci.yml`.
[<TestFixture>]
[<Category("Stress")>]
type StressTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    // A few hundred of these racing concurrently is the fan-out load below.
    let quickEcho = shell "echo stress-tick"

    // Long enough to still be alive when its handle is disposed without being awaited — the
    // start/dispose cycle below deliberately kills a LIVE child on every iteration, not a finished one.
    let sleeper =
        if isWindows then
            shell "ping 127.0.0.1 -n 5"
        else
            shell "sleep 5"

    let runner: IProcessRunner = JobRunner()

    [<Test>]
    member _.``a fan-out of hundreds of concurrent OutputStringAsync runs returns to baseline``() : Task =
        StressHarness.runAndAssertNoLeak (fun () ->
            task {
                let concurrency = 300
                let runs = [ for _ in 1..concurrency -> quickEcho.OutputStringAsync() ]
                let! results = Task.WhenAll runs

                for result in results do
                    match result with
                    | Ok r -> Assert.That(r.Stdout.Trim(), Is.EqualTo "stress-tick")
                    | Error error -> Assert.Fail $"fan-out run failed: {error.Message}"
            }
            :> Task)

    [<Test>]
    member _.``a churn of StartAsync + kill-on-drop Dispose cycles leaves no zombies or handle growth``() : Task =
        task {
            let cycles = 150
            let pids = ResizeArray<int>()

            do!
                StressHarness.runAndAssertNoLeak (fun () ->
                    task {
                        for _ in 1..cycles do
                            match! runner.StartAsync(sleeper, CancellationToken.None) with
                            | Error error -> Assert.Fail $"StartAsync failed mid-churn: {error.Message}"
                            | Ok running ->
                                running.Pid |> Option.iter pids.Add
                                // Dispose while the child is still alive — the kill-on-drop teardown path,
                                // not the buffered-verb path (already covered by the fan-out test above).
                                do! (running :> IAsyncDisposable).DisposeAsync()
                    }
                    :> Task)

            if isLinux then
                for pid in pids do
                    Assert.That(
                        StressHarness.isZombie pid,
                        Is.False,
                        $"child pid {pid} was left as a zombie after {cycles} start/kill-dispose cycles"
                    )
        }
        :> Task

    [<Test>]
    member _.``a Supervisor restart storm does not leak tasks or memory``() : Task =
        StressHarness.runAndAssertNoLeak (fun () ->
            task {
                // Every incarnation but the last one crashes: a single `RunAsync()` call drives the
                // supervisor through hundreds of incarnations back-to-back with zero backoff, so the
                // restart loop's own per-incarnation allocations (tasks, delegates, backoff state) are
                // what gets stress-tested here — not real subprocess spawning, which the other two
                // scenarios above already cover.
                let totalIncarnations = 300
                let mutable calls = 0

                let scripted: IProcessRunner =
                    ScriptedRunner()
                        .When(
                            Func<Command, bool>(fun _ -> Interlocked.Increment(&calls) < totalIncarnations),
                            Reply.Fail(1, "boom")
                        )
                        .Fallback(Reply.Ok "ok")

                let supervisor =
                    Supervisor(Command.create "stress-supervised")
                        .WithRunner(scripted)
                        .Backoff(TimeSpan.Zero, 1.0)
                        .Jitter(false)
                        .MaxRestarts(totalIncarnations + 10)

                match! supervisor.RunAsync() with
                | Ok outcome ->
                    Assert.That(outcome.Restarts, Is.EqualTo(totalIncarnations - 1))
                    Assert.That(outcome.Stopped, Is.EqualTo StopReason.PolicySatisfied)
                    Assert.That(outcome.FinalResult.IsSuccess, Is.True)
                | Error error -> Assert.Fail $"{error}"
            }
            :> Task)
