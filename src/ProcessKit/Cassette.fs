namespace ProcessKit.Testing

open System
open System.Collections.Generic
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
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
        /// The captured standard output as text (verbatim; may contain secrets). For a `byte[]` capture
        /// (recorded via the bytes verb) this is empty and the exact bytes live in `StdoutBase64` — a
        /// string-verb replay of such an entry decodes `StdoutBase64` with the command's stdout encoding.
        Stdout: string
        /// The captured standard error (verbatim; may contain secrets). Always text — a `byte[]` capture's
        /// stderr is decoded, so stderr never needs a base64 form.
        Stderr: string
        /// The exact captured stdout bytes, base64-encoded — present only for a `byte[]` capture recording
        /// (`CaptureBytesAsync`), so replay of the bytes verb reproduces non-UTF-8 output exactly. `null`
        /// for a text recording (and for a pre-v2 cassette), where `Stdout` carries the (decoded) text.
        StdoutBase64: string | null
        /// The exit code, or `null` if the process did not exit normally (e.g. it was signalled).
        Code: Nullable<int>
        /// Whether the run was terminated by a timeout.
        TimedOut: bool
        /// The terminating signal number on POSIX, or `null` if the process was not signalled.
        Signal: Nullable<int>
        /// Whether the captured output was truncated by an output-buffer policy (so a bounded-policy
        /// recording replays as truncated). Absent in a pre-1.x cassette — defaults to `false`.
        Truncated: bool
        /// The recorded wall-clock duration in milliseconds, so `ProcessResult.Duration` survives replay.
        /// Absent in a pre-1.x cassette — defaults to `0`.
        DurationMs: double
    }

/// The on-disk cassette envelope: a format `version` (so a format newer than this build understands is
/// rejected rather than misread, while an older compatible version still loads) wrapping the recorded
/// `entries`. Public so `System.Text.Json` can serialize it; inspect a cassette file directly rather
/// than depending on this shape.
[<CLIMutable>]
type CassetteFile =
    {
        /// The cassette format version. A file whose version is newer than this build's is rejected; an
        /// older, still-supported version loads (missing fields default).
        Version: int
        /// The recorded invocation→result rows, in capture order.
        Entries: CassetteEntry[]
    }

/// Optional knobs for a `RecordReplayRunner` — matching customization (a stdin file-content digest, an
/// argument normalizer) and a record-time redaction hook. Immutable and fluent; the same instance must
/// be used at record and replay time, since it changes how invocations are keyed. Default: path-only
/// stdin-file matching, verbatim args, no redaction.
[<Sealed>]
type RecordReplayOptions
    private
    (hashFileStdinContents: bool, argNormalizer: (string[] -> string[]) option, redaction: (string -> string) option) =

    /// The defaults: a `Stdin.FromFile` source is keyed by its **path** (not contents), arguments are
    /// matched verbatim, and captured output is stored as-is.
    new() = RecordReplayOptions(false, None, None)

    member internal _.HashFileStdinContents = hashFileStdinContents
    member internal _.ArgNormalizer = argNormalizer
    member internal _.Redaction = redaction

    /// Key a `Stdin.FromFile` source by its **contents** (a SHA-256 of the file's bytes) rather than its
    /// path, so a cassette matches on what was actually fed to the child. Opt-in: reading the file has a
    /// cost, and the file must exist at both record and replay time (an unreadable file surfaces
    /// `ProcessError.Stdin`). A content digest matches a `Stdin.FromBytes` of the same bytes.
    member _.WithFileStdinContentHashing() =
        RecordReplayOptions(true, argNormalizer, redaction)

    /// Normalize the argument list before it is used to match an invocation, so a volatile argument (a
    /// temp directory, a nonce) no longer defeats the match — e.g. drop it, or rewrite it to a stable
    /// placeholder. Applied to both the recorded and the live command, so keying stays symmetric; the
    /// **raw** arguments are still stored verbatim in the cassette for inspection.
    member _.WithArgNormalizer(normalizer: Func<string[], string[]>) =
        ArgumentNullException.ThrowIfNull normalizer
        RecordReplayOptions(hashFileStdinContents, Some normalizer.Invoke, redaction)

    /// Scrub captured **text** before it is written to the cassette, so a secret echoed to stdout/stderr
    /// (a token, a password) never reaches disk. Applied at record time to the stdout and stderr text of
    /// a string capture and to the stderr of a bytes capture; a `byte[]` stdout capture is stored opaquely
    /// (base64) and is **not** passed through the redactor.
    member _.WithRedaction(redact: Func<string, string>) =
        ArgumentNullException.ThrowIfNull redact
        RecordReplayOptions(hashFileStdinContents, argNormalizer, Some redact.Invoke)

// Match key: program + args + cwd + whether-stdin + stdin digest. F# tuple/list have structural
// equality, so this works as a Dictionary key.
type private Key = string * string list * string option * bool * string option

// One key's entries in capture order, with the order-then-repeat-last cursor. `Entries` is mutable so
// Auto mode can append a freshly-recorded (missed) entry to an existing key's group.
type private ReplaySlot =
    { mutable Entries: CassetteEntry[]
      mutable Next: int }

type private Mode =
    | RecordMode of inner: IProcessRunner * recorded: List<CassetteEntry> * dirty: bool ref
    | ReplayMode of slots: Dictionary<Key, ReplaySlot>
    // Replay what the cassette holds; delegate a miss to `inner`, record it, and persist (VCR "new
    // episodes"). `recorded` seeds from the loaded entries and grows on each miss; `slots` is the
    // live replay index, updated so a repeat of a just-recorded key replays.
    | AutoMode of
        inner: IProcessRunner *
        slots: Dictionary<Key, ReplaySlot> *
        recorded: List<CassetteEntry> *
        dirty: bool ref

/// A record/replay `IProcessRunner`.
///
/// **Record** mode wraps a real inner runner, captures each completed call to a JSON cassette
/// (written on `Save`, or best-effort on dispose), and returns the live result. Errors (a spawn
/// failure) record nothing; non-zero exits and captured timeouts are results and are recorded.
///
/// **Replay** mode loads the cassette and serves results with **no subprocess**: a match is keyed on
/// program + args + cwd + stdin-source digest; duplicates replay in capture order then repeat the
/// last; an unmatched call is `ProcessError.CassetteMiss` (never a surprise subprocess). Covers the
/// text and **bytes** capture verbs (`CaptureStringAsync` / `CaptureBytesAsync`, the latter reproducing
/// exact bytes from a bytes recording) and `SpawnAsync` (a live handle is reconstructed from the
/// recording, so streaming/readiness consumers replay too). A one-shot stdin source (`FromStream` /
/// `FromLines` / `FromAsyncLines`) cannot be keyed and errors.
///
/// **Auto** mode (`Auto`) replays what the cassette holds and records+persists any miss, so a cassette
/// is easy to grow. Record-mode `SpawnAsync` is unsupported (a live stream cannot be captured without
/// racing the consumer) — record a streaming call through a capture verb, then replay it as a stream.
[<Sealed>]
type RecordReplayRunner private (mode: Mode, path: string, options: RecordReplayOptions) =

    // Omit null fields on write so a text cassette stays as compact and diffable as a v1 one (the new
    // base64 / signal / code fields don't add noisy `null` lines); load coalesces omitted fields anyway.
    static let jsonOptions =
        JsonSerializerOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)

    // The cassette format version this build writes. Older, still-supported versions load (missing
    // fields default); a version newer than this is rejected. Bump when the on-disk schema changes.
    // v2 added `StdoutBase64` (exact bytes for the bytes capture verb).
    static let currentFormatVersion = 2

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

    // SHA-256 hex of raw bytes — the shared digest for in-memory stdin and (opt-in) file-content stdin.
    static let hashBytes (bytes: byte[]) : string =
        Convert.ToHexString(SHA256.HashData bytes)

    // Apply the optional argument normalizer for keying (used identically when building the replay index
    // and when matching a live command). A user-supplied `Func` could return null despite its type, so a
    // null result coalesces to an empty list rather than tripping a NullReferenceException at match time.
    static let applyNormalizer (normalizer: (string[] -> string[]) option) (args: string[]) : string list =
        match normalizer with
        | None -> List.ofArray args
        | Some f ->
            let result = f args

            if obj.ReferenceEquals(result, null) then
                []
            else
                List.ofArray result

    static let normalizeEntry (entry: CassetteEntry) : CassetteEntry =
        // Clamp a crafted/corrupted `DurationMs` into `TimeSpan`'s range (and a NaN/∞ to 0), so replay's
        // `TimeSpan.FromMilliseconds` can't overflow-throw on a hand-edited cassette — same "a partial /
        // crafted entry can't trip replay" guarantee the null-coalescing below gives the string fields.
        let durationMs =
            if Double.IsFinite entry.DurationMs then
                Math.Clamp(entry.DurationMs, 0.0, TimeSpan.MaxValue.TotalMilliseconds)
            else
                0.0

        { entry with
            Program = stringOrEmpty entry.Program
            Args = arrayOrEmpty entry.Args
            EnvNames = arrayOrEmpty entry.EnvNames
            Stdout = stringOrEmpty entry.Stdout
            Stderr = stringOrEmpty entry.Stderr
            DurationMs = durationMs }

    // Build a replay index from cassette entries, grouping duplicates of a key in capture order and
    // freezing each group to an immutable array once (not `Array.append` per duplicate, which is O(n²)).
    // The key uses the same argument normalizer that a live match will, so the two sides stay symmetric.
    static let buildSlots
        (normalizer: (string[] -> string[]) option)
        (entries: CassetteEntry[])
        : Dictionary<Key, ReplaySlot> =
        let grouped = Dictionary<Key, ResizeArray<CassetteEntry>>()

        for entry in entries do
            let key =
                entry.Program,
                applyNormalizer normalizer entry.Args,
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

        slots

    // Parse and normalize a cassette file, rejecting a version this build does not understand. Shared by
    // Replay and Auto (Auto tolerates a missing file — a fresh cassette to grow).
    static let loadEntries (path: string) : Result<CassetteEntry[], ProcessError> =
        try
            let file =
                match JsonSerializer.Deserialize<CassetteFile>(File.ReadAllText path, jsonOptions) with
                | null -> { Version = 0; Entries = [||] }
                | loaded -> loaded

            // Accept a compatible (older-or-equal) version; reject a format newer than this build, or a
            // nonsensical version (< 1, e.g. an omitted `Version` deserializing to 0).
            if file.Version < 1 || file.Version > currentFormatVersion then
                Error(
                    ProcessError.Io
                        $"unsupported cassette format version {file.Version} (this build reads versions 1..{currentFormatVersion})"
                )
            else
                Ok(arrayOrEmpty file.Entries |> Array.map normalizeEntry)
        with ex ->
            Error(ProcessError.Io ex.Message)

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

    // Apply the optional record-time redaction hook to captured text (coalescing a null return to "").
    let redactText (text: string) : string =
        match options.Redaction with
        | None -> text
        | Some redact ->
            let scrubbed = redact text
            if obj.ReferenceEquals(scrubbed, null) then "" else scrubbed

    // The stdin-source digest used for matching, computed WITHOUT consuming the source: in-memory
    // bytes hash their content, a file source hashes its path (or, opt-in, its contents). A one-shot
    // streaming source can't be keyed without consuming it, so it is rejected.
    let stdinDigest (command: Command) : Result<string option, ProcessError> =
        match command.Config.StdinSource with
        | None -> Ok None
        | Some stdin ->
            match stdin.Source with
            | StdinSource.Empty -> Ok None
            | StdinSource.Bytes bytes -> Ok(Some(hashBytes bytes))
            | StdinSource.File filePath ->
                if options.HashFileStdinContents then
                    try
                        Ok(Some(hashBytes (File.ReadAllBytes filePath)))
                    with ex ->
                        Error(ProcessError.Stdin(command.Program, ex.Message))
                else
                    Ok(Some(hashBytes (Encoding.UTF8.GetBytes("file:" + filePath))))
            | StdinSource.Lines _
            | StdinSource.Reader _
            | StdinSource.AsyncLines _ ->
                Error(
                    ProcessError.Unsupported
                        "record/replay cannot key a one-shot stdin source (FromStream / FromLines / FromAsyncLines)"
                )

    let keyOf (command: Command) (digest: string option) : Key =
        let args = applyNormalizer options.ArgNormalizer (List.toArray command.Config.Args)
        command.Program, args, command.WorkingDirectory, command.Config.StdinSource.IsSome, digest

    let envNamesOf (command: Command) : string[] =
        command.Config.EnvOverrides
        |> List.map fst
        |> List.distinct
        |> List.sort
        |> List.toArray

    let signalOf (outcome: Outcome) : Nullable<int> =
        match outcome with
        | Outcome.Signalled(Some s) -> Nullable s
        | _ -> Nullable()

    let codeOf (code: int option) : Nullable<int> =
        match code with
        | Some c -> Nullable c
        | None -> Nullable()

    // Record a text capture: stdout/stderr are the decoded strings (redacted); no base64.
    let entryOfText (command: Command) (result: ProcessResult<string>) (digest: string option) : CassetteEntry =
        { Program = command.Program
          Args = List.toArray command.Config.Args
          Cwd = Option.toObj command.WorkingDirectory
          StdinDigest = Option.toObj digest
          HasStdin = command.Config.StdinSource.IsSome
          EnvNames = envNamesOf command
          Stdout = redactText result.Stdout
          Stderr = redactText result.Stderr
          StdoutBase64 = null
          Code = codeOf result.Code
          TimedOut = result.IsTimedOut
          Signal = signalOf result.Outcome
          Truncated = result.Truncated
          DurationMs = result.Duration.TotalMilliseconds }

    // Record a bytes capture: exact stdout bytes go to base64 (Stdout text stays empty — a string-verb
    // replay decodes the base64); stderr is text (redacted). The opaque bytes are not redacted.
    let entryOfBytes (command: Command) (result: ProcessResult<byte[]>) (digest: string option) : CassetteEntry =
        { Program = command.Program
          Args = List.toArray command.Config.Args
          Cwd = Option.toObj command.WorkingDirectory
          StdinDigest = Option.toObj digest
          HasStdin = command.Config.StdinSource.IsSome
          EnvNames = envNamesOf command
          Stdout = ""
          Stderr = redactText result.Stderr
          StdoutBase64 = Convert.ToBase64String result.Stdout
          Code = codeOf result.Code
          TimedOut = result.IsTimedOut
          Signal = signalOf result.Outcome
          Truncated = result.Truncated
          DurationMs = result.Duration.TotalMilliseconds }

    let outcomeOf (entry: CassetteEntry) : Outcome =
        if entry.TimedOut then
            Outcome.TimedOut
        elif entry.Signal.HasValue then
            Outcome.Signalled(Some entry.Signal.Value)
        elif entry.Code.HasValue then
            Outcome.Exited entry.Code.Value
        else
            Outcome.Exited 0

    // The recorded stdout as text: a bytes recording (base64 present) decodes with the command's stdout
    // encoding — exactly what a real bytes→text conversion would do; a text recording uses `Stdout`.
    let stdoutText (command: Command) (entry: CassetteEntry) : string =
        match entry.StdoutBase64 with
        | null -> entry.Stdout
        | base64 ->
            try
                command.Config.StdoutEncoding.GetString(Convert.FromBase64String base64)
            with _ ->
                // A hand-corrupted base64 field can't take down replay — fall back to the text form.
                entry.Stdout

    let resultText (command: Command) (entry: CassetteEntry) : ProcessResult<string> =
        ProcessResult<string>(
            command.Program,
            stdoutText command entry,
            entry.Stderr,
            outcomeOf entry,
            TimeSpan.FromMilliseconds entry.DurationMs,
            entry.Truncated,
            command.Config.OkCodes
        )

    // Replay a bytes result: only a bytes recording (base64 present) can promise exact bytes; a text /
    // pre-v2 entry is rejected rather than handing back a lossy re-encode (the honest-results contract).
    let resultBytes (command: Command) (entry: CassetteEntry) : Result<ProcessResult<byte[]>, ProcessError> =
        match entry.StdoutBase64 with
        | null ->
            Error(
                ProcessError.Unsupported
                    "this cassette entry was recorded as text; re-record the call with the bytes capture verb to replay exact bytes"
            )
        | base64 ->
            try
                Ok(
                    ProcessResult<byte[]>(
                        command.Program,
                        Convert.FromBase64String base64,
                        entry.Stderr,
                        outcomeOf entry,
                        TimeSpan.FromMilliseconds entry.DurationMs,
                        entry.Truncated,
                        command.Config.OkCodes
                    )
                )
            with ex ->
                Error(ProcessError.Io $"corrupt base64 stdout in cassette entry for '{command.Program}': {ex.Message}")

    // Reconstruct a live handle from a recorded entry, reusing the same in-memory `FakeProcess` the
    // scripted double builds — so a replayed stream agrees with a real run on line splitting, encoding,
    // OkCodes, and outcome.
    let spawnFromEntry (command: Command) (entry: CassetteEntry) : RunningProcess =
        FakeProcess
            .OfCommand(command)
            .WithStdout(stdoutText command entry)
            .WithStderr(entry.Stderr)
            .WithOutcome(outcomeOf entry)
            .Build()

    let play (slots: Dictionary<Key, ReplaySlot>) (key: Key) : CassetteEntry option =
        match slots.TryGetValue key with
        | true, slot ->
            let index = min slot.Next (slot.Entries.Length - 1)
            slot.Next <- slot.Next + 1
            Some slot.Entries[index]
        | _ -> None

    // Register a freshly-recorded (missed) entry into the live replay index, so a repeat of the same key
    // in an Auto session replays it instead of hitting the inner runner again.
    let remember (slots: Dictionary<Key, ReplaySlot>) (key: Key) (entry: CassetteEntry) : unit =
        match slots.TryGetValue key with
        | true, slot -> slot.Entries <- Array.append slot.Entries [| entry |]
        | _ -> slots[key] <- { Entries = [| entry |]; Next = 0 }

    /// Start recording real runs (delegated to `inner`) to a cassette at `path`.
    static member Record(path: string, inner: IProcessRunner) =
        RecordReplayRunner.Record(path, inner, RecordReplayOptions())

    /// Start recording real runs (delegated to `inner`) to a cassette at `path`, with matching/redaction
    /// `options` (the same `options` must be used when the cassette is later replayed).
    static member Record(path: string, inner: IProcessRunner, options: RecordReplayOptions) =
        ArgumentNullException.ThrowIfNull path
        ArgumentNullException.ThrowIfNull inner
        ArgumentNullException.ThrowIfNull options
        new RecordReplayRunner(RecordMode(inner, List<CassetteEntry>(), ref false), path, options)

    /// Load a cassette at `path` for hermetic replay.
    static member Replay(path: string) : Result<RecordReplayRunner, ProcessError> =
        RecordReplayRunner.Replay(path, RecordReplayOptions())

    /// Load a cassette at `path` for hermetic replay, with the matching `options` used when it was recorded.
    static member Replay(path: string, options: RecordReplayOptions) : Result<RecordReplayRunner, ProcessError> =
        ArgumentNullException.ThrowIfNull path
        ArgumentNullException.ThrowIfNull options

        match loadEntries path with
        | Error error -> Error error
        | Ok entries -> Ok(new RecordReplayRunner(ReplayMode(buildSlots options.ArgNormalizer entries), path, options))

    /// Replay a cassette at `path`, recording and persisting any invocation that **misses** (VCR "new
    /// episodes"): existing entries replay hermetically, a first-seen call is delegated to `inner`,
    /// recorded, and grown into the cassette on `Save`/dispose. A missing file starts an empty cassette.
    static member Auto(path: string, inner: IProcessRunner) : Result<RecordReplayRunner, ProcessError> =
        RecordReplayRunner.Auto(path, inner, RecordReplayOptions())

    /// Replay-with-record-on-miss (see `Auto(path, inner)`), with matching/redaction `options`.
    static member Auto
        (path: string, inner: IProcessRunner, options: RecordReplayOptions)
        : Result<RecordReplayRunner, ProcessError> =
        ArgumentNullException.ThrowIfNull path
        ArgumentNullException.ThrowIfNull inner
        ArgumentNullException.ThrowIfNull options

        // Auto grows a cassette, so a missing OR empty file is a fresh start (not a load error): a
        // just-touched path — `Path.GetTempFileName`, a `touch`ed fixture — begins recording cleanly.
        let loaded =
            if not (File.Exists path) then
                Ok [||]
            else
                let text =
                    try
                        File.ReadAllText path
                    with _ ->
                        // Unreadable here surfaces as a load error below; treat as non-empty to reach it.
                        "?"

                if String.IsNullOrWhiteSpace text then
                    Ok [||]
                else
                    loadEntries path

        match loaded with
        | Error error -> Error error
        | Ok entries ->
            Ok(
                new RecordReplayRunner(
                    AutoMode(inner, buildSlots options.ArgNormalizer entries, List<CassetteEntry>(entries), ref false),
                    path,
                    options
                )
            )

    /// Write the recorded cassette to its path (owner-only `0600` on Unix). A no-op in replay mode.
    member _.Save() : Result<unit, ProcessError> =
        match mode with
        | ReplayMode _ -> Ok()
        | RecordMode(_, recorded, dirty)
        | AutoMode(_, _, recorded, dirty) ->
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
                // Honour the cancelled-is-always-an-error contract on every mode: replay ignored the
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
                                recorded.Add(entryOfText command result digest)
                                dirty.Value <- true)

                            return Ok result
                    | ReplayMode slots ->
                        match lock gate (fun () -> play slots (keyOf command digest)) with
                        | Some entry -> return Ok(resultText command entry)
                        | None -> return Error(ProcessError.CassetteMiss command.Program)
                    | AutoMode(inner, slots, recorded, dirty) ->
                        let key = keyOf command digest

                        match lock gate (fun () -> play slots key) with
                        | Some entry -> return Ok(resultText command entry)
                        | None ->
                            match! inner.CaptureStringAsync(command, cancellationToken) with
                            | Error error -> return Error error
                            | Ok result ->
                                let entry = entryOfText command result digest

                                lock gate (fun () ->
                                    recorded.Add entry
                                    remember slots key entry
                                    dirty.Value <- true)

                                return Ok result
        }

    member private this.CaptureBytes(command: Command, cancellationToken: CancellationToken) =
        task {
            if cancellationToken.IsCancellationRequested then
                return Error(ProcessError.Cancelled command.Program)
            else
                match stdinDigest command with
                | Error error -> return Error error
                | Ok digest ->
                    match mode with
                    | RecordMode(inner, recorded, dirty) ->
                        match! inner.CaptureBytesAsync(command, cancellationToken) with
                        | Error error -> return Error error
                        | Ok result ->
                            lock gate (fun () ->
                                recorded.Add(entryOfBytes command result digest)
                                dirty.Value <- true)

                            return Ok result
                    | ReplayMode slots ->
                        match lock gate (fun () -> play slots (keyOf command digest)) with
                        | Some entry -> return resultBytes command entry
                        | None -> return Error(ProcessError.CassetteMiss command.Program)
                    | AutoMode(inner, slots, recorded, dirty) ->
                        let key = keyOf command digest

                        match lock gate (fun () -> play slots key) with
                        | Some entry -> return resultBytes command entry
                        | None ->
                            match! inner.CaptureBytesAsync(command, cancellationToken) with
                            | Error error -> return Error error
                            | Ok result ->
                                let entry = entryOfBytes command result digest

                                lock gate (fun () ->
                                    recorded.Add entry
                                    remember slots key entry
                                    dirty.Value <- true)

                                return Ok result
        }

    // Replay a live handle from the cassette. Record mode cannot capture a live stream without racing
    // the consumer, so it is unsupported there — record a streaming call through a capture verb first.
    member private this.Spawn
        (command: Command, cancellationToken: CancellationToken)
        : Result<RunningProcess, ProcessError> =
        if cancellationToken.IsCancellationRequested then
            Error(ProcessError.Cancelled command.Program)
        else
            match mode with
            | RecordMode _ ->
                Error(
                    ProcessError.Unsupported
                        "RecordReplayRunner cannot record a live SpawnAsync stream — record the call through a capture verb, then replay it as a stream"
                )
            | ReplayMode slots
            | AutoMode(_, slots, _, _) ->
                match stdinDigest command with
                | Error error -> Error error
                | Ok digest ->
                    match lock gate (fun () -> play slots (keyOf command digest)) with
                    | Some entry -> Ok(spawnFromEntry command entry)
                    | None ->
                        // Auto cannot auto-record a live stream any more than record mode can; both surface
                        // a miss rather than a surprise subprocess or a silently uncaptured recording.
                        match mode with
                        | AutoMode _ ->
                            Error(
                                ProcessError.Unsupported
                                    "RecordReplayRunner (Auto) cannot record a missing SpawnAsync stream — record the call through a capture verb first"
                            )
                        | _ -> Error(ProcessError.CassetteMiss command.Program)

    interface IProcessRunner with
        member this.CaptureStringAsync(command, cancellationToken) =
            this.Capture(command, cancellationToken)

        member this.CaptureBytesAsync(command, cancellationToken) =
            this.CaptureBytes(command, cancellationToken)

        member this.SpawnAsync(command, cancellationToken) =
            Task.FromResult(this.Spawn(command, cancellationToken))

    interface IDisposable with
        member _.Dispose() =
            match mode with
            | RecordMode(_, recorded, dirty)
            | AutoMode(_, _, recorded, dirty) when lock gate (fun () -> dirty.Value) ->
                try
                    let snapshot = lock gate (fun () -> recorded.ToArray())
                    writeCassette path snapshot
                with _ ->
                    // Best-effort drop-time flush; an explicit Save surfaces write errors.
                    ()
            | _ -> ()
