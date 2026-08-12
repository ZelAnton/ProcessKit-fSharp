namespace ProcessKit

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks

/// A handle for writing to a running child's standard input interactively.
///
/// Obtained from `RunningProcess.TakeStdin` when the command was built with `Command.Stdin` /
/// `Command.KeepStdinOpen`. Call `FinishAsync` to close stdin (the child sees end-of-file).
///
/// Each write accepts an optional `CancellationToken`: a child that stops reading fills the stdin
/// pipe and blocks the write, so a token lets the caller bound how long it waits (a cancelled write
/// throws `OperationCanceledException`, the .NET convention for a cancelled `Task`). As with any
/// cancellable stream write, a cancelled write may already have delivered *some* of its bytes to the
/// child, so the safe recovery from a timed-out interactive write is to abandon the session — not to
/// retry the write, which would duplicate the delivered prefix.
[<Sealed>]
type ProcessStdin internal (stream: Stream, encoding: Text.Encoding) =

    /// Write raw bytes to the child's stdin. `bytes` must not be null (`ArgumentNullException` —
    /// a C# caller that forgets a null check would otherwise see a raw `NullReferenceException`).
    member _.WriteAsync(bytes: byte[], [<Optional>] cancellationToken: CancellationToken) : Task =
        ArgumentNullException.ThrowIfNull bytes
        stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken)

    /// Write a line of text encoded with this command's `StdinEncoding` followed by `\n`. `text` must not
    /// be null (`ArgumentNullException`).
    member _.WriteLineAsync(text: string, [<Optional>] cancellationToken: CancellationToken) : Task =
        ArgumentNullException.ThrowIfNull text
        let bytes = Pump.lineWithLf encoding text
        stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken)

    /// Flush buffered input to the child.
    member _.FlushAsync([<Optional>] cancellationToken: CancellationToken) : Task = stream.FlushAsync cancellationToken

    /// Close the child's stdin — the child sees end-of-file. Idempotent (safe to call more than once,
    /// or after the run's own teardown has closed stdin), mirroring `IAsyncDisposable.DisposeAsync`.
    /// Uncancellable by the same convention: closing flushes any buffered input, which a full pipe can
    /// block — to bound an interactive session, cancel the `WriteAsync`/`WriteLineAsync`/`FlushAsync`
    /// calls above before closing rather than the close itself. Writes through this handle are refused
    /// once it has been finished.
    ///
    /// Under a POSIX **PTY** there is no stdin pipe to close — stdin is a view over the terminal the
    /// child's output also comes from — so the end of input is delivered as the terminal's own
    /// end-of-input character (`termios.c_cc[VEOF]`, Ctrl-D on a default terminal) instead. The child
    /// therefore sees EOF only while its terminal is in canonical mode; one that switched its tty to raw
    /// mode reads that character as ordinary input, which is the line discipline's contract. A genuine
    /// failure to deliver it throws (an `IOException`) rather than leaving a child that reads to EOF
    /// waiting forever — a child that has already closed its terminal, and a run whose own teardown has
    /// been through here, both still complete quietly.
    member _.FinishAsync() : Task =
        match box stream with
        | :? Native.Common.IStdinFinisher as finisher -> finisher.FinishAsync()
        | _ -> Pump.disposeQuietlyAsync stream
