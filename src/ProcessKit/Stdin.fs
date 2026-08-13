namespace ProcessKit

open System
open System.Collections.Generic
open System.IO
open System.Runtime.CompilerServices
open System.Threading

/// Where a child process's standard input comes from. Internal representation behind `Stdin`.
type internal StdinSource =
    | Empty
    | Text of string
    | Bytes of byte[]
    | File of path: string
    | Reader of Stream
    | Lines of seq<string>
    | AsyncLines of IAsyncEnumerable<string>
    /// The child is handed the PARENT's own standard input directly (inherited), with no pipe and no
    /// feeder — for interactive/console programs (an editor from `git commit`, a tool that prompts on
    /// the terminal, a pipe from the parent's stdin). Set via `Command.InheritStdin`; incompatible with
    /// `KeepStdinOpen`, `RunningProcess.TakeStdin`, and any feeder source (there is no pipe for them to
    /// use), rejected at the builder boundary.
    | Inherit

/// A source for a child process's standard input, attached with `Command.Stdin`.
///
/// When set, the child's stdin is a pipe fed from this source; the pipe is closed (EOF) once the source
/// is exhausted — unless `Command.KeepStdinOpen` is also set, in which case the pipe is left open after
/// the source is drained so the caller can keep writing to it interactively via
/// `RunningProcess.TakeStdin` (which becomes available once the source feed has finished, so the source
/// and the interactive writer never write the pipe at the same time).
[<Sealed>]
type Stdin internal (source: StdinSource) =

    member internal _.Source = source

    /// No input — the child sees end-of-file immediately.
    static member Empty = Stdin(StdinSource.Empty)

    /// Text encoded with the command's `StdinEncoding` (UTF-8 by default). `text` must not be null
    /// (`ArgumentNullException` — a C# caller that forgets a null check would otherwise fail obscurely
    /// inside the background feeder rather than at the API boundary).
    static member FromString(text: string) =
        ArgumentNullException.ThrowIfNull text
        Stdin(StdinSource.Text text)

    /// Raw bytes. `bytes` must not be null (`ArgumentNullException`). A defensive copy is taken at this
    /// boundary — the built `Command`/`Stdin` never aliases the caller's array, so mutating it afterward
    /// (including between retries) has no effect on what is written to the child.
    static member FromBytes(bytes: byte[]) =
        ArgumentNullException.ThrowIfNull bytes
        Stdin(StdinSource.Bytes(Array.copy bytes))

    /// The contents of a file, streamed to the child. `path` must not be null (`ArgumentNullException`).
    static member FromFile(path: string) =
        ArgumentNullException.ThrowIfNull path
        Stdin(StdinSource.File path)

    /// An open readable stream, copied to the child. `stream` must not be null (`ArgumentNullException`).
    static member FromStream(stream: Stream) =
        ArgumentNullException.ThrowIfNull stream
        Stdin(StdinSource.Reader stream)

    /// Lines (each written followed by `\n`) produced eagerly from a sequence. `lines` must not be null
    /// (`ArgumentNullException`).
    static member FromLines(lines: seq<string>) =
        ArgumentNullException.ThrowIfNull lines
        Stdin(StdinSource.Lines lines)

    /// Lines (each written followed by `\n`) produced asynchronously. `lines` must not be null
    /// (`ArgumentNullException`).
    static member FromAsyncLines(lines: IAsyncEnumerable<string>) =
        ArgumentNullException.ThrowIfNull lines
        Stdin(StdinSource.AsyncLines lines)

    /// Inherit the parent process's own standard input directly (no pipe, no feeder). Internal — set
    /// through `Command.InheritStdin`, which validates it against the incompatible stdin knobs at the
    /// builder boundary; it is deliberately not a public `Stdin.From*` factory (the single builder
    /// method keeps the inherit mode from being combined with a feeder source through the same field).
    static member internal Inherit = Stdin(StdinSource.Inherit)

[<RequireQualifiedAccess>]
module internal StdinSource =

    /// The *payload object* behind a one-shot source — the very `Stream` / `seq` / `IAsyncEnumerable`
    /// that can be read only once — or `None` for a repeatable source. It is the payload, not the
    /// `Stdin` wrapper around it, that has the single-use lifetime: two `Stdin.FromStream` values
    /// built over one `Stream` are two wrappers over one payload, and the second one to be pumped
    /// finds it exhausted. `OneShotStdin` keys its claims on this identity for that reason.
    let oneShotPayload (source: StdinSource) : obj option =
        match source with
        // An upcast rather than `box`: the payloads are already reference types (so there is nothing to
        // box), and `box` would widen the result to a nullable `obj` the claim table cannot key on.
        | StdinSource.Reader stream -> Some(stream :> obj)
        | StdinSource.Lines lines -> Some(lines :> obj)
        | StdinSource.AsyncLines lines -> Some(lines :> obj)
        | StdinSource.Empty
        | StdinSource.Text _
        | StdinSource.Bytes _
        | StdinSource.File _
        | StdinSource.Inherit -> None

    /// True for a source that can only be pumped once: a live `Stream` (`FromStream`), or a sequence
    /// of lines (`FromLines`/`FromAsyncLines`) that may be backed by a one-shot enumerator (a
    /// generator, a non-seekable reader). Re-pumping an already-exhausted one-shot source into a
    /// second attempt silently feeds the child empty/truncated input instead of replaying the
    /// original one — see T-088 (ports ProcessKit-rs `c1f39c7`/`8472007`). The repeatable sources are
    /// unaffected: `Empty` has nothing to exhaust, `Bytes` is an immutable in-memory array pumped
    /// fresh from the start on every attempt, `File` reopens its path fresh on every attempt, and
    /// `Inherit` runs no feeder at all — the child reads the parent's stdin directly, so there is
    /// nothing for a second attempt to have exhausted.
    ///
    /// Derived from `oneShotPayload`, so the classification and the payload identity the claims are
    /// keyed on cannot drift apart.
    let isOneShot (source: StdinSource) : bool = (oneShotPayload source).IsSome

    /// True for the inherited-stdin source (`Command.InheritStdin`): the child reads the parent's own
    /// standard input directly, so the native spawn creates no pipe and runs no feeder for it.
    let isInherit (source: StdinSource) : bool =
        match source with
        | StdinSource.Inherit -> true
        | StdinSource.Empty
        | StdinSource.Text _
        | StdinSource.Bytes _
        | StdinSource.File _
        | StdinSource.Reader _
        | StdinSource.Lines _
        | StdinSource.AsyncLines _ -> false

[<RequireQualifiedAccess>]
module internal Stdin =

    /// True when `stdin` carries a one-shot source (see `StdinSource.isOneShot`); `false` for `None`
    /// — no stdin source at all is trivially repeatable, since there is nothing to exhaust.
    let isOneShot (stdin: Stdin option) : bool =
        stdin
        |> Option.map (fun s -> StdinSource.isOneShot s.Source)
        |> Option.defaultValue false

    /// True when `stdin` is the inherited-stdin source (`Command.InheritStdin`); `false` for `None` or
    /// any feeder source. The native spawn keys off this to hand the child the parent's own standard
    /// input directly instead of creating a pipe.
    let isInherit (stdin: Stdin option) : bool =
        stdin |> Option.exists (fun s -> StdinSource.isInherit s.Source)

/// The single-owner claim on ONE one-shot stdin payload (`StdinSource.oneShotPayload`). Three states,
/// moved only by atomic transitions:
///
/// - **available** — nobody owns the payload; a run may reserve it.
/// - **reserved** — a run owns the payload but has not launched a child with it yet, so it is still
///   intact: rolling the reservation back returns the payload to *available*.
/// - **consumed** — a child was launched with the payload, so it is spent for good; every later
///   reservation is refused instead of silently feeding the next child empty or truncated input.
[<Sealed>]
type internal OneShotStdinClaim() =

    // 0 = available, 1 = reserved, 2 = consumed.
    let mutable state = 0

    /// Atomically take an *available* payload for the calling run. `false` means the payload is
    /// already reserved by another run, or already consumed — either way this run must not feed it.
    member _.TryReserve() =
        Interlocked.CompareExchange(&state, 1, 0) = 0

    /// Mark the payload spent because a child that reads it now exists. Deliberately unconditional
    /// (from *any* state): the launching run is not always the reserving one — a run with no retry
    /// budget reserves nothing, yet its child consumes the payload for everyone.
    member _.Commit() = Volatile.Write(&state, 2)

    /// Hand a still-intact payload back for a later run. Only from *reserved*: a payload already
    /// consumed — by this run's own child or by another run's — must never become available again.
    member _.Release() =
        Interlocked.CompareExchange(&state, 0, 1) |> ignore

    /// True once a child has been launched with this payload.
    member _.IsConsumed = Volatile.Read(&state) = 2

/// One run's transactional hold on a one-shot stdin payload: taken before the run's first attempt,
/// committed at the launch boundary by whichever attempt produced a live child
/// (`OneShotStdin.commitLaunch`), or rolled back by the run when no attempt ever reached one.
[<Sealed>]
type internal OneShotStdinReservation internal (claim: OneShotStdinClaim) =

    let mutable settled = 0

    /// True once a child was launched with this payload, so it can no longer be replayed.
    member _.IsConsumed = claim.IsConsumed

    /// Roll the reservation back — the payload never reached a child, so hand it to the next run.
    /// Idempotent: a second call does nothing, so it can never free a payload that some other run has
    /// reserved in the meantime.
    member _.Rollback() =
        if Interlocked.Exchange(&settled, 1) = 0 then
            claim.Release()

/// The narrow, retry-scoped ownership of a one-shot stdin payload. `Runner.withRetry` reserves the
/// payload for a run that may attempt the command more than once, and the capture launch boundary
/// (`CaptureVerbs.runToCompletion`) commits it the moment a child exists — so the retry loop can tell
/// "no child ever saw this payload" from "a child may already have drained it" by evidence rather than
/// by guessing from the error alone.
///
/// **Scope, honestly stated.** This is *not* a general cross-runner reservation: only a retrying run
/// reserves, and only the capture launch boundary commits. A payload drained by a streaming
/// `SpawnAsync`, a pipeline, or a supervised incarnation is therefore not recorded here, so a later
/// retrying run can still reserve it and feed its child an exhausted source. That same boundary is
/// what lets a run hand an unspent payload back (`Runner.withRetry` releases only what no attempt of
/// its own could have fed to a child), so a custom `IProcessRunner` that launches without going
/// through it is invisible to both halves. Making every launch reserve — for every runner and every
/// verb — is a separate, wider change.
[<RequireQualifiedAccess>]
module internal OneShotStdin =

    // Weak keys: a claim lives exactly as long as the payload it guards, so tracking payloads costs
    // no lifetime extension and leaks nothing.
    let private claims = ConditionalWeakTable<obj, OneShotStdinClaim>()

    let private claimFor (stdin: Stdin option) : OneShotStdinClaim option =
        stdin
        |> Option.bind (fun s -> StdinSource.oneShotPayload s.Source)
        |> Option.map (fun payload -> claims.GetValue(payload, (fun _ -> OneShotStdinClaim())))

    /// Reserve `stdin`'s one-shot payload for one run.
    ///
    /// - `Ok None` — nothing to reserve: no stdin source, or a repeatable one (`FromString`/
    ///   `FromBytes`/`FromFile`/`Empty`/`InheritStdin`), which every attempt may replay unchanged.
    /// - `Ok(Some reservation)` — this run now owns the payload until it is committed or rolled back.
    /// - `Error` — refuse the run: another run owns the payload, or a child has already drained it,
    ///   and feeding it again would hand a child empty or truncated input instead of the caller's.
    let reserve (program: string) (stdin: Stdin option) : Result<OneShotStdinReservation option, ProcessError> =
        match claimFor stdin with
        | None -> Ok None
        | Some claim ->
            if claim.TryReserve() then
                Ok(Some(OneShotStdinReservation claim))
            else
                Error(
                    ProcessError.Unsupported
                        $"'{program}' has a one-shot stdin source that another run already holds, or that a child has already read: such a source feeds a single run, so this one would find it exhausted. Use a repeatable source (Stdin.FromString / FromBytes / FromFile), or rebuild the command with a fresh source"
                )

    /// Commit `stdin`'s one-shot payload at the launch boundary: a child that reads it now exists, so
    /// the payload is spent whether or not this run reserved it (an unreserved run still consumes it).
    /// A no-op for a repeatable source or no source at all.
    let commitLaunch (stdin: Stdin option) : unit =
        match claimFor stdin with
        | Some claim -> claim.Commit()
        | None -> ()
