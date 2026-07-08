using System.Threading.Tasks;
using NUnit.Framework;
using ProcessKit;

namespace ProcessKit.CSharp.Tests;

/// Covers `TryParser<'T>` (`TryParser.fs`): the standard .NET `bool TryX(string, out T)` shape lets
/// C# pass a BCL parser like `int.TryParse` directly, with an explicit type argument since the BCL
/// method is overloaded and can't be inferred from a byref lambda parameter.
[TestFixture]
public class TryParserTests
{
    [Test]
    public async Task Command_TryParseAsync_parses_trimmed_stdout_with_int_TryParse()
    {
        var command = Shell.Run("echo 42");

        var result = await command.TryParseAsync<int>(int.TryParse);

        Assert.That(result.IsOk, Is.True);
        Assert.That(result.ResultValue, Is.EqualTo(42));
    }

    [Test]
    public async Task Command_TryParseAsync_surfaces_a_Parse_error_when_the_parser_rejects_the_output()
    {
        var command = Shell.Run("echo not-a-number");

        var result = await command.TryParseAsync<int>(int.TryParse);

        Assert.That(result.IsOk, Is.False);
        Assert.That(result.ErrorValue.IsParse, Is.True);
    }

    [Test]
    public async Task IProcessRunner_TryParseAsync_extension_parses_the_same_way()
    {
        IProcessRunner runner = new JobRunner();
        var command = Shell.Run("echo 7");

        var result = await runner.TryParseAsync<int>(command, int.TryParse);

        Assert.That(result.IsOk, Is.True);
        Assert.That(result.ResultValue, Is.EqualTo(7));
    }
}
