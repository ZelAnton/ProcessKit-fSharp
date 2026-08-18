using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ProcessKit;
using ProcessKit.Testing;

namespace ProcessKit.CSharp.Tests;

/// Covers the C# ergonomics of the completion-ordered batch stream (`Exec.outputStream` /
/// `Exec.outputStreamBytes`, `docs/cookbook.md`): a plain `await foreach` over the returned
/// `IAsyncEnumerable`, reading each item's `Index` and `Result` the way a C# consumer would — the
/// half of "F#/C#-reachable" that only a C# compilation can actually prove.
[TestFixture]
public class ExecStreamTests
{
    [Test]
    public async Task await_foreach_yields_one_item_per_command_tagged_with_its_input_index()
    {
        var runner = new ScriptedRunner()
            .On(["step", "0"], Reply.Ok("zero"))
            .On(["step", "1"], Reply.Ok("one"))
            .On(["step", "2"], Reply.Ok("two"));

        var commands = new[]
        {
            new Command("step").Arg("0"),
            new Command("step").Arg("1"),
            new Command("step").Arg("2"),
        };

        var stdoutByIndex = new Dictionary<int, string>();

        // Completion order, so the index — not the arrival position — is what identifies a result.
        await foreach (var item in Exec.outputStream(2, runner, commands, CancellationToken.None))
        {
            Assert.That(item.Result.IsOk, Is.True);
            stdoutByIndex[item.Index] = item.Result.ResultValue.Stdout;
        }

        Assert.That(stdoutByIndex, Has.Count.EqualTo(3));
        Assert.That(stdoutByIndex[0], Is.EqualTo("zero"));
        Assert.That(stdoutByIndex[1], Is.EqualTo("one"));
        Assert.That(stdoutByIndex[2], Is.EqualTo("two"));
    }

    [Test]
    public async Task await_foreach_over_the_bytes_stream_captures_raw_stdout()
    {
        var runner = new ScriptedRunner().On(["cat", "artifact"], Reply.Ok("binary-ish"));
        var commands = new[] { new Command("cat").Arg("artifact") };

        var decoded = new List<string>();

        await foreach (
            var item in Exec.outputStreamBytes(1, runner, commands, CancellationToken.None)
        )
        {
            Assert.That(item.Index, Is.Zero);
            Assert.That(item.Result.IsOk, Is.True);
            decoded.Add(Encoding.UTF8.GetString(item.Result.ResultValue.Stdout));
        }

        Assert.That(decoded, Is.EqualTo(new[] { "binary-ish" }));
    }
}
