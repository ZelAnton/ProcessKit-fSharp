namespace ProcessKit.Testing

open System
open System.Collections.Generic
open ProcessKit

/// A subprocess-free `IProcessRunner` for a `--dry-run` seam: instead of spawning anything, every verb
/// renders the command to a deterministic string — the program, its ordinary arguments (quoted when they
/// contain whitespace or a quote), any Windows raw fragments verbatim, and the working directory when the
/// command set one — and returns that render as the successful stdout of the run. No member ever touches
/// the filesystem or the network.
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
        if command.Config.ExtraFds.Count > 0 then
            Error(ProcessError.Unsupported "DryRunRunner cannot emulate extra POSIX file-descriptor channels")
        else
            let render = DryRunRunner.Render command
            lock gate (fun () -> history.Add render)
            let fake = FakeProcess.OfCommand(command).WithStdout(render)
            let fake = if command.Config.Pty.IsSome then fake.WithPty() else fake
            Ok(fake.Build())

    let seam = Seam.runner resolve

    /// Render `command` deterministically: the program, ordinarily quoted arguments, Windows raw fragments
    /// verbatim, then `(argv0: <value>)` when `Command.Arg0` overrode the child's `argv[0]`, then
    /// `(rlimits: <resource>=<soft>:<hard>, ...)` when `Command.Rlimit` capped any per-process resource,
    /// then `(io_priority: <class>[:<level>])` when `Command.IoPriority` set the child's Linux
    /// I/O-scheduling priority, then `(cwd: <directory>)` when the command set a working directory. Two
    /// commands built the same way always render identically.
    ///
    /// The rlimits and the I/O priority are part of the render on purpose: a dry run is what a consumer
    /// inspects INSTEAD of launching anything, so a cap or a scheduling class that would have been applied
    /// to the real child has to be visible here too — a preview that silently omitted it would report a
    /// weaker command than the one that would actually run. Each entry is `Rlimit.ToString()` /
    /// `IoPriority.ToString()`, so the render carries the same stable identifiers the builders accept, in
    /// the order they were configured, and no argv or environment value.
    static member Render(command: Command) : string =
        ArgumentNullException.ThrowIfNull command

        let ordinary = command.Config.Args |> Seq.map quoteIfNeeded |> List.ofSeq
        let raw = command.Config.WindowsRawArgs |> List.ofSeq
        let tokens = command.Program :: (ordinary @ raw)

        let line = String.Join(" ", tokens)

        let line =
            match command.Config.Arg0 with
            | Some arg0 -> $"{line} (argv0: {quoteIfNeeded arg0})"
            | None -> line

        let line =
            if command.Config.Rlimits.IsEmpty then
                line
            else
                let rendered = command.Config.Rlimits |> Seq.map string |> String.concat ", "

                $"{line} (rlimits: {rendered})"

        let line =
            match command.Config.IoPriority with
            | Some priority -> $"{line} (io_priority: {priority})"
            | None -> line

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
