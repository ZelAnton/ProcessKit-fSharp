using System;
using System.Collections.Generic;
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
    /// as a `NullReferenceException`/mis-named `ArgumentNullException` from deep inside the runner.
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
}
