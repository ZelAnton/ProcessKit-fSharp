namespace ProcessKit

open System
open System.Collections.Generic
open System.IO
open System.Text

/// Where a child process's standard input comes from. Internal representation behind `Stdin`.
type internal StdinSource =
    | Empty
    | Bytes of byte[]
    | File of path: string
    | Reader of Stream
    | Lines of seq<string>
    | AsyncLines of IAsyncEnumerable<string>

/// A source for a child process's standard input, attached with `Command.Stdin`.
///
/// When set, the child's stdin is a pipe fed from this source; the pipe is closed (EOF) once
/// the source is exhausted, unless `Command.KeepStdinOpen` is also set.
[<Sealed>]
type Stdin internal (source: StdinSource) =

    member internal _.Source = source

    /// No input — the child sees end-of-file immediately.
    static member Empty = Stdin(StdinSource.Empty)

    /// The UTF-8 bytes of `text`. `text` must not be null (`ArgumentNullException` — a C# caller that
    /// forgets a null check would otherwise fail obscurely inside the background feeder rather than at
    /// the API boundary).
    static member FromString(text: string) =
        ArgumentNullException.ThrowIfNull text
        Stdin(StdinSource.Bytes(Encoding.UTF8.GetBytes text))

    /// Raw bytes. `bytes` must not be null (`ArgumentNullException`).
    static member FromBytes(bytes: byte[]) =
        ArgumentNullException.ThrowIfNull bytes
        Stdin(StdinSource.Bytes bytes)

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

[<RequireQualifiedAccess>]
module internal StdinSource =

    /// True for a source that can only be pumped once: a live `Stream` (`FromStream`), or a sequence
    /// of lines (`FromLines`/`FromAsyncLines`) that may be backed by a one-shot enumerator (a
    /// generator, a non-seekable reader). Re-pumping an already-exhausted one-shot source into a
    /// second attempt silently feeds the child empty/truncated input instead of replaying the
    /// original one — see T-088 (ports ProcessKit-rs `c1f39c7`/`8472007`). The repeatable sources are
    /// unaffected: `Empty` has nothing to exhaust, `Bytes` is an immutable in-memory array pumped
    /// fresh from the start on every attempt, and `File` reopens its path fresh on every attempt.
    let isOneShot (source: StdinSource) : bool =
        match source with
        | StdinSource.Reader _
        | StdinSource.Lines _
        | StdinSource.AsyncLines _ -> true
        | StdinSource.Empty
        | StdinSource.Bytes _
        | StdinSource.File _ -> false

[<RequireQualifiedAccess>]
module internal Stdin =

    /// True when `stdin` carries a one-shot source (see `StdinSource.isOneShot`); `false` for `None`
    /// — no stdin source at all is trivially repeatable, since there is nothing to exhaust.
    let isOneShot (stdin: Stdin option) : bool =
        stdin
        |> Option.map (fun s -> StdinSource.isOneShot s.Source)
        |> Option.defaultValue false
