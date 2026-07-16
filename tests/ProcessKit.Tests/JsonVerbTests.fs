namespace ProcessKit.Tests

open System.Text.Json
open System.Text.Json.Serialization.Metadata
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing

/// A small F# record deserialized through STJ's constructor-based deserialization (matches JSON
/// property names to constructor parameter names, case-sensitively by default — see the case-
/// insensitive `options` test below). Must be public: STJ's constructor-based deserialization needs
/// an accessible constructor, which a `private` record's generated constructor is not.
type Widget = { Name: string; Count: int }

/// Covers `OutputJsonAsync<'T>` (T-104) on every surface — `Command`, any `IProcessRunner`,
/// `CliClient`, and `Pipeline` — entirely through `ScriptedRunner`, so no real process is spawned.
[<TestFixture>]
type JsonVerbTests() =

    let runner: IProcessRunner =
        ScriptedRunner()
            .On([ "widget-tool" ], Reply.Ok """{"Name":"gizmo","Count":3}""")
            .On([ "widget-tool"; "lower" ], Reply.Ok """{"name":"gizmo","count":3}""")
            .On([ "bad-json-tool" ], Reply.Ok "not json at all")
            .On([ "failing-tool" ], Reply.Fail(1, "boom"))

    // F# cannot run Roslyn source generators; the C# JsonVerbTests supplies generated metadata. These
    // type infos still exercise the F#-facing overloads with the same public JsonTypeInfo contract.
    let widgetTypeInfo =
        JsonSerializerOptions.Default.GetTypeInfo(typeof<Widget>) :?> JsonTypeInfo<Widget>

    let intTypeInfo =
        JsonSerializerOptions.Default.GetTypeInfo(typeof<int>) :?> JsonTypeInfo<int>

    [<Test>]
    member _.``Runner.outputJson deserializes valid JSON into a typed value``() : Task =
        task {
            let! result =
                Command.create "widget-tool"
                |> Runner.outputJson<Widget> runner CancellationToken.None None

            match result with
            | Ok widget ->
                Assert.That(widget.Name, Is.EqualTo "gizmo")
                Assert.That(widget.Count, Is.EqualTo 3)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Runner.outputJsonTyped deserializes valid JSON into a typed value``() : Task =
        task {
            let! result =
                Command.create "widget-tool"
                |> Runner.outputJsonTyped<Widget> runner CancellationToken.None widgetTypeInfo

            match result with
            | Ok widget ->
                Assert.That(widget.Name, Is.EqualTo "gizmo")
                Assert.That(widget.Count, Is.EqualTo 3)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Runner.outputJson surfaces invalid JSON as ProcessError.Parse``() : Task =
        task {
            let! result =
                Command.create "bad-json-tool"
                |> Runner.outputJson<Widget> runner CancellationToken.None None

            match result with
            | Error(ProcessError.Parse _) -> Assert.Pass()
            | other -> Assert.Fail $"expected a Parse error, got {other}"
        }
        :> Task

    [<Test>]
    member _.``Runner.outputJsonTyped surfaces invalid JSON as ProcessError.Parse``() : Task =
        task {
            let! result =
                Command.create "bad-json-tool"
                |> Runner.outputJsonTyped<Widget> runner CancellationToken.None widgetTypeInfo

            match result with
            | Error(ProcessError.Parse _) -> Assert.Pass()
            | other -> Assert.Fail $"expected a Parse error, got {other}"
        }
        :> Task

    [<Test>]
    member _.``Runner.outputJson surfaces a non-zero exit as ProcessError.Exit, not a parse attempt``() : Task =
        task {
            let! result =
                Command.create "failing-tool"
                |> Runner.outputJson<Widget> runner CancellationToken.None None

            match result with
            | Error(ProcessError.Exit(_, 1, _, stderr)) -> Assert.That(stderr, Is.EqualTo "boom")
            | other -> Assert.Fail $"expected an Exit error, got {other}"
        }
        :> Task

    [<Test>]
    member _.``Runner.outputJsonTyped surfaces a non-zero exit as ProcessError.Exit, not a parse attempt``() : Task =
        task {
            let! result =
                Command.create "failing-tool"
                |> Runner.outputJsonTyped<Widget> runner CancellationToken.None widgetTypeInfo

            match result with
            | Error(ProcessError.Exit(_, 1, _, stderr)) -> Assert.That(stderr, Is.EqualTo "boom")
            | other -> Assert.Fail $"expected an Exit error, got {other}"
        }
        :> Task

    [<Test>]
    member _.``Runner.outputJson honours JsonSerializerOptions (case-insensitive property matching)``() : Task =
        task {
            let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

            let! result =
                Command.create "widget-tool"
                |> Command.arg "lower"
                |> Runner.outputJson<Widget> runner CancellationToken.None (Some options)

            match result with
            | Ok widget ->
                Assert.That(widget.Name, Is.EqualTo "gizmo")
                Assert.That(widget.Count, Is.EqualTo 3)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Command.OutputJsonAsync reaches the default runner (token omitted and passed)``() : Task =
        task {
            // The default runner is a real JobRunner; a bare JSON number sidesteps cross-shell string-
            // quoting differences (cmd.exe vs. /bin/sh) while still exercising the real spawn path.
            let isWindows =
                System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows
                )

            let echoNumber =
                if isWindows then
                    Command.create "cmd.exe" |> Command.args [ "/c"; "echo 42" ]
                else
                    Command.create "/bin/sh" |> Command.args [ "-c"; "echo 42" ]

            match! echoNumber.OutputJsonAsync<int>() with
            | Ok value -> Assert.That(value, Is.EqualTo 42)
            | Error error -> Assert.Fail $"{error}"

            match! echoNumber.OutputJsonAsync<int>(cancellationToken = CancellationToken.None) with
            | Ok value -> Assert.That(value, Is.EqualTo 42)
            | Error error -> Assert.Fail $"(ct) {error}"
        }
        :> Task

    [<Test>]
    member _.``Command.OutputJsonAsync with source-generated type info reaches the default runner``() : Task =
        task {
            let isWindows =
                System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows
                )

            let echoNumber =
                if isWindows then
                    Command.create "cmd.exe" |> Command.args [ "/c"; "echo 42" ]
                else
                    Command.create "/bin/sh" |> Command.args [ "-c"; "echo 42" ]

            match! echoNumber.OutputJsonAsync<int>(intTypeInfo) with
            | Ok value -> Assert.That(value, Is.EqualTo 42)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Command.OutputJsonAsync with source-generated type info surfaces invalid JSON as ProcessError.Parse``
        ()
        : Task =
        task {
            let isWindows =
                System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows
                )

            let echoGarbage =
                if isWindows then
                    Command.create "cmd.exe" |> Command.args [ "/c"; "echo not-json-at-all" ]
                else
                    Command.create "/bin/sh" |> Command.args [ "-c"; "echo not-json-at-all" ]

            match! echoGarbage.OutputJsonAsync<int>(intTypeInfo) with
            | Error(ProcessError.Parse _) -> Assert.Pass()
            | other -> Assert.Fail $"expected a Parse error, got {other}"
        }
        :> Task

    [<Test>]
    member _.``IProcessRunner.OutputJsonAsync extension deserializes through the configured runner``() : Task =
        task {
            match! runner.OutputJsonAsync<Widget>(Command.create "widget-tool") with
            | Ok widget -> Assert.That(widget.Name, Is.EqualTo "gizmo")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``IProcessRunner.OutputJsonAsync extension with source-generated type info deserializes through a ScriptedRunner``
        ()
        : Task =
        task {
            match! runner.OutputJsonAsync<Widget>(Command.create "widget-tool", widgetTypeInfo) with
            | Ok widget -> Assert.That(widget.Name, Is.EqualTo "gizmo")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``IProcessRunner.OutputJsonAsync extension with source-generated type info surfaces invalid JSON as ProcessError.Parse``
        ()
        : Task =
        task {
            match! runner.OutputJsonAsync<Widget>(Command.create "bad-json-tool", widgetTypeInfo) with
            | Error(ProcessError.Parse _) -> Assert.Pass()
            | other -> Assert.Fail $"expected a Parse error, got {other}"
        }
        :> Task

    [<Test>]
    member _.``CliClient.OutputJsonAsync routes through the configured runner``() : Task =
        task {
            let client = CliClient("widget-tool").WithRunner runner

            match! client.OutputJsonAsync<Widget> [] with
            | Ok widget ->
                Assert.That(widget.Name, Is.EqualTo "gizmo")
                Assert.That(widget.Count, Is.EqualTo 3)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``CliClient.OutputJsonAsync with source-generated type info routes through the configured runner``
        ()
        : Task =
        task {
            let client = CliClient("widget-tool").WithRunner runner

            match! client.OutputJsonAsync<Widget>([], widgetTypeInfo) with
            | Ok widget ->
                Assert.That(widget.Name, Is.EqualTo "gizmo")
                Assert.That(widget.Count, Is.EqualTo 3)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``CliClient.OutputJsonAsync with source-generated type info surfaces invalid JSON as ProcessError.Parse``
        ()
        : Task =
        task {
            let client = CliClient("bad-json-tool").WithRunner runner

            match! client.OutputJsonAsync<Widget>([], widgetTypeInfo) with
            | Error(ProcessError.Parse _) -> Assert.Pass()
            | other -> Assert.Fail $"expected a Parse error, got {other}"
        }
        :> Task

    [<Test>]
    member _.``Pipeline.OutputJsonAsync deserializes the pipefail-representative stage's stdout``() : Task =
        task {
            // A pipeline spawns its stages directly (bypassing IProcessRunner), so this exercises real
            // processes rather than ScriptedRunner — `sort` is a portable single-line passthrough
            // (present on both Windows and POSIX), mirroring PipelineTests.fs's own `sortStage`; a bare
            // JSON number sidesteps cross-shell string-quoting differences.
            let isWindows =
                System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows
                )

            let source =
                if isWindows then
                    Command.create "cmd.exe" |> Command.args [ "/c"; "echo 99" ]
                else
                    Command.create "/bin/sh" |> Command.args [ "-c"; "echo 99" ]

            let pipeline = source.Pipe(Command.create "sort")

            match! pipeline.OutputJsonAsync<int>() with
            | Ok value -> Assert.That(value, Is.EqualTo 99)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Pipeline.OutputJsonAsync with source-generated type info deserializes the pipefail-representative stage's stdout``
        ()
        : Task =
        task {
            let isWindows =
                System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows
                )

            let source =
                if isWindows then
                    Command.create "cmd.exe" |> Command.args [ "/c"; "echo 99" ]
                else
                    Command.create "/bin/sh" |> Command.args [ "-c"; "echo 99" ]

            let pipeline = source.Pipe(Command.create "sort")

            match! pipeline.OutputJsonAsync<int>(intTypeInfo) with
            | Ok value -> Assert.That(value, Is.EqualTo 99)
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Pipeline.OutputJsonAsync with source-generated type info surfaces invalid JSON as ProcessError.Parse``
        ()
        : Task =
        task {
            // Real-process pipeline; a non-numeric line is not valid JSON for `int`.
            let isWindows =
                System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows
                )

            let source =
                if isWindows then
                    Command.create "cmd.exe" |> Command.args [ "/c"; "echo not-json-at-all" ]
                else
                    Command.create "/bin/sh" |> Command.args [ "-c"; "echo not-json-at-all" ]

            let pipeline = source.Pipe(Command.create "sort")

            match! pipeline.OutputJsonAsync<int>(intTypeInfo) with
            | Error(ProcessError.Parse _) -> Assert.Pass()
            | other -> Assert.Fail $"expected a Parse error, got {other}"
        }
        :> Task
