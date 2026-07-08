namespace ProcessKit.Testing

open System
open System.Collections.Generic
open ProcessKit

/// A subprocess-free `IProcessRunner` for a `--dry-run` seam: instead of spawning anything, every verb
/// renders the command to a deterministic string — the program, its arguments (quoted when they contain
/// whitespace or a quote), and the working directory when the command set one — and returns that render
/// as the successful stdout of the run. No member ever touches the filesystem or the network.
///
/// `SpawnAsync` (and the streaming/readiness verbs a live handle feeds) is served exactly like the
/// capture verbs: the render becomes a `FakeProcess`'s stdout, exiting `0` with no live pumping. This is
/// a deliberate choice over `Unsupported` — it keeps all three verbs consistent (a `--dry-run` consumer
/// that goes through `StartAsync`/streaming sees the exact same render a capture verb would return), and
/// it costs nothing to support: `FakeProcess` builds a purely in-memory `RunningProcess`, so there is
/// still no subprocess, filesystem, or network access. There is no real output to trickle in, so the
/// whole render simply arrives as the process's one line of stdout instead of streaming incrementally.
///
/// Every command "run" through an instance is recorded (thread-safe) so a test can inspect what a dry
/// run would have executed via `History`.
[<Sealed>]
type DryRunRunner() =

    let history = List<string>()
    let gate = obj ()

    // Quote an argument containing whitespace or a double quote so the render stays a single,
    // unambiguous token, doubling any embedded quote — the common shell-quoting convention, not a new
    // one invented here.
    static let quoteIfNeeded (arg: string) : string =
        if arg.Length = 0 || arg |> Seq.exists (fun c -> Char.IsWhiteSpace c || c = '"') then
            "\"" + arg.Replace("\"", "\"\"") + "\""
        else
            arg

    // Render, append it to the history under the lock (so concurrent verbs can never interleave a
    // partial append), and build the in-memory fake process every verb shares — mirroring
    // `ScriptedRunner`'s approach so a dry-run capture and a dry-run stream agree byte-for-byte
    // (encoding, `OkCodes`, output-buffer policy) and differ only in the final projection `Seam.runner`
    // applies. A missed (cancelled) run is never recorded — `Seam.runner` guards cancellation before
    // this ever runs, matching `JobRunner` / `ScriptedRunner`.
    let resolve (command: Command) : Result<RunningProcess, ProcessError> =
        let render = DryRunRunner.Render command
        lock gate (fun () -> history.Add render)
        Ok(FakeProcess.OfCommand(command).WithStdout(render).Build())

    let seam = Seam.runner resolve

    /// Render `command` deterministically: the program, then its arguments (quoted when needed), then
    /// `(cwd: <directory>)` when the command set a working directory. Two commands built the same way
    /// always render identically.
    static member Render(command: Command) : string =
        ArgumentNullException.ThrowIfNull command

        let tokens =
            command.Program :: (command.Arguments |> Seq.map quoteIfNeeded |> List.ofSeq)

        let line = String.Join(" ", tokens)

        match command.WorkingDirectory with
        | Some dir -> $"{line} (cwd: {dir})"
        | None -> line

    /// A snapshot, in call order, of every command "run" through this instance so far — safe to read
    /// while another verb is still recording concurrently.
    member _.History: IReadOnlyList<string> = lock gate (fun () -> history.ToArray())

    interface IProcessRunner with
        member _.CaptureStringAsync(command, cancellationToken) =
            seam.CaptureStringAsync(command, cancellationToken)

        member _.SpawnAsync(command, cancellationToken) =
            seam.SpawnAsync(command, cancellationToken)

        member _.CaptureBytesAsync(command, cancellationToken) =
            seam.CaptureBytesAsync(command, cancellationToken)
