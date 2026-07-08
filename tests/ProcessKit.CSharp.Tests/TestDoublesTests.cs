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
}
