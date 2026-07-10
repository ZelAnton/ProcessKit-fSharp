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

// libc `open`/`fsync`/`close` for the Unix-only best-effort parent-directory fsync in `writeCassette`
// below (a plain `File.Open` cannot open a directory, so this needs a raw P/Invoke rather than a
// managed API). A module, not class `static let` bindings, because F# DllImport `extern` declarations
// must be module-level or type `static member` — a `static let` in a class cannot carry `DllImport`.
module private NativeDirFsync =
    [<DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)>]
    extern int openDirFd(string path, int flags)

    [<DllImport("libc", SetLastError = true)>]
    extern int fsyncFd(int fd)

    [<DllImport("libc", SetLastError = true)>]
    extern int closeFd(int fd)

/// One captured `invocation → result` pair — a row inside the `CassetteFile` envelope (public so
/// `System.Text.Json` can serialize it; inspect a cassette file directly rather than depending on
/// this shape). Env values are never stored in clear text — only the variable *names* and a redacting
/// `EnvFingerprint` of the effective environment; `program`, `args`, `cwd`, `stdout`, and `stderr`
/// are verbatim and can carry secrets — review a cassette before committing it.
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
        /// A stable, versioned fingerprint of the child's **effective** environment semantics — whether
        /// the inherited environment was cleared (`EnvClear`) plus the final effect of the explicit
        /// overrides (each name's last set value or its removal, folded under the platform's env-name
        /// case rules). It is part of the replay match key, so a call with a genuinely different
        /// environment (a changed value, name, removal, or `EnvClear`) no longer replays an unrelated
        /// recording, while repeated/no-op overrides with the same net effect still match. Env **values**
        /// are hashed into it (SHA-256), never stored in clear text — but a low-entropy value (a short
        /// token/PIN) can be recovered from the digest by brute force, so treat a cassette recorded with
        /// secret env values as sensitive. `null` in a pre-v3 cassette (no fingerprint recorded): such an
        /// entry keys as the default, un-customized environment (see `RecordReplayRunner`).
        EnvFingerprint: string | null
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
/// argument normalizer, opt-in `cwd` matching) and a record-time redaction hook. Immutable and fluent;
/// the same instance must be used at record and replay time, since it changes how invocations are keyed.
/// Default: path-only stdin-file matching, verbatim args, no redaction, and the working directory does
/// **not** participate in matching (see `WithCwdMatching`).
[<Sealed>]
type RecordReplayOptions
    private
    (
        hashFileStdinContents: bool,
        argNormalizer: (string[] -> string[]) option,
        redaction: (string -> string) option,
        matchCwd: bool
    ) =

    /// The defaults: a `Stdin.FromFile` source is keyed by its **path** (not contents), arguments are
    /// matched verbatim, captured output is stored as-is, and the working directory is **not** part of
    /// the match key (see `WithCwdMatching`).
    new() = RecordReplayOptions(false, None, None, false)

    member internal _.HashFileStdinContents = hashFileStdinContents
    member internal _.ArgNormalizer = argNormalizer
    member internal _.Redaction = redaction
    member internal _.MatchCwd = matchCwd

    /// Key a `Stdin.FromFile` source by its **contents** (a SHA-256 of the file's bytes) rather than its
    /// path, so a cassette matches on what was actually fed to the child. Opt-in: reading the file has a
    /// cost, and the file must exist at both record and replay time (an unreadable file surfaces
    /// `ProcessError.Stdin`). A content digest matches a `Stdin.FromBytes` of the same bytes.
    member _.WithFileStdinContentHashing() =
        RecordReplayOptions(true, argNormalizer, redaction, matchCwd)

    /// Normalize the argument list before it is used to match an invocation, so a volatile argument (a
    /// temp directory, a nonce) no longer defeats the match — e.g. drop it, or rewrite it to a stable
    /// placeholder. Applied to both the recorded and the live command, so keying stays symmetric; the
    /// **raw** arguments are still stored verbatim in the cassette for inspection.
    member _.WithArgNormalizer(normalizer: Func<string[], string[]>) =
        ArgumentNullException.ThrowIfNull normalizer
        RecordReplayOptions(hashFileStdinContents, Some normalizer.Invoke, redaction, matchCwd)

    /// Scrub captured **text** before it is written to the cassette, so a secret echoed to stdout/stderr
    /// (a token, a password) never reaches disk. Applied at record time to the stdout and stderr text of
    /// a string capture and to the stderr of a bytes capture; a `byte[]` stdout capture is stored opaquely
    /// (base64) and is **not** passed through the redactor.
    member _.WithRedaction(redact: Func<string, string>) =
        ArgumentNullException.ThrowIfNull redact
        RecordReplayOptions(hashFileStdinContents, argNormalizer, Some redact.Invoke, matchCwd)

    /// Restore the working directory (`Command.CurrentDir`) as part of the replay match key, so two
    /// otherwise-identical invocations that ran in different directories are treated as distinct
    /// recordings. Opt-in: by default `cwd` does **not** participate in matching, because a cassette's
    /// absolute working directory is almost always an artifact of where it happened to be recorded (a
    /// developer's checkout, a CI runner's workspace) rather than something a call genuinely depends on —
    /// with `cwd` in the key by default, a cassette recorded on one machine silently fails to replay on
    /// another (`ProcessError.CassetteMiss`), which is the common case this option exists to opt back out
    /// of. `cwd` is still stored verbatim in every `CassetteEntry.Cwd` for inspection regardless of this
    /// setting. Must be applied symmetrically — the same setting used to record a cassette must be used
    /// to replay it, or the match key will silently disagree between the two.
    member _.WithCwdMatching() =
        RecordReplayOptions(hashFileStdinContents, argNormalizer, redaction, true)

// Match key: program + args + cwd (only when `RecordReplayOptions.WithCwdMatching` is set; `None`
// otherwise, so `cwd` never distinguishes two entries by default) + whether-stdin + stdin digest +
// effective-environment fingerprint. F# tuple/list have structural equality, so this works as a
// Dictionary key.
type private Key = string * string list * string option * bool * string option * string

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
/// program + args + stdin-source digest + an effective-environment fingerprint (so a call whose env
/// values/names/removals or `EnvClear` differ no longer replays an unrelated recording — a pre-v3
/// cassette with no fingerprint keys as the un-customized environment); the working directory does
/// **not** participate in the key by default (a cassette recorded in one `cwd` replays from another),
/// though `CassetteEntry.Cwd` still stores it verbatim for inspection — opt into cwd-sensitive matching
/// with `RecordReplayOptions.WithCwdMatching()`, applied symmetrically at record and replay time;
/// duplicates replay in capture order then repeat the last; an unmatched call is
/// `ProcessError.CassetteMiss` (never a surprise subprocess). Covers the
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
    // v2 added `StdoutBase64` (exact bytes for the bytes capture verb); v3 added `EnvFingerprint` (the
    // effective-environment fingerprint that folds env semantics into the replay match key).
    static let currentFormatVersion = 3

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

    // The env-fingerprint scheme version, independent of the cassette FILE version: it tags the string
    // below so a fingerprint from an older scheme can never silently compare equal to a newer one. Bump
    // it if the canonical serialization changes.
    static let envFingerprintScheme = 1

    // The fingerprint of the default environment: inherit the parent's, no overrides, not cleared. A
    // pre-v3 entry (no stored fingerprint) maps here as well, so a cassette recorded from commands that
    // never customized the environment keeps replaying unchanged after the upgrade. It is distinct from
    // an `EnvClear` with no overrides (an empty environment, not an inherited one), which is hashed below.
    static let defaultEnvFingerprint = $"{envFingerprintScheme}|default"

    // A stable, versioned fingerprint of a command's effective environment SEMANTICS: the `ClearEnv`
    // flag plus the FINAL effect of the ordered overrides (last write wins per name; a name ends either
    // set to a value or removed), folded under the platform's env-name case rules (Windows names are
    // case-insensitive → canonical upper-case; POSIX case-sensitive → verbatim). Repeated or no-op
    // overrides with the same net effect collapse to one fingerprint; a genuinely different environment
    // (a changed value, name, removal, or `ClearEnv`) yields a different one. Env VALUES are hashed in
    // (SHA-256 over a length-prefixed canonical form — each name/value is written as `<charCount>:<text>`
    // so no name or value, whatever it contains, can straddle a field boundary and collide with another),
    // never emitted in clear text.
    static let envFingerprint (clearEnv: bool) (overrides: (string * string option) seq) : string =
        let canon (name: string) =
            if isWindows then name.ToUpperInvariant() else name

        let effective = Dictionary<string, string option>(StringComparer.Ordinal)

        for name, value in overrides do
            effective[canon name] <- value // last write wins per canonical name

        if not clearEnv && effective.Count = 0 then
            // No environment customization at all: the shared default fingerprint (also the pre-v3 map).
            defaultEnvFingerprint
        else
            let sb = StringBuilder()

            sb
                .Append(envFingerprintScheme)
                .Append('|')
                .Append(if isWindows then 'i' else 's')
                .Append('|')
                .Append(if clearEnv then "clear" else "keep")
            |> ignore

            // Ordinal name sort (F#'s default string comparison) keeps the serialization order-stable and
            // culture-independent, so the same effective environment always hashes to the same digest.
            // Each field is length-prefixed (`<charCount>:<text>`), a self-delimiting (netstring-style)
            // form: the reader consumes exactly that many chars, so no name/value — whatever characters it
            // holds — can straddle a boundary and let two distinct environments encode to the same bytes.
            let appendField (text: string) =
                sb.Append(text.Length).Append(':').Append(text) |> ignore

            for name in effective.Keys |> Seq.sort do
                match effective[name] with
                | Some value ->
                    sb.Append 'S' |> ignore
                    appendField name
                    appendField value
                | None ->
                    sb.Append 'R' |> ignore
                    appendField name

            let digest =
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))

            $"{envFingerprintScheme}|{digest}"

    // The env fingerprint stored in a cassette entry — the recorded value for a v3 entry, or the default
    // fingerprint for a pre-v3 entry (`null`), so a legacy entry keys as the un-customized environment.
    static let entryEnvFingerprint (entry: CassetteEntry) : string =
        match entry.EnvFingerprint with
        | null -> defaultEnvFingerprint
        | fingerprint -> fingerprint

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
    // The key uses the same argument normalizer (and the same cwd-matching setting) that a live match
    // will, so the two sides stay symmetric.
    static let buildSlots
        (normalizer: (string[] -> string[]) option)
        (matchCwd: bool)
        (entries: CassetteEntry[])
        : Dictionary<Key, ReplaySlot> =
        let grouped = Dictionary<Key, ResizeArray<CassetteEntry>>()

        for entry in entries do
            let key =
                entry.Program,
                applyNormalizer normalizer entry.Args,
                (if matchCwd then Option.ofObj entry.Cwd else None),
                entry.HasStdin,
                Option.ofObj entry.StdinDigest,
                entryEnvFingerprint entry

            match grouped.TryGetValue key with
            | true, bucket -> bucket.Add entry
            | _ -> grouped[key] <- ResizeArray [ entry ]

        let slots = Dictionary<Key, ReplaySlot>()

        for kvp in grouped do
            slots[kvp.Key] <-
                { Entries = kvp.Value.ToArray()
                  Next = 0 }

        slots

    // Reject an entry whose terminal-state fields are self-contradictory (more than one of
    // TimedOut/Signal/Code set) or that is missing its required `Program` — a corrupted or hand-edited
    // cassette, not a value a real recording ever produces. Absence of ALL THREE terminal-state fields
    // is deliberately NOT rejected here: it is a legitimate (if degenerate) partial cassette and replays
    // honestly as `Outcome.Unobserved` (see `outcomeOf`) rather than being rejected or fabricating a
    // clean exit. The index identifies the offending entry without echoing any of its (possibly secret)
    // content — `Program`/`Args`/`Stdout`/`Stderr` never appear in this message.
    static let validateEntry (index: int) (entry: CassetteEntry) : Result<CassetteEntry, ProcessError> =
        let terminalStatesSet =
            [ entry.TimedOut; entry.Signal.HasValue; entry.Code.HasValue ]
            |> List.filter id
            |> List.length

        if terminalStatesSet > 1 then
            Error(
                ProcessError.Io
                    $"cassette entry {index} has a contradictory terminal state (more than one of TimedOut/Signal/Code is set)"
            )
        elif String.IsNullOrWhiteSpace entry.Program then
            Error(ProcessError.Io $"cassette entry {index} is missing its required 'Program' field")
        else
            Ok entry

    // Validate every entry in capture order, failing on the FIRST invalid one (its index pinpoints the
    // offending row without scanning/reporting the rest).
    static let validateEntries (entries: CassetteEntry[]) : Result<CassetteEntry[], ProcessError> =
        let rec loop index =
            if index >= entries.Length then
                Ok entries
            else
                match validateEntry index entries[index] with
                | Error error -> Error error
                | Ok _ -> loop (index + 1)

        loop 0

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
                arrayOrEmpty file.Entries |> Array.map normalizeEntry |> validateEntries
        with ex ->
            Error(ProcessError.Io ex.Message)

    static let O_RDONLY_FOR_DIR_FSYNC = 0

    // Best-effort fsync of the directory containing `path`, so a preceding atomic `rename` into it is
    // durable across a crash — the renamed file's own bytes are already flushed to disk by `writeContent`
    // (see `writeCassette`), but the directory-entry swap `rename` performs is a separate metadata write
    // that needs its own flush. Unix-only (Windows has no portable directory-fsync; NTFS's own metadata
    // journaling plus `File.Move`'s atomic rename already provide the durable-replacement guarantee
    // there) and deliberately best-effort: any failure (a read-only/unusual filesystem, a sandboxed host)
    // is swallowed rather than surfaced, because the rename itself already succeeded and the cassette
    // write must not fail just because the OS could not also confirm the directory entry hit disk.
    static let bestEffortFsyncParentDir (path: string) : unit =
        if not isWindows then
            try
                let dir =
                    match Path.GetDirectoryName path with
                    | null
                    | "" -> "."
                    | d -> d

                let fd = NativeDirFsync.openDirFd (dir, O_RDONLY_FOR_DIR_FSYNC)

                if fd >= 0 then
                    try
                        NativeDirFsync.fsyncFd fd |> ignore
                    finally
                        NativeDirFsync.closeFd fd |> ignore
            with _ ->
                // Best-effort: an unwriteable/exotic filesystem or a sandboxed host must not fail a
                // write whose rename already succeeded.
                ()

    // Write the cassette atomically and owner-only: serialize into a sibling temp file created `0600`
    // from the start (so the secret-bearing bytes are never even briefly group/world-readable), flush
    // its content to disk before the rename (so the bytes are durable even if the process crashes right
    // after), then rename it over the target — same-directory rename is atomic on one filesystem, so a
    // reader never sees a half-written cassette — and best-effort fsync the parent directory on Unix so
    // the rename itself is durable too. On Windows the file inherits the directory ACL (restrict the
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
                writer.Flush()
                // `flushToDisk = true` asks the OS to fsync the temp file's own bytes, so they are
                // durable BEFORE the rename below swaps it into place — a crash right after the rename
                // can then never leave `path` pointing at a renamed-but-not-yet-flushed temp.
                stream.Flush true

        try
            writeContent () // `use` disposes here, flushing/closing before the rename
            File.Move(tempPath, path, true)
            // Best-effort: the directory-entry swap the rename just performed is a separate metadata
            // write from the file's own (already-durable) bytes; failure here must not fail the write.
            bestEffortFsyncParentDir path
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
        let args = applyNormalizer options.ArgNormalizer (Seq.toArray command.Config.Args)

        command.Program,
        args,
        (if options.MatchCwd then command.WorkingDirectory else None),
        command.Config.StdinSource.IsSome,
        digest,
        envFingerprint command.Config.ClearEnv command.Config.EnvOverrides

    let envNamesOf (command: Command) : string[] =
        command.Config.EnvOverrides
        |> Seq.map fst
        |> Seq.distinct
        |> Seq.sort
        |> Seq.toArray

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
          Args = Seq.toArray command.Config.Args
          Cwd = Option.toObj command.WorkingDirectory
          StdinDigest = Option.toObj digest
          HasStdin = command.Config.StdinSource.IsSome
          EnvNames = envNamesOf command
          EnvFingerprint = envFingerprint command.Config.ClearEnv command.Config.EnvOverrides
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
          Args = Seq.toArray command.Config.Args
          Cwd = Option.toObj command.WorkingDirectory
          StdinDigest = Option.toObj digest
          HasStdin = command.Config.StdinSource.IsSome
          EnvNames = envNamesOf command
          EnvFingerprint = envFingerprint command.Config.ClearEnv command.Config.EnvOverrides
          Stdout = ""
          Stderr = redactText result.Stderr
          StdoutBase64 = Convert.ToBase64String result.Stdout
          Code = codeOf result.Code
          TimedOut = result.IsTimedOut
          Signal = signalOf result.Outcome
          Truncated = result.Truncated
          DurationMs = result.Duration.TotalMilliseconds }

    // The cassette schema records exactly the three "normal" outcomes (`TimedOut`/`Signal`/`Code`) a
    // live run can be recorded as via `entryOfText`/`entryOfBytes`. `loadEntries`/`validateEntry` already
    // rejected any entry setting more than one of them, so by the time an entry reaches here at most one
    // is set. If NONE is set (an omitted / hand-crafted / pre-1.x entry, or a partial cassette the caller
    // is still growing by hand) this is honestly `Outcome.Unobserved` — never a fabricated `Exited 0`.
    // (`Outcome.Unobserved` itself is not one of the three recordable states, so a *live* one degrades to
    // this same fallback on replay — an astronomically rare native-race edge case, not something a
    // deterministic test fixture would ever intentionally set up.)
    let outcomeOf (entry: CassetteEntry) : Outcome =
        if entry.TimedOut then
            Outcome.TimedOut
        elif entry.Signal.HasValue then
            Outcome.Signalled(Some entry.Signal.Value)
        elif entry.Code.HasValue then
            Outcome.Exited entry.Code.Value
        else
            Outcome.Unobserved "cassette entry has no recorded terminal state (TimedOut/Signal/Code all absent)"

    // Decode a cassette entry's base64 stdout, reporting corruption as the SAME `ProcessError.Io` shape
    // regardless of which verb (string capture, bytes capture, or replayed `SpawnAsync`) is asking — a
    // corrupt payload is never silently swapped for an empty/placeholder stdout on any of the three paths.
    let decodeStdoutBase64 (command: Command) (base64: string) : Result<byte[], ProcessError> =
        try
            Ok(Convert.FromBase64String base64)
        with ex ->
            Error(ProcessError.Io $"corrupt base64 stdout in cassette entry for '{command.Program}': {ex.Message}")

    // The recorded stdout as text: a bytes recording (base64 present) decodes with the command's stdout
    // encoding — exactly what a real bytes→text conversion would do; a text recording uses `Stdout`. A
    // corrupt base64 payload (or a decode that the configured `StdoutEncoding` can't complete) is an
    // honest `Io` error here, never a silent fallback to `Stdout`/empty text.
    let stdoutText (command: Command) (entry: CassetteEntry) : Result<string, ProcessError> =
        match entry.StdoutBase64 with
        | null -> Ok entry.Stdout
        | base64 ->
            match decodeStdoutBase64 command base64 with
            | Error error -> Error error
            | Ok bytes ->
                try
                    Ok(command.Config.StdoutEncoding.GetString bytes)
                with ex ->
                    Error(
                        ProcessError.Io $"corrupt base64 stdout in cassette entry for '{command.Program}': {ex.Message}"
                    )

    let resultText (command: Command) (entry: CassetteEntry) : Result<ProcessResult<string>, ProcessError> =
        match stdoutText command entry with
        | Error error -> Error error
        | Ok stdout ->
            Ok(
                ProcessResult<string>(
                    command.Program,
                    stdout,
                    entry.Stderr,
                    outcomeOf entry,
                    TimeSpan.FromMilliseconds entry.DurationMs,
                    entry.Truncated,
                    command.Config.OkCodes
                )
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
            match decodeStdoutBase64 command base64 with
            | Error error -> Error error
            | Ok bytes ->
                Ok(
                    ProcessResult<byte[]>(
                        command.Program,
                        bytes,
                        entry.Stderr,
                        outcomeOf entry,
                        TimeSpan.FromMilliseconds entry.DurationMs,
                        entry.Truncated,
                        command.Config.OkCodes
                    )
                )

    // Reconstruct a live handle from a recorded entry, reusing the same in-memory `FakeProcess` the
    // scripted double builds — so a replayed stream agrees with a real run on line splitting, encoding,
    // OkCodes, and outcome. A corrupt base64 stdout errors here exactly as it does for the capture verbs,
    // rather than silently starting the fake process with empty/placeholder stdout.
    let spawnFromEntry (command: Command) (entry: CassetteEntry) : Result<RunningProcess, ProcessError> =
        match stdoutText command entry with
        | Error error -> Error error
        | Ok stdout ->
            Ok(
                FakeProcess
                    .OfCommand(command)
                    .WithStdout(stdout)
                    .WithStderr(entry.Stderr)
                    .WithOutcome(outcomeOf entry)
                    .Build()
            )

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
        | Ok entries ->
            Ok(
                new RecordReplayRunner(
                    ReplayMode(buildSlots options.ArgNormalizer options.MatchCwd entries),
                    path,
                    options
                )
            )

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
                    AutoMode(
                        inner,
                        buildSlots options.ArgNormalizer options.MatchCwd entries,
                        List<CassetteEntry>(entries),
                        ref false
                    ),
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

    // The shared mode logic behind both capture verbs: Record delegates to `inner` and captures the
    // live result; Replay serves strictly from the cassette (a miss is `CassetteMiss`, never a
    // surprise subprocess); Auto replays a hit and delegates+records a miss (VCR "new episodes").
    // Parameterized over `captureInner` (which of `inner`'s two capture verbs to call),
    // `entryOf` (how to turn a live result into a `CassetteEntry`), and `resultOf` (how to turn a
    // replayed entry back into a result — `resultBytes` alone can fail, on a text/pre-v2 entry), so
    // the text and bytes paths can never drift apart on the mode/lock/dirty discipline itself.
    member private this.CaptureVia<'a>
        (
            command: Command,
            cancellationToken: CancellationToken,
            captureInner:
                IProcessRunner -> Command -> CancellationToken -> Task<Result<ProcessResult<'a>, ProcessError>>,
            entryOf: Command -> ProcessResult<'a> -> string option -> CassetteEntry,
            resultOf: Command -> CassetteEntry -> Result<ProcessResult<'a>, ProcessError>
        ) : Task<Result<ProcessResult<'a>, ProcessError>> =
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
                        match! captureInner inner command cancellationToken with
                        | Error error -> return Error error
                        | Ok result ->
                            lock gate (fun () ->
                                recorded.Add(entryOf command result digest)
                                dirty.Value <- true)

                            return Ok result
                    | ReplayMode slots ->
                        match lock gate (fun () -> play slots (keyOf command digest)) with
                        | Some entry -> return resultOf command entry
                        | None -> return Error(ProcessError.CassetteMiss command.Program)
                    | AutoMode(inner, slots, recorded, dirty) ->
                        let key = keyOf command digest

                        match lock gate (fun () -> play slots key) with
                        | Some entry -> return resultOf command entry
                        | None ->
                            match! captureInner inner command cancellationToken with
                            | Error error -> return Error error
                            | Ok result ->
                                let entry = entryOf command result digest

                                lock gate (fun () ->
                                    recorded.Add entry
                                    remember slots key entry
                                    dirty.Value <- true)

                                return Ok result
        }

    member private this.Capture(command: Command, cancellationToken: CancellationToken) =
        this.CaptureVia(
            command,
            cancellationToken,
            (fun inner c t -> inner.CaptureStringAsync(c, t)),
            entryOfText,
            resultText
        )

    member private this.CaptureBytes(command: Command, cancellationToken: CancellationToken) =
        this.CaptureVia(
            command,
            cancellationToken,
            (fun inner c t -> inner.CaptureBytesAsync(c, t)),
            entryOfBytes,
            resultBytes
        )

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
                    | Some entry -> spawnFromEntry command entry
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
