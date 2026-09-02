using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using ProcessKit;
using ProcessKit.Testing;

namespace ProcessKit.CSharp.Tests;

[TestFixture]
public class NullValidationTests
{
    private static IEnumerable<TestCaseData> PublicBoundaryCases()
    {
        yield return new TestCaseData((Action)(() => _ = new Command(null!)), "program").SetName(
            "Command constructor names program"
        );

        yield return new TestCaseData((Action)(() => _ = new CliClient(null!)), "program").SetName(
            "CliClient constructor names program"
        );

        yield return new TestCaseData((Action)(() => _ = Stdin.FromString(null!)), "text").SetName(
            "Stdin factory names text"
        );

        yield return new TestCaseData(
            (Action)(() => _ = CommandVerbs.RunAsync(null!, CancellationToken.None)),
            "command"
        ).SetName("Command verb names command");

        yield return new TestCaseData(
            (Action)(() => _ = ProcessRunnerExtensions.OutputStringAsync(null!, new Command("tool"), default)),
            "runner"
        ).SetName("Runner extension names runner");

        yield return new TestCaseData((Action)(() => _ = DryRunRunner.Render(null!)), "command").SetName(
            "Testing runner names command"
        );

        yield return new TestCaseData(
            (Action)(() => _ = new RecordReplayOptions().WithArgNormalizer(null!)),
            "normalizer"
        ).SetName("Testing options name normalizer");

        yield return Case(() => _ = new Command("tool").Priority(null!), "priority", "Command.Priority");
        yield return Case(() => _ = new Command("tool").IoPriority(null!), "priority", "Command.IoPriority");
        yield return Case(
            () => _ = new Command("tool").LineTerminator(null!),
            "terminator",
            "Command.LineTerminator"
        );
        yield return Case(
            () => _ = new Command("tool").StdoutLineTerminator(null!),
            "terminator",
            "Command.StdoutLineTerminator"
        );
        yield return Case(
            () => _ = new Command("tool").StderrLineTerminator(null!),
            "terminator",
            "Command.StderrLineTerminator"
        );
        yield return Case(() => _ = new Command("tool").Stdout(null!), "mode", "Command.Stdout");
        yield return Case(() => _ = new Command("tool").Stderr(null!), "mode", "Command.Stderr");
        yield return Case(() => _ = new Command("tool").StopSignal(null!), "signal", "Command.StopSignal");
        yield return Case(() => _ = new Command("tool").CancelSignal(null!), "signal", "Command.CancelSignal");
        yield return Case(
            () => _ = new Command("tool").WindowsIntegrityLevel(null!),
            "level",
            "Command.WindowsIntegrityLevel"
        );
        yield return Case(
            () => _ = OutputBufferPolicy.Default.WithOverflow(null!),
            "overflow",
            "OutputBufferPolicy.WithOverflow"
        );
        yield return Case(
            () => _ = StreamBufferPolicy.Bounded(1).WithFullMode(null!),
            "fullMode",
            "StreamBufferPolicy.WithFullMode"
        );
        yield return Case(
            () => _ = StreamBufferPolicy.Bounded(1, null!),
            "fullMode",
            "StreamBufferPolicy.Bounded fullMode"
        );
        yield return Case(() => _ = ProcessResult.Failure("", null!, 1), "stderr", "ProcessResult.Failure");
        yield return Case(
            () => _ = ProcessResult.Create("", null!, Outcome.NewExited(0), TimeSpan.Zero),
            "stderr",
            "ProcessResult.Create stderr"
        );
        yield return Case(
            () => _ = ProcessResult.Create("", "", null!, TimeSpan.Zero),
            "outcome",
            "ProcessResult.Create outcome"
        );
    }

    [TestCaseSource(nameof(PublicBoundaryCases))]
    public void Public_null_boundaries_report_the_CSharp_parameter_name(Action call, string expectedParamName)
    {
        AssertParamName(call, expectedParamName);
    }

    [Test]
    public void Command_Env_validates_key_before_value()
    {
        var command = new Command("tool");

        AssertParamName(() => _ = command.Env(null!, null!), "key");
        AssertParamName(() => _ = command.Env("KEY", null!), "value");
    }

    [Test]
    public void Runner_ParseAsync_preserves_runner_command_parser_validation_order()
    {
        var runner = new DryRunRunner();
        var command = new Command("tool");

        AssertParamName(
            () => _ = ProcessRunnerExtensions.ParseAsync<string>(null!, null!, null!, default),
            "runner"
        );
        AssertParamName(
            () => _ = ProcessRunnerExtensions.ParseAsync<string>(runner, null!, null!, default),
            "command"
        );
        AssertParamName(
            () => _ = ProcessRunnerExtensions.ParseAsync<string>(runner, command, null!, default),
            "parser"
        );
    }

    [Test]
    public void Record_preserves_path_inner_options_validation_order()
    {
        var path = Path.Combine(Path.GetTempPath(), "processkit-null-validation.json");
        var inner = new DryRunRunner();

        AssertParamName(() => _ = RecordReplayRunner.Record(null!, null!, null!), "path");
        AssertParamName(() => _ = RecordReplayRunner.Record(path, null!, null!), "inner");
        AssertParamName(() => _ = RecordReplayRunner.Record(path, inner, null!), "options");
    }

    [Test]
    public void CliClient_ParseAsync_preserves_parser_before_args_validation_order()
    {
        var client = new CliClient("tool");

        AssertParamName(() => _ = client.ParseAsync<string>(null!, null!, default), "parser");
        AssertParamName(() => _ = client.ParseAsync(null!, static value => value, default), "args");
    }

    [Test]
    public void ProcessResult_Create_validates_stderr_before_outcome()
    {
        AssertParamName(() => _ = ProcessResult.Create("", null!, null!, TimeSpan.Zero), "stderr");
    }

    [Test]
    public void StreamBufferPolicy_Bounded_validates_capacity_before_fullMode()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => _ = StreamBufferPolicy.Bounded(0, null!)
        );

        Assert.That(exception!.ParamName, Is.EqualTo("capacity"));
    }

    private static TestCaseData Case(Action call, string expectedParamName, string name) =>
        new TestCaseData(call, expectedParamName).SetName(name);

    private static void AssertParamName(Action call, string expectedParamName)
    {
        var exception = Assert.Throws<ArgumentNullException>(() => call());
        Assert.That(exception!.ParamName, Is.EqualTo(expectedParamName));
    }
}
