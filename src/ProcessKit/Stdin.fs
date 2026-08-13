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
/// lent to each attempt's own launch (`OneShotStdinLaunch`, which commits it as soon as that attempt
/// produces a live child), or rolled back by the run when no attempt ever reached one.
[<Sealed>]
type internal OneShotStdinReservation internal (claim: OneShotStdinClaim) =

    let mutable settled = 0

    /// True once a child was launched with this payload, so it can no longer be replayed.
    member _.IsConsumed = claim.IsConsumed

    /// True when this reservation is the hold on `candidate`. The identity check a launch boundary
    /// makes before it treats an already-reserved payload as its own: a run that owns the payload is
    /// lending it to its own attempt (which may proceed), whereas a payload reserved by SOMEONE ELSE
    /// is exactly the concurrent second consumer that must be refused.
    member _.Holds(candidate: OneShotStdinClaim) = obj.ReferenceEquals(claim, candidate)

    /// Roll the reservation back — the payload never reached a child, so hand it to the next run.
    /// Idempotent: a second call does nothing, so it can never free a payload that some other run has
    /// reserved in the meantime.
    member _.Rollback() =
        if Interlocked.Exchange(&settled, 1) = 0 then
            claim.Release()

/// ONE launch's transactional hold on a one-shot stdin payload, taken at the boundary that actually
/// creates a child: `ProcessGroup.BuildHost` (every single-command verb, runner and streaming start
/// goes through it) and the pipeline's own stage-0 spawn. Reserved BEFORE the spawn, so a second
/// consumer is refused before it can create a child of its own; committed the instant that child
/// exists — before its stdin feeder reads a byte — and rolled back when the spawn never produced one.
///
/// Two shapes, differing only in what `Rollback` does:
///
/// - **owned** — this launch reserved the payload itself, so a spawn that produced no child hands it
///   straight back for the next launch to take.
/// - **inherited** — an enclosing run (`Runner.withRetry`) already holds the payload for the whole of
///   its possibly-retried run and lends it to this attempt. Committing is still this launch's to do
///   (a child now exists), but handing the payload back is the RUN's decision, taken once its last
///   attempt is over: releasing it here would free a payload the run still means to feed to its next
///   attempt, and hand it to a concurrent run in the meantime.
[<Sealed>]
type internal OneShotStdinLaunch internal (claim: OneShotStdinClaim option, ownsReservation: bool) =

    let mutable settled = 0

    /// A child that reads this payload now exists, so it is spent for good — mark it before the feeder
    /// can read a byte of it, so a later launch is refused rather than handed an exhausted source.
    /// Unconditional from any state (see `OneShotStdinClaim.Commit`), hence idempotent: a capture verb
    /// that witnesses the same launch again through `OneShotStdin.commitLaunch` writes the same value.
    /// A repeatable source (or no source at all) has no claim, so this is a no-op there.
    member _.Commit() =
        claim |> Option.iter (fun c -> c.Commit())

    /// No child was created — nothing can have read the payload — so hand an OWNED reservation back for
    /// the next launch. Idempotent, and deliberately a no-op for an inherited one, whose enclosing run
    /// settles it instead.
    member _.Rollback() =
        if ownsReservation && Interlocked.Exchange(&settled, 1) = 0 then
            claim |> Option.iter (fun c -> c.Release())

/// Ownership of a one-shot stdin payload, held by at most ONE incarnation at a time.
///
/// Two boundaries take a hold, and they compose:
///
/// - **Every launch** (`reserveLaunch`) — the boundary that is about to create a child reserves the
///   payload BEFORE it spawns, commits it the instant the child exists (before that child's stdin
///   feeder reads a byte), and rolls the reservation back when no child was created. Both places that
///   can actually drain a payload sit behind it: `ProcessGroup.BuildHost` — the single spawn point of
///   `StartAsync`, every capture verb, streaming and supervision — and the pipeline's stage-0 spawn.
///   So a second run, a second verb, or a concurrent one is refused with a typed error before its own
///   spawn instead of quietly reading the exhausted remains of someone else's payload.
/// - **A retrying run** (`reserve`) — `Runner.withRetry` takes the payload for the whole of a run that
///   may attempt the command more than once, and lends it to each attempt's launch (which recognizes
///   the run's own hold through `OneShotStdinReservation.Holds` and neither re-reserves nor releases
///   it). That keeps the payload off-limits to other runs across the gaps BETWEEN attempts, and lets
///   the loop tell "no child ever saw this payload" from "a child may already have drained it" by
///   evidence — the reservation's own `IsConsumed` — rather than by guessing from the error alone.
///
/// A repeatable source (`FromString`/`FromBytes`/`FromFile`/`Empty`/`InheritStdin`) has no payload to
/// claim (`StdinSource.oneShotPayload` returns `None` for it), so every verb here is a no-op for it and
/// it stays replayable as often as the caller likes.
///
/// **Residual scope.** A custom `IProcessRunner` that neither spawns through `BuildHost` nor drives a
/// pipeline never commits — but it also cannot reach the payload (`Stdin.Source` and `StdinSource` are
/// internal, and the feeder that reads them is `Pump`'s), so nothing outside the library can drain a
/// payload unrecorded. `CaptureVerbs.runToCompletion` still commits at the capture boundary as well,
/// which is what records a launch made by an in-library double/seam that hands back a `RunningProcess`
/// without going through `BuildHost` at all.
[<RequireQualifiedAccess>]
module internal OneShotStdin =

    // Weak keys: a claim lives exactly as long as the payload it guards, so tracking payloads costs
    // no lifetime extension and leaks nothing.
    let private claims = ConditionalWeakTable<obj, OneShotStdinClaim>()

    let private claimFor (stdin: Stdin option) : OneShotStdinClaim option =
        stdin
        |> Option.bind (fun s -> StdinSource.oneShotPayload s.Source)
        |> Option.map (fun payload -> claims.GetValue(payload, (fun _ -> OneShotStdinClaim())))

    // The single refusal, shared by both holds so a run refused up front and a launch refused at the
    // spawn boundary are the same typed error with the same wording — one contract, told once.
    // `Unsupported` (which `ProcessError.isTransient` rejects) is deliberate: an exhausted payload is a
    // permanent condition, so no retry classifier may re-try into it.
    let private refusal (program: string) : ProcessError =
        ProcessError.Unsupported
            $"'{program}' has a one-shot stdin source that another run already holds, or that a child has already read: such a source feeds a single run, so this one would find it exhausted. Use a repeatable source (Stdin.FromString / FromBytes / FromFile), or rebuild the command with a fresh source"

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
                Error(refusal program)

    /// Reserve `stdin`'s one-shot payload for ONE launch, about to create a child.
    ///
    /// `held` is the reservation the enclosing run already owns (`Runner.withRetry`'s, carried on the
    /// command), or `None` for a launch that answers to no run-level hold. A launch whose payload is
    /// already held by its OWN run proceeds on that hold — it commits at the spawn but never releases,
    /// leaving the run to settle its reservation when its last attempt is over. Anything else must
    /// take the payload for itself, and is refused with `Error` when it cannot: another run owns it, or
    /// a child has already drained it.
    ///
    /// Always call `Commit()` on the returned launch the moment a child exists (before its stdin feeder
    /// starts), and `Rollback()` when the spawn produced none.
    let reserveLaunch
        (program: string)
        (stdin: Stdin option)
        (held: OneShotStdinReservation option)
        : Result<OneShotStdinLaunch, ProcessError> =
        match claimFor stdin with
        | None -> Ok(OneShotStdinLaunch(None, false))
        | Some claim ->
            if held |> Option.exists (fun reservation -> reservation.Holds claim) then
                Ok(OneShotStdinLaunch(Some claim, false))
            elif claim.TryReserve() then
                Ok(OneShotStdinLaunch(Some claim, true))
            else
                Error(refusal program)

    /// Commit `stdin`'s one-shot payload at the launch boundary: a child that reads it now exists, so
    /// the payload is spent whether or not this run reserved it (an unreserved run still consumes it).
    /// A no-op for a repeatable source or no source at all.
    let commitLaunch (stdin: Stdin option) : unit =
        match claimFor stdin with
        | Some claim -> claim.Commit()
        | None -> ()
