namespace ProcessKit.Testing

open System
open System.Collections.Generic
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open ProcessKit

/// One captured `invocation → result` pair — a row inside the `CassetteFile` envelope (public so
/// `System.Text.Json` can serialize it; inspect a cassette file directly rather than depending on
/// this shape). Only env *names* are stored (values redacted); `program`, `args`, `cwd`, `stdout`,
/// and `stderr` are verbatim and can carry secrets — review a cassette before committing it.
[<CLIMutable>]
type CassetteEntry =
    {
        /// The program (executable) that was invoked.
        Program: string
        /// The arguments passed to the program.
        Args: string[]
        /// The working directory, or `null` if the command did not set one.
        Cwd: string | null
        /// Digest of the stdin source; part of the replay match key (`null` when there was no stdin).
        /// For an in-memory stdin source this is a SHA-256 of the content — a low-entropy stdin secret
        /// (a short password/PIN) can be recovered from it by brute force, so treat a cassette with
        /// stdin as sensitive and review it before committing.
        StdinDigest: string | null
        /// Whether the invocation supplied a stdin source.
        HasStdin: bool
        /// Names of the environment variables set on the command — values are redacted, never stored.
        EnvNames: string[]
        /// The captured standard output (verbatim; may contain secrets).
        Stdout: string
        /// The captured standard error (verbatim; may contain secrets).
        Stderr: string
        /// The exit code, or `null` if the process did not exit normally (e.g. it was signalled).
        Code: Nullable<int>
        /// Whether the run was terminated by a timeout.
        TimedOut: bool
        /// The terminating signal number on POSIX, or `null` if the process was not signalled.
        Signal: Nullable<int>
    }

/// The on-disk cassette envelope: a format `version` (so an incompatible future format is rejected
/// rather than misread) wrapping the recorded `entries`. Public so `System.Text.Json` can serialize
/// it; inspect a cassette file directly rather than depending on this shape.
[<CLIMutable>]
type CassetteFile =
    {
        /// The cassette format version; a file whose major differs from this build's is rejected.
        Version: int
        /// The recorded invocation→result rows, in capture order.
        Entries: CassetteEntry[]
    }

// Match key: program + args + cwd + whether-stdin + stdin digest. F# tuple/list have structural
// equality, so this works as a Dictionary key.
type private Key = string * string list * string option * bool * string option

// One key's entries in capture order, with the order-then-repeat-last cursor.
type private ReplaySlot =
    { Entries: CassetteEntry[]
      mutable Next: int }

type private Mode =
    | RecordMode of inner: IProcessRunner * recorded: List<CassetteEntry> * dirty: bool ref
    | ReplayMode of slots: Dictionary<Key, ReplaySlot>

/// A record/replay `IProcessRunner`.
///
/// **Record** mode wraps a real inner runner, captures each completed call to a JSON cassette
/// (written on `Save`, or best-effort on dispose), and returns the live result. Errors (a spawn
/// failure) record nothing; non-zero exits and captured timeouts are results and are recorded.
///
/// **Replay** mode loads the cassette and serves results with **no subprocess**: a match is keyed on
/// program + args + cwd + stdin-source digest; duplicates replay in capture order then repeat the
/// last; an unmatched call is `ProcessError.CassetteMiss` (never a surprise subprocess). Covers
/// `CaptureStringAsync` (and `CaptureBytesAsync` via a UTF-8 round-trip of the recorded stdout); `SpawnAsync` is
/// unsupported. A one-shot stdin source (`FromStream` / `FromLines` / `FromAsyncLines`) cannot be
/// keyed and errors.
[<Sealed>]
type RecordReplayRunner private (mode: Mode, path: string) =

    static let jsonOptions = JsonSerializerOptions(WriteIndented = true)

    // The cassette format version this build writes and accepts. Bump the (single) version when the
    // on-disk schema changes incompatibly; a file with a different version is rejected on load.
    static let currentFormatVersion = 1

    static let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

    // Coalesce a possibly-null deserialized field (omitted JSON fields land as null even though the
    // record type says non-null), so a crafted/partial cassette can't surface as a NullReferenceException
    // at replay time (e.g. a null `Stdout` reaching `TrimEnd`).
    static let stringOrEmpty (s: string | null) : string =
        match s with
        | null -> ""
        | value -> value

    static let arrayOrEmpty (a: 'a[] | null) : 'a[] =
        match a with
        | null -> [||]
        | value -> value

    static let normalizeEntry (entry: CassetteEntry) : CassetteEntry =
        { entry with
            Program = stringOrEmpty entry.Program
            Args = arrayOrEmpty entry.Args
            EnvNames = arrayOrEmpty entry.EnvNames
            Stdout = stringOrEmpty entry.Stdout
            Stderr = stringOrEmpty entry.Stderr }

    // Write the cassette atomically and owner-only: serialize into a sibling temp file created `0600`
    // from the start (so the secret-bearing bytes are never even briefly group/world-readable), then
    // rename it over the target — same-directory rename is atomic on one filesystem, so a reader never
    // sees a half-written cassette. On Windows the file inherits the directory ACL (restrict the
    // directory instead). Throws on failure after cleaning up the temp; callers decide how to report.
    static let writeCassette (path: string) (snapshot: CassetteEntry[]) : unit =
        let json =
            JsonSerializer.Serialize(
                { Version = currentFormatVersion
                  Entries = snapshot },
                jsonOptions
            )

        let dir =
            match Path.GetDirectoryName path with
            | null
            | "" -> "."
            | d -> d

        let tempPath =
            Path.Combine(dir, stringOrEmpty (Path.GetFileName path) + ".tmp-" + Guid.NewGuid().ToString "N")

        let writeContent () =
            if isWindows then
                File.WriteAllText(tempPath, json)
            else
                let options =
                    FileStreamOptions(
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                        UnixCreateMode = (UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                    )

                use stream = new FileStream(tempPath, options)
                use writer = new StreamWriter(stream)
                writer.Write json

        try
            writeContent () // `use` disposes here, flushing/closing before the rename
            File.Move(tempPath, path, true)
        with _ ->
            try
                File.Delete tempPath
            with _ ->
                // The temp may never have been created (CreateNew failed); nothing to clean up.
                ()

            reraise ()

    let gate = obj ()

    // The stdin-source digest used for matching, computed WITHOUT consuming the source: in-memory
    // bytes hash their content, a file source hashes its path. A one-shot streaming source can't be
    // keyed without consuming it, so it is rejected.
    let stdinDigest (command: Command) : Result<string option, ProcessError> =
        match command.Config.StdinSource with
        | None -> Ok None
        | Some stdin ->
            match stdin.Source with
            | StdinSource.Empty -> Ok None
            | StdinSource.Bytes bytes -> Ok(Some(Convert.ToHexString(SHA256.HashData bytes)))
            | StdinSource.File filePath ->
                Ok(Some(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("file:" + filePath)))))
            | StdinSource.Lines _
            | StdinSource.Reader _
            | StdinSource.AsyncLines _ ->
                Error(
                    ProcessError.Unsupported
                        "record/replay cannot key a one-shot stdin source (FromStream / FromLines / FromAsyncLines)"
                )

    let keyOf (command: Command) (digest: string option) : Key =
        command.Program, command.Config.Args, command.WorkingDirectory, command.Config.StdinSource.IsSome, digest

    let entryOf (command: Command) (result: ProcessResult<string>) (digest: string option) : CassetteEntry =
        let envNames =
            command.Config.EnvOverrides
            |> List.map fst
            |> List.distinct
            |> List.sort
            |> List.toArray

        let signal =
            match result.Outcome with
            | Outcome.Signalled(Some s) -> Nullable s
            | _ -> Nullable()

        { Program = command.Program
          Args = List.toArray command.Config.Args
          Cwd = Option.toObj command.WorkingDirectory
          StdinDigest = Option.toObj digest
          HasStdin = command.Config.StdinSource.IsSome
          EnvNames = envNames
          Stdout = result.Stdout
          Stderr = result.Stderr
          Code =
            (match result.Code with
             | Some c -> Nullable c
             | None -> Nullable())
          TimedOut = result.IsTimedOut
          Signal = signal }

    let outcomeOf (entry: CassetteEntry) : Outcome =
        if entry.TimedOut then
            Outcome.TimedOut
        elif entry.Signal.HasValue then
            Outcome.Signalled(Some entry.Signal.Value)
        elif entry.Code.HasValue then
            Outcome.Exited entry.Code.Value
        else
            Outcome.Exited 0

    let resultText (command: Command) (entry: CassetteEntry) : ProcessResult<string> =
        ProcessResult<string>(
            command.Program,
            entry.Stdout,
            entry.Stderr,
            outcomeOf entry,
            TimeSpan.Zero,
            false,
            command.Config.OkCodes
        )

    let play (slots: Dictionary<Key, ReplaySlot>) (key: Key) : CassetteEntry option =
        match slots.TryGetValue key with
        | true, slot ->
            let index = min slot.Next (slot.Entries.Length - 1)
            slot.Next <- slot.Next + 1
            Some slot.Entries[index]
        | _ -> None

    /// Start recording real runs (delegated to `inner`) to a cassette at `path`.
    static member Record(path: string, inner: IProcessRunner) =
        ArgumentNullException.ThrowIfNull path
        ArgumentNullException.ThrowIfNull inner
        new RecordReplayRunner(RecordMode(inner, List<CassetteEntry>(), ref false), path)

    /// Load a cassette at `path` for hermetic replay.
    static member Replay(path: string) : Result<RecordReplayRunner, ProcessError> =
        try
            let file =
                match JsonSerializer.Deserialize<CassetteFile>(File.ReadAllText path, jsonOptions) with
                | null -> { Version = 0; Entries = [||] }
                | loaded -> loaded

            if file.Version <> currentFormatVersion then
                Error(
                    ProcessError.Io
                        $"unsupported cassette format version {file.Version} (this build reads version {currentFormatVersion})"
                )
            else

                // Normalize every row so a crafted/partial cassette (omitted fields → null) can't NRE at
                // replay time, then group by key, accumulating into ResizeArrays (O(1) append) and freezing
                // to the immutable per-slot arrays once — not `Array.append` per duplicate, which is O(n²).
                let entries = arrayOrEmpty file.Entries |> Array.map normalizeEntry

                let grouped = Dictionary<Key, ResizeArray<CassetteEntry>>()

                for entry in entries do
                    let key =
                        entry.Program,
                        List.ofArray entry.Args,
                        Option.ofObj entry.Cwd,
                        entry.HasStdin,
                        Option.ofObj entry.StdinDigest

                    match grouped.TryGetValue key with
                    | true, bucket -> bucket.Add entry
                    | _ -> grouped[key] <- ResizeArray [ entry ]

                let slots = Dictionary<Key, ReplaySlot>()

                for kvp in grouped do
                    slots[kvp.Key] <-
                        { Entries = kvp.Value.ToArray()
                          Next = 0 }

                Ok(new RecordReplayRunner(ReplayMode slots, path))
        with ex ->
            Error(ProcessError.Io ex.Message)

    /// Write the recorded cassette to its path (owner-only `0600` on Unix). A no-op in replay mode.
    member _.Save() : Result<unit, ProcessError> =
        match mode with
        | ReplayMode _ -> Ok()
        | RecordMode(_, recorded, dirty) ->
            try
                let snapshot = lock gate (fun () -> recorded.ToArray())
                writeCassette path snapshot
                // Clear `dirty` only if nothing was recorded during the write — otherwise a `Capture`
                // that raced this `Save` would have its entry dropped from the drop-time flush.
                lock gate (fun () ->
                    if recorded.Count = snapshot.Length then
                        dirty.Value <- false)

                Ok()
            with ex ->
                Error(ProcessError.Io ex.Message)

    member private this.Capture(command: Command, cancellationToken: CancellationToken) =
        task {
            if cancellationToken.IsCancellationRequested then
                // Honour the cancelled-is-always-an-error contract on both modes: replay ignored the
                // token entirely, and record should not capture a run the caller cancelled up front.
                return Error(ProcessError.Cancelled command.Program)
            else
                match stdinDigest command with
                | Error error -> return Error error
                | Ok digest ->
                    match mode with
                    | RecordMode(inner, recorded, dirty) ->
                        match! inner.CaptureStringAsync(command, cancellationToken) with
                        | Error error -> return Error error
                        | Ok result ->
                            lock gate (fun () ->
                                recorded.Add(entryOf command result digest)
                                dirty.Value <- true)

                            return Ok result
                    | ReplayMode slots ->
                        match lock gate (fun () -> play slots (keyOf command digest)) with
                        | Some entry -> return Ok(resultText command entry)
                        | None -> return Error(ProcessError.CassetteMiss command.Program)
        }

    interface IProcessRunner with
        member this.CaptureStringAsync(command, cancellationToken) =
            this.Capture(command, cancellationToken)

        member this.CaptureBytesAsync(command, cancellationToken) =
            task {
                match! this.Capture(command, cancellationToken) with
                | Error error -> return Error error
                | Ok result ->
                    // Cassettes are text; the bytes verb round-trips the recorded stdout through UTF-8.
                    return
                        Ok(
                            ProcessResult<byte[]>(
                                result.Program,
                                Encoding.UTF8.GetBytes result.Stdout,
                                result.Stderr,
                                result.Outcome,
                                result.Duration,
                                false,
                                command.Config.OkCodes
                            )
                        )
            }

        member _.SpawnAsync(_command, _cancellationToken) =
            Task.FromResult(
                Error(ProcessError.Unsupported "RecordReplayRunner does not support SpawnAsync (live streaming)")
            )

    interface IDisposable with
        member _.Dispose() =
            match mode with
            | RecordMode(_, recorded, dirty) when lock gate (fun () -> dirty.Value) ->
                try
                    let snapshot = lock gate (fun () -> recorded.ToArray())
                    writeCassette path snapshot
                with _ ->
                    // Best-effort drop-time flush; an explicit Save surfaces write errors.
                    ()
            | _ -> ()
