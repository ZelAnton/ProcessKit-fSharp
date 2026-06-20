namespace ProcessKit.Tests

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Testing

/// A deterministic inner `IProcessRunner` for record-mode tests: every call returns `stdout`/`code`.
type private FixedRunner(stdout: string, code: int) =
    interface IProcessRunner with
        member _.OutputString(command, _cancellationToken) =
            Task.FromResult(
                Ok(ProcessResult<string>(command.Program, stdout, "", Outcome.Exited code, TimeSpan.Zero, false, [ 0 ]))
            )

        member _.OutputBytes(command, _cancellationToken) =
            Task.FromResult(
                Ok(
                    ProcessResult<byte[]>(
                        command.Program,
                        Encoding.UTF8.GetBytes stdout,
                        "",
                        Outcome.Exited code,
                        TimeSpan.Zero,
                        false,
                        [ 0 ]
                    )
                )
            )

        member _.Start(_command, _cancellationToken) =
            Task.FromResult(Error(ProcessError.Unsupported "FixedRunner has no Start"))

[<TestFixture>]
type CassetteTests() =

    let withCassette (body: string -> Task) : Task =
        task {
            let path = Path.GetTempFileName()

            try
                do! body path
            finally
                if File.Exists path then
                    File.Delete path
        }

    let runner (r: RecordReplayRunner) : IProcessRunner = r

    [<Test>]
    member _.``record then replay round-trips a result without the inner runner``() : Task =
        withCassette (fun path ->
            task {
                let recorder = RecordReplayRunner.Record(path, FixedRunner("recorded-output", 0))
                let command = Command.create "tool" |> Command.args [ "build"; "--fast" ]

                match! (runner recorder).OutputString(command, CancellationToken.None) with
                | Ok result -> Assert.That(result.Stdout, Is.EqualTo "recorded-output")
                | Error error -> Assert.Fail $"{error}"

                match recorder.Save() with
                | Ok() -> ()
                | Error error -> Assert.Fail $"save: {error}"

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"replay load: {error}"
                | Ok replayer ->
                    match! (runner replayer).OutputString(command, CancellationToken.None) with
                    | Ok result ->
                        Assert.That(result.Stdout, Is.EqualTo "recorded-output")
                        Assert.That(result.Code, Is.EqualTo(Some 0))
                    | Error error -> Assert.Fail $"{error}"
            })

    [<Test>]
    member _.``an unrecorded invocation is a CassetteMiss``() : Task =
        withCassette (fun path ->
            task {
                let recorder = RecordReplayRunner.Record(path, FixedRunner("out", 0))
                let recorded = Command.create "tool" |> Command.arg "x"
                let! _ = (runner recorder).OutputString(recorded, CancellationToken.None)
                recorder.Save() |> ignore

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"{error}"
                | Ok replayer ->
                    let unseen = Command.create "tool" |> Command.arg "y"

                    match! (runner replayer).OutputString(unseen, CancellationToken.None) with
                    | Error(ProcessError.CassetteMiss "tool") -> Assert.Pass()
                    | other -> Assert.Fail $"expected CassetteMiss, got {other}"
            })

    [<Test>]
    member _.``the stdin source is part of the match key``() : Task =
        withCassette (fun path ->
            task {
                let recorder = RecordReplayRunner.Record(path, FixedRunner("with-input", 0))
                let recorded = Command.create "tool" |> Command.stdin (Stdin.FromString "input-a")
                let! _ = (runner recorder).OutputString(recorded, CancellationToken.None)
                recorder.Save() |> ignore

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"{error}"
                | Ok replayer ->
                    // Same stdin matches.
                    let same = Command.create "tool" |> Command.stdin (Stdin.FromString "input-a")

                    match! (runner replayer).OutputString(same, CancellationToken.None) with
                    | Ok result -> Assert.That(result.Stdout, Is.EqualTo "with-input")
                    | Error error -> Assert.Fail $"same stdin should match: {error}"

                    // Different stdin misses.
                    let different = Command.create "tool" |> Command.stdin (Stdin.FromString "input-b")

                    match! (runner replayer).OutputString(different, CancellationToken.None) with
                    | Error(ProcessError.CassetteMiss _) -> Assert.Pass()
                    | other -> Assert.Fail $"different stdin should miss, got {other}"
            })

    [<Test>]
    member _.``a one-shot stdin source cannot be keyed``() : Task =
        withCassette (fun path ->
            task {
                let recorder = RecordReplayRunner.Record(path, FixedRunner("out", 0))
                use reader = new MemoryStream(Encoding.UTF8.GetBytes "data")
                let command = Command.create "tool" |> Command.stdin (Stdin.FromReader reader)

                match! (runner recorder).OutputString(command, CancellationToken.None) with
                | Error(ProcessError.Unsupported _) -> Assert.Pass()
                | other -> Assert.Fail $"expected Unsupported for a one-shot stdin source, got {other}"
            })

    [<Test>]
    member _.``Dispose flushes the cassette and OutputBytes round-trips``() : Task =
        withCassette (fun path ->
            task {
                let command = Command.create "tool" |> Command.arg "z"

                // No explicit Save — the drop-time flush must persist the recording.
                do!
                    task {
                        use recorder = RecordReplayRunner.Record(path, FixedRunner("byte-output", 0))
                        let! _ = (runner recorder).OutputString(command, CancellationToken.None)
                        ()
                    }

                match RecordReplayRunner.Replay path with
                | Error error -> Assert.Fail $"{error}"
                | Ok replayer ->
                    match! (runner replayer).OutputBytes(command, CancellationToken.None) with
                    | Ok result -> Assert.That(Encoding.UTF8.GetString result.Stdout, Is.EqualTo "byte-output")
                    | Error error -> Assert.Fail $"{error}"
            })
