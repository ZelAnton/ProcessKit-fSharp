using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using NUnit.Framework;
using ProcessKit;

namespace ProcessKit.CSharp.Tests;

/// Covers the C#-idiomatic consumption of `Result<'T, ProcessError>` and `Outcome` documented in
/// `docs/README.md` / `ResultExtensions.fs` / `Outcome.fs`: property-pattern `switch` matching on
/// `IsOk`/`ResultValue`/`ErrorValue` with no projection helper, and the `Match`/`Switch`/
/// `TryGetValue`/`GetValueOrThrow` extensions for the non-`switch` styles.
[TestFixture]
public class ResultAndOutcomeTests
{
    [Test]
    public async Task OutputStringAsync_switch_pattern_matches_success_with_no_helper()
    {
        var command = Shell.Run("echo ready");

        var message = await command.OutputStringAsync() switch
        {
            { IsOk: true, ResultValue: var result } => result.Stdout.Trim(),
            { IsOk: false, ErrorValue: var err } => err.Message,
        };

        Assert.That(message, Is.EqualTo("ready"));
    }

    [Test]
    public async Task RunAsync_surfaces_a_non_zero_exit_as_a_typed_Exit_error()
    {
        var command = Shell.Run("exit 3");

        var result = await command.RunAsync();

        Assert.That(result.IsOk, Is.False);
        var err = result.ErrorValue;
        Assert.That(err.IsExit, Is.True);
        Assert.That(err.Code is { Value: 3 });
    }

    [Test]
    public async Task Outcome_tester_property_and_Code_accessor_read_without_destructuring()
    {
        await using var running = (await Shell.Run("exit 5").StartAsync()).GetValueOrThrow();

        var outcome = await running.WaitAsync();

        Assert.That(outcome.IsExited, Is.True);
        Assert.That(outcome.IsSignalled, Is.False);
        Assert.That(outcome.IsTimedOut, Is.False);
        Assert.That(outcome.Code is { Value: 5 });
    }

    [Test]
    public void Match_projects_both_arms_to_a_single_value()
    {
        var ok = FSharpResult<string, ProcessError>.NewOk("hi");
        var error = FSharpResult<string, ProcessError>.NewError(ProcessError.NewCancelled("git"));

        Assert.That(ok.Match(v => v.ToUpperInvariant(), e => e.Message), Is.EqualTo("HI"));
        Assert.That(error.Match(v => v, _ => "fallback"), Is.EqualTo("fallback"));
    }

    [Test]
    public void Switch_runs_the_matching_side_effect_only()
    {
        var ok = FSharpResult<int, ProcessError>.NewOk(42);
        var seen = -1;

        ok.Switch(v => seen = v, _ => seen = -1);

        Assert.That(seen, Is.EqualTo(42));
    }

    [Test]
    public void TryGetValue_yields_the_value_on_success_and_the_error_on_failure()
    {
        var ok = FSharpResult<int, ProcessError>.NewOk(7);

        Assert.That(ok.TryGetValue(out var value, out var noError), Is.True);
        Assert.That(value, Is.EqualTo(7));
        Assert.That(noError, Is.Null);

        var failure = FSharpResult<int, ProcessError>.NewError(ProcessError.NewCancelled("git"));

        Assert.That(failure.TryGetValue(out _, out var error), Is.False);
        Assert.That(error!.IsCancelled, Is.True);
    }

    [Test]
    public void GetValueOrThrow_returns_the_value_on_success()
    {
        var ok = FSharpResult<int, ProcessError>.NewOk(9);
        Assert.That(ok.GetValueOrThrow(), Is.EqualTo(9));
    }

    [Test]
    public void GetValueOrThrow_raises_ProcessException_carrying_the_error_on_failure()
    {
        var failure = FSharpResult<int, ProcessError>.NewError(ProcessError.NewIo("disk full"));

        var thrown = Assert.Throws<ProcessException>(() => failure.GetValueOrThrow());
        Assert.That(thrown!.Error.IsIo, Is.True);
    }
}
