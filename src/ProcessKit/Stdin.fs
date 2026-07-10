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
