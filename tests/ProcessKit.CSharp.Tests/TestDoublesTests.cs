using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ProcessKit;
using ProcessKit.Testing;

namespace ProcessKit.CSharp.Tests;

/// Covers the subprocess-free doubles in `ProcessKit.Testing` (`docs/testing.md`), used here — as the
/// task criteria intend — for scenarios about testability rather than real process behaviour:
/// `ScriptedRunner` for the bulk capture verbs, and `FakeProcess` to build a live `RunningProcess`
/// double for the streaming surface, both with no subprocess spawned.
[TestFixture]
public class TestDoublesTests
{
    private static void AssertNullCommandRejected(IProcessRunner runner, Func<int> sideEffectCount)
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Action[] calls =
        [
            () => runner.CaptureStringAsync(null!, cancelled.Token),
            () => runner.CaptureBytesAsync(null!, cancelled.Token),
            () => runner.SpawnAsync(null!, cancelled.Token),
        ];

        foreach (var call in calls)
        {
            var exception = Assert.Throws<ArgumentNullException>(() => call());
            Assert.That(exception!.ParamName, Is.EqualTo("command"));
            Assert.That(sideEffectCount(), Is.Zero);
        }
    }

    /// A small typed wrapper, generic over the runner it is given - the seam every C# consumer is
    /// meant to depend on (`IProcessRunner`), mirroring `docs/testing.md`'s `Git` example.
    private static Task<Microsoft.FSharp.Core.FSharpResult<string, ProcessError>> Head(
        IProcessRunner runner,
        CancellationToken ct
    ) => runner.RunAsync(new Command("git").Args(["rev-parse", "HEAD"]), ct);

    [Test]
    public async Task ScriptedRunner_stubs_a_matched_command_with_no_real_spawn()
    {
        var runner = new ScriptedRunner().On(["git", "rev-parse", "HEAD"], Reply.Ok("abc123\n"));

        var result = await Head(runner, CancellationToken.None);

        Assert.That(result.IsOk, Is.True);
        Assert.That(result.ResultValue, Is.EqualTo("abc123"));
    }

    [Test]
    public async Task ScriptedRunner_fallback_reports_a_non_zero_exit_as_data_through_an_honest_result_verb()
    {
        var runner = new ScriptedRunner().Fallback(Reply.Fail(2, "boom"));
        var grep = new Command("grep").Args(["needle", "file"]);

        var outcome = await runner.OutputStringAsync(grep, CancellationToken.None);

        Assert.That(outcome.IsOk, Is.True);
        Assert.That(outcome.ResultValue.IsSuccess, Is.False);
        Assert.That(outcome.ResultValue.Code is { Value: 2 });
    }

    [Test]
    public async Task FakeProcess_builds_a_RunningProcess_double_for_streaming_with_no_real_spawn()
    {
        await using var fake = FakeProcess.Create("stub").WithStdoutLines(["first", "second"]).WithExit(0).Build();

        var lines = new List<string>();

        await foreach (var line in fake.StdoutLinesAsync())
        {
            lines.Add(line);
        }

        var finished = (await fake.FinishAsync()).GetValueOrThrow();

        Assert.That(lines, Is.EqualTo(new[] { "first", "second" }));
        Assert.That(finished.Outcome.IsExited, Is.True);
    }

    /// A null `stdout` from a C# call site must fail loudly at the public entry point with
    /// `ArgumentNullException` naming the actual parameter - not deep inside `Encoding.GetBytes`/
    /// `.Length` with an unrelated parameter name (the bug T-195 fixes).
    [Test]
    public void FakeProcess_WithStdout_null_throws_ArgumentNullException_naming_text()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => FakeProcess.Create("stub").WithStdout(null!));

        Assert.That(ex!.ParamName, Is.EqualTo("text"));
    }

    /// A null `stdout` passed to `Reply.Ok` must fail at the public entry point, not resurface later
    /// as a `NullReferenceException`/misnamed `ArgumentNullException` from deep inside the runner.
    [Test]
    public void Reply_Ok_null_stdout_throws_ArgumentNullException_naming_stdout()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => Reply.Ok(null!));

        Assert.That(ex!.ParamName, Is.EqualTo("stdout"));
    }

    /// A null `tokens` sequence passed to `ScriptedRunner.On` must fail immediately, rather than
    /// falling all the way through to a deep `NullReferenceException` inside `List.ofSeq`.
    [Test]
    public void ScriptedRunner_On_null_tokens_throws_ArgumentNullException_naming_tokens()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new ScriptedRunner().On(null!, Reply.Ok("")));

        Assert.That(ex!.ParamName, Is.EqualTo("tokens"));
    }

    [Test]
    public void ScriptedRunner_primitives_reject_null_without_recording_an_invocation()
    {
        var runner = new ScriptedRunner();

        AssertNullCommandRejected(runner, () => runner.Received.Count);
    }

    [Test]
    public void DryRunRunner_primitives_reject_null_without_recording_history()
    {
        var runner = new DryRunRunner();

        AssertNullCommandRejected(runner, () => runner.History.Count);
    }

    [Test]
    public void FaultInjectingRunner_primitives_reject_null_without_consuming_an_injection()
    {
        var runner = new FaultInjectingRunner(new ScriptedRunner(), 3, FaultInjection.Delegate());

        AssertNullCommandRejected(runner, () => runner.InvocationCount);
    }

    [Test]
    public void RecordReplayRunner_record_primitives_reject_null_without_delegating_or_writing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"processkit-null-{Guid.NewGuid():N}.json");
        var inner = new ScriptedRunner();

        using (var runner = RecordReplayRunner.Record(path, inner))
        {
            AssertNullCommandRejected(runner, () => inner.Received.Count);
        }

        Assert.That(File.Exists(path), Is.False);
    }

    [Test]
    public async Task RecordReplayRunner_replay_primitives_reject_null_without_consuming_the_entry()
    {
        var path = Path.Combine(Path.GetTempPath(), $"processkit-null-{Guid.NewGuid():N}.json");
        var command = new Command("probe");

        try
        {
            using (var recorder = RecordReplayRunner.Record(path, new ScriptedRunner().Fallback(Reply.Ok("ready"))))
            {
                var recorded = await ((IProcessRunner)recorder).CaptureStringAsync(command, CancellationToken.None);
                Assert.That(recorded.IsOk, Is.True);
                Assert.That(recorder.Save().IsOk, Is.True);
            }

            using var replayer = RecordReplayRunner.Replay(path).GetValueOrThrow();
            AssertNullCommandRejected(replayer, () => 0);

            var replayed = await ((IProcessRunner)replayer).CaptureStringAsync(command, CancellationToken.None);
            Assert.That(replayed.IsOk, Is.True);
            Assert.That(replayed.ResultValue.Stdout, Is.EqualTo("ready"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
