using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using NUnit.Framework;
using ProcessKit;
using ProcessKit.Testing;

namespace ProcessKit.CSharp.Tests;

/// A small record deserialized via `OutputJsonAsync<T>` (T-104).
internal sealed record Widget(string Name, int Count);

[JsonSerializable(typeof(Widget))]
[JsonSerializable(typeof(int))]
internal partial class JsonVerbTestContext : JsonSerializerContext;

/// Covers `OutputJsonAsync<T>` (`CommandVerbs.fs` / `ProcessRunnerExtensions.fs`) called idiomatically
/// from C#: an explicit type argument (there is no parser delegate to infer it from), an optional
/// `JsonSerializerOptions`, invalid JSON surfacing as a typed `ProcessError.Parse`, and — via
/// `ScriptedRunner` — no real process spawned for the object-shaped payloads.
[TestFixture]
public class JsonVerbTests
{
    [Test]
    public async Task Command_OutputJsonAsync_reaches_the_default_runner_via_a_real_process()
    {
        // The default runner (CommandVerbs.DefaultRunner) is a real JobRunner; a bare JSON number
        // sidesteps cross-shell string-quoting differences (cmd.exe vs. /bin/sh) while still proving
        // the verb reaches an actual spawn.
        var command = Shell.Run("echo 42");

        var result = await command.OutputJsonAsync<int>();

        Assert.That(result.IsOk, Is.True);
        Assert.That(result.ResultValue, Is.EqualTo(42));
    }

    [Test]
    public async Task Command_OutputJsonAsync_with_source_generated_type_info_reaches_the_default_runner()
    {
        var command = Shell.Run("echo 42");

        var result = await command.OutputJsonAsync(JsonVerbTestContext.Default.Int32);

        Assert.That(result.IsOk, Is.True);
        Assert.That(result.ResultValue, Is.EqualTo(42));
    }

    [Test]
    public async Task Command_OutputJsonAsync_surfaces_invalid_json_as_a_Parse_error()
    {
        var command = Shell.Run("echo not-json-at-all");

        var result = await command.OutputJsonAsync<Widget>();

        Assert.That(result.IsOk, Is.False);
        Assert.That(result.ErrorValue.IsParse, Is.True);
    }

    [Test]
    public async Task IProcessRunner_OutputJsonAsync_extension_deserializes_through_a_ScriptedRunner()
    {
        IProcessRunner runner = new ScriptedRunner().On(["widget-tool"], Reply.Ok("""{"Name":"gizmo","Count":3}"""));
        var command = new Command("widget-tool");

        var result = await runner.OutputJsonAsync<Widget>(command);

        Assert.That(result.IsOk, Is.True);
        Assert.That(result.ResultValue.Name, Is.EqualTo("gizmo"));
        Assert.That(result.ResultValue.Count, Is.EqualTo(3));
    }

    [Test]
    public async Task IProcessRunner_OutputJsonAsync_extension_deserializes_through_a_ScriptedRunner_with_source_generated_type_info()
    {
        IProcessRunner runner = new ScriptedRunner().On(["widget-tool"], Reply.Ok("""{"Name":"gizmo","Count":3}"""));
        var command = new Command("widget-tool");

        var result = await runner.OutputJsonAsync(command, JsonVerbTestContext.Default.Widget);

        Assert.That(result.IsOk, Is.True);
        Assert.That(result.ResultValue.Name, Is.EqualTo("gizmo"));
        Assert.That(result.ResultValue.Count, Is.EqualTo(3));
    }

    [Test]
    public async Task IProcessRunner_OutputJsonAsync_honours_JsonSerializerOptions_case_insensitive_matching()
    {
        IProcessRunner runner = new ScriptedRunner().On(["widget-tool"], Reply.Ok("""{"name":"gizmo","count":3}"""));
        var command = new Command("widget-tool");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var result = await runner.OutputJsonAsync<Widget>(command, options);

        Assert.That(result.IsOk, Is.True);
        Assert.That(result.ResultValue.Name, Is.EqualTo("gizmo"));
        Assert.That(result.ResultValue.Count, Is.EqualTo(3));
    }

    [Test]
    public async Task IProcessRunner_OutputJsonAsync_surfaces_invalid_json_as_a_Parse_error_with_no_real_process()
    {
        IProcessRunner runner = new ScriptedRunner().On(["widget-tool"], Reply.Ok("not json at all"));
        var command = new Command("widget-tool");

        var result = await runner.OutputJsonAsync<Widget>(command);

        Assert.That(result.IsOk, Is.False);
        Assert.That(result.ErrorValue.IsParse, Is.True);
    }

    [Test]
    public async Task IProcessRunner_OutputJsonAsync_surfaces_invalid_json_as_a_Parse_error_with_source_generated_type_info()
    {
        IProcessRunner runner = new ScriptedRunner().On(["widget-tool"], Reply.Ok("not json at all"));
        var command = new Command("widget-tool");

        var result = await runner.OutputJsonAsync(command, JsonVerbTestContext.Default.Widget);

        Assert.That(result.IsOk, Is.False);
        Assert.That(result.ErrorValue.IsParse, Is.True);
    }
}