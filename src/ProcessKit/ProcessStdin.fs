namespace ProcessKit

open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks

/// A handle for writing to a running child's standard input interactively.
///
/// Obtained from `RunningProcess.TakeStdin` when the command was built with `Command.Stdin` /
/// `Command.KeepStdinOpen`. Call `Finish` to close stdin (the child sees end-of-file).
[<Sealed>]
type ProcessStdin internal (stream: Stream) =

    /// Write raw bytes to the child's stdin.
    member _.Write(bytes: byte[]) : Task =
        stream.WriteAsync(bytes, 0, bytes.Length, CancellationToken.None)

    /// Write a line of UTF-8 text followed by `\n`.
    member _.WriteLine(text: string) : Task =
        let bytes = Encoding.UTF8.GetBytes(text + "\n")
        stream.WriteAsync(bytes, 0, bytes.Length, CancellationToken.None)

    /// Flush buffered input to the child.
    member _.Flush() : Task = stream.FlushAsync()

    /// Close the child's stdin — the child sees end-of-file.
    member _.Finish() : Task = stream.DisposeAsync().AsTask()
