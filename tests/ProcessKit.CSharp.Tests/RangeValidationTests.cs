using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using ProcessKit;
using ProcessKit.Extensions.DependencyInjection;
using ProcessKit.Extensions.Hosting;

namespace ProcessKit.CSharp.Tests;

[TestFixture]
public class RangeValidationTests
{
    private static readonly TimeSpan NegativeDuration = TimeSpan.FromTicks(-1);

    private static IEnumerable<TestCaseData> PublicBoundaryCases()
    {
        yield return Case(() => new Command("tool").Timeout(NegativeDuration), "duration", "Command.Timeout");
        yield return Case(() => new Command("tool").TimeoutGrace(NegativeDuration), "grace", "Command.TimeoutGrace");
        yield return Case(() => new Command("tool").IdleTimeout(NegativeDuration), "duration", "Command.IdleTimeout");
        yield return Case(() => new Command("tool").CancelGrace(NegativeDuration), "grace", "Command.CancelGrace");
        yield return Case(
            () => new Command("tool").Retry(1, NegativeDuration, _ => true),
            "delay",
            "Command.Retry delay"
        );
        yield return Case(
            () => new Command("tool").RetryBackoff(1, NegativeDuration, 1.0, TimeSpan.Zero, false, _ => true),
            "baseDelay",
            "Command.RetryBackoff baseDelay"
        );
        yield return Case(
            () => new Command("tool").RetryBackoff(1, TimeSpan.Zero, 1.0, NegativeDuration, false, _ => true),
            "maxDelay",
            "Command.RetryBackoff maxDelay"
        );
        yield return Case(() => new Command("tool").ExtraFd(2), "targetFd", "Command.ExtraFd");
        yield return Case(() => new Command("tool").Umask(-1), "mask", "Command.Umask lower bound");
        yield return Case(() => new Command("tool").Umask(4096), "mask", "Command.Umask upper bound");
        yield return Case(() => new Command("tool").Uid(-1), "uid", "Command.Uid");
        yield return Case(() => new Command("tool").Gid(-1), "gid", "Command.Gid");
        yield return Case(() => new Command("tool").User(-1, 0), "uid", "Command.User uid");
        yield return Case(() => new Command("tool").User(0, -1), "gid", "Command.User gid");

        yield return Case(() => ResourceLimits.None.WithMemoryMax(0), "bytes", "ResourceLimits.WithMemoryMax");
        yield return Case(() => ResourceLimits.None.WithMaxProcesses(0), "count", "ResourceLimits.WithMaxProcesses");
        yield return Case(
            () => new ProcessGroupOptions().WithShutdownTimeout(NegativeDuration),
            "timeout",
            "ProcessGroupOptions.WithShutdownTimeout"
        );

        yield return Case(() => OutputBufferPolicy.Bounded(-1), "maxLines", "OutputBufferPolicy.Bounded");
        yield return Case(() => OutputBufferPolicy.FailLoud(-1), "maxLines", "OutputBufferPolicy.FailLoud");
        yield return Case(
            () => OutputBufferPolicy.Unbounded.WithMaxLines(-1),
            "maxLines",
            "OutputBufferPolicy.WithMaxLines"
        );
        yield return Case(
            () => OutputBufferPolicy.Unbounded.WithMaxBytes(-1),
            "maxBytes",
            "OutputBufferPolicy.WithMaxBytes"
        );
        yield return Case(() => StreamBufferPolicy.Bounded(0), "capacity", "StreamBufferPolicy.Bounded");
        yield return Case(
            () => StreamBufferPolicy.Bounded(0, StreamFullMode.Error),
            "capacity",
            "StreamBufferPolicy.Bounded with mode"
        );

        yield return Case(
            () => new Command("left").Pipe(new Command("right")).Timeout(NegativeDuration),
            "duration",
            "Pipeline.Timeout"
        );
        yield return Case(() => ProcessResult.Failure("out", "err", 0), "exitCode", "ProcessResult.Failure");

        var rotatingSink = Uninitialized<RotatingFileSink>();
        yield return Case(() => rotatingSink.Write(new byte[1], -1, 0), "offset", "RotatingFileSink.Write offset");
        yield return Case(() => rotatingSink.Write(new byte[1], 0, -1), "count", "RotatingFileSink.Write count");

        var running = Uninitialized<RunningProcess>();
        yield return Case(() => running.StopAsync(NegativeDuration), "gracePeriod", "RunningProcess.StopAsync");
        yield return Case(() => running.ProfileAsync(TimeSpan.Zero), "interval", "RunningProcess.ProfileAsync");

        var group = Uninitialized<ProcessGroup>();
        yield return Case(() => group.SampleStatsAsync(TimeSpan.Zero), "interval", "ProcessGroup.SampleStatsAsync");
        yield return Case(() => group.ShutdownAsync(NegativeDuration), "gracePeriod", "ProcessGroup.ShutdownAsync");
        yield return Case(
            () => group.ShutdownReportAsync(NegativeDuration),
            "gracePeriod",
            "ProcessGroup.ShutdownReportAsync"
        );

        var session = Uninitialized<SupervisionSession>();
        yield return Case(() => session.StopAsync(NegativeDuration), "gracePeriod", "SupervisionSession.StopAsync");

        var supervisor = new Supervisor(new Command("tool"));
        yield return Case(() => supervisor.MaxRestarts(-1), "count", "Supervisor.MaxRestarts");
        yield return Case(() => supervisor.Backoff(NegativeDuration, 1.0), "baseDelay", "Supervisor.Backoff");
        yield return Case(() => supervisor.MaxBackoff(NegativeDuration), "cap", "Supervisor.MaxBackoff");
        yield return Case(() => supervisor.StormPause(NegativeDuration), "pause", "Supervisor.StormPause");
        yield return Case(() => supervisor.FailureDecay(NegativeDuration), "decay", "Supervisor.FailureDecay");

        yield return Case(
            () => new ProcessKitOptions { DefaultTimeout = NegativeDuration },
            "value",
            "ProcessKitOptions.DefaultTimeout"
        );
        yield return Case(
            () => new HostedProcessOptions { ShutdownGracePeriod = NegativeDuration },
            "value",
            "HostedProcessOptions.ShutdownGracePeriod"
        );
    }

    [TestCaseSource(nameof(PublicBoundaryCases))]
    public void Public_range_boundaries_report_the_CSharp_parameter_name(Action call, string expectedParamName)
    {
        AssertParamName(call, expectedParamName);
    }

    [Test]
    public void Multi_argument_boundaries_preserve_first_failure_order()
    {
        AssertNullParamName(
            () => new Command("tool").RetryBackoff(-1, NegativeDuration, 1.0, NegativeDuration, false, null!),
            "shouldRetry"
        );
        AssertParamName(
            () => new Command("tool").RetryBackoff(-1, NegativeDuration, 1.0, NegativeDuration, false, _ => true),
            "maxAttempts"
        );
        AssertParamName(() => new Command("tool").User(-1, -1), "uid");

        var sink = Uninitialized<RotatingFileSink>();
        AssertNullParamName(() => sink.Write(null!, -1, -1), "buffer");
        AssertParamName(() => sink.Write(new byte[1], -1, -1), "offset");
        AssertParamName(() => sink.Write(new byte[1], 0, -1), "count");
    }

    [Test]
    public void Valid_range_boundaries_remain_accepted()
    {
        Assert.DoesNotThrow(() => new Command("tool").Timeout(TimeSpan.Zero).TimeoutGrace(TimeSpan.Zero));
        Assert.DoesNotThrow(() => new Command("tool").Umask(0).Umask(4095).ExtraFd(3));
        Assert.DoesNotThrow(() => ResourceLimits.None.WithMemoryMax(1).WithMaxProcesses(1));
        Assert.DoesNotThrow(() => OutputBufferPolicy.Bounded(0).WithMaxBytes(0));
        Assert.DoesNotThrow(() => StreamBufferPolicy.Bounded(1, StreamFullMode.Error));
        Assert.DoesNotThrow(() => ProcessResult.Failure("out", "err", -1));
        Assert.DoesNotThrow(() => new Supervisor(new Command("tool")).MaxRestarts(0).MaxBackoff(TimeSpan.Zero));
        Assert.DoesNotThrow(() => new ProcessKitOptions { DefaultTimeout = TimeSpan.Zero });
        Assert.DoesNotThrow(() => new HostedProcessOptions { ShutdownGracePeriod = TimeSpan.Zero });
    }

    [Test]
    public void Uninitialized_fixtures_do_not_run_production_finalizers()
    {
        var reference = CollectibleUninitializedReference<ProcessGroup>();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.That(reference.IsAlive, Is.False);
    }

    private static TestCaseData Case(Action call, string expectedParamName, string name) =>
        new TestCaseData(call, expectedParamName).SetName($"{name} names {expectedParamName}");

    private static void AssertParamName(Action call, string expectedParamName)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => call());
        Assert.That(exception!.ParamName, Is.EqualTo(expectedParamName));
    }

    private static void AssertNullParamName(Action call, string expectedParamName)
    {
        var exception = Assert.Throws<ArgumentNullException>(() => call());
        Assert.That(exception!.ParamName, Is.EqualTo(expectedParamName));
    }

    private static T Uninitialized<T>() where T : class
    {
        var instance = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        GC.SuppressFinalize(instance);
        return instance;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CollectibleUninitializedReference<T>() where T : class =>
        new(Uninitialized<T>());
}
