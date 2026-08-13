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

// libc entry points the cassette writer needs and .NET exposes no managed equivalent for: `open`/
// `fsync`/`close` for the Unix-only parent-directory fsync in `writeCassette` (a plain `File.Open`
// cannot open a directory), and `flock` for the POSIX half of the cross-process save lock
// (`tryAcquireSaveLockOnce`). A module, not class `static let` bindings, because F# DllImport `extern`
// declarations must be module-level or type `static member` — a `static let` in a class cannot carry
// `DllImport`. Every binding names its libc symbol through an explicit `EntryPoint`: an F# `extern`
// otherwise looks the symbol up under the *F# function's* name, which fails at the first call with an
// `EntryPointNotFoundException` — and inside a best-effort `try` that failure is invisible.
module private NativeCassetteIo =
    [<DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)>]
    extern int openDirFd(string path, int flags)

    [<DllImport("libc", EntryPoint = "fsync", SetLastError = true)>]
    extern int fsyncFd(int fd)

    [<DllImport("libc", EntryPoint = "close", SetLastError = true)>]
    extern int closeFd(int fd)

    [<DllImport("libc", EntryPoint = "flock", SetLastError = true)>]
    extern int flockFd(int fd, int operation)

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
        /// Version 5 prefixes it by source domain (`inherit|`, `path|`, or `bytes|`) so an in-memory
        /// value cannot alias inherited stdin or a path-only file source. For an in-memory stdin source
        /// this contains a SHA-256 of the content — a low-entropy stdin secret (a short password/PIN)
        /// can be recovered from it by brute force, so treat a cassette with stdin as sensitive and
        /// review it before committing.
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
        /// Whether the run was terminated by a signal. This distinguishes a signal whose number was
        /// unavailable from a cassette entry with no recorded terminal state. Absent in a pre-v6 cassette
        /// — defaults to `false`; a legacy non-null `Signal` still represents a signalled process.
        Signalled: bool
        /// The terminating signal number on POSIX, or `null` when the process was not signalled or its
        /// signal number was unavailable.
        Signal: Nullable<int>
        /// Whether the captured output was truncated by an output-buffer policy (so a bounded-policy
        /// recording replays as truncated). Absent in a pre-1.x cassette — defaults to `false`.
        Truncated: bool
        /// The recorded wall-clock duration in milliseconds, so `ProcessResult.Duration` survives replay.
        /// Absent in a pre-1.x cassette — defaults to `0`.
        DurationMs: double
        /// Whether the recording was of a `Command.Pty` (pseudo-terminal) run — a single **merged**
        /// stdout+stderr terminal stream (D3). On replay the reconstructed handle is a merged-stream
        /// fake (`OutputEvent.Stderr` is never produced; the recorded `Stdout` **is** the merged stream).
        /// Absent in a pre-v4 cassette — defaults to `false`.
        Pty: bool
        /// The PTY's initial terminal width in columns when `Pty` is set; `null` otherwise (and in a
        /// pre-v4 cassette). Recorded for inspection/replay fidelity — the captured merged output itself
        /// lives in `Stdout`.
        PtyCols: Nullable<int>
        /// The PTY's initial terminal height in rows when `Pty` is set; `null` otherwise (and in a
        /// pre-v4 cassette).
        PtyRows: Nullable<int>
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

// The result of ONE non-blocking attempt at a cassette's sibling save lock: the held handle (closing it
// releases the lock), a refusal because another writer holds it at that instant, or a genuine I/O
// failure. `Busy` is kept distinct from `LockFailed` so only real contention is retried/reported as the
// transient conflict — a broken path or a permissions problem is not.
type private SaveLockAttempt =
    | Acquired of handle: FileStream
    | Busy
    | LockFailed of error: ProcessError

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
    // effective-environment fingerprint that folds env semantics into the replay match key); v4 added
    // `Pty`/`PtyCols`/`PtyRows` (the merged-stream pseudo-terminal recording — D3). v5 prefixes stdin
    // digests by source domain, preventing an in-memory value from aliasing inherited stdin or a
    // path-only file source. v1-v4 digests still replay through the legacy key fallback. A pre-v4 entry
    // with no `Pty` flag loads as a non-PTY recording (`Pty` defaults `false`, the geometry `null`). v6
    // adds `Signalled` so a signal with an unavailable number remains distinct from no terminal state.
    static let currentFormatVersion = 6

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
    // TimedOut/(Signalled or Signal)/Code set) or that is missing its required `Program` — a corrupted
    // or hand-edited cassette, not a value a real recording ever produces. Absence of every terminal
    // state is deliberately NOT rejected here: it is a legitimate (if degenerate) partial cassette and
    // replays honestly as `Outcome.Unobserved` (see `outcomeOf`) rather than being rejected or
    // fabricating a clean exit. The index identifies the offending entry without echoing any of its
    // (possibly secret) content — `Program`/`Args`/`Stdout`/`Stderr` never appear in this message.
    static let validateEntry (index: int) (entry: CassetteEntry) : Result<CassetteEntry, ProcessError> =
        let terminalStatesSet =
            [ entry.TimedOut
              entry.Signalled || entry.Signal.HasValue
              entry.Code.HasValue ]
            |> List.filter id
            |> List.length

        if terminalStatesSet > 1 then
            Error(
                ProcessError.Io
                    $"cassette entry {index} has a contradictory terminal state (more than one of TimedOut/Signalled/Signal/Code is set)"
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

    // The directory holding `path` — `.` for a bare filename, whose `Path.GetDirectoryName` is empty.
    static let directoryOf (path: string) : string =
        match Path.GetDirectoryName path with
        | null
        | "" -> "."
        | dir -> dir

    // fsync the directory containing `path`, so a preceding atomic `rename` into it is durable across a
    // crash — the renamed file's own bytes are already flushed to disk by `writeContent` (see
    // `writeCassette`), but the directory-entry swap `rename` performs is a separate metadata write that
    // needs its own flush. Unix-only: Windows has no portable directory-fsync (NTFS's own metadata
    // journaling plus the file's `FlushFileBuffers` and `File.Move`'s atomic replace already provide the
    // durable-replacement guarantee there), so it reports success without doing anything.
    //
    // Returns the failure instead of raising it, so the caller can stay best-effort (see
    // `bestEffortFsyncParentDir`) while a test can still prove the call actually reaches the platform —
    // a P/Invoke that never resolves would otherwise look exactly like a silent success.
    static let fsyncParentDir (path: string) : Result<unit, string> =
        if isWindows then
            Ok()
        else
            try
                let dir = directoryOf path
                let fd = NativeCassetteIo.openDirFd (dir, O_RDONLY_FOR_DIR_FSYNC)

                if fd < 0 then
                    Error $"could not open the parent directory (errno {Marshal.GetLastPInvokeError()})"
                else
                    try
                        if NativeCassetteIo.fsyncFd fd = 0 then
                            Ok()
                        else
                            Error $"fsync of the parent directory failed (errno {Marshal.GetLastPInvokeError()})"
                    finally
                        NativeCassetteIo.closeFd fd |> ignore
            with ex ->
                Error ex.Message

    // The write path's use of the above: deliberately best-effort — any failure (a read-only/unusual
    // filesystem, a sandboxed host) is swallowed rather than surfaced, because the rename itself already
    // succeeded and the cassette write must not fail just because the OS could not also confirm the
    // directory entry hit disk.
    static let bestEffortFsyncParentDir (path: string) : unit = fsyncParentDir path |> ignore

    // `flock` operations, whose values are identical on Linux and macOS/BSD: take an EXCLUSIVE lock
    // (`LOCK_EX` = 2) and fail immediately rather than wait when another open file description holds one
    // (`LOCK_NB` = 4).
    static let LOCK_EX_NB = 2 ||| 4

    // The errno `flock(LOCK_NB)` reports when another holder has the lock (`EWOULDBLOCK`, which POSIX
    // defines equal to `EAGAIN`): 11 on Linux, 35 on macOS/BSD.
    static let EWOULDBLOCK =
        if RuntimeInformation.IsOSPlatform OSPlatform.OSX then
            35
        else
            11

    // The sibling advisory-lock path for a cassette. It is a 0-byte rendezvous file and is deliberately
    // NEVER deleted or truncated by ordinary operation: unlinking a locked file races a fresh
    // create-and-lock of the same name, which would hand two writers two different inodes and no mutual
    // exclusion at all. A crashed writer therefore leaves nothing worse than an empty file behind — the
    // OS drops its lock when the process dies.
    static let lockPathOf (path: string) : string = path + ".lock"

    // The typed refusal a save gets when another writer holds the lock. `ProcessError.Io` is transient
    // (`ProcessError.IsTransient` is `true`), so the loser can simply retry once the winner is done —
    // and, crucially, the last saved cassette is still on disk, untouched.
    static let concurrentSaveConflict (path: string) : ProcessError =
        ProcessError.Io
            $"another writer is saving the cassette '{path}' right now — saves to one cassette path are serialized by an advisory lock on '{lockPathOf path}', and the writer that loses it is refused (a transient, retryable error) rather than silently overwriting the last saved cassette"

    // Windows reports deny-share contention as ERROR_SHARING_VIOLATION (32) or ERROR_LOCK_VIOLATION
    // (33), carried in the low 16 bits of the exception's HRESULT.
    static let isWindowsSharingViolation (ex: IOException) : bool =
        let code = ex.HResult &&& 0xFFFF
        code = 32 || code = 33

    // Create the sibling lock file if it is not there yet, owner-only on Unix. Creation is separate from
    // acquisition so the acquiring open below can be a plain `FileMode.Open` whose only job is the OS
    // lock; an existing file (from this run or any earlier one) is reused as-is, because the lock — not
    // the file's existence — is what arbitrates.
    static let ensureLockFile (lockPath: string) : unit =
        if not (File.Exists lockPath) then
            try
                let options =
                    if isWindows then
                        FileStreamOptions(
                            Mode = FileMode.CreateNew,
                            Access = FileAccess.Write,
                            Share = FileShare.ReadWrite
                        )
                    else
                        FileStreamOptions(
                            Mode = FileMode.CreateNew,
                            Access = FileAccess.Write,
                            Share = FileShare.ReadWrite,
                            UnixCreateMode = (UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                        )

                use _rendezvous = new FileStream(lockPath, options)
                ()
            with :? IOException ->
                // Another writer created (or is already holding) the rendezvous file first — the normal
                // outcome under contention, and exactly what the lock below is there to resolve.
                ()

    // ONE non-blocking attempt at the cassette's sibling advisory lock. The returned handle holds the
    // lock until it is disposed — or until this process dies, so a crash never wedges later saves.
    //
    // Windows: a deny-share (`FileShare.None`) open — any other handle, in this process or another,
    // fails with a sharing violation. Unix: the same open (which .NET maps onto `flock`) PLUS an
    // explicit `flock(LOCK_EX | LOCK_NB)` on the opened descriptor, so the mutual exclusion holds
    // whichever way the runtime chooses to implement `FileShare`. Both mechanisms are per open file
    // description, so two threads of one process contend exactly as two processes do.
    static let tryAcquireSaveLockOnce (path: string) : SaveLockAttempt =
        let lockPath = lockPathOf path

        let opened =
            try
                ensureLockFile lockPath
                Ok(new FileStream(lockPath, FileMode.Open, FileAccess.Write, FileShare.None))
            with
            // A path that does not exist is a real failure, not contention — say so, rather than send the
            // caller off to retry a conflict that will never clear.
            | :? DirectoryNotFoundException as ex -> Error(LockFailed(ProcessError.Io ex.Message))
            | :? FileNotFoundException as ex -> Error(LockFailed(ProcessError.Io ex.Message))
            | :? IOException as ex ->
                // Contention surfaces as an IOException on both platforms — a sharing violation on
                // Windows (which names itself in the HRESULT), a refused advisory lock on Unix (which
                // does not, so any remaining I/O failure there is read as contention). Any OTHER Windows
                // I/O failure keeps its own message rather than being mislabelled a retryable conflict.
                if isWindows && not (isWindowsSharingViolation ex) then
                    Error(LockFailed(ProcessError.Io ex.Message))
                else
                    Error Busy
            | ex -> Error(LockFailed(ProcessError.Io ex.Message))

        match opened with
        | Error attempt -> attempt
        | Ok handle ->
            if isWindows then
                Acquired handle
            else
                try
                    let fd = handle.SafeFileHandle.DangerousGetHandle().ToInt32()

                    if NativeCassetteIo.flockFd (fd, LOCK_EX_NB) = 0 then
                        Acquired handle
                    elif Marshal.GetLastPInvokeError() = EWOULDBLOCK then
                        handle.Dispose()
                        Busy
                    else
                        // A filesystem that cannot arbitrate advisory locks at all (a documented
                        // divergence — see `Save`): the save proceeds, still serialized within this
                        // process by the recorder's own save gate, rather than failing outright.
                        Acquired handle
                with
                | :? EntryPointNotFoundException
                | :? DllNotFoundException ->
                    // A host whose libc this binding cannot reach at all. The deny-share open above is
                    // still in force, so this is the same documented divergence as a filesystem without
                    // advisory locks — and never a reason to stop being able to save.
                    Acquired handle

    // Acquire the sibling save lock, retrying a *busy* lock only while `budget` allows — never longer,
    // and not at all for `TimeSpan.Zero` (what `Save` passes: it refuses instead of blocking). A genuine
    // I/O failure is returned immediately; contention that outlives the budget becomes the typed,
    // retryable conflict.
    static let acquireSaveLock (budget: TimeSpan) (path: string) : Result<FileStream, ProcessError> =
        let deadline = Environment.TickCount64 + int64 budget.TotalMilliseconds

        let rec attempt () =
            match tryAcquireSaveLockOnce path with
            | Acquired handle -> Ok handle
            | LockFailed error -> Error error
            | Busy ->
                if Environment.TickCount64 < deadline then
                    Thread.Sleep 10
                    attempt ()
                else
                    Error(concurrentSaveConflict path)

        attempt ()

    // How long the best-effort drop-time flush may wait for another writer's save to finish before
    // giving up. `Save` itself never waits; a dispose does, briefly, because a momentary overlap should
    // not silently drop a recording that nothing else will write.
    static let disposeFlushLockWait = TimeSpan.FromMilliseconds 250.0

    // Write the cassette atomically and owner-only: serialize into a UNIQUELY named sibling temp file,
    // created exclusively (`CreateNew`) and `0600` from the start on Unix (so the secret-bearing bytes
    // are never even briefly group/world-readable), flush its content all the way to disk before the
    // rename (so the bytes are durable even if the process crashes right after), then rename it over the
    // target — same-directory rename is atomic on one filesystem, so a reader never sees a half-written
    // cassette — and best-effort fsync the parent directory on Unix so the rename itself is durable too.
    // On Windows the file inherits the directory ACL (restrict the directory instead).
    //
    // The temp is unique per in-flight write and created exclusively, so a writer only ever opens — and,
    // on failure, only ever deletes — a file it created itself: two concurrent writes cannot stomp one
    // temp, and a stale temp left behind by a crashed writer is an inert orphan rather than something to
    // collide with or clear away (it could equally be another writer's live temp). Throws on failure
    // after cleaning up its own temp; callers decide how to report. Serializing concurrent writers to
    // one cassette is a level up, in `saveUnderLocks`.
    static let writeCassette (path: string) (snapshot: CassetteEntry[]) : unit =
        let json =
            JsonSerializer.Serialize(
                { Version = currentFormatVersion
                  Entries = snapshot },
                jsonOptions
            )

        let tempPath =
            Path.Combine(
                directoryOf path,
                stringOrEmpty (Path.GetFileName path) + ".tmp-" + Guid.NewGuid().ToString "N"
            )

        let options =
            if isWindows then
                FileStreamOptions(Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None)
            else
                FileStreamOptions(
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    UnixCreateMode = (UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
                )

        // Only a temp this call actually created may be cleaned up below; a `CreateNew` that failed
        // means the path is someone else's file, never ours to delete.
        let mutable created = false

        let writeContent () =
            use stream = new FileStream(tempPath, options)
            created <- true
            use writer = new StreamWriter(stream)
            writer.Write json
            writer.Flush()
            // `flushToDisk = true` asks the OS to fsync the temp file's own bytes (`FlushFileBuffers` on
            // Windows), so they are durable BEFORE the rename below swaps it into place — a crash right
            // after the rename can then never leave `path` pointing at a renamed-but-not-yet-flushed temp.
            stream.Flush true

        try
            writeContent () // `use` disposes here, flushing/closing before the rename
            File.Move(tempPath, path, true)
            // Best-effort: the directory-entry swap the rename just performed is a separate metadata
            // write from the file's own (already-durable) bytes; failure here must not fail the write.
            bestEffortFsyncParentDir path
        with _ ->
            if created then
                try
                    File.Delete tempPath
                with _ ->
                    // Best-effort cleanup of our own temp; the previous cassette (if any) is untouched
                    // either way, so a failure to remove the orphan must not replace the real error.
                    ()

            reraise ()

    let gate = obj ()

    // Serializes THIS recorder's saves end to end — the whole snapshot → write → rename → fsync critical
    // section runs under it, so two `Save` calls (or a `Save` racing the drop-time flush) can neither
    // interleave their writes nor land out of order: each snapshot is taken while its own write already
    // holds the gate, and `recorded` only ever grows, so the save that writes last necessarily carries
    // the newest snapshot. Deliberately a different lock from `gate` (which guards the recorded entries
    // for a few instructions at a time) so an in-flight capture never waits on disk I/O. Lock order is
    // always `saveGate` → `gate`, never the reverse.
    let saveGate = obj ()

    // Apply the optional record-time redaction hook to captured text (coalescing a null return to "").
    let redactText (text: string) : string =
        match options.Redaction with
        | None -> text
        | Some redact ->
            let scrubbed = redact text
            if obj.ReferenceEquals(scrubbed, null) then "" else scrubbed

    // The stdin-source digest used for matching, computed WITHOUT consuming the source: in-memory
    // text/bytes hash their encoded content, a file source hashes its path (or, opt-in, its contents). In the v5
    // scheme, prefixes make the source domains structurally disjoint: `inherit|`, `path|<sha256>`, and
    // `bytes|<sha256>`. A one-shot streaming source can't be keyed without consuming it, so it is
    // rejected. Legacy is used only as a read fallback for v1-v4 cassette entries.
    let stdinDigest (legacy: bool) (command: Command) : Result<string option, ProcessError> =
        let bytesDigest (bytes: byte[]) =
            let digest = hashBytes bytes
            if legacy then digest else $"bytes|{digest}"

        match command.Config.StdinSource with
        | None -> Ok None
        | Some stdin ->
            match stdin.Source with
            | StdinSource.Empty -> Ok None
            | StdinSource.Inherit ->
                // `Command.InheritStdin`: the child reads the PARENT's own standard input, whose bytes
                // are external to the command and unknowable here. Key it by a fixed sentinel — a stable
                // "inherit" marker, distinct from `Empty`/`None` (no stdin) so two inherited-stdin
                // invocations match each other but not a no-stdin one. Recording still spawns for real
                // (the inner runner inherits the parent's stdin); replay just returns the recorded result.
                if legacy then
                    Ok(Some(hashBytes (Encoding.UTF8.GetBytes "inherit-stdin")))
                else
                    Ok(Some "inherit|")
            | StdinSource.Text text -> Ok(Some(bytesDigest (command.Config.StdinEncoding.GetBytes text)))
            | StdinSource.Bytes bytes -> Ok(Some(bytesDigest bytes))
            | StdinSource.File filePath ->
                if options.HashFileStdinContents then
                    try
                        Ok(Some(bytesDigest (File.ReadAllBytes filePath)))
                    with ex ->
                        Error(ProcessError.Stdin(command.Program, ex.Message))
                else
                    let pathDigest = hashBytes (Encoding.UTF8.GetBytes("file:" + filePath))
                    Ok(Some(if legacy then pathDigest else $"path|{pathDigest}"))
            | StdinSource.Lines _
            | StdinSource.Reader _
            | StdinSource.AsyncLines _ ->
                Error(
                    ProcessError.Unsupported
                        "record/replay cannot key a one-shot stdin source (FromStream / FromLines / FromAsyncLines)"
                )

    let keyOf (command: Command) (digest: string option) : Key =
        let args = applyNormalizer options.ArgNormalizer (Seq.toArray command.Arguments)

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

    let signalOf (outcome: Outcome) : bool * Nullable<int> =
        match outcome with
        | Outcome.Signalled(Some s) -> true, Nullable s
        | Outcome.Signalled None -> true, Nullable()
        | _ -> false, Nullable()

    let codeOf (code: int option) : Nullable<int> =
        match code with
        | Some c -> Nullable c
        | None -> Nullable()

    // The PTY fields for a recording: whether the command asked for a pseudo-terminal (D3 merged
    // stream) and, if so, its initial geometry — recorded so replay reconstructs a merged-stream handle
    // at the right size. A non-PTY command records `false`/`null`, indistinguishable from a pre-v4
    // cassette (which is exactly the back-compat intent).
    let ptyFieldsOf (command: Command) : bool * Nullable<int> * Nullable<int> =
        match command.Config.Pty with
        | Some pty -> true, Nullable pty.Cols, Nullable pty.Rows
        | None -> false, Nullable(), Nullable()

    // Record a text capture: stdout/stderr are the decoded strings (redacted); no base64. For a PTY run
    // (D3) `Stdout` IS the single merged stream, so the redaction hook that scrubs it covers the whole
    // captured PTY output (where an echoed credential could otherwise land) — there is no separate
    // stderr to leak through.
    let entryOfText (command: Command) (result: ProcessResult<string>) (digest: string option) : CassetteEntry =
        let pty, ptyCols, ptyRows = ptyFieldsOf command
        let signalled, signal = signalOf result.Outcome

        { Program = command.Program
          Args = Seq.toArray command.Arguments
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
          Signalled = signalled
          Signal = signal
          Truncated = result.Truncated
          DurationMs = result.Duration.TotalMilliseconds
          Pty = pty
          PtyCols = ptyCols
          PtyRows = ptyRows }

    // Record a bytes capture: exact stdout bytes go to base64 (Stdout text stays empty — a string-verb
    // replay decodes the base64); stderr is text (redacted). The opaque bytes are not redacted.
    let entryOfBytes (command: Command) (result: ProcessResult<byte[]>) (digest: string option) : CassetteEntry =
        let pty, ptyCols, ptyRows = ptyFieldsOf command
        let signalled, signal = signalOf result.Outcome

        { Program = command.Program
          Args = Seq.toArray command.Arguments
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
          Signalled = signalled
          Signal = signal
          Truncated = result.Truncated
          DurationMs = result.Duration.TotalMilliseconds
          Pty = pty
          PtyCols = ptyCols
          PtyRows = ptyRows }

    // A v6 cassette explicitly records every signal through `Signalled`; `Signal` carries its optional
    // number. A pre-v6 cassette lacks that marker, so a non-null legacy `Signal` still means a known
    // signal. `loadEntries`/`validateEntry` rejected contradictory state before an entry reaches here.
    // If none is set (an omitted / hand-crafted / pre-1.x entry, or a partial cassette the caller is
    // still growing by hand) this is honestly `Outcome.Unobserved` — never a fabricated `Exited 0`.
    // (`Outcome.Unobserved` itself is not one of the recordable states, so a *live* one degrades to this
    // same fallback on replay — an astronomically rare native-race edge case, not something a
    // deterministic test fixture would ever intentionally set up.)
    let outcomeOf (entry: CassetteEntry) : Outcome =
        if entry.TimedOut then
            Outcome.TimedOut
        elif entry.Signalled || entry.Signal.HasValue then
            if entry.Signal.HasValue then
                Outcome.Signalled(Some entry.Signal.Value)
            else
                Outcome.Signalled None
        elif entry.Code.HasValue then
            Outcome.Exited entry.Code.Value
        else
            Outcome.Unobserved
                "cassette entry has no recorded terminal state (TimedOut/Signalled/Signal/Code all absent)"

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
                        command.Config.OkCodes,
                        stdoutEncoding = command.Config.StdoutEncoding
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
            let fake =
                FakeProcess.OfCommand(command).WithStdout(stdout).WithStderr(entry.Stderr).WithOutcome(outcomeOf entry)

            // A PTY recording (D3) replays as a merged-stream handle: `OutputEventsAsync` yields only
            // `OutputEvent.Stdout` and `ResizeAsync` is a recorded no-op success. The recorded `Stdout`
            // is the merged stream; the entry flag is authoritative (independent of the live command).
            let fake = if entry.Pty then fake.WithPty() else fake
            Ok(fake.Build())

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

    // New cassettes use the v5 domain-separated key. A v1-v4 entry has an unprefixed digest, so only a
    // second legacy lookup can find it; this also lets Auto safely retain old rows while adding v5 rows.
    let replayEntry
        (slots: Dictionary<Key, ReplaySlot>)
        (command: Command)
        (digest: string option)
        : Result<CassetteEntry option, ProcessError> =
        match lock gate (fun () -> play slots (keyOf command digest)) with
        | Some entry -> Ok(Some entry)
        | None ->
            match stdinDigest true command with
            | Error error -> Error error
            | Ok legacyDigest -> Ok(lock gate (fun () -> play slots (keyOf command legacyDigest)))

    // The whole save critical section, shared by `Save` and the drop-time flush: this recorder's own
    // save gate, then the cross-instance/cross-process advisory lock on the target, and only then
    // snapshot → serialize → write → rename → fsync. Taking the snapshot INSIDE both locks is what makes
    // the write ordered rather than merely atomic: whatever a save publishes always includes everything
    // recorded up to the moment it won the lock, so a save that finishes later can never put back an
    // older picture than one that already completed.
    //
    // `lockWait` bounds how long a *busy* sibling lock may be waited for — `TimeSpan.Zero` for an
    // explicit `Save`, which refuses instead of blocking.
    let saveUnderLocks
        (recorded: List<CassetteEntry>)
        (dirty: bool ref)
        (lockWait: TimeSpan)
        : Result<unit, ProcessError> =
        lock saveGate (fun () ->
            // Acquiring the lock is inside the handler too, so a save reports every failure as a value:
            // the one thing it may never do is throw where it is expected to return `Error`.
            try
                match acquireSaveLock lockWait path with
                | Error error -> Error error
                | Ok held ->
                    use _held = held
                    let snapshot = lock gate (fun () -> recorded.ToArray())
                    writeCassette path snapshot
                    // Clear `dirty` only if nothing was recorded during the write — otherwise a `Capture`
                    // that raced this `Save` would have its entry dropped from the drop-time flush.
                    lock gate (fun () ->
                        if recorded.Count = snapshot.Length then
                            dirty.Value <- false)

                    Ok()
            with ex ->
                Error(ProcessError.Io ex.Message))

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
    ///
    /// **Durable and atomic.** The whole cassette is written to a uniquely named sibling temp, flushed to
    /// disk, then renamed over the target — an atomic replacement within one filesystem — after which the
    /// parent directory is fsync'd on Unix so the rename itself survives a crash (Windows needs no
    /// counterpart: NTFS journals that metadata). An interrupted or failed save therefore never truncates
    /// or corrupts the cassette already on disk; the old file stays intact until the new one is complete.
    ///
    /// **Serialized, never silently lost.** Saves of one recorder run one at a time and in order, so a
    /// slower earlier save can never put back an older recording over a newer one. Saves from *different*
    /// recorders or processes to the same path are serialized through an advisory lock on a sibling
    /// `&lt;path&gt;.lock` file (a deny-share open on Windows, `flock` on Unix; both released when the
    /// process exits, so a crash never wedges later saves, and the file itself is never deleted). That
    /// lock is taken **without waiting**: if another writer holds it at that instant, this save is refused
    /// with a transient `ProcessError.Io` (`IsTransient` is `true` — retry once the other save completes)
    /// rather than overwriting what that writer just saved. Failing loud beats last-writer-wins, which is
    /// silent. On a filesystem that cannot arbitrate advisory locks at all, cross-process serialization is
    /// not available and the save proceeds with only this recorder's own ordering guarantee.
    member _.Save() : Result<unit, ProcessError> =
        match mode with
        | ReplayMode _ -> Ok()
        | RecordMode(_, recorded, dirty)
        | AutoMode(_, _, recorded, dirty) -> saveUnderLocks recorded dirty TimeSpan.Zero

    /// Test seam (`InternalsVisibleTo`): hold the very lock `Save` takes for `path`, so a test can prove
    /// that a save which loses it is refused rather than clobbering the cassette — exercising the real
    /// primitive instead of a copy of it that could drift. Disposing the handle releases the lock.
    static member internal HoldSaveLockForTests(path: string) : Result<IDisposable, ProcessError> =
        match acquireSaveLock TimeSpan.Zero path with
        | Ok handle -> Ok(handle :> IDisposable)
        | Error error -> Error error

    /// Test seam (`InternalsVisibleTo`): run the parent-directory fsync a successful save performs and
    /// report the failure it normally swallows, so a test can prove the call actually reaches the
    /// platform. Success on Windows, where there is deliberately nothing to do.
    static member internal FsyncParentDirectoryForTests(path: string) : Result<unit, string> = fsyncParentDir path

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
            use linkedCts =
                match command.Config.CancelOn with
                | Some extra -> CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, extra)
                | None -> CancellationTokenSource.CreateLinkedTokenSource cancellationToken

            let effectiveToken = linkedCts.Token

            if effectiveToken.IsCancellationRequested then
                // Completion verbs honour both their own token and `Command.CancelOn`, including in
                // replay mode where no inner runner exists to observe the command configuration.
                return Error(ProcessError.Cancelled command.Program)
            elif command.Config.ExtraFds.Count > 0 then
                return
                    Error(
                        ProcessError.Unsupported
                            "RecordReplayRunner cannot record or replay extra POSIX file-descriptor channels"
                    )
            else
                match stdinDigest false command with
                | Error error -> return Error error
                | Ok digest ->
                    match mode with
                    | RecordMode(inner, recorded, dirty) ->
                        match! captureInner inner command effectiveToken with
                        | Error error -> return Error error
                        | Ok result ->
                            if effectiveToken.IsCancellationRequested then
                                return Error(ProcessError.Cancelled command.Program)
                            else
                                lock gate (fun () ->
                                    recorded.Add(entryOf command result digest)
                                    dirty.Value <- true)

                                return Ok result
                    | ReplayMode slots ->
                        match replayEntry slots command digest with
                        | Error error -> return Error error
                        | Ok(Some entry) ->
                            if effectiveToken.IsCancellationRequested then
                                return Error(ProcessError.Cancelled command.Program)
                            else
                                return resultOf command entry
                        | Ok None -> return Error(ProcessError.CassetteMiss command.Program)
                    | AutoMode(inner, slots, recorded, dirty) ->
                        let key = keyOf command digest

                        match replayEntry slots command digest with
                        | Error error -> return Error error
                        | Ok(Some entry) ->
                            if effectiveToken.IsCancellationRequested then
                                return Error(ProcessError.Cancelled command.Program)
                            else
                                return resultOf command entry
                        | Ok None ->
                            match! captureInner inner command effectiveToken with
                            | Error error -> return Error error
                            | Ok result ->
                                if effectiveToken.IsCancellationRequested then
                                    return Error(ProcessError.Cancelled command.Program)
                                else
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
        elif command.Config.ExtraFds.Count > 0 then
            Error(
                ProcessError.Unsupported
                    "RecordReplayRunner cannot record or replay extra POSIX file-descriptor channels"
            )
        else
            match mode with
            | RecordMode _ ->
                Error(
                    ProcessError.Unsupported
                        "RecordReplayRunner cannot record a live SpawnAsync stream — record the call through a capture verb, then replay it as a stream"
                )
            | ReplayMode slots
            | AutoMode(_, slots, _, _) ->
                match stdinDigest false command with
                | Error error -> Error error
                | Ok digest ->
                    match replayEntry slots command digest with
                    | Error error -> Error error
                    | Ok(Some entry) -> spawnFromEntry command entry
                    | Ok None ->
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
                    // Same serialized, atomic write path as `Save`, but best-effort in every direction: a
                    // busy sibling lock is waited on only briefly and then given up on, and a write error
                    // is returned rather than raised (an explicit `Save` is what surfaces one). The new
                    // locking must not turn a drop-time flush into a throwing dispose.
                    saveUnderLocks recorded dirty disposeFlushLockWait |> ignore
                with _ ->
                    // Belt and braces around the locking itself: a dispose never propagates an exception,
                    // whatever the filesystem does.
                    ()
            | _ -> ()
