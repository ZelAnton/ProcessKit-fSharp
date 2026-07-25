using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcessKit;

namespace DocSnippets;

/// <summary>
/// Compile-time stand-ins for the values the guides introduce in prose around a sample
/// ("<c>cmd</c> is the command you built above", "<c>proc</c> is the running child").
/// Every generated snippet unit imports this class with <c>using static</c>, so a block
/// that starts mid-story still compiles - with the real ProcessKit types, so a renamed or
/// re-typed API still breaks the build.
/// </summary>
/// <remarks>
/// Nothing here is ever executed: the harness is compiled, never run, so the null
/// stand-ins never reach a call. The members are deliberately named the way the guides
/// name them (camelCase), because the samples refer to them by those names.
/// </remarks>
internal static class Fixtures
{
    // Commands and clients.
    internal static Command cmd => default!;
    internal static Command command => default!;
    internal static Pipeline pipeline => default!;
    internal static CliClient git => default!;
    internal static Supervisor supervisor => default!;

    // Live children and containers.
    internal static RunningProcess proc => default!;
    internal static RunningProcess a => default!;
    internal static RunningProcess b => default!;
    internal static ProcessGroup group => default!;
    internal static ProcessGroupOptions options => default!;
    internal static Process external => default!;

    // Results the prose has already obtained.
    internal static ProcessResult<string> result => default!;
    internal static Outcome outcome => default!;

    // Ambient plumbing.
    internal static IProcessRunner runner => default!;
    internal static ILogger logger => default!;
    internal static IServiceCollection services => default!;
    internal static CancellationToken shutdownToken => default;
    internal static CancellationToken appLifetimeToken => default;

    // ---------------------------------------------------------------------
    // The reader's own code
    // ---------------------------------------------------------------------
    // Names the guides use as a stand-in for whatever the reader plugs in - the
    // JSON payload their tool prints, the handler they run per line, the code
    // under test. The guides never define these, so the harness must, and their
    // shapes come from how the samples call them. A sample that needs a NEW
    // placeholder fails with a clear "does not exist" error pointing at the
    // markdown line: add it here, or mark the block with `docsnippet:ignore`.

    /// <summary><c>record Widget(string Name, int Count)</c>, as docs/commands.md spells it out.</summary>
    internal sealed record Widget(string Name, int Count);

    internal static IEnumerable<string> bigInput => default!;
    internal static IEnumerable<string> files => default!;
    internal static void handle(string line) { }
    internal static Task<bool> healthCheck() => default!;
    internal static Task<bool> pingWorkerAsync() => default!;
    internal static Task<bool> PingWorkerAsync() => default!;
    internal static Task deploy(IProcessRunner runner) => default!;
    internal static Task Deploy(IProcessRunner runner) => default!;
    internal static void installThenRetry() { }
    internal static void scheduleRetry() { }
    internal static void fail(ProcessError error) { }

    /// <summary>The secret the record/replay redaction sample scrubs.</summary>
    internal static string token => default!;

    /// <summary>The app's own configuration, bound in the dependency-injection guide.</summary>
    internal static IConfiguration configuration => default!;

    /// <summary>The app's own metrics sink, as the hosted-process sample calls into it.</summary>
    internal static MetricsSink metrics => default!;

    internal sealed class MetricsSink
    {
        internal Counter Restarts => default!;

        internal sealed class Counter
        {
            internal void Add(int delta) { }
        }
    }
}
