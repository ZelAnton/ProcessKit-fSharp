namespace ProcessKit.Tests

open System
open System.Collections.Generic
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open Microsoft.Win32.SafeHandles
open NUnit.Framework
open ProcessKit

/// A stand-in for the ConPTY session's host-input pipe (T-335): it records every byte written, counts how
/// many times it is actually closed, and can be told to fail writes with a chosen exception. The parts under
/// test — who owns that pipe (`Native.Windows.ConPtyInputKeepalive`), the non-owning writer over it
/// (`Native.Windows.ConPtyStdinStream`), and the console end-of-input gesture the writer's finish delivers —
/// are ordinary managed code over a `Stream`, so they are exercised on every platform rather than only where
/// a real pseudoconsole can be created.
type internal ConPtyHostInputDouble(failWrites: exn option) =
    inherit Stream()

    let written = ResizeArray<byte>()
    let mutable disposeCount = 0

    new() = new ConPtyHostInputDouble(None)

    /// Everything written through this pipe, in order.
    member _.Written = written.ToArray()

    /// How many times this pipe has actually been closed — the "exactly once" a session teardown owes it.
    member _.DisposeCount = disposeCount

    override _.CanRead = false
    override _.CanSeek = false
    override _.CanWrite = true

    override _.Length =
        raise (NotSupportedException "the host-input pipe double has no length")

    override _.Position
        with get () = raise (NotSupportedException "the host-input pipe double has no position")
        and set (_: int64) = raise (NotSupportedException "the host-input pipe double has no position")

    override _.Flush() = ()

    override _.Read(_buffer: byte[], _offset: int, _count: int) =
        raise (NotSupportedException "the host-input pipe double is write-only")

    override _.Seek(_offset: int64, _origin: SeekOrigin) =
        raise (NotSupportedException "the host-input pipe double is not seekable")

    override _.SetLength(_value: int64) =
        raise (NotSupportedException "the host-input pipe double has no length")

    override _.Write(buffer: byte[], offset: int, count: int) =
        match failWrites with
        | Some ex -> raise ex
        | None -> written.AddRange(Array.sub buffer offset count)

    override _.Dispose(disposing) =
        disposeCount <- disposeCount + 1
        base.Dispose disposing

/// Tests for the opt-in PTY (pseudo-terminal) mode: Stage 1 (T-137) — `PtyConfig`, the `Command.Pty` /
/// `Command.pty` builders, the build-time guards (D4/D8 + the pipeline guard), the Windows ConPTY spawn;
/// and Stage 2 (T-138) — the real POSIX `openpty` (`posix_openpt`) + `setsid --ctty` spawn: a merged
/// terminal stream (no separate stderr, D3), a real controlling terminal (`isatty` true, `/dev/tty`
/// openable), and an honest typed `Unsupported` where the ctty helper is absent (D9).
///
/// Live-ConPTY round-trip note (documented skip-gate, per the ADR/task): a ConPTY child's *text* output
/// is only captured through the pseudoconsole when the parent process has **no** inherited interactive
/// console; a console-attached parent (a developer terminal, some test hosts) makes a console-subsystem
/// child attach to that console instead — a well-known ConPTY caveat, not a defect (verified: the spawn,
/// containment, merged-stream shape, and conhost teardown are all correct, and full text capture works
/// from a console-less parent). So the Windows test below asserts the robust, environment-independent
/// contract — the run spawns, produces a single merged stream (no stderr), and exits cleanly — rather
/// than a specific captured string.
///
/// The POSIX spawn tests are Linux-gated (the Stage-2 ctty helper is util-linux `setsid --ctty`, absent
/// on macOS/BSD — an honest `Unsupported` there, asserted deterministically via the internal
/// `ptyCttyHelperAvailableForTests` seam). They run sequentially (NUnit's default within a fixture), so
/// that forced-missing-helper seam never races the real-pty tests.
[<TestFixture>]
type PtyTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isLinux = RuntimeInformation.IsOSPlatform OSPlatform.Linux
    let runner: IProcessRunner = JobRunner()

    let ptyStreamForTests () =
        new Native.Posix.PtyStream(new SafeFileHandle(IntPtr.Zero, ownsHandle = false))

    // The stdin (write) view over a pty master, with an explicit end-of-input character standing in for the
    // one a real spawn reads out of the slave's termios. Non-owning, over no real fd — every write goes
    // through the `ptyWriteForTests` seam below.
    let ptyStdinForTests (eofChar: byte) =
        new Native.Posix.PtyStdinStream(new SafeFileHandle(IntPtr.Zero, ownsHandle = false), eofChar)

    // Record every byte handed to the raw pty write, accepting at most `accept` of them per call so a
    // partial write can be forced.
    let recordPtyWrites (written: ResizeArray<byte>) (accept: int) =
        Native.Posix.ptyWriteForTests <-
            Some(fun _ ptr count ->
                let take = min accept (int count)
                let buffer = Array.zeroCreate<byte> take
                Marshal.Copy(ptr, buffer, 0, take)
                written.AddRange buffer
                nativeint take)

    // A path that certainly does not exist, so the eager `StdinSource.File` open fails at spawn — the one
    // place a bulk stdin delivery ends before it has written anything.
    let missingStdinPath () =
        Path.Combine(Path.GetTempPath(), $"pk-pty-missing-{Guid.NewGuid():N}")

    // Collect an async sequence (the streaming event/line verbs) into a list for assertions.
    let collect (items: IAsyncEnumerable<'T>) =
        task {
            let acc = ResizeArray<'T>()
            let e = items.GetAsyncEnumerator()
            let mutable more = true

            while more do
                match! e.MoveNextAsync() with
                | true -> acc.Add e.Current
                | false -> more <- false

            do! e.DisposeAsync()
            return acc
        }

    // ----------------------------------------------------------------------------------
    // Config + builders
    // ----------------------------------------------------------------------------------

    [<Test>]
    member _.``PtyConfig.Default is 80x24 with echo on``() =
        let d = PtyConfig.Default
        Assert.That(d.Cols, Is.EqualTo 80)
        Assert.That(d.Rows, Is.EqualTo 24)
        Assert.That(d.Echo, Is.True)

    [<Test>]
    member _.``Pty builders accept valid geometry without throwing``() =
        // Member overloads and the module function all build a Command without error.
        (Command.create "cmd").Pty() |> ignore
        (Command.create "cmd").Pty(100, 40) |> ignore
        (Command.create "cmd").Pty({ Cols = 120; Rows = 30; Echo = false }) |> ignore
        (Command.create "cmd" |> Command.pty) |> ignore

    [<Test>]
    member _.``PtyStream rejects invalid buffer ranges before native I/O``() =
        let mutable readCalls = 0
        let mutable writeCalls = 0

        Native.Posix.ptyReadForTests <-
            Some(fun _ _ _ ->
                readCalls <- readCalls + 1
                0n)

        Native.Posix.ptyWriteForTests <-
            Some(fun _ _ _ ->
                writeCalls <- writeCalls + 1
                1n)

        try
            use stream = ptyStreamForTests ()
            let buffer = Array.zeroCreate<byte> 2

            Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> stream.Read(buffer, -1, 1) |> ignore))
            |> ignore

            Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> stream.Read(buffer, 0, -1) |> ignore))
            |> ignore

            Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> stream.Read(buffer, 1, 2) |> ignore))
            |> ignore

            Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> stream.Write(buffer, -1, 1)))
            |> ignore

            Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> stream.Write(buffer, 0, -1)))
            |> ignore

            Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> stream.Write(buffer, 1, 2)))
            |> ignore
        finally
            Native.Posix.ptyReadForTests <- None
            Native.Posix.ptyWriteForTests <- None

        Assert.That(readCalls, Is.Zero, "invalid reads must not reach native I/O")
        Assert.That(writeCalls, Is.Zero, "invalid writes must not reach native I/O")

    [<Test>]
    member _.``PtyStream turns a zero-byte write into an I/O error``() =
        let mutable writeCalls = 0

        Native.Posix.ptyWriteForTests <-
            Some(fun _ _ _ ->
                writeCalls <- writeCalls + 1
                0n)

        try
            use stream = ptyStreamForTests ()

            let checkException () =
                let ex = Assert.Throws<IOException>(Action(fun () -> stream.Write([| 1uy |], 0, 1)))

                match ex with
                | null -> failwith "expected a zero-byte write to throw IOException"
                | ex -> Assert.That(ex.Message, Does.Contain "made no progress")

            checkException ()
        finally
            Native.Posix.ptyWriteForTests <- None

        Assert.That(writeCalls, Is.EqualTo 1, "a zero-byte write must fail instead of retrying indefinitely")

    // ----------------------------------------------------------------------------------
    // T-332: ending a POSIX pty stdin delivers a TERMINAL end of input. The view owns no fd to close (it
    // shares the master with the merged-output stream), so a child reading to EOF only ever sees one if
    // the pty's own end-of-input character reaches the line discipline.
    // ----------------------------------------------------------------------------------

    [<Test>]
    member _.``finishing a pty stdin delivers the configured VEOF twice, then refuses further input``() : Task =
        task {
            // A deliberately non-default end-of-input character (Ctrl-Q, 0x11) instead of Ctrl-D: what is
            // delivered must be the character the pty is CONFIGURED with, read from its slave termios when
            // the pair was created, never an assumed default. This entire test runs through the mocked pty
            // write seam (`ptyWriteForTests`), which does not involve a real kernel, termios policy, or
            // IXON flow control, so 0x11 is perfectly safe here — the test is pure mock behavior.
            let written = ResizeArray<byte>()
            recordPtyWrites written 64

            try
                let stdin = ptyStdinForTests 0x11uy
                Assert.That(stdin.EofChar, Is.EqualTo 0x11uy)
                do! stdin.FinishAsync()

                // Twice: the first terminates the unterminated canonical line (handing it to the child's
                // pending read), the second lands on the now-empty line and IS the child's end of input.
                Assert.That(written.ToArray(), Is.EqualTo<byte[]>([| 0x11uy; 0x11uy |]))
                Assert.That(stdin.IsFinished, Is.True)

                // Input after the end of input is refused, never written silently past the EOF the child saw.
                Assert.Throws<ObjectDisposedException>(Action(fun () -> stdin.Write([| 1uy |], 0, 1)))
                |> ignore

                // Idempotent, as `ProcessStdin.FinishAsync`/`PtySession.CloseStdinAsync` both promise.
                do! stdin.FinishAsync()

                Assert.That(written.Count, Is.EqualTo 2, "a repeated finish must not deliver a second end of input")
            finally
                Native.Posix.ptyWriteForTests <- None
        }

    [<Test>]
    member _.``a real pty reads the configured slave VEOF and delivers it to cat``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only POSIX termios PTY integration"
            else
                // Configure the actual slave-side termios before openPtyPair reads c_cc[VEOF]. The child then
                // proves both halves of the contract: the non-default byte is discovered and the canonical
                // line discipline turns two of those bytes into payload delivery followed by EOF.
                //
                // A deliberately non-default end-of-input character (Ctrl-A, 0x01 / SOH) is chosen specifically
                // because a real pty and the Linux kernel line discipline are involved. The byte 0x11
                // (Ctrl-Q / VSTART, the original value) collides with the default c_cc[VSTART] used by IXON
                // (software flow control) on Linux. The kernel intercepts 0x11 as an XON flow-control signal
                // BEFORE the line discipline's VEOF processing runs, causing the byte to be silently swallowed
                // as a flow control byte rather than delivered to the child as end-of-input. Using 0x01 avoids
                // this collision entirely and ensures the VEOF byte reaches the child correctly.
                Native.Posix.ptyConfigureTermiosForTests <-
                    Some(fun slave -> Native.Posix.setPtyEofCharForTests slave 0x01uy)

                try
                    let payload = "termios-configured-vEOF"

                    let command =
                        (Command.create "/bin/cat").Pty({ Cols = 80; Rows = 24; Echo = false })
                        |> Command.stdin (Stdin.FromString payload)
                        |> Command.timeout (TimeSpan.FromSeconds 30.0)

                    match! command.OutputStringAsync() with
                    | Error(ProcessError.Unsupported msg) -> Assert.Ignore $"host lacks a PTY: {msg}"
                    | Error other -> Assert.Fail $"unexpected configured-VEOF PTY failure: {other}"
                    | Ok result ->
                        Assert.That(result.Stdout, Does.Contain payload)

                        match result.Outcome with
                        | Outcome.Exited 0 -> ()
                        | other -> Assert.Fail $"configured slave VEOF did not produce clean child EOF: {other}"
                finally
                    Native.Posix.ptyConfigureTermiosForTests <- None
        }

    [<Test>]
    member _.``pty finish waits for an admitted payload write before delivering VEOF``() : Task =
        task {
            let written = ResizeArray<byte>()
            use entered = new ManualResetEventSlim(false)
            use release = new ManualResetEventSlim(false)

            Native.Posix.ptyWriteForTests <-
                Some(fun _ ptr count ->
                    entered.Set()
                    release.Wait()
                    let buffer = Array.zeroCreate<byte> (int count)
                    Marshal.Copy(ptr, buffer, 0, buffer.Length)
                    written.AddRange buffer
                    nativeint buffer.Length)

            try
                use stdin = ptyStdinForTests 4uy
                let payload = [| 0x41uy; 0x42uy |]

                let writeTask: Task =
                    Task.Run(Action(fun () -> stdin.Write(payload, 0, payload.Length)))

                Assert.That(
                    entered.Wait(TimeSpan.FromSeconds 5.0),
                    Is.True,
                    "payload write did not enter the native seam"
                )

                // Finish is invoked concurrently while the payload write owns the gate. It must remain
                // pending until that write releases the gate, rather than overtaking it with VEOF.
                let finishTask: Task = Task.Run(Func<Task>(fun () -> stdin.FinishAsync()))
                Assert.That(finishTask.Wait(TimeSpan.FromMilliseconds 100.0), Is.False)

                release.Set()
                do! writeTask
                do! finishTask

                Assert.That(written.ToArray(), Is.EqualTo<byte[]>([| 0x41uy; 0x42uy; 4uy; 4uy |]))

                Assert.Throws<ObjectDisposedException>(Action(fun () -> stdin.Write([| 0x43uy |], 0, 1)))
                |> ignore
            finally
                release.Set()
                Native.Posix.ptyWriteForTests <- None
        }

    [<Test>]
    member _.``a pending pty finish retains the shared master after owner teardown``() : Task =
        task {
            use entered = new ManualResetEventSlim(false)
            use release = new ManualResetEventSlim(false)
            use ownerClosed = new ManualResetEventSlim(false)

            Native.Posix.ptyMasterCloseForTests <- Some(fun () -> ownerClosed.Set())

            Native.Posix.ptyWriteForTests <-
                Some(fun _ ptr count ->
                    entered.Set()
                    release.Wait()
                    let buffer = Array.zeroCreate<byte> (int count)
                    Marshal.Copy(ptr, buffer, 0, buffer.Length)
                    nativeint buffer.Length)

            try
                use owner = ptyStreamForTests ()
                use stdin = new Native.Posix.PtyStdinStream(owner.MasterLifetime, 4uy)
                let finishTask = stdin.FinishAsync()

                Assert.That(entered.Wait(TimeSpan.FromSeconds 5.0), Is.True, "finish did not enter the native seam")
                owner.Dispose()

                // The non-owning view's retained lease keeps the shared master alive even after stdout's
                // owner is disposed. This is the deterministic form of the close/reuse race protection.
                Assert.That(ownerClosed.IsSet, Is.False)

                release.Set()
                do! finishTask
                Assert.That(ownerClosed.Wait(TimeSpan.FromSeconds 5.0), Is.True)
            finally
                release.Set()
                Native.Posix.ptyWriteForTests <- None
                Native.Posix.ptyMasterCloseForTests <- None
        }

    [<Test>]
    member _.``T332_R04_concurrent FinishAsync callers share one successful delivery task``() =
        use entered = new ManualResetEventSlim(false)
        use release = new ManualResetEventSlim(false)
        let written = ResizeArray<byte>()

        Native.Posix.ptyWriteForTests <-
            Some(fun _ ptr count ->
                entered.Set()
                release.Wait()
                let buffer = Array.zeroCreate<byte> (int count)
                Marshal.Copy(ptr, buffer, 0, buffer.Length)
                written.AddRange buffer
                nativeint buffer.Length)

        try
            use stdin = ptyStdinForTests 4uy
            let first = stdin.FinishAsync()
            Assert.That(entered.Wait(TimeSpan.FromSeconds 5.0), Is.True, "finish did not enter the delayed native seam")

            let secondResult =
                new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously)

            let secondCaller =
                Task.Run(Action(fun () -> secondResult.SetResult(stdin.FinishAsync())))

            Assert.That(
                secondCaller.Wait(TimeSpan.FromSeconds 5.0),
                Is.True,
                "a repeated FinishAsync caller must not wait for the first delivery to finish"
            )

            let second = secondResult.Task.Result

            Assert.That(Object.ReferenceEquals(first, second), Is.True)
            Assert.That(first.IsCompleted, Is.False)

            release.Set()
            Assert.That(first.Wait(TimeSpan.FromSeconds 5.0), Is.True, "the cached finish task did not complete")
            Assert.That(second.Wait(TimeSpan.FromSeconds 5.0), Is.True, "the repeated finish task did not complete")
            Assert.That(written.ToArray(), Is.EqualTo<byte[]>([| 4uy; 4uy |]))
        finally
            release.Set()
            Native.Posix.ptyWriteForTests <- None

    [<Test>]
    member _.``a pty end-of-input delivery resumes a partial write and retries EINTR``() : Task =
        task {
            // The raw write takes ONE byte per call and is interrupted (EINTR) in between, so both
            // characters only arrive if the delivery honours partial writes and retries an interrupted
            // one — the same loop an ordinary stdin write goes through.
            let written = ResizeArray<byte>()
            let calls = ref 0

            Native.Posix.ptyWriteForTests <-
                Some(fun _ ptr count ->
                    calls.Value <- calls.Value + 1

                    if calls.Value = 2 then
                        // Interrupted before any byte was written: the remainder must be retried, not lost.
                        Marshal.SetLastPInvokeError 4 // EINTR
                        -1n
                    else
                        let take = min 1 (int count)
                        let buffer = Array.zeroCreate<byte> take
                        Marshal.Copy(ptr, buffer, 0, take)
                        written.AddRange buffer
                        nativeint take)

            try
                let stdin = ptyStdinForTests 4uy
                do! stdin.FinishAsync()

                Assert.That(written.ToArray(), Is.EqualTo<byte[]>([| 4uy; 4uy |]))
                Assert.That(calls.Value, Is.EqualTo 3, "one accepted byte, one EINTR retry, then the second byte")
            finally
                Native.Posix.ptyWriteForTests <- None
        }

    [<Test>]
    member _.``a pty with its end-of-input character disabled reports an honest failure``() : Task =
        task {
            // `_POSIX_VDISABLE` means the terminal has NO end-of-input character. Sending the byte anyway
            // would push a NUL through as ordinary input while looking like a delivered EOF.
            let written = ResizeArray<byte>()
            recordPtyWrites written 64

            try
                let stdin = ptyStdinForTests 0uy
                let action = Func<Task>(fun () -> stdin.FinishAsync())

                match Assert.ThrowsAsync<IOException>(action) with
                | null -> Assert.Fail "a disabled end-of-input character must fail the delivery, not pretend"
                | fault -> Assert.That(fault.Message, Does.Contain "_POSIX_VDISABLE")

                Assert.That(written.Count, Is.Zero, "a disabled end-of-input character must not send a byte at all")
            finally
                Native.Posix.ptyWriteForTests <- None
        }

    [<Test>]
    member _.``a failed pty end-of-input delivery surfaces instead of being swallowed``() : Task =
        task {
            // A genuine write failure (EBADF here) must reach the caller: a child reading to EOF would
            // otherwise wait forever on an end of input that was silently dropped.
            Native.Posix.ptyWriteForTests <-
                Some(fun _ _ _ ->
                    Marshal.SetLastPInvokeError 9 // EBADF
                    -1n)

            try
                let stdin = ptyStdinForTests 4uy
                let first = stdin.FinishAsync()
                let second = stdin.FinishAsync()
                Assert.That(Object.ReferenceEquals(first, second), Is.True)

                let observe (delivery: Task) =
                    task {
                        try
                            do! delivery
                            return None
                        with :? IOException as ex ->
                            return Some ex.Message
                    }

                let! firstError = observe first
                let! secondError = observe second

                match firstError, secondError with
                | Some firstMessage, Some secondMessage ->
                    Assert.That(firstMessage, Does.Contain "errno 9")
                    Assert.That(secondMessage, Is.EqualTo firstMessage)
                | _ -> Assert.Fail "every repeated FinishAsync caller must observe the delivery failure"
            finally
                Native.Posix.ptyWriteForTests <- None
        }

    [<Test>]
    member _.``finishing a pty stdin whose child already closed the terminal completes quietly``() : Task =
        task {
            // EIO on the master write is the pty hangup: the child dropped its last slave fd, so there is no
            // line discipline left to hand an end of input to — and no child left to read one. Moot, not a
            // failed delivery, exactly as closing a pipe whose peer is gone is not an error.
            Native.Posix.ptyWriteForTests <-
                Some(fun _ _ _ ->
                    Marshal.SetLastPInvokeError 5 // EIO
                    -1n)

            try
                let stdin = ptyStdinForTests 4uy
                do! stdin.FinishAsync()
                Assert.That(stdin.IsFinished, Is.True)
            finally
                Native.Posix.ptyWriteForTests <- None
        }

    [<Test>]
    member _.``Pty rejects a non-positive number of columns``() =
        Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> (Command.create "cmd").Pty(0, 24) |> ignore))
        |> ignore

    [<Test>]
    member _.``Pty rejects a non-positive number of rows``() =
        Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> (Command.create "cmd").Pty(80, 0) |> ignore))
        |> ignore

    // ----------------------------------------------------------------------------------
    // Build-time guards (D4: no separate stderr observers; D8: not with Setsid; pipeline guard)
    // ----------------------------------------------------------------------------------

    [<Test>]
    member _.``Pty then StderrTee is rejected (D4)``() =
        use sink = new MemoryStream()

        Assert.Throws<ArgumentException>(
            Action(fun () -> (Command.create "cmd" |> Command.pty).StderrTee(sink) |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``StderrTee then Pty is rejected (D4, reverse order)``() =
        use sink = new MemoryStream()

        Assert.Throws<ArgumentException>(Action(fun () -> ((Command.create "cmd").StderrTee sink).Pty() |> ignore))
        |> ignore

    [<Test>]
    member _.``Pty then OnStderrLine is rejected (D4)``() =
        Assert.Throws<ArgumentException>(
            Action(fun () ->
                (Command.create "cmd" |> Command.pty).OnStderrLine(Action<string>(ignore))
                |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``OnStderrLine then Pty is rejected (D4, reverse order)``() =
        Assert.Throws<ArgumentException>(
            Action(fun () -> ((Command.create "cmd").OnStderrLine(Action<string>(ignore))).Pty() |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``Pty then Setsid is rejected (D8)``() =
        Assert.Throws<ArgumentException>(Action(fun () -> (Command.create "cmd" |> Command.pty).Setsid() |> ignore))
        |> ignore

    [<Test>]
    member _.``Setsid then Pty is rejected (D8, reverse order)``() =
        Assert.Throws<ArgumentException>(Action(fun () -> ((Command.create "cmd").Setsid()).Pty() |> ignore))
        |> ignore

    [<Test>]
    member _.``Pty on a non-last pipeline stage is rejected``() =
        Assert.Throws<ArgumentException>(
            Action(fun () -> (Command.create "a" |> Command.pty).Pipe(Command.create "b") |> ignore)
        )
        |> ignore

    [<Test>]
    member _.``Pty combined with MergeStderr is allowed (redundant, not rejected)``() =
        // A PTY already implies merge semantics, so pairing them is redundant but not a conflict (ADR).
        (Command.create "cmd" |> Command.mergeStderr |> Command.pty) |> ignore
        (Command.create "cmd" |> Command.pty |> Command.mergeStderr) |> ignore

    // ----------------------------------------------------------------------------------
    // Spawn behaviour: POSIX honest-Unsupported; Windows ConPTY merged stream
    // ----------------------------------------------------------------------------------

    [<Test>]
    member _.``Pty on POSIX merges stdout and stderr onto the single terminal stream (D3)``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only: the Stage-2 POSIX ctty helper is util-linux setsid --ctty"
            else
                // A tty is one device, so the child's fd 1 and fd 2 both write the pty slave: OUT and ERR
                // land, in order, on the single captured master stream — and there is no separate stderr.
                let cmd =
                    Command.create "/bin/sh"
                    |> Command.args [ "-c"; "printf 'OUT-marker\\n'; printf 'ERR-marker\\n' >&2" ]
                    |> Command.pty
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! cmd.OutputStringAsync() with
                | Error(ProcessError.Unsupported msg) -> Assert.Ignore $"host lacks a PTY: {msg}"
                | Error other -> Assert.Fail $"unexpected error from a POSIX pty spawn: {other}"
                | Ok result ->
                    Assert.That(result.Stdout, Does.Contain "OUT-marker")
                    Assert.That(result.Stdout, Does.Contain "ERR-marker", "stderr is merged into the pty stream (D3)")
                    Assert.That(result.Stderr, Is.Empty, "a PTY produces no separate stderr (D3)")

                    match result.Outcome with
                    | Outcome.Exited 0 -> ()
                    | other -> Assert.Fail $"expected a clean exit from the pty child, got {other}"
        }

    [<Test>]
    member _.``Pty on POSIX gives the child a real controlling terminal (isatty + /dev/tty)``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only PTY spawn"
            else
                // `test -t 0/1/2` proves all three descriptors are ttys (a plain pipe run prints NOTATTY);
                // opening `/dev/tty` succeeds only when the session HAS a controlling terminal — which
                // `setsid --ctty` established on the pty slave. Together they prove the controlling-tty
                // (session) invariant, not merely that a device is attached.
                let script =
                    "if test -t 0 && test -t 1 && test -t 2; then printf ALLTTY; else printf NOTATTY; fi; "
                    + "if : < /dev/tty; then printf =HASCTTY; else printf =NOCTTY; fi"

                let cmd =
                    Command.create "/bin/sh"
                    |> Command.args [ "-c"; script ]
                    |> Command.pty
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! cmd.OutputStringAsync() with
                | Error(ProcessError.Unsupported msg) -> Assert.Ignore $"host lacks a PTY: {msg}"
                | Error other -> Assert.Fail $"unexpected error from a POSIX pty spawn: {other}"
                | Ok result ->
                    Assert.That(result.Stdout, Does.Contain "ALLTTY", "the child's stdin/stdout/stderr must be a tty")

                    Assert.That(
                        result.Stdout,
                        Does.Contain "HASCTTY",
                        "the pty must be the child's controlling terminal"
                    )
        }

    [<Test>]
    member _.``Pty on POSIX feeds the streaming verbs, emitting only Stdout events (D3)``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only PTY spawn"
            else
                let cmd =
                    Command.create "/bin/sh"
                    |> Command.args [ "-c"; "printf 'evt-out\\n'; printf 'evt-err\\n' >&2" ]
                    |> Command.pty
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! runner.StartAsync(cmd, CancellationToken.None) with
                | Error(ProcessError.Unsupported msg) -> Assert.Ignore $"host lacks a PTY: {msg}"
                | Error other -> Assert.Fail $"unexpected error from a POSIX pty spawn: {other}"
                | Ok running ->
                    let! events = collect (running.OutputEventsAsync())

                    // Under a PTY there is no separate stderr stream to tag: every event is a Stdout event.
                    let allStdout =
                        events
                        |> Seq.forall (fun e ->
                            match e with
                            | OutputEvent.Stdout _ -> true
                            | OutputEvent.Stderr _ -> false)

                    Assert.That(allStdout, Is.True, "every event must be a Stdout event under a PTY (D3)")

                    let text = events |> Seq.map (fun e -> e.Text) |> String.concat "\n"
                    Assert.That(text, Does.Contain "evt-out")
                    Assert.That(text, Does.Contain "evt-err", "the merged stderr line arrives as a Stdout event")
        }

    [<Test>]
    member _.``Pty on POSIX applies the configured winsize (Cols/Rows) to the child terminal``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only PTY spawn"
            else
                // `stty size` reads its controlling terminal's window size (TIOCGWINSZ) and prints
                // "rows cols". A pty opened WITHOUT applying PtyConfig.Cols/Rows carries the kernel's 0x0
                // default and would print "0 0"; a 30x120 result proves the initial geometry is honoured
                // via ioctl(TIOCSWINSZ) — parity with the Windows CreatePseudoConsole path, and no silent
                // cross-platform downgrade of a validated user field.
                let cmd =
                    (Command.create "/bin/sh" |> Command.args [ "-c"; "stty size" ]).Pty(120, 30)
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! cmd.OutputStringAsync() with
                | Error(ProcessError.Unsupported msg) -> Assert.Ignore $"host lacks a PTY: {msg}"
                | Error other -> Assert.Fail $"unexpected error from a POSIX pty spawn: {other}"
                | Ok result ->
                    Assert.That(
                        result.Stdout.Trim(),
                        Does.Contain "30 120",
                        "the pty must carry the configured 30 rows x 120 cols winsize, not the kernel 0x0 default"
                    )
        }

    [<Test>]
    member _.``Pty on POSIX feeds an interactive stdin to the child terminal and exits cleanly``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only PTY spawn"
            else
                // A fed/interactive stdin under a PTY is written to the SINGLE pty master (there is no
                // `dup` of it — a dup would drop the master's O_CLOEXEC and leak a writable master into a
                // concurrent spawn, keeping the child from ever seeing its stdin EOF). `read` returns on
                // the fed newline (it does not need EOF), so the child consumes the line and exits
                // cleanly; a hang would trip the timeout. This exercises the interactive-stdin pty path
                // end-to-end (previously untested).
                let cmd =
                    Command.create "/bin/sh"
                    |> Command.args [ "-c"; "read line; printf 'GOT=%s' \"$line\"" ]
                    |> Command.stdin (Stdin.FromString "pty-stdin-marker\n")
                    |> Command.pty
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! cmd.OutputStringAsync() with
                | Error(ProcessError.Unsupported msg) -> Assert.Ignore $"host lacks a PTY: {msg}"
                | Error other -> Assert.Fail $"unexpected error from a POSIX pty spawn: {other}"
                | Ok result ->
                    Assert.That(
                        result.Stdout,
                        Does.Contain "GOT=pty-stdin-marker",
                        "the fed stdin line must reach the pty child"
                    )

                    match result.Outcome with
                    | Outcome.Exited 0 -> ()
                    | other -> Assert.Fail $"expected a clean exit from the pty child that read stdin, got {other}"
        }

    [<Test>]
    member _.``Pty on a host without the ctty helper is a typed Unsupported, never a fake tty (D9)``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only: forces the missing-ctty-helper path a real macOS/BSD host takes"
            else
                // Force the "no setsid --ctty helper" verdict (the macOS/BSD / old-util-linux case)
                // deterministically on a host that DOES carry setsid, so the honest typed Unsupported is
                // exercised — never a socketpair silently standing in for a tty.
                Native.Posix.ptyCttyHelperAvailableForTests <- Some(fun () -> false)

                try
                    let cmd =
                        Command.create "/bin/sh" |> Command.args [ "-c"; "echo hi" ] |> Command.pty

                    match! cmd.OutputStringAsync() with
                    | Error(ProcessError.Unsupported msg) ->
                        Assert.That(msg, Does.Contain "Pty")
                        Assert.That(msg, Does.Contain "setsid")
                    | Error other -> Assert.Fail $"expected ProcessError.Unsupported, got {other}"
                    | Ok _ ->
                        Assert.Fail "a PTY without the ctty helper must fail Unsupported, not succeed with a fake tty"
                finally
                    Native.Posix.ptyCttyHelperAvailableForTests <- None
        }

    [<Test>]
    member _.``Pty on Windows spawns a ConPTY child, one merged stream, clean exit``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only ConPTY path"
            else
                let cmd =
                    Command.create "cmd.exe"
                    |> Command.args [ "/c"; "echo pty-stage1" ]
                    |> Command.pty
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! cmd.OutputStringAsync() with
                | Error(ProcessError.Unsupported msg) when msg.Contains "1809" ->
                    // Pre-1809 host without ConPTY — the documented typed-Unsupported path (D9).
                    Assert.Ignore $"host lacks ConPTY: {msg}"
                | Error other -> Assert.Fail $"unexpected error from a ConPTY spawn: {other}"
                | Ok result ->
                    // D3: a PTY is one merged terminal stream, so there is never a separate stderr.
                    Assert.That(result.Stderr, Is.Empty, "a PTY must produce no separate stderr (D3)")
                    // The pseudoconsole output stream was captured (conhost emits terminal setup at least),
                    // proving the merged pty stream is wired through the normal capture path.
                    Assert.That(result.Stdout.Length, Is.GreaterThan 0, "the merged pty stream should be captured")
                    // A clean exit proves spawn + Job containment + pseudoconsole teardown all worked and
                    // the capture did not deadlock (the ConPTY output-EOF coordination on child exit).
                    match result.Outcome with
                    | Outcome.Exited _ -> ()
                    | other -> Assert.Fail $"expected a clean exit from the ConPTY child, got {other}"
        }

    // ----------------------------------------------------------------------------------
    // T-335: a ConPTY run's host-input pipe belongs to the SESSION, not to the stdin writer. Closing it asks
    // conhost to end the console session (a child that has not run yet can be closed out from under itself),
    // so ending stdin delivers the console's own end-of-input gesture — Ctrl-Z + Enter — instead.
    // ----------------------------------------------------------------------------------

    [<Test>]
    member _.``finishing a ConPTY stdin delivers Ctrl-Z + Enter and leaves the session's pipe open (T-335)``() : Task =
        task {
            let pipe = new ConPtyHostInputDouble()
            let keepalive = new Native.Windows.ConPtyInputKeepalive(pipe)
            use stdin = new Native.Windows.ConPtyStdinStream(keepalive)

            do! stdin.WriteAsync([| 0x41uy |], 0, 1)
            do! stdin.FinishAsync()

            // The payload first, then the gesture: Ctrl-Z (0x1A) submits the pending console line as end of
            // input, and the carriage return is the Enter a terminal sends to commit it.
            Assert.That(pipe.Written, Is.EqualTo<byte[]>([| 0x41uy; 0x1Auy; 0x0Duy |]))
            Assert.That(stdin.IsFinished, Is.True)

            // The session's pipe is untouched by the finish — closing it would end the child's console
            // instead of its input, which is the whole point of the split.
            Assert.That(pipe.DisposeCount, Is.Zero, "finishing stdin must not close the session's host-input pipe")

            // Input after the end of input is refused, never written silently past the EOF the child saw.
            Assert.Throws<ObjectDisposedException>(Action(fun () -> stdin.Write([| 1uy |], 0, 1)))
            |> ignore

            // Idempotent, as `ProcessStdin.FinishAsync`/`PtySession.CloseStdinAsync` both promise.
            do! stdin.FinishAsync()

            Assert.That(pipe.Written.Length, Is.EqualTo 3, "a repeated finish must not deliver a second gesture")
        }

    [<Test>]
    member _.``a dropped ConPTY stdin writer leaves the session running, and teardown closes the pipe once (T-335)``
        ()
        : Task =
        task {
            let pipe = new ConPtyHostInputDouble()
            let keepalive = new Native.Windows.ConPtyInputKeepalive(pipe)

            // Dropping the writer (or never taking one, which is the same code path with no writer at all)
            // must not take the console session with it.
            let writer = new Native.Windows.ConPtyStdinStream(keepalive)
            writer.Dispose()

            Assert.That(pipe.DisposeCount, Is.Zero, "a non-owning writer must not close the session's pipe")
            Assert.That(keepalive.IsReleased, Is.False)

            // A released writer refuses further input rather than pushing bytes into a session it no longer
            // has a handle on, and its finish is quiet: the run's own teardown has been through here.
            Assert.Throws<ObjectDisposedException>(Action(fun () -> writer.Write([| 1uy |], 0, 1)))
            |> ignore

            do! writer.FinishAsync()
            Assert.That(pipe.Written, Is.Empty, "a writer the run already released has no end of input to send")

            // Child exit and the setup unwind are two independent paths onto the same pipe; the shared
            // one-shot guard means whichever arrives first is the only real close.
            keepalive.Release()
            (keepalive :> IDisposable).Dispose()
            keepalive.Release()

            Assert.That(pipe.DisposeCount, Is.EqualTo 1, "the session's host-input pipe must be closed exactly once")
            Assert.That(keepalive.IsReleased, Is.True)
        }

    [<Test>]
    member _.``finishing a ConPTY stdin whose session already ended completes quietly (T-335)``() : Task =
        task {
            // The child exited: the child-exit teardown closed the pseudoconsole and released the session's
            // pipe. There is no console left to hand an end of input to, and no child left to read one —
            // moot, not a failed delivery, exactly as closing a pipe whose peer is gone is not an error.
            let pipe = new ConPtyHostInputDouble()
            let keepalive = new Native.Windows.ConPtyInputKeepalive(pipe)
            use stdin = new Native.Windows.ConPtyStdinStream(keepalive)
            keepalive.Release()

            do! stdin.FinishAsync()

            Assert.That(stdin.IsFinished, Is.True)
            Assert.That(pipe.Written, Is.Empty)

            // The same applies when the write itself is what reports the hangup: conhost let go of the read
            // end (ERROR_BROKEN_PIPE as HRESULT_FROM_WIN32) a moment before our own teardown got there.
            let brokenPipe =
                new ConPtyHostInputDouble(Some(IOException("Pipe is broken.", 0x8007006D)))

            let liveKeepalive = new Native.Windows.ConPtyInputKeepalive(brokenPipe)
            use brokenStdin = new Native.Windows.ConPtyStdinStream(liveKeepalive)

            do! brokenStdin.FinishAsync()
            Assert.That(brokenStdin.IsFinished, Is.True)
        }

    [<Test>]
    member _.``a failed ConPTY end-of-input delivery surfaces instead of being swallowed (T-335)``() : Task =
        task {
            // A genuine write failure — not the "the console is already gone" hangup — must reach the caller:
            // a child reading to EOF would otherwise wait forever on a gesture that was silently dropped.
            let pipe =
                new ConPtyHostInputDouble(Some(IOException "the console host rejected the write"))

            let keepalive = new Native.Windows.ConPtyInputKeepalive(pipe)
            use stdin = new Native.Windows.ConPtyStdinStream(keepalive)

            let first = stdin.FinishAsync()
            let second = stdin.FinishAsync()
            Assert.That(Object.ReferenceEquals(first, second), Is.True)

            let observe (delivery: Task) =
                task {
                    try
                        do! delivery
                        return None
                    with :? IOException as ex ->
                        return Some ex.Message
                }

            let! firstError = observe first
            let! secondError = observe second

            match firstError, secondError with
            | Some firstMessage, Some secondMessage ->
                Assert.That(firstMessage, Does.Contain "the console host rejected the write")
                Assert.That(secondMessage, Is.EqualTo firstMessage)
            | _ -> Assert.Fail "every repeated FinishAsync caller must observe the delivery failure"
        }

    [<Test>]
    member _.``a ConPTY child that never reads stdin runs to its last statement (T-335)``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only ConPTY path"
            else
                // The regression this guards: with no stdin source and no `KeepStdinOpen`, the spawn used to
                // close the pseudoconsole's host-input pipe the moment the child was resumed, which asks
                // conhost to end the console session and can reach the child as a CTRL_CLOSE_EVENT before it
                // has run. The proof is the child's own EXIT CODE, not its output: a ConPTY child's captured
                // text depends on the parent's console (see the fixture note), while `exit 7` can only be
                // reached by a child that survived to its last statement. The `ping` ahead of it is the
                // window that close used to land in.
                let cmd =
                    Command.create "cmd.exe"
                    |> Command.args [ "/c"; "ping -n 3 127.0.0.1 >NUL & exit 7" ]
                    |> Command.pty
                    |> Command.timeout (TimeSpan.FromSeconds 60.0)

                match! cmd.OutputStringAsync() with
                | Error(ProcessError.Unsupported msg) when msg.Contains "1809" ->
                    // Pre-1809 host without ConPTY — the documented typed-Unsupported path (D9).
                    Assert.Ignore $"host lacks ConPTY: {msg}"
                | Error other -> Assert.Fail $"unexpected error from a ConPTY spawn: {other}"
                | Ok result ->
                    match result.Outcome with
                    | Outcome.Exited 7 -> ()
                    | other ->
                        Assert.Fail
                            $"a ConPTY child that never reads stdin must run to its last statement (exit 7), got {other}"
        }

    // ----------------------------------------------------------------------------------
    // Stage 4 (T-140): RunningProcess.ResizeAsync + PtyConfig.Echo effect
    // ----------------------------------------------------------------------------------

    [<Test>]
    member _.``ResizeAsync off a PTY validates geometry, reports Unsupported, and never consumes the handle (D6, K-031/K-016)``
        ()
        : Task =
        task {
            // A plain (non-PTY) run — cross-platform. `ResizeAsync` must reject bad geometry up front,
            // return a typed `Unsupported` (never a silent no-op), and — crucially — NOT be a consuming
            // verb: an exit-consuming `WaitAsync` still succeeds afterward (KB K-031), and the shared
            // reap-once exit wait is untouched (KB K-016).
            let baseCmd =
                if isWindows then
                    Command.create "cmd.exe" |> Command.args [ "/c"; "echo hi" ]
                else
                    Command.create "/bin/sh" |> Command.args [ "-c"; "echo hi" ]

            let cmd = baseCmd |> Command.timeout (TimeSpan.FromSeconds 30.0)

            match! runner.StartAsync(cmd, CancellationToken.None) with
            | Error e -> Assert.Fail $"spawn failed: {e}"
            | Ok running ->
                use _running = running

                // Programmer-error geometry is rejected synchronously, matching the Command.Pty builder.
                Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> running.ResizeAsync(0, 24) |> ignore))
                |> ignore

                Assert.Throws<ArgumentOutOfRangeException>(Action(fun () -> running.ResizeAsync(80, 0) |> ignore))
                |> ignore

                // Non-PTY run: a typed Unsupported, never a silent/garbled resize (D6).
                match! running.ResizeAsync(120, 40) with
                | Error(ProcessError.Unsupported msg) -> Assert.That(msg, Does.Contain "Resize")
                | Error other -> Assert.Fail $"expected ProcessError.Unsupported, got {other}"
                | Ok() -> Assert.Fail "ResizeAsync on a non-PTY run must not succeed"

                // The consuming verb still runs — proving ResizeAsync claimed no consumption (K-031) and
                // did not touch the reap-once wait path (K-016): no "already consumed by another verb".
                let! outcome = running.WaitAsync()

                match outcome with
                | Outcome.Exited _ -> ()
                | other -> Assert.Fail $"expected a clean exit after ResizeAsync, got {other}"
        }

    [<Test>]
    member _.``ResizeAsync resizes the POSIX pty and the child observes the new geometry (D6)``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only PTY spawn"
            else
                // The child blocks on `read _`, THEN prints its terminal size (`stty size` → "rows cols").
                // We `ResizeAsync` to 120x40 BEFORE unblocking `read`, so the size it reports is the
                // POST-resize winsize (ioctl(TIOCSWINSZ) on the master, shared with the slave, + SIGWINCH):
                // a "40 120" line proves the live resize actually reached the child's terminal.
                let cmd =
                    (Command.create "/bin/sh" |> Command.args [ "-c"; "read _; stty size" ]).Pty(80, 24)
                    |> Command.keepStdinOpen
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! runner.StartAsync(cmd, CancellationToken.None) with
                | Error(ProcessError.Unsupported msg) -> Assert.Ignore $"host lacks a PTY: {msg}"
                | Error other -> Assert.Fail $"unexpected error from a POSIX pty spawn: {other}"
                | Ok running ->
                    use _running = running

                    match! running.ResizeAsync(120, 40) with
                    | Ok() -> ()
                    | Error e -> Assert.Fail $"ResizeAsync on a live pty run failed: {e}"

                    // Unblock `read` so the child proceeds to `stty size` (its output — a few bytes — sits
                    // in the pty buffer until the drain below).
                    match running.TakeStdin() with
                    | Some stdin ->
                        do! stdin.WriteLineAsync ""
                        do! stdin.FlushAsync()
                    | None -> Assert.Fail "expected an interactive stdin on a KeepStdinOpen pty run"

                    let! events = collect (running.OutputEventsAsync())
                    let text = events |> Seq.map (fun e -> e.Text) |> String.concat "\n"

                    Assert.That(
                        text,
                        Does.Contain "40 120",
                        "the child's terminal must report the resized 40 rows x 120 cols, not the initial 24x80"
                    )
        }

    [<Test>]
    member _.``ResizeAsync after the run is torn down returns a typed error, never a wrong-target resize (T-203)``
        ()
        : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only PTY spawn"
            else
                // A live pty run that exits on its own. A terminal verb (`WaitAsync`) reaps it and tears the
                // run down: teardown's `closeStreams` closes the pty master fd held in `spawned.PtyControl`,
                // whose NUMBER a concurrent spawn can reuse immediately (unlike a pid). A late `ResizeAsync`
                // must therefore NOT `ioctl(TIOCSWINSZ)`/`SIGWINCH` that possibly-recycled fd/pid, but pass
                // through the same lifecycle gate the kill verbs use (T-093) and return a typed,
                // non-transient `Unsupported` — and it must not throw.
                let cmd =
                    (Command.create "/bin/sh" |> Command.args [ "-c"; "echo hi" ]).Pty(80, 24)
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! runner.StartAsync(cmd, CancellationToken.None) with
                | Error(ProcessError.Unsupported msg) -> Assert.Ignore $"host lacks a PTY: {msg}"
                | Error other -> Assert.Fail $"unexpected error from a POSIX pty spawn: {other}"
                | Ok running ->
                    use _running = running

                    // A live resize still works BEFORE teardown — proving the gate is not blocking the
                    // ordinary path (no regression of the live pty resize).
                    match! running.ResizeAsync(100, 30) with
                    | Ok() -> ()
                    | Error e -> Assert.Fail $"ResizeAsync on a live pty run must still succeed: {e}"

                    // Tear the run down through a terminal verb: reaps the child and closes the pty master fd.
                    let! outcome = running.WaitAsync()

                    match outcome with
                    | Outcome.Exited _ -> ()
                    | other -> Assert.Fail $"expected a clean exit before the post-teardown resize, got {other}"

                    // The resize now hits the lifecycle gate: a typed, non-transient Unsupported — never a
                    // wrong-target ioctl/SIGWINCH on a recycled fd/pid — and it does not throw.
                    match! running.ResizeAsync(120, 40) with
                    | Error(ProcessError.Unsupported _) -> ()
                    | Error other -> Assert.Fail $"expected ProcessError.Unsupported after teardown, got {other}"
                    | Ok() -> Assert.Fail "ResizeAsync after teardown must not succeed (wrong-target fd/pid risk)"
        }

    [<Test>]
    member _.``ResizeAsync racing teardown never throws and stays typed (T-203, R-01 concurrent window)``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only PTY spawn"
            else
                // The sequential test above proves a resize AFTER teardown is refused. This one targets the
                // CONCURRENT window R-01 flagged: `Teardown` both raises `runTornDown` and closes the pty master
                // fd, and a resize that takes `sync` BETWEEN those two steps must NEVER `ioctl`/`SIGWINCH` a
                // closed-and-recycled fd. The fix raises the flag BEFORE `closeStreams`, so a resize racing
                // teardown must always resolve to a well-typed `Result` — `Ok` while the pty is live, or a typed
                // `Unsupported`/`Io` once torn down — and must never throw. We hammer resize in a tight loop
                // while a terminal verb reaps the run, across many iterations, so the race is actually hit.
                let mutable spawned = 0

                for _ in 1..30 do
                    let cmd =
                        (Command.create "/bin/sh" |> Command.args [ "-c"; "echo hi" ]).Pty(80, 24)
                        |> Command.timeout (TimeSpan.FromSeconds 30.0)

                    match! runner.StartAsync(cmd, CancellationToken.None) with
                    | Error(ProcessError.Unsupported _) -> ()
                    | Error other -> Assert.Fail $"unexpected error from a POSIX pty spawn: {other}"
                    | Ok running ->
                        spawned <- spawned + 1
                        use _running = running
                        use stop = new CancellationTokenSource()

                        // Spin resize concurrently with the teardown below (on the thread pool, so it truly
                        // races `WaitAsync`'s reapGuard). Every result must be a typed `Result` — never a raised
                        // exception, and never a garbled `Ok` produced by an `ioctl` on a torn-down fd.
                        let resizeLoop =
                            Task.Run(fun () ->
                                let loop =
                                    task {
                                        while not stop.IsCancellationRequested do
                                            match! running.ResizeAsync(100, 30) with
                                            | Ok()
                                            | Error(ProcessError.Unsupported _)
                                            | Error(ProcessError.Io _) -> ()
                                            | Error other ->
                                                Assert.Fail
                                                    $"resize racing teardown returned an unexpected error: {other}"
                                    }

                                loop :> Task)

                        // Reap the run — its `reapGuard` runs `Teardown` (flag-then-`closeStreams`) — while the
                        // resize loop hammers the same pty. Then stop the loop and observe any escaped failure.
                        let! _ = running.WaitAsync()
                        stop.Cancel()
                        do! resizeLoop

                Assert.That(spawned, Is.GreaterThan 0, "at least one pty run must have spawned to exercise the race")
        }

    [<Test>]
    member _.``Pty with Echo=false keeps a fed credential out of the captured output (secret-safety)``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only PTY spawn"
            else
                // The secret-invariant round-trip: `read pw` consumes the fed line SILENTLY (the child
                // itself never prints it — it only prints "done"), so the ONLY path by which `secret` could
                // reach the CAPTURED merged output is the terminal's cooked-mode ECHO. Echo=false clears the
                // pty slave's termios ECHO bit at spawn, so the fed credential must NOT appear.
                let secret = "hunter2-SECRET-should-not-echo"

                let cmd =
                    (Command.create "/bin/sh" |> Command.args [ "-c"; "read pw; printf 'done\\n'" ])
                        .Pty({ Cols = 80; Rows = 24; Echo = false })
                    |> Command.stdin (Stdin.FromString(secret + "\n"))
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! cmd.OutputStringAsync() with
                | Error(ProcessError.Unsupported msg) -> Assert.Ignore $"host lacks a PTY: {msg}"
                | Error other -> Assert.Fail $"unexpected error from a POSIX pty spawn: {other}"
                | Ok result ->
                    Assert.That(result.Stdout, Does.Contain "done", "the child must have read the fed credential line")

                    Assert.That(
                        result.Stdout,
                        Does.Not.Contain secret,
                        "Echo=false must keep the fed credential out of the captured merged output (secret-safety)"
                    )
        }

    [<Test>]
    member _.``Pty with the default cooked echo reflects fed input into the captured output``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only PTY spawn"
            else
                // The deliberate contrast to the Echo=false secret test: with the OS cooked-mode default
                // (echo on), the pty line discipline DOES echo fed input back into the captured output —
                // proving the terminal really echoes by default, so Echo=false's suppression above is a
                // genuine effect and not a coincidental no-op.
                let marker = "echoed-marker-xyz"

                let cmd =
                    (Command.create "/bin/sh" |> Command.args [ "-c"; "read line; printf 'done\\n'" ]).Pty(80, 24) // echo on (the ratified default)
                    |> Command.stdin (Stdin.FromString(marker + "\n"))
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! cmd.OutputStringAsync() with
                | Error(ProcessError.Unsupported msg) -> Assert.Ignore $"host lacks a PTY: {msg}"
                | Error other -> Assert.Fail $"unexpected error from a POSIX pty spawn: {other}"
                | Ok result ->
                    Assert.That(
                        result.Stdout,
                        Does.Contain marker,
                        "with cooked-mode echo on (the default), fed input is echoed into the captured output"
                    )
        }

    [<Test>]
    member _.``a PTY bulk stdin source with no trailing newline still ends the child's input (T-332)``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only PTY spawn"
            else
                // `cat` reads until EOF and the payload carries NO trailing newline, so this run can only
                // finish if draining the source delivers a real terminal end of input: the first
                // end-of-input character hands the unterminated line to `cat`, the second ends its input.
                // Without one the child waits forever and the run is killed by its timeout instead.
                // Echo=false so the payload can only reach the captured output by way of `cat` itself,
                // never the terminal's own echo of the input.
                let payload = "unterminated"

                let cmd =
                    (Command.create "/bin/cat").Pty({ Cols = 80; Rows = 24; Echo = false })
                    |> Command.stdin (Stdin.FromString payload)
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! cmd.OutputStringAsync() with
                | Error(ProcessError.Unsupported msg) -> Assert.Ignore $"host lacks a PTY: {msg}"
                | Error other -> Assert.Fail $"unexpected error from a POSIX pty spawn: {other}"
                | Ok result ->
                    Assert.That(
                        result.Stdout,
                        Does.Contain payload,
                        "the unterminated payload must have reached the child, which copies it back"
                    )

                    match result.Outcome with
                    | Outcome.Exited 0 -> ()
                    | other -> Assert.Fail $"the child should have seen EOF and exited cleanly, got {other}"
        }

    [<Test>]
    member _.``a ConPTY bulk stdin source ends the child's input with the console gesture (T-335)``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only ConPTY path"
            else
                // The Windows counterpart of the POSIX bulk-stdin test above. `copy con` is the canonical
                // console reader that ends only at the console's own end of input, so this run can finish at
                // all only if draining the source delivers the Ctrl-Z + Enter gesture through the
                // pseudoconsole; without it the child reads forever and the run ends as a timeout kill
                // instead (confirmed by deleting the gesture bytes: the same child then burns its whole
                // timeout). The payload carries no trailing newline, so the gesture also has to submit the
                // line the child is holding.
                //
                // The EXIT CODE is the assertion, not the captured text: a ConPTY child's text capture
                // depends on the parent's console (see the fixture note), while `exit 7` can only be reached
                // by a child whose `copy con` actually saw end of input.
                let cmd =
                    Command.create "cmd.exe"
                    |> Command.args [ "/c"; "copy con NUL & exit 7" ]
                    |> Command.pty
                    |> Command.stdin (Stdin.FromString "conpty-end-of-input")
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! cmd.OutputStringAsync() with
                | Error(ProcessError.Unsupported msg) when msg.Contains "1809" ->
                    // Pre-1809 host without ConPTY — the documented typed-Unsupported path (D9).
                    Assert.Ignore $"host lacks ConPTY: {msg}"
                | Error other -> Assert.Fail $"unexpected error from a ConPTY spawn: {other}"
                | Ok result ->
                    match result.Outcome with
                    | Outcome.Exited 7 -> ()
                    | other ->
                        Assert.Fail $"draining the stdin source must end the ConPTY child's input (exit 7), got {other}"
        }

    [<Test>]
    member _.``an interactive ConPTY stdin ends the child's input on FinishAsync (T-335)``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only ConPTY path"
            else
                // The interactive half of the same contract: the writer handed out by `TakeStdin` sends a
                // line and then ends the child's input through `ProcessStdin.FinishAsync`, which must reach
                // `copy con` as end of input rather than closing the console session under it. A second
                // finish is a no-op and a write afterwards is refused.
                let cmd =
                    Command.create "cmd.exe"
                    |> Command.args [ "/c"; "copy con NUL & exit 7" ]
                    |> Command.pty
                    |> Command.keepStdinOpen
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! runner.StartAsync(cmd, CancellationToken.None) with
                | Error(ProcessError.Unsupported msg) when msg.Contains "1809" ->
                    // Pre-1809 host without ConPTY — the documented typed-Unsupported path (D9).
                    Assert.Ignore $"host lacks ConPTY: {msg}"
                | Error other -> Assert.Fail $"unexpected error from a ConPTY spawn: {other}"
                | Ok running ->
                    use _running = running

                    match running.TakeStdin() with
                    | None -> Assert.Fail "a KeepStdinOpen ConPTY run must hand out an interactive stdin"
                    | Some stdin ->
                        do! stdin.WriteAsync(Text.Encoding.UTF8.GetBytes "conpty-interactive")
                        do! stdin.FinishAsync()
                        // Idempotent, and no further input can trail past the end of input the child saw.
                        do! stdin.FinishAsync()

                        Assert.ThrowsAsync<ObjectDisposedException>(
                            Func<Task>(fun () -> stdin.WriteAsync(Text.Encoding.UTF8.GetBytes "late"))
                        )
                        |> ignore

                    let! outcome = running.WaitAsync()

                    match outcome with
                    | Outcome.Exited 7 -> ()
                    | other -> Assert.Fail $"FinishAsync must end the ConPTY child's input (exit 7), got {other}"
        }

    [<Test>]
    member _.``Windows ConPTY ProcessStdin WriteLineAsync submits Enter to Console ReadLine``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only ConPTY path"
            else
                // Same style as the T-335 tests above (R-01): the verdict is the EXIT CODE, reachable only if
                // the cooked console reader actually saw the line "hello" terminated by Enter — i.e. only if
                // `WriteLineAsync`'s CR terminator was accepted as Enter. No dependency on captured stdout, and
                // no skip gate on the outcome itself (removed per R-01) — the only legitimate skip is the
                // pre-1809 no-ConPTY host, handled below before any data is sent.
                //
                // The reader has to be `cmd.exe`'s `set /p ... <CON`, not a managed line reader
                // (`[Console]::In.ReadLine()`, `Read-Host`): investigating this finding's original failure
                // (Exited 9, empty line) traced it to a Windows/ConPTY host-process quirk, unrelated to the CR
                // terminator this task changes — a managed process's inherited `STD_INPUT_HANDLE` under this
                // spawn does not resolve to the pseudoconsole's console (confirmed: `GetConsoleMode` on it
                // fails), so anything reading through that handle (`[Console]::In`, and even `cmd.exe`'s own
                // `set /p` without `<CON`) sees no input at all before any terminator byte is even in play. The
                // T-335 tests above sidestep this the same way `copy con` always has: by opening the console
                // device explicitly (`CON`) rather than trusting the inherited standard handle. `<CON` applies
                // that same, already-proven-reliable path to a genuine Enter-terminated line read, which is
                // exactly the contract this test exists to prove. A batch file (not an inline `cmd.exe /c`
                // one-liner) avoids `%line%`'s parse-time (not run-time) expansion inside a single compound
                // command line — an unrelated batch-scripting quirk, not a ConPTY one.
                // R-02: `Command.args` only applies Windows argv-quoting, not cmd.exe metacharacter
                // escaping — quotes do not stop cmd.exe's own command-line parser from acting on `&`,
                // `|`, `^`, `<`, `>`, `%`, or `!` before the quoted argument ever reaches the target
                // exe. A `IO.Path.GetTempPath()` containing one of those would make this test either
                // fail to run the intended batch file or, worse, run an attacker-shaped command line.
                // Document and enforce the assumption instead of trusting it silently: skip rather than
                // give a false pass/fail on a host whose TEMP path violates it.
                let tempPath = IO.Path.GetTempPath()
                let cmdMetacharacters = [| '&'; '|'; '^'; '<'; '>'; '%'; '!'; '"' |]

                if tempPath |> Seq.exists (fun c -> Array.contains c cmdMetacharacters) then
                    Assert.Ignore
                        $"IO.Path.GetTempPath() ({tempPath}) contains a cmd.exe metacharacter; this test assumes a TEMP path free of shell metacharacters for its `cmd.exe /c <path>` invocation"

                let batchFile =
                    IO.Path.Combine(tempPath, "conpty-writeline-" + Guid.NewGuid().ToString("N") + ".cmd")

                IO.File.WriteAllLines(
                    batchFile,
                    [| "@echo off"
                       "set \"line=\""
                       "set /p line=<CON"
                       "if errorlevel 1 goto :fail"
                       "if not \"%line%\"==\"hello\" goto :fail"
                       "exit 7"
                       ":fail"
                       "exit 9" |]
                )

                try
                    let command =
                        Command.create "cmd.exe"
                        |> Command.args [ "/c"; batchFile ]
                        |> Command.pty
                        |> Command.keepStdinOpen
                        |> Command.timeout (TimeSpan.FromSeconds 30.0)

                    match! runner.StartAsync(command, CancellationToken.None) with
                    | Error(ProcessError.Unsupported message) when message.Contains "1809" ->
                        Assert.Ignore $"host lacks ConPTY: {message}"
                    | Error other -> Assert.Fail $"unexpected error from a ConPTY spawn: {other}"
                    | Ok running ->
                        use running = running

                        match running.TakeStdin() with
                        | None -> Assert.Fail "a KeepStdinOpen ConPTY run must hand out an interactive stdin"
                        | Some stdin -> do! stdin.WriteLineAsync "hello"

                        let! outcome = running.WaitAsync()

                        match outcome with
                        | Outcome.Exited 7 -> ()
                        | other ->
                            Assert.Fail
                                $"WriteLineAsync's CR terminator did not submit Enter to the cooked console reader, got {other}"
                finally
                    try
                        IO.File.Delete batchFile
                    with :? IOException ->
                        // Best-effort cleanup of the scratch batch file; a leaked temp file does not affect
                        // correctness of this or later runs (each run gets a fresh GUID-named file).
                        ()
        }

    [<Test>]
    member _.``a ConPTY stdin whose FromFile source cannot be opened still ends the child's input (F-17)``() : Task =
        task {
            // The eager `StdinSource.File` open runs at spawn, before any feed exists, so its failure IS where
            // this run's bulk delivery ends. Dropping the writer there delivers nothing — the view owns no
            // handle, the session's pipe stays open by design, and a child reading to EOF would wait forever.
            let pipe = new ConPtyHostInputDouble()
            let keepalive = new Native.Windows.ConPtyInputKeepalive(pipe)
            use stdin = new Native.Windows.ConPtyStdinStream(keepalive)

            let feeder =
                Pump.feedStdinSource (Some(stdin :> Stream)) (Some(Stdin.FromFile(missingStdinPath ()))) false

            let! fault = feeder.Task

            // The source failure stays the primary cause the terminal verb reports...
            match fault with
            | Some ex -> Assert.That(ex, Is.InstanceOf<FileNotFoundException>())
            | None -> Assert.Fail "a source that could not be opened must still stash its failure"

            // ...and the child's console saw the end of input regardless, through a pipe the session keeps.
            Assert.That(pipe.Written, Is.EqualTo<byte[]>([| 0x1Auy; 0x0Duy |]))
            Assert.That(stdin.IsFinished, Is.True)

            Assert.That(
                pipe.DisposeCount,
                Is.Zero,
                "the session's host-input pipe must outlive a source that could not be opened"
            )
        }

    [<Test>]
    member _.``a pty stdin whose FromFile source cannot be opened still ends the child's input (F-17)``() : Task =
        task {
            // The POSIX half of the same seam. This view is non-owning too, so closing it hands the line
            // discipline no end of input — only the terminal's own end-of-input character does.
            let written = ResizeArray<byte>()
            recordPtyWrites written 64

            try
                let stdin = ptyStdinForTests 4uy

                let feeder =
                    Pump.feedStdinSource (Some(stdin :> Stream)) (Some(Stdin.FromFile(missingStdinPath ()))) false

                let! fault = feeder.Task

                match fault with
                | Some ex -> Assert.That(ex, Is.InstanceOf<FileNotFoundException>())
                | None -> Assert.Fail "a source that could not be opened must still stash its failure"

                Assert.That(written.ToArray(), Is.EqualTo<byte[]>([| 4uy; 4uy |]))
                Assert.That(stdin.IsFinished, Is.True)
            finally
                Native.Posix.ptyWriteForTests <- None
        }

    [<Test>]
    member _.``a KeepStdinOpen run keeps its PTY stdin after a FromFile source cannot be opened (F-17)``() : Task =
        task {
            // `KeepStdinOpen` means the caller keeps writing and ends the child's input itself, so a source
            // that could not be opened must neither send that unrepeatable gesture on the caller's behalf nor
            // take away the very stream the caller needs to send it.
            let pipe = new ConPtyHostInputDouble()
            let keepalive = new Native.Windows.ConPtyInputKeepalive(pipe)
            use stdin = new Native.Windows.ConPtyStdinStream(keepalive)

            let feeder =
                Pump.feedStdinSource (Some(stdin :> Stream)) (Some(Stdin.FromFile(missingStdinPath ()))) true

            let! fault = feeder.Task

            Assert.That(fault.IsSome, Is.True, "the source failure is reported whether or not stdin is kept open")
            Assert.That(stdin.IsFinished, Is.False)
            Assert.That(pipe.Written, Is.Empty)

            // The interactive stdin still works, and the caller's own finish is what ends the child's input.
            do! stdin.WriteAsync([| 0x41uy |], 0, 1)
            do! stdin.FinishAsync()
            Assert.That(pipe.Written, Is.EqualTo<byte[]>([| 0x41uy; 0x1Auy; 0x0Duy |]))
        }

    [<Test>]
    member _.``a PTY run whose FromFile source is missing reports Stdin instead of hanging (F-17)``() : Task =
        task {
            if not isLinux then
                Assert.Ignore "Linux-only PTY spawn"
            else
                // `cat` reads until EOF and the source failed before one byte could be written, so this run
                // can finish at all only if that failure still delivers the terminal's end of input. Without
                // one the child waits forever and the verb reports the timeout kill instead of the real cause.
                let cmd =
                    (Command.create "/bin/cat").Pty({ Cols = 80; Rows = 24; Echo = false })
                    |> Command.stdin (Stdin.FromFile(missingStdinPath ()))
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! cmd.OutputStringAsync() with
                | Error(ProcessError.Unsupported msg) -> Assert.Ignore $"host lacks a PTY: {msg}"
                | Error(ProcessError.Stdin _) -> ()
                | Error other -> Assert.Fail $"a missing PTY stdin source must report ProcessError.Stdin, got {other}"
                | Ok result ->
                    // Before F-17 this is where a hung child landed: the timeout kill is honest data, so the
                    // verb answered `Ok`/`TimedOut` a whole timeout late, with the source failure lost.
                    Assert.Fail $"a missing stdin source must not pass through as a success, got {result.Outcome}"
        }

    [<Test>]
    member _.``a ConPTY run whose FromFile source is missing reports Stdin instead of hanging (F-17)``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only ConPTY path"
            else
                // The Windows counterpart: `copy con` ends only at the console's own end of input, so the
                // child exits (and the verb answers with the honest source failure) only if the failed source
                // still delivers the Ctrl-Z + Enter gesture. Closing the writer cannot: it owns no handle.
                let cmd =
                    Command.create "cmd.exe"
                    |> Command.args [ "/c"; "copy con NUL & exit 0" ]
                    |> Command.pty
                    |> Command.stdin (Stdin.FromFile(missingStdinPath ()))
                    |> Command.timeout (TimeSpan.FromSeconds 30.0)

                match! cmd.OutputStringAsync() with
                | Error(ProcessError.Unsupported msg) when msg.Contains "1809" ->
                    // Pre-1809 host without ConPTY — the documented typed-Unsupported path (D9).
                    Assert.Ignore $"host lacks ConPTY: {msg}"
                | Error(ProcessError.Stdin _) -> ()
                | Error other ->
                    Assert.Fail $"a missing ConPTY stdin source must report ProcessError.Stdin, got {other}"
                | Ok result ->
                    // Before F-17 this is where a hung child landed: the timeout kill is honest data, so the
                    // verb answered `Ok`/`TimedOut` a whole timeout late, with the source failure lost.
                    Assert.Fail $"a missing stdin source must not pass through as a success, got {result.Outcome}"
        }

    // ----------------------------------------------------------------------------------
    // The default (no PTY) path is unchanged: separate stdout/stderr, exactly as before.
    // ----------------------------------------------------------------------------------

    [<Test>]
    member _.``Without Pty the default path keeps stdout and stderr separate (D1/D2)``() : Task =
        task {
            let script =
                if isWindows then
                    "echo out-marker&echo err-marker 1>&2"
                else
                    "echo out-marker; echo err-marker >&2"

            let cmd =
                if isWindows then
                    Command.create "cmd.exe" |> Command.args [ "/c"; script ]
                else
                    Command.create "/bin/sh" |> Command.args [ "-c"; script ]

            match! cmd.OutputStringAsync() with
            | Ok result ->
                Assert.That(result.Stdout, Does.Contain "out-marker")
                Assert.That(result.Stderr, Does.Contain "err-marker")
            | Error e -> Assert.Fail $"a plain (no-PTY) run should still capture separate streams: {e}"
        }
