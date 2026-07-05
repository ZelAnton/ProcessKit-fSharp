namespace ProcessKit

open System
open System.IO
open System.Runtime.InteropServices
open System.Text
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
type ProcessStdin internal (stream: Stream) =

    /// Write raw bytes to the child's stdin. `bytes` must not be null (`ArgumentNullException` —
    /// a C# caller that forgets a null check would otherwise see a raw `NullReferenceException`).
    member _.WriteAsync(bytes: byte[], [<Optional>] cancellationToken: CancellationToken) : Task =
        ArgumentNullException.ThrowIfNull bytes
        stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken)

    /// Write a line of UTF-8 text followed by `\n`. `text` must not be null (`ArgumentNullException`).
    member _.WriteLineAsync(text: string, [<Optional>] cancellationToken: CancellationToken) : Task =
        ArgumentNullException.ThrowIfNull text
        let bytes = Encoding.UTF8.GetBytes(text + "\n")
        stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken)

    /// Flush buffered input to the child.
    member _.FlushAsync([<Optional>] cancellationToken: CancellationToken) : Task = stream.FlushAsync cancellationToken

    /// Close the child's stdin — the child sees end-of-file. Idempotent (safe to call more than once,
    /// or after the run's own teardown has closed stdin), mirroring `IAsyncDisposable.DisposeAsync`.
    /// Uncancellable by the same convention: closing flushes any buffered input, which a full pipe can
    /// block — to bound an interactive session, cancel the `WriteAsync`/`WriteLineAsync`/`FlushAsync`
    /// calls above before closing rather than the close itself.
    member _.FinishAsync() : Task = stream.DisposeAsync().AsTask()
