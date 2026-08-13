namespace ProcessKit.Tests

open System
open NUnit.Framework
open ProcessKit

/// Deterministic boundary tests written against SPECIFIC surviving mutants reported by the mutation
/// tier (`scripts/mutate.ps1`; see CONTRIBUTING.md, "Mutation testing").
///
/// Every fixture member here exists because a mutation of the code it covers was applied to the built
/// assembly, the whole gated slice was re-run, and nothing failed — i.e. the limit was executed but
/// never actually pinned. They are therefore assertions about exact values at exact boundaries, not
/// smoke tests: an assertion loose enough to pass under the mutant would defeat the purpose.
///
/// The comment on each member names the mutant it kills, so a later reader can tell a deliberate
/// boundary assertion from an incidental one, and so widening the assertion is visibly a regression
/// of the tier rather than a harmless tidy-up.
///
/// Everything here is hermetic and synchronous — no subprocess, no clock, no I/O. That is what lets
/// the mutation loop re-run this slice once per mutant in seconds, and it is why this fixture is
/// named in `mutation-baseline.json`'s `testFilter`.
[<TestFixture>]
type MutationBoundaryTests() =

    // A stand-in for the jitter source, so every jitter result below is exact rather than sampled.
    let fixedSample (value: float) = fun () -> value

    // ---- Backoff.exponentialDelay -------------------------------------------------------------

    /// Kills: negate-conditional on the `baseDelay <= TimeSpan.Zero` guard.
    [<Test>]
    member _.``exponentialDelay is zero when the base delay is not positive``() =
        let cap = TimeSpan.FromSeconds 30.0
        Assert.That(Backoff.exponentialDelay TimeSpan.Zero 2.0 3 cap, Is.EqualTo TimeSpan.Zero)
        Assert.That(Backoff.exponentialDelay (TimeSpan.FromSeconds -1.0) 2.0 3 cap, Is.EqualTo TimeSpan.Zero)

    /// Kills: negate-conditional on the `cap <= TimeSpan.Zero` guard.
    [<Test>]
    member _.``exponentialDelay is zero when the cap is not positive``() =
        let baseDelay = TimeSpan.FromSeconds 1.0
        Assert.That(Backoff.exponentialDelay baseDelay 2.0 3 TimeSpan.Zero, Is.EqualTo TimeSpan.Zero)
        Assert.That(Backoff.exponentialDelay baseDelay 2.0 3 (TimeSpan.FromSeconds -1.0), Is.EqualTo TimeSpan.Zero)

    /// Kills: negate-conditional on the "under the cap" branch. The expected value is exact
    /// (1s x 2^3 = 8s), so any change to the scaling is visible here rather than merely "still a
    /// positive delay".
    [<Test>]
    member _.``exponentialDelay scales the base delay by factor to the power of the exponent``() =
        let actual =
            Backoff.exponentialDelay (TimeSpan.FromSeconds 1.0) 2.0 3 (TimeSpan.FromSeconds 30.0)

        Assert.That(actual, Is.EqualTo(TimeSpan.FromSeconds 8.0))

    /// Kills: negate-conditional on the `scaled >= cap` clamp. 1s x 2^5 = 32s, over the 30s cap.
    [<Test>]
    member _.``exponentialDelay clamps a scaled delay that reaches the cap``() =
        let cap = TimeSpan.FromSeconds 30.0
        Assert.That(Backoff.exponentialDelay (TimeSpan.FromSeconds 1.0) 2.0 5 cap, Is.EqualTo cap)

    /// Kills: negate-conditional on the `not (Double.IsFinite scaled)` guard. Without it an infinite
    /// scaled delay would reach `TimeSpan.FromSeconds`, which throws rather than clamping.
    [<Test>]
    member _.``exponentialDelay clamps a non-finite scaled delay to the cap``() =
        let cap = TimeSpan.FromSeconds 30.0

        Assert.That(Backoff.exponentialDelay (TimeSpan.FromSeconds 1.0) Double.PositiveInfinity 1 cap, Is.EqualTo cap)

    // ---- Backoff.jitterFactor / applyJitter ----------------------------------------------------

    /// Kills: the `0.5` offset constant and the `+` in `0.5 + nextDouble ()`. Both endpoints are
    /// asserted so neither a shifted offset nor a flipped sign can pass.
    [<Test>]
    member _.``jitterFactor maps a unit sample onto the half-open range starting at one half``() =
        Assert.That(Backoff.jitterFactor (fixedSample 0.0), Is.EqualTo 0.5)
        Assert.That(Backoff.jitterFactor (fixedSample 0.25), Is.EqualTo 0.75)
        Assert.That(Backoff.jitterFactor (fixedSample 0.5), Is.EqualTo 1.0)

    /// Kills: negate-conditional on the `delay <= TimeSpan.Zero` guard.
    [<Test>]
    member _.``applyJitter is zero when the delay is not positive``() =
        Assert.That(Backoff.applyJitter TimeSpan.Zero true (fixedSample 0.0), Is.EqualTo TimeSpan.Zero)

        Assert.That(Backoff.applyJitter (TimeSpan.FromSeconds -1.0) true (fixedSample 0.0), Is.EqualTo TimeSpan.Zero)

    /// Kills: negate-conditional on the `if enabled` branch — a disabled jitter must return the delay
    /// unchanged, not a jittered one.
    [<Test>]
    member _.``applyJitter leaves the delay untouched when jitter is disabled``() =
        let delay = TimeSpan.FromSeconds 4.0
        Assert.That(Backoff.applyJitter delay false (fixedSample 0.0), Is.EqualTo delay)

    /// Kills: the `*` in `delay.TotalSeconds * jitterFactor nextDouble` (a `/` would turn the
    /// bottom-of-range factor 0.5 into a doubling instead of a halving), and the offset constant
    /// again through an exact expected value.
    [<Test>]
    member _.``applyJitter multiplies the delay by the sampled factor``() =
        let delay = TimeSpan.FromSeconds 4.0
        Assert.That(Backoff.applyJitter delay true (fixedSample 0.0), Is.EqualTo(TimeSpan.FromSeconds 2.0))
        Assert.That(Backoff.applyJitter delay true (fixedSample 0.5), Is.EqualTo(TimeSpan.FromSeconds 4.0))

    /// Kills: negate-conditional on the `not (Double.IsFinite scaled)` clamp — the guard that keeps a
    /// pathological sample from reaching `TimeSpan.FromSeconds`, which would throw.
    [<Test>]
    member _.``applyJitter clamps a non-finite scaled delay to the maximum delay``() =
        let delay = TimeSpan.FromSeconds 4.0

        Assert.That(Backoff.applyJitter delay true (fixedSample Double.PositiveInfinity), Is.EqualTo Backoff.maxDelay)

    /// Kills: negate-conditional on the `scaled >= maxDelay` clamp, and pins the documented ceiling:
    /// the result stays inside what `Task.Delay` accepts.
    [<Test>]
    member _.``applyJitter clamps an over-large jittered delay to the maximum delay``() =
        let delay = TimeSpan.FromMilliseconds(float Int32.MaxValue)
        Assert.That(Backoff.applyJitter delay true (fixedSample 0.75), Is.EqualTo Backoff.maxDelay)

    // ---- RetryDelayPolicy.delay -----------------------------------------------------------------

    /// Kills: negate-conditional on the policy match — a `Fixed` policy must ignore both the retry
    /// number and the jitter source.
    [<Test>]
    member _.``RetryDelayPolicy Fixed returns the same delay for every retry``() =
        let policy = RetryDelayPolicy.Fixed(TimeSpan.FromSeconds 2.0)

        for retryNumber in 1..5 do
            let actual = RetryDelayPolicy.delay retryNumber (fixedSample 0.0) policy
            Assert.That(actual, Is.EqualTo(TimeSpan.FromSeconds 2.0))

    /// Kills: the `max 0` floor in `max 0 (retryNumber - 1)`. Raising that floor to 1 would make the
    /// first retry wait a full backoff step instead of the base delay.
    [<Test>]
    member _.``RetryDelayPolicy Exponential uses exponent zero on the first retry``() =
        let policy =
            RetryDelayPolicy.Exponential(TimeSpan.FromSeconds 1.0, 2.0, TimeSpan.FromSeconds 60.0, false)

        let actual = RetryDelayPolicy.delay 1 (fixedSample 0.0) policy
        Assert.That(actual, Is.EqualTo(TimeSpan.FromSeconds 1.0))

    /// Kills: the `- 1` in `retryNumber - 1` — both the `1` constant and the subtraction itself. The
    /// third retry is exponent 2, so 1s x 2^2 = 4s; a flipped sign gives 16s and a shifted constant
    /// gives 2s, and both are excluded by an exact expectation.
    [<Test>]
    member _.``RetryDelayPolicy Exponential offsets the retry number by exactly one``() =
        let policy =
            RetryDelayPolicy.Exponential(TimeSpan.FromSeconds 1.0, 2.0, TimeSpan.FromSeconds 60.0, false)

        Assert.That(RetryDelayPolicy.delay 2 (fixedSample 0.0) policy, Is.EqualTo(TimeSpan.FromSeconds 2.0))
        Assert.That(RetryDelayPolicy.delay 3 (fixedSample 0.0) policy, Is.EqualTo(TimeSpan.FromSeconds 4.0))

    /// Pins that the jitter flag is threaded through rather than dropped on the floor.
    [<Test>]
    member _.``RetryDelayPolicy Exponential applies jitter when the policy asks for it``() =
        let policy =
            RetryDelayPolicy.Exponential(TimeSpan.FromSeconds 4.0, 2.0, TimeSpan.FromSeconds 60.0, true)

        let actual = RetryDelayPolicy.delay 1 (fixedSample 0.0) policy
        Assert.That(actual, Is.EqualTo(TimeSpan.FromSeconds 2.0))

    // ---- CgroupCpuMax ---------------------------------------------------------------------------

    /// Kills: the `*` in `cores * PeriodMicroseconds` and the period constant itself. Exact expected
    /// quotas, because "some positive number of microseconds" would survive both mutations.
    [<Test>]
    member _.``calculateQuota scales a core share by the cgroup period``() =
        Assert.That(CgroupCpuMax.calculateQuota 0.5, Is.EqualTo 50_000.0)
        Assert.That(CgroupCpuMax.calculateQuota 1.0, Is.EqualTo 100_000.0)
        Assert.That(CgroupCpuMax.calculateQuota 2.5, Is.EqualTo 250_000.0)

    /// Kills: the `1.0` floor in `max 1.0 ...`. A cgroup rejects a zero quota, so a vanishing core
    /// share has to round up to one microsecond rather than down to zero.
    [<Test>]
    member _.``calculateQuota floors a vanishing core share at one microsecond``() =
        Assert.That(CgroupCpuMax.calculateQuota 0.0, Is.EqualTo 1.0)
        Assert.That(CgroupCpuMax.calculateQuota 1e-9, Is.EqualTo 1.0)

    /// Kills: the negate-conditional over the `||` chain and the comparison behind
    /// `quota >= Int64.MaxValue`. Both the accepting and the rejecting side are asserted, so
    /// inverting the predicate cannot pass.
    [<Test>]
    member _.``isQuotaOverflow rejects exactly the quotas that cannot be written``() =
        Assert.That(CgroupCpuMax.isQuotaOverflow Double.NaN, Is.True)
        Assert.That(CgroupCpuMax.isQuotaOverflow Double.PositiveInfinity, Is.True)
        Assert.That(CgroupCpuMax.isQuotaOverflow Double.NegativeInfinity, Is.True)
        Assert.That(CgroupCpuMax.isQuotaOverflow (float Int64.MaxValue), Is.True)
        Assert.That(CgroupCpuMax.isQuotaOverflow 100_000.0, Is.False)
        Assert.That(CgroupCpuMax.isQuotaOverflow 1.0, Is.False)

    /// Kills: the period constant and the interpolation holes in the `cpu.max` line. The kernel parses
    /// this file positionally, so the exact rendered text is the contract.
    [<Test>]
    member _.``formatCpuMax renders the quota and the period as a cgroup cpu-max line``() =
        Assert.That(CgroupCpuMax.formatCpuMax 50_000.0, Is.EqualTo "50000 100000")
        Assert.That(CgroupCpuMax.formatCpuMax 1.0, Is.EqualTo "1 100000")

    // ---- LineTerminator -------------------------------------------------------------------------

    /// Asserts the whole matrix (each case against each predicate), because checking only the true
    /// cases would let a widened comparison through.
    ///
    /// Note for the mutation tier: mutants inside the GENERATED `IsLf`/`IsCr`/... property bodies are
    /// not killable from F# and are excluded by scope (`excludeCompilerGeneratedMethods`) — F# inlines
    /// the tag comparison at the call site, so the emitted bodies never run. This test still earns its
    /// place: it pins the observable contract for the C# consumers that DO call those properties, and
    /// for the `LineTerminatorRules` predicates below, whose mutants are killable and are killed here.
    [<Test>]
    member _.``LineTerminator case predicates identify exactly one case each``() =
        let cases =
            [ LineTerminator.Lf, (true, false, false, false)
              LineTerminator.Cr, (false, true, false, false)
              LineTerminator.CrLf, (false, false, true, false)
              LineTerminator.Any, (false, false, false, true) ]

        for terminator, (isLf, isCr, isCrLf, isAny) in cases do
            Assert.That(terminator.IsLf, Is.EqualTo isLf)
            Assert.That(terminator.IsCr, Is.EqualTo isCr)
            Assert.That(terminator.IsCrLf, Is.EqualTo isCrLf)
            Assert.That(terminator.IsAny, Is.EqualTo isAny)

    // ---- OutputBufferPolicy / StreamBufferPolicy argument boundaries -----------------------------

    /// Kills: the `1` in `ThrowIfLessThan(capacity, 1)`. Capacity 1 is the smallest legal bounded
    /// channel and must be accepted; 0 must not.
    [<Test>]
    member _.``StreamBufferPolicy Bounded accepts a capacity of one and rejects zero``() =
        Assert.That(StreamBufferPolicy.Bounded(1).Capacity, Is.EqualTo 1)

        Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> StreamBufferPolicy.Bounded 0 |> ignore))
        |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> StreamBufferPolicy.Bounded -1 |> ignore))
        |> ignore

    /// Kills: the `1` in the SECOND `Bounded` overload's `ThrowIfLessThan(capacity, 1)`.
    ///
    /// The two overloads validate independently, and the tier proved it: the one-argument overload was
    /// pinned by the test above while the explicit-full-mode overload's identical guard went unnoticed.
    /// That is the whole point of mutation testing over coverage — both lines were "covered".
    [<Test>]
    member _.``StreamBufferPolicy Bounded with an explicit full mode validates its capacity too``() =
        let policy = StreamBufferPolicy.Bounded(1, StreamFullMode.DropOldest)
        Assert.That(policy.Capacity, Is.EqualTo 1)
        Assert.That(policy.FullMode, Is.EqualTo StreamFullMode.DropOldest)

        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> StreamBufferPolicy.Bounded(0, StreamFullMode.Error) |> ignore)
        )
        |> ignore

    /// Guards the mirror-image boundary on the buffered policy: zero is a legal "retain nothing"
    /// ceiling, only a negative one is rejected.
    [<Test>]
    member _.``OutputBufferPolicy line and byte ceilings accept zero and reject negatives``() =
        Assert.That(OutputBufferPolicy.Bounded(0).MaxLines, Is.EqualTo(Some 0))
        Assert.That(OutputBufferPolicy.Unbounded.WithMaxBytes(0).MaxBytes, Is.EqualTo(Some 0))

        Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> OutputBufferPolicy.Bounded -1 |> ignore))
        |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> OutputBufferPolicy.Unbounded.WithMaxBytes -1 |> ignore)
        )
        |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(
            Action(fun () -> OutputBufferPolicy.Unbounded.WithMaxLines -1 |> ignore)
        )
        |> ignore

    // ---- Pump.LineBuffer ------------------------------------------------------------------------

    /// Kills: the `0` in `let bytes = if needBytes then ... else 0`.
    ///
    /// Under the default policy the per-line UTF-8 scan is skipped as an optimisation, and the
    /// documented consequence is that `TotalBytes` is meaningless (zero) in that mode. Nothing
    /// asserted that, so a mutant that started accounting bytes anyway went unnoticed — which would
    /// have silently reintroduced the per-line scan the fast path exists to avoid.
    [<Test>]
    member _.``LineBuffer accounts no bytes under a policy with neither a byte cap nor a fail-loud ceiling``() =
        let buffer = Pump.LineBuffer(OutputBufferPolicy.Unbounded)
        [ "alpha"; "beta"; "gamma" ] |> List.iter buffer.Add

        Assert.That(buffer.TotalLines, Is.EqualTo 3)
        Assert.That(buffer.TotalBytes, Is.EqualTo 0)
        Assert.That(buffer.Text, Is.EqualTo "alpha\nbeta\ngamma")

    /// Kills: the short-circuit in `dropNewestByteCapClosed <- dropNewestByteCapClosed || ...`.
    ///
    /// `DropNewest` promises a CONTIGUOUS prefix: once the byte cap has rejected a line, every later
    /// line is dropped even if it would have fitted, so the captured output is never a prefix with a
    /// hole in it. The existing coverage stopped one line too early — it ended on the first line after
    /// the rejection, which is dropped for an unrelated reason (it also exceeds the cap). Only a
    /// SECOND short line distinguishes "the cap stayed closed" from "the cap reopened", and that is
    /// the line this test adds.
    [<Test>]
    member _.``LineBuffer DropNewest keeps the byte cap closed for every later line``() =
        let buffer =
            Pump.LineBuffer(OutputBufferPolicy.Unbounded.WithMaxBytes(10).WithOverflow OverflowMode.DropNewest)

        [ "aaaa"; String('b', 11); "cc"; "dd"; "e" ] |> List.iter buffer.Add

        Assert.That(buffer.Text, Is.EqualTo "aaaa")
        Assert.That(buffer.Truncated, Is.True)
        Assert.That(buffer.TotalLines, Is.EqualTo 5)

    // ---- Pump.RawBuffer -------------------------------------------------------------------------

    /// Pins the documented head/tail split at the cap boundary: `DropOldest` keeps the LAST `cap`
    /// bytes, `DropNewest` and `Error` keep the FIRST `cap` bytes. Asserting the exact retained bytes
    /// (not merely their length) is what makes an eviction that trims the wrong end observable.
    [<Test>]
    member _.``RawBuffer retains the documented end of an over-cap stream``() =
        let feed (overflow: OverflowMode) =
            let buffer = Pump.RawBuffer(4, overflow)

            for byte in [| 1uy; 2uy; 3uy; 4uy; 5uy; 6uy |] do
                buffer.Append([| byte |], 0, 1)

            buffer

        let tail = feed OverflowMode.DropOldest
        Assert.That(tail.ToArray(), Is.EqualTo<byte[]> [| 3uy; 4uy; 5uy; 6uy |])
        Assert.That(tail.Truncated, Is.True)
        Assert.That(tail.TooLarge, Is.False)
        Assert.That(tail.TotalBytes, Is.EqualTo 6)

        let head = feed OverflowMode.DropNewest
        Assert.That(head.ToArray(), Is.EqualTo<byte[]> [| 1uy; 2uy; 3uy; 4uy |])
        Assert.That(head.Truncated, Is.True)

        let failLoud = feed OverflowMode.Error
        Assert.That(failLoud.ToArray(), Is.EqualTo<byte[]> [| 1uy; 2uy; 3uy; 4uy |])
        Assert.That(failLoud.TooLarge, Is.True)
        Assert.That(failLoud.Truncated, Is.False)

    /// The exact-cap boundary: a stream of exactly `cap` bytes is retained whole and is NOT reported
    /// as truncated or over-large. This is the "equal to the limit is retained" half of the contract,
    /// which an over-cap test alone cannot pin.
    [<Test>]
    member _.``RawBuffer keeps a stream of exactly the cap and reports no truncation``() =
        for overflow in [ OverflowMode.DropOldest; OverflowMode.DropNewest; OverflowMode.Error ] do
            let buffer = Pump.RawBuffer(4, overflow)
            buffer.Append([| 1uy; 2uy; 3uy; 4uy |], 0, 4)

            Assert.That(buffer.ToArray(), Is.EqualTo<byte[]> [| 1uy; 2uy; 3uy; 4uy |])
            Assert.That(buffer.Truncated, Is.False)
            Assert.That(buffer.TooLarge, Is.False)
            Assert.That(buffer.TotalBytes, Is.EqualTo 4)

    /// A tail eviction that has to trim INSIDE a retained chunk rather than drop it whole — the
    /// sub-chunk `frontOffset` path. Chunked appends of uneven size are what a real pipe delivers, so
    /// this pins the byte order across an eviction that lands mid-chunk.
    [<Test>]
    member _.``RawBuffer evicts inside a chunk when only part of it is stale``() =
        let buffer = Pump.RawBuffer(5, OverflowMode.DropOldest)
        buffer.Append([| 1uy; 2uy; 3uy; 4uy |], 0, 4)
        buffer.Append([| 5uy; 6uy; 7uy |], 0, 3)

        Assert.That(buffer.ToArray(), Is.EqualTo<byte[]> [| 3uy; 4uy; 5uy; 6uy; 7uy |])
        Assert.That(buffer.Truncated, Is.True)
        Assert.That(buffer.TotalBytes, Is.EqualTo 7)

    /// The `offset`/`count` window has to be honoured: the buffer copies out only the requested slice,
    /// because the caller reuses the source array across reads.
    [<Test>]
    member _.``RawBuffer copies only the requested slice of the source array``() =
        let buffer = Pump.RawBuffer(8, OverflowMode.DropOldest)
        let source = [| 9uy; 9uy; 1uy; 2uy; 3uy; 9uy |]
        buffer.Append(source, 2, 3)

        Assert.That(buffer.ToArray(), Is.EqualTo<byte[]> [| 1uy; 2uy; 3uy |])
        Assert.That(buffer.TotalBytes, Is.EqualTo 3)
