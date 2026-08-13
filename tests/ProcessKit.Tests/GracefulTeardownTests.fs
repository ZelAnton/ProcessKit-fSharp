namespace ProcessKit.Tests

open System
open System.Collections.Generic
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit

type private FakeTeardownClock() =
    let delays = List<TimeSpan>()
    let mutable elapsed = TimeSpan.Zero

    member _.Elapsed = elapsed
    member _.Delays = List.ofSeq delays
    member _.Start() = fun () -> elapsed

    member _.Delay(duration: TimeSpan) =
        delays.Add duration
        elapsed <- elapsed + duration
        Task.CompletedTask

[<TestFixture>]
type GracefulTeardownTests() =
    let run (clock: FakeTeardownClock) grace alive forceKill =
        GracefulTeardown.pollUsing clock.Start clock.Delay ignore alive forceKill grace

    [<Test>]
    member _.``zero grace escalates immediately without delaying``() : Task =
        task {
            let clock = FakeTeardownClock()
            let mutable forceKillCount = 0

            do! run clock TimeSpan.Zero (fun () -> true) (fun () -> forceKillCount <- forceKillCount + 1)

            Assert.That(clock.Delays, Is.Empty)
            Assert.That(clock.Elapsed, Is.EqualTo TimeSpan.Zero)
            Assert.That(forceKillCount, Is.EqualTo 1)
        }
        :> Task

    [<Test>]
    member _.``grace shorter than one poll interval delays only to its deadline``() : Task =
        task {
            let clock = FakeTeardownClock()
            let grace = TimeSpan.FromMilliseconds 1.0
            let mutable forceKillAt = None

            do! run clock grace (fun () -> true) (fun () -> forceKillAt <- Some clock.Elapsed)

            Assert.That(clock.Delays, Is.EqualTo<TimeSpan list>([ grace ]))
            Assert.That(clock.Elapsed, Is.EqualTo grace)
            Assert.That(forceKillAt, Is.EqualTo(Some grace))
        }
        :> Task

    [<Test>]
    member _.``intermediate poll delay is bounded by the remaining grace budget``() : Task =
        task {
            let clock = FakeTeardownClock()
            let grace = TimeSpan.FromMilliseconds 120.0
            let mutable forceKillAt = None

            do! run clock grace (fun () -> true) (fun () -> forceKillAt <- Some clock.Elapsed)

            Assert.That(
                clock.Delays,
                Is.EqualTo<TimeSpan list>
                    [ TimeSpan.FromMilliseconds 50.0
                      TimeSpan.FromMilliseconds 50.0
                      TimeSpan.FromMilliseconds 20.0 ]
            )

            Assert.That(clock.Elapsed, Is.EqualTo grace)
            Assert.That(forceKillAt, Is.EqualTo(Some grace))
        }
        :> Task

    [<Test>]
    member _.``process exiting at the grace boundary is not force killed``() : Task =
        task {
            let clock = FakeTeardownClock()
            let grace = TimeSpan.FromMilliseconds 50.0
            let mutable forceKillCount = 0

            do! run clock grace (fun () -> clock.Elapsed < grace) (fun () -> forceKillCount <- forceKillCount + 1)

            Assert.That(clock.Delays, Is.EqualTo<TimeSpan list>([ grace ]))
            Assert.That(clock.Elapsed, Is.EqualTo grace)
            Assert.That(forceKillCount, Is.Zero)
        }
        :> Task

    [<Test>]
    member _.``very large grace keeps the armed delay in range``() : Task =
        task {
            let clock = FakeTeardownClock()
            let mutable aliveChecks = 0

            let alive () =
                aliveChecks <- aliveChecks + 1
                aliveChecks = 1

            do! run clock TimeSpan.MaxValue alive (fun () -> Assert.Fail "process was force killed")

            Assert.That(clock.Delays, Is.EqualTo<TimeSpan list>([ TimeSpan.FromMilliseconds 50.0 ]))
            Assert.That(clock.Delays |> List.forall Timeouts.isArmable, Is.True)
        }
        :> Task
