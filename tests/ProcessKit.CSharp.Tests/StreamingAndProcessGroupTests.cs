using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ProcessKit;

namespace ProcessKit.CSharp.Tests;

/// Covers `await using` for the two live, kill-on-dispose handles (`docs/streaming.md` /
/// `docs/process-groups.md`): the per-run `RunningProcess` returned by `StartAsync()`, and a shared
/// `ProcessGroup` that containment for a whole tree of children.
[TestFixture]
public class StreamingAndProcessGroupTests
{
    private static string LingeringEcho(string marker) =>
        Shell.IsWindows ? $"echo {marker}&ping 127.0.0.1 -n 3 >NUL" : $"echo {marker}; sleep 2";

    private static string Lingering() => Shell.IsWindows ? "ping 127.0.0.1 -n 5 >NUL" : "sleep 4";

    [Test]
    public void ProcessGroup_options_overloads_reject_null_at_the_public_boundary()
    {
        var createException = Assert.Throws<ArgumentNullException>(() => ProcessGroup.Create(null!));
        var capabilitiesException = Assert.Throws<ArgumentNullException>(() => ProcessGroup.Capabilities(null!));

        Assert.That(createException!.ParamName, Is.EqualTo("options"));
        Assert.That(capabilitiesException!.ParamName, Is.EqualTo("options"));
    }

    [Test]
    public void ProcessGroup_StartAsync_null_command_wins_over_cancellation_without_mutating_the_group()
    {
        using var group = ProcessGroup.Create().GetValueOrThrow();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var membersBefore = group.Members().GetValueOrThrow();

        var exception = Assert.Throws<ArgumentNullException>(() => group.StartAsync(null!, cancelled.Token));

        Assert.That(exception!.ParamName, Is.EqualTo("command"));
        Assert.That(group.Members().GetValueOrThrow(), Is.EqualTo(membersBefore));
    }

    [TestCase("capture-string")]
    [TestCase("capture-bytes")]
    [TestCase("spawn")]
    public void JobRunner_primitives_reject_null_command_before_cancellation_or_spawn(string primitive)
    {
        IProcessRunner runner = new JobRunner();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Action call = primitive switch
        {
            "capture-string" => () => runner.CaptureStringAsync(null!, cancelled.Token),
            "capture-bytes" => () => runner.CaptureBytesAsync(null!, cancelled.Token),
            _ => () => runner.SpawnAsync(null!, cancelled.Token),
        };

        var exception = Assert.Throws<ArgumentNullException>(() => call());

        Assert.That(exception!.ParamName, Is.EqualTo("command"));
    }

    [TestCase("capture-string")]
    [TestCase("capture-bytes")]
    [TestCase("spawn")]
    public void ProcessGroup_runner_primitives_reject_null_without_mutating_the_group(string primitive)
    {
        using var group = ProcessGroup.Create().GetValueOrThrow();
        IProcessRunner runner = group;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var membersBefore = group.Members().GetValueOrThrow();

        Action call = primitive switch
        {
            "capture-string" => () => runner.CaptureStringAsync(null!, cancelled.Token),
            "capture-bytes" => () => runner.CaptureBytesAsync(null!, cancelled.Token),
            _ => () => runner.SpawnAsync(null!, cancelled.Token),
        };

        var exception = Assert.Throws<ArgumentNullException>(() => call());

        Assert.That(exception!.ParamName, Is.EqualTo("command"));
        Assert.That(group.Members().GetValueOrThrow(), Is.EqualTo(membersBefore));
    }

    [Test]
    public async Task await_using_a_RunningProcess_waits_for_a_line_then_reaps_the_rest_on_dispose()
    {
        var command = Shell.Run(LingeringEcho("ready"));

        await using var running = (await command.StartAsync()).GetValueOrThrow();

        var line = (await running.WaitForLineAsync(l => l.Contains("ready"), TimeSpan.FromSeconds(5)))
            .GetValueOrThrow();

        Assert.That(line, Does.Contain("ready"));
        // Falling out of the `await using` scope disposes the handle and kills the whole tree.
    }

    [Test]
    public async Task await_using_a_ProcessGroup_contains_everything_it_starts()
    {
        await using var group = ProcessGroup.Create().GetValueOrThrow();

        await using var running = (await group.StartAsync(Shell.Run(Lingering()))).GetValueOrThrow();

        var members = group.Members().GetValueOrThrow();
        Assert.That(members, Is.Not.Empty);

        // Disposing the group (at the end of this scope) reaps the whole tree, even though
        // disposing just `running` above only detaches its I/O (the group owns its lifetime).
    }

    [Test]
    public async Task a_released_ProcessGroup_rejects_further_tree_control_calls()
    {
        var group = ProcessGroup.Create().GetValueOrThrow();

        await using (group)
        {
            // Empty scope: dispose fires deterministically at the closing brace.
        }

        var members = group.Members();

        Assert.That(members.IsOk, Is.False);
        Assert.That(members.ErrorValue.IsUnsupported, Is.True);
    }

    [Test]
    public void AdoptByPid_is_usable_from_CSharp_and_refuses_this_process_by_its_own_pid()
    {
        // The bare-pid door exists mainly for callers who hold nothing but a number — a pidfile, a
        // registry, an FFI/IPC boundary — which is exactly the shape a C# orchestrator arrives in. Read
        // here the way such a caller reads it: an `int` in, a `Result` out, no F# pattern matching.
        using var group = ProcessGroup.Create().GetValueOrThrow();

        var refused = group.AdoptByPid(Environment.ProcessId);

        Assert.That(refused.IsOk, Is.False, "adopting our own pid would enlist us in our own group's teardown");
        Assert.That(refused.ErrorValue.IsAdopt, Is.True);
        Assert.That(refused.ErrorValue.Message, Is.Not.Empty);

        // The capability snapshot answers the same question up front, and its own axis for it.
        var adoption = ProcessGroup.Capabilities().AdoptionByPid;
        Assert.That(adoption.IsAvailable || adoption.IsQualified || adoption.IsUnsupported, Is.True);
    }

    [Test]
    public async Task WaitForNamedPipeAsync_is_usable_from_CSharp_and_reports_a_listening_pipe_as_ready()
    {
        // Windows-only readiness verb (T-378): every other platform refuses with a typed Unsupported
        // before ever touching a pipe, exercised in F# (ReadinessTests.fs); this test proves the C#
        // call shape itself — an explicit type, no F#-specific convenience — against a real pipe.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Windows-only: exercises the named-pipe readiness probe");
        }

        var pipeName = $"processkit-csharp-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1);
        _ = server.WaitForConnectionAsync();

        await using var running = (await Shell.Run(Lingering()).StartAsync()).GetValueOrThrow();

        var ready = await running.WaitForNamedPipeAsync(pipeName, TimeSpan.FromSeconds(5));

        Assert.That(ready.IsOk, Is.True, ready.IsOk ? "" : ready.ErrorValue.Message);
    }
}
