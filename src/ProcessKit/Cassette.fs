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

/// One captured `invocation → result` pair — the on-disk cassette row (public so `System.Text.Json`
/// can serialize it; inspect a cassette file directly rather than depending on this shape). Only env
/// *names* are stored (values redacted); `program`, `args`, `cwd`, `stdout`, and `stderr` are
/// verbatim and can carry secrets — review a cassette before committing it.
[<CLIMutable>]
type CassetteEntry =
    { Program: string
      Args: string[]
      Cwd: string | null
      StdinDigest: string | null
      HasStdin: bool
      EnvNames: string[]
      Stdout: string
      Stderr: string
      Code: Nullable<int>
      TimedOut: bool
      Signal: Nullable<int> }

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
/// `OutputString` (and `OutputBytes` via a UTF-8 round-trip of the recorded stdout); `Start` is
/// unsupported. A one-shot stdin source (`FromReader` / `FromLines` / `FromAsyncLines`) cannot be
/// keyed and errors.
[<Sealed>]
type RecordReplayRunner private (mode: Mode, path: string) =

    static let jsonOptions = JsonSerializerOptions(WriteIndented = true)

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
                        "record/replay cannot key a one-shot stdin source (FromReader / FromLines / FromAsyncLines)"
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
            let entries =
                match JsonSerializer.Deserialize<CassetteEntry[]>(File.ReadAllText path, jsonOptions) with
                | null -> [||]
                | loaded -> loaded

            // Group entries by key, accumulating into ResizeArrays (O(1) append) and freezing to the
            // immutable per-slot arrays once — not `Array.append` per duplicate, which is O(n²).
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
                File.WriteAllText(path, JsonSerializer.Serialize(snapshot, jsonOptions))

                if not (RuntimeInformation.IsOSPlatform OSPlatform.Windows) then
                    // The cassette stores argv/cwd/stdout/stderr verbatim (secrets possible); keep it
                    // owner-only. Windows inherits the directory ACL — restrict the directory instead.
                    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

                dirty.Value <- false
                Ok()
            with ex ->
                Error(ProcessError.Io ex.Message)

    member private this.Capture(command: Command, cancellationToken: CancellationToken) =
        task {
            match stdinDigest command with
            | Error error -> return Error error
            | Ok digest ->
                match mode with
                | RecordMode(inner, recorded, dirty) ->
                    match! inner.OutputString(command, cancellationToken) with
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
        member this.OutputString(command, cancellationToken) =
            this.Capture(command, cancellationToken)

        member this.OutputBytes(command, cancellationToken) =
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

        member _.Start(_command, _cancellationToken) =
            Task.FromResult(
                Error(ProcessError.Unsupported "RecordReplayRunner does not support Start (live streaming)")
            )

    interface IDisposable with
        member _.Dispose() =
            match mode with
            | RecordMode(_, recorded, dirty) when dirty.Value ->
                try
                    let snapshot = lock gate (fun () -> recorded.ToArray())
                    File.WriteAllText(path, JsonSerializer.Serialize(snapshot, jsonOptions))

                    if not (RuntimeInformation.IsOSPlatform OSPlatform.Windows) then
                        File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                with _ ->
                    // Best-effort drop-time flush; an explicit Save surfaces write errors.
                    ()
            | _ -> ()
