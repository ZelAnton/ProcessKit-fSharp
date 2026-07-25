namespace ProcessKit

open System

/// One line of captured output, with its terminating newline stripped, its UTC capture time, and a
/// per-run sequence number shared by stdout and stderr.
[<Sealed>]
type OutputLine internal (text: string, timestampUtc: DateTimeOffset, sequence: int64) =

    /// The line text, without the trailing `\n` / `\r\n`.
    member _.Text = text

    /// The UTC time at which ProcessKit framed this line, from the command's `TimeProvider`.
    member _.TimestampUtc = timestampUtc

    /// A one-based, monotonically increasing number shared by stdout and stderr for this run.
    member _.Sequence = sequence

    override _.ToString() = text

/// A single event in a merged stdout+stderr stream, tagged with its origin.
[<RequireQualifiedAccess>]
type OutputEvent =

    /// A line from standard output.
    | Stdout of Line: OutputLine

    /// A line from standard error.
    | Stderr of Line: OutputLine

    /// The line text, regardless of origin.
    member this.Text =
        match this with
        | OutputEvent.Stdout line
        | OutputEvent.Stderr line -> line.Text

    /// The line's UTC capture time, regardless of origin.
    member this.TimestampUtc =
        match this with
        | OutputEvent.Stdout line
        | OutputEvent.Stderr line -> line.TimestampUtc

    /// The line's per-run sequence number, regardless of origin.
    member this.Sequence =
        match this with
        | OutputEvent.Stdout line
        | OutputEvent.Stderr line -> line.Sequence
