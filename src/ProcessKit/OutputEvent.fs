namespace ProcessKit

/// One line of captured output, with its terminating newline stripped.
///
/// A sealed type (not a bare `string`) so the line can gain metadata — a timestamp, a
/// monotonic sequence number — in a later minor version without breaking the frozen API.
[<Sealed>]
type OutputLine internal (text: string) =

    /// The line text, without the trailing `\n` / `\r\n`.
    member _.Text = text

    override _.ToString() = text

/// A single event in a merged stdout+stderr stream, tagged with its origin.
[<RequireQualifiedAccess>]
type OutputEvent =

    /// A line from standard output.
    | Stdout of OutputLine

    /// A line from standard error.
    | Stderr of OutputLine

    /// The line text, regardless of origin.
    member this.Text =
        match this with
        | OutputEvent.Stdout line
        | OutputEvent.Stderr line -> line.Text
