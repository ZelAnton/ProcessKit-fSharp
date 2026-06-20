namespace ProcessKit

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

    /// The UTF-8 bytes of `text`.
    static member FromString(text: string) =
        Stdin(StdinSource.Bytes(Encoding.UTF8.GetBytes text))

    /// Raw bytes.
    static member FromBytes(bytes: byte[]) = Stdin(StdinSource.Bytes bytes)

    /// The contents of a file, streamed to the child.
    static member FromFile(path: string) = Stdin(StdinSource.File path)

    /// An open readable stream, copied to the child.
    static member FromReader(reader: Stream) = Stdin(StdinSource.Reader reader)

    /// Lines (each written followed by `\n`) produced eagerly from a sequence.
    static member FromLines(lines: seq<string>) = Stdin(StdinSource.Lines lines)

    /// Lines (each written followed by `\n`) produced asynchronously.
    static member FromAsyncLines(lines: IAsyncEnumerable<string>) = Stdin(StdinSource.AsyncLines lines)
