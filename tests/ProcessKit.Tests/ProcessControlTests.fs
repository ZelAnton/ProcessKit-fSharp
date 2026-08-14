namespace ProcessKit.Tests

open System
open System.Diagnostics
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open ProcessKit
open ProcessKit.Native

/// Windows-only helpers for the suspend/resume delivery-failure tests. `openLikeProduction` opens exactly
/// the least-privilege handle the production walk opens, so a test that takes over the walk's open seam
/// can still hand it a REAL handle for a REAL member (one `IsProcessInJob` confirms and `NtSuspendProcess`
/// accepts) while answering for a member that has vanished. The walk closes every handle it is given, so
/// these are never closed here.
module private WindowsMemberControlInterop =

    [<Literal>]
    let PROCESS_SUSPEND_RESUME = 0x0800u

    [<Literal>]
    let PROCESS_QUERY_LIMITED_INFORMATION = 0x1000u

    /// `OpenProcess`'s "this pid does not exist" answer — the only open failure that PROVES a member is
    /// gone, and so the only one the walk may treat as a benign no-op.
    [<Literal>]
    let ERROR_INVALID_PARAMETER = 87

    /// An open refused WITHOUT proving the process is gone; the walk must not read it as a success.
    [<Literal>]
    let ERROR_ACCESS_DENIED = 5

    /// STATUS_ACCESS_DENIED (0xC0000022): a genuine native refusal — deliberately NOT
    /// STATUS_PROCESS_IS_TERMINATING (0xC000010A), which means the member is already on its way out.
    [<Literal>]
    let STATUS_ACCESS_DENIED = -1073741790

    /// STATUS_PROCESS_IS_TERMINATING (0xC000010A): the member exited between the membership check and the
    /// native call — nothing left to freeze or thaw, so a successful no-op rather than a failure.
    [<Literal>]
    let STATUS_PROCESS_IS_TERMINATING = -1073741558

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern nativeint OpenProcess(uint32 dwDesiredAccess, bool bInheritHandle, uint32 dwProcessId)

    let openLikeProduction (pid: int) : Result<nativeint, int> =
        let handle =
            OpenProcess(PROCESS_SUSPEND_RESUME ||| PROCESS_QUERY_LIMITED_INFORMATION, false, uint32 pid)

        if handle = IntPtr.Zero then
            Error(Marshal.GetLastWin32Error())
        else
            Ok handle

    /// Make a hard-kill seam (`Windows.terminateProcessHook` / `Windows.terminateJobObjectHook`) refuse the
    /// way Windows refuses, last-error included — production reads it back with `Marshal.GetLastWin32Error`.
    let refuseTermination () =
        Marshal.SetLastPInvokeError ERROR_ACCESS_DENIED
        false

    /// Start a process, let it exit with `exitCode`, and hand back the (still open, still queryable)
    /// handle to the corpse — the idempotent "the target beat us to it" case every kill verb must accept.
    let startAndAwaitExit (exitCode: int) : Process =
        let psi = ProcessStartInfo("cmd.exe", $"/c exit {exitCode}")
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true

        match Process.Start psi with
        | null -> failwith "failed to start the test process"
        | p ->
            p.WaitForExit()
            p

    /// Fill a `JOBOBJECT_BASIC_PROCESS_ID_LIST` buffer with `pids` — the shape
    /// `Windows.queryInformationJobObjectHook` must produce for `membersWindows`.
    let writeMemberList (buffer: nativeint) (pids: int list) =
        Marshal.WriteInt32(buffer, 0, pids.Length) // NumberOfAssignedProcesses
        Marshal.WriteInt32(buffer, 4, pids.Length) // NumberOfProcessIdsInList

        pids
        |> List.iteri (fun i pid -> Marshal.WriteIntPtr(buffer, 8 + i * IntPtr.Size, nativeint pid))

        struct (true, 0)

[<TestFixture>]
type ProcessControlTests() =

    let isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows
    let isMacOs = RuntimeInformation.IsOSPlatform OSPlatform.OSX

    let shell (script: string) =
        if isWindows then
            Command.create "cmd.exe" |> Command.args [ "/c"; script ]
        else
            Command.create "/bin/sh" |> Command.args [ "-c"; script ]

    // Sleeps ~3s; killed well before that by every test here.
    // Sporadic ping.exe/whoami.exe STATUS_DLL_INIT_FAILED errors under the Codex workspace-write sandbox
    // are a known host-environment behavior, not a ProcessKit bug. TestHostSetUp suppresses only the
    // inherited modal hard-error dialog; see K-119 in the local project knowledge base.
    let sleeper =
        if isWindows then
            shell "ping -n 4 127.0.0.1 >nul"
        else
            shell "sleep 3"

    let create () =
        match ProcessGroup.Create() with
        | Ok group -> group
        | Error error -> failwith $"ProcessGroup.Create failed: {error}"

    // Start a childless, long-running EXTERNAL process (started OUTSIDE ProcessKit) suitable for
    // `ProcessGroup.Adopt`. `ping -n N` (Windows) and `sleep N` (POSIX) both outlast every test here and
    // fork no children, so the adopted process itself is the entire tree — killing it is the whole effect.
    let startExternalSleeper () : Process =
        let psi =
            if isWindows then
                let p = ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
                // Swallow ping's slow, tiny output into an unread pipe so it neither spams the test host
                // nor (in the few seconds before the group kills it) fills the buffer and blocks.
                p.RedirectStandardOutput <- true
                p
            else
                ProcessStartInfo("/bin/sh", "-c \"sleep 30\"")

        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true

        match Process.Start psi with
        | null -> failwith "failed to start the external test process"
        | p -> p

    [<Test>]
    member _.``Members lists a child started into the group``() : Task =
        task {
            use group = create ()

            match! group.StartAsync sleeper with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let members =
                    match group.Members() with
                    | Ok pids -> pids
                    | Error error -> failwith $"Members failed: {error}"

                Assert.That(members, Is.Not.Empty)

                match running.Pid with
                | Some pid -> Assert.That(members, Does.Contain pid)
                | None -> Assert.Fail "expected a pid"

                running.Kill()
                let! _ = running.WaitAsync()
                ()
        }
        :> Task

    [<Test>]
    member _.``Signal delivers to the group (Kill on Windows, Term on POSIX)``() : Task =
        task {
            use group = create ()

            match! group.StartAsync sleeper with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                if isWindows then
                    match group.Signal Signal.Term with
                    | Error(ProcessError.Unsupported _) -> ()
                    | other -> Assert.Fail $"expected Unsupported for Term on Windows, got {other}"

                    match group.Signal Signal.Kill with
                    | Ok() -> ()
                    | Error error -> Assert.Fail $"{error}"

                    let! outcome = running.WaitAsync()
                    Assert.That(outcome.IsExited, Is.True)
                else
                    match group.Signal Signal.Term with
                    | Ok() -> ()
                    | Error error -> Assert.Fail $"{error}"

                    let! outcome = running.WaitAsync()

                    match outcome with
                    | Outcome.Signalled _ -> Assert.Pass()
                    | other -> Assert.Fail $"expected Signalled, got {other}"
        }
        :> Task

    [<Test>]
    member _.``Signal with an invalid raw number returns Error on POSIX``() : Task =
        task {
            if isWindows then
                Assert.Ignore "Signal.Other's errno-aware failure path is POSIX-only."
            else
                use group = create ()

                match! group.StartAsync sleeper with
                | Error error -> Assert.Fail $"{error}"
                | Ok running ->
                    // Signal numbers are conventionally 1..31/64; a wildly out-of-range value is
                    // rejected by the kernel (EINVAL), which must now surface honestly instead of a
                    // silently-swallowed `Ok()`.
                    match group.Signal(Signal.Other 999) with
                    | Error(ProcessError.Io _) -> ()
                    | other -> Assert.Fail $"expected Error(ProcessError.Io), got {other}"

                    running.Kill()
                    let! _ = running.WaitAsync()
                    ()
        }
        :> Task

    [<Test>]
    member _.``Signal.Other 0 (and a negative) no longer reports a false success on POSIX``() : Task =
        task {
            if isWindows then
                Assert.Ignore "Signal.Other is always Unsupported on Windows; the false-success was POSIX-only."
            else
                use group = create ()

                match! group.StartAsync sleeper with
                | Error error -> Assert.Fail $"{error}"
                | Ok running ->
                    // Signal 0 is a liveness probe that delivers nothing; with a LIVE child in the group it
                    // used to return Ok() (a false delivery). It must now be a typed error, not a silent
                    // success — the honesty regression this task closes.
                    match group.Signal(Signal.Other 0) with
                    | Error(ProcessError.Unsupported _) -> ()
                    | other -> Assert.Fail $"expected Unsupported for Signal.Other 0, got {other}"

                    // A negative number is likewise not a signal, so it is refused the same way.
                    match group.Signal(Signal.Other(-1)) with
                    | Error(ProcessError.Unsupported _) -> ()
                    | other -> Assert.Fail $"expected Unsupported for Signal.Other -1, got {other}"

                    running.Kill()
                    let! _ = running.WaitAsync()
                    ()
        }
        :> Task

    [<Test>]
    member _.``ProcessGroupBackend refuses Signal.Other 0 and negatives before any delivery``() =
        if isWindows then
            Assert.Ignore "The POSIX process-group backend is not used on Windows."

        let backend: IContainmentBackend = ProcessGroupBackend()

        // The guard sits at the API boundary (before the per-pgid delivery loop), so it rejects a probe
        // even on an empty group — no child is needed to prove the false success is gone.
        for raw in [ 0; -1 ] do
            match backend.Signal(Signal.Other raw) with
            | Error(ProcessError.Unsupported _) -> ()
            | other -> Assert.Fail $"expected Unsupported for Signal.Other {raw}, got {other}"

    [<Test>]
    member _.``CgroupBackend refuses Signal.Other 0 and negatives before any delivery``() =
        if isWindows || isMacOs then
            Assert.Ignore "The cgroup v2 backend is Linux-only."

        // The guard short-circuits before cgroup.procs is ever read, so a placeholder path never matters.
        let backend: IContainmentBackend =
            CgroupBackend "/nonexistent/processkit-signal-guard-probe"

        for raw in [ 0; -1 ] do
            match backend.Signal(Signal.Other raw) with
            | Error(ProcessError.Unsupported _) -> ()
            | other -> Assert.Fail $"expected Unsupported for Signal.Other {raw}, got {other}"

    [<Test>]
    member _.``signalProcessGroup refuses a non-deliverable number without a false Delivered``() =
        if isWindows then
            Assert.Ignore "killpg-based process-group signalling is POSIX-only."

        // The guard returns before killpg, so the pgid is never actually signalled — any value stands in.
        // The primitive must report DeliveryFailed, never the Delivered that killpg(pgid, 0)'s success
        // return would otherwise yield (the false success this fixes, one layer below the backend).
        for raw in [ 0; -1 ] do
            match Native.Posix.signalProcessGroup 999999 raw with
            | Native.Common.SignalDelivery.DeliveryFailed _ -> ()
            | other -> Assert.Fail $"expected DeliveryFailed for signal {raw}, got {other}"

    [<Test>]
    member _.``Signal on an empty/concluded group still returns Ok``() : Task =
        task {
            if isWindows then
                Assert.Ignore "This exercises the POSIX best-effort ESRCH path specifically."
            else
                use group = create ()

                // No child was ever started: the group's member set is empty, so delivery has
                // nothing to fail on — a vacuous broadcast is a success, not an error.
                match group.Signal Signal.Term with
                | Ok() -> ()
                | Error error -> Assert.Fail $"expected Ok for an empty group, got {error}"
        }
        :> Task

    [<Test>]
    member _.``Windows: Signal.Int/Term without WindowsCtrlSignals is honest Unsupported``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Console CTRL-event delivery is a Windows-only concern."
            else
                use group = create ()

                // The child was NOT started with WindowsCtrlSignals(), so it is not in its own console
                // process group and no CTRL+BREAK can reach it. Both soft signals must fail honestly —
                // never silently downgrade to the Job kill.
                match! group.StartAsync sleeper with
                | Error error -> Assert.Fail $"{error}"
                | Ok running ->
                    for soft in [ Signal.Int; Signal.Term ] do
                        match group.Signal soft with
                        | Error(ProcessError.Unsupported _) -> ()
                        | other ->
                            Assert.Fail $"expected Unsupported for {soft} without WindowsCtrlSignals, got {other}"

                    running.Kill()
                    let! _ = running.WaitAsync()
                    ()
        }
        :> Task

    [<Test>]
    member _.``Windows: CTRL+BREAK rejects non-positive process groups before the native API``() =
        if not isWindows then
            Assert.Ignore "Console CTRL-event delivery is a Windows-only concern."

        let original = Windows.generateConsoleCtrlEventHook
        let mutable invoked = false

        try
            Windows.generateConsoleCtrlEventHook <-
                fun _ ->
                    invoked <- true
                    true

            for processGroupId in [ 0; -1 ] do
                match Windows.sendConsoleCtrlBreakWindows processGroupId with
                | Error _ -> ()
                | Ok() -> Assert.Fail $"expected non-positive group id {processGroupId} to be rejected"

            Assert.That(invoked, Is.False, "an invalid process group id reached GenerateConsoleCtrlEvent")
        finally
            Windows.generateConsoleCtrlEventHook <- original

    [<Test>]
    member _.``Windows: GetProcessId failure leaves a ctrl child unregistered and without a pid``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Console CTRL-event delivery is a Windows-only concern."

            let original = Windows.getProcessIdHook

            try
                // `GetProcessId` returns zero on failure. The child remains contained by its Job, but it
                // must not be registered for CTRL+BREAK because group zero broadcasts to this console.
                Windows.getProcessIdHook <- fun _ -> 0u
                use group = create ()

                let consoleChild =
                    (Command.create "ping" |> Command.args [ "-n"; "30"; "127.0.0.1" ])
                        .WindowsCtrlSignals()
                        .Stdout(StdioMode.Null)
                        .Timeout(TimeSpan.FromSeconds 15.0)

                match! group.StartAsync consoleChild with
                | Error error -> Assert.Fail $"{error}"
                | Ok running ->
                    Assert.That(running.Pid, Is.EqualTo None, "GetProcessId failure must not become Some 0")

                    match group.Signal Signal.Int with
                    | Error(ProcessError.Unsupported _) -> ()
                    | other -> Assert.Fail $"expected no CTRL-capable child after GetProcessId failure, got {other}"

                    running.Kill()
                    let! _ = running.WaitAsync()
                    ()
            finally
                Windows.getProcessIdHook <- original
        }
        :> Task

    [<Test>]
    member _.``Windows: Signal.Int stops a console child started with WindowsCtrlSignals``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Console CTRL-event delivery is a Windows-only concern."
            else
                use group = create ()

                // A console child spawned in its OWN process group (CREATE_NEW_PROCESS_GROUP). The 15s
                // timeout bounds the run so a delivery miss surfaces as TimedOut rather than hanging.
                let consoleChild =
                    (Command.create "ping" |> Command.args [ "-n"; "30"; "127.0.0.1" ])
                        .WindowsCtrlSignals()
                        .Stdout(StdioMode.Null)
                        .Timeout(TimeSpan.FromSeconds 15.0)

                match! group.StartAsync consoleChild with
                | Error error -> Assert.Fail $"{error}"
                | Ok running ->
                    match group.Signal Signal.Int with
                    | Ok() ->
                        let! outcome = running.WaitAsync()

                        match outcome with
                        | Outcome.Exited _ -> Assert.Pass()
                        | Outcome.TimedOut ->
                            // The event was generated but the child never received it — best-effort
                            // delivery needs the child to actually share the caller's console, which
                            // some test hosts do not provide. Not a code defect.
                            Assert.Ignore
                                "CTRL+BREAK was generated but the child did not share the caller's console (best-effort)."
                        | other -> Assert.Fail $"unexpected outcome {other}"
                    | Error(ProcessError.Unsupported _) ->
                        // No console to share in this environment — the honest best-effort outcome.
                        running.Kill()
                        let! _ = running.WaitAsync()
                        Assert.Ignore "The test host has no console to share with the child (best-effort)."
                    | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Suspend then Resume leaves the process able to complete``() : Task =
        task {
            use group = create ()

            // Sleeps briefly then prints; a 10s timeout turns a failed Resume (process stuck frozen)
            // into a TimedOut outcome the assertion catches, instead of hanging the test.
            let printer =
                if isWindows then
                    shell "ping -n 2 127.0.0.1 >nul & echo done"
                else
                    shell "sleep 0.5; echo done"
                |> Command.timeout (TimeSpan.FromSeconds 10.0)

            match! group.StartAsync printer with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match group.Suspend() with
                | Ok() -> ()
                | Error error -> Assert.Fail $"suspend: {error}"

                match group.Resume() with
                | Ok() -> ()
                | Error error -> Assert.Fail $"resume: {error}"

                let! outcome = running.WaitAsync()

                match outcome with
                | Outcome.Exited _ -> Assert.Pass()
                | Outcome.TimedOut -> Assert.Fail "process stayed suspended — Resume did not thaw it"
                | other -> Assert.Fail $"{other}"
        }
        :> Task

    // --- T-298: Windows suspend/resume must report a member it did not touch. The walk is best-effort in
    // WHAT it can reach, never in what it CLAIMS: a member that is confirmed live and confirmed ours and
    // still fails becomes a `ProcessError.Io`, while the exit and pid-recycle races stay successful no-ops
    // (a caller may not be told the whole tree is frozen while part of it kept running). The native calls
    // cannot be made to fail on demand on a healthy host, so the two seams under test replace exactly the
    // `OpenProcess` and `NtSuspendProcess`/`NtResumeProcess` calls and nothing else. ---

    [<Test>]
    member _.``Windows Suspend and Resume surface a native delivery failure on a live member``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only: the NtSuspendProcess/NtResumeProcess delivery path."

            use group = create ()

            match! group.StartAsync(sleeper |> Command.timeout (TimeSpan.FromSeconds 15.0)) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let pid =
                    match running.Pid with
                    | Some pid -> pid
                    | None -> failwith "expected a pid"

                let originalSuspend = Windows.suspendProcessHook
                let originalResume = Windows.resumeProcessHook

                try
                    // The member is real, live, and confirmed by IsProcessInJob to be ours — only the
                    // native call fails. There is no benign reading of that: the process kept running.
                    Windows.suspendProcessHook <- fun _ -> WindowsMemberControlInterop.STATUS_ACCESS_DENIED

                    Windows.resumeProcessHook <- fun _ -> WindowsMemberControlInterop.STATUS_ACCESS_DENIED

                    match group.Suspend() with
                    | Error(ProcessError.Io message) ->
                        Assert.That(
                            message,
                            Does.Contain(string pid),
                            "the error must name the member that was not suspended"
                        )

                        Assert.That(message, Does.Contain "suspend", "the error must name the operation")

                        Assert.That(
                            message,
                            Does.Contain "0xC0000022",
                            "the error must carry the NTSTATUS the native call refused with"
                        )
                    | other -> Assert.Fail $"expected Error(ProcessError.Io) from Suspend, got {other}"

                    match group.Resume() with
                    | Error(ProcessError.Io message) ->
                        Assert.That(message, Does.Contain(string pid))
                        Assert.That(message, Does.Contain "resume", "the error must name the operation")
                    | other -> Assert.Fail $"expected Error(ProcessError.Io) from Resume, got {other}"
                finally
                    Windows.suspendProcessHook <- originalSuspend
                    Windows.resumeProcessHook <- originalResume

                // The failed delivery changed nothing about the tree (the seam replaced the whole native
                // call), so the child is still running and still killable.
                running.Kill()
                let! _ = running.WaitAsync()
                ()
        }
        :> Task

    [<Test>]
    member _.``Windows Suspend treats a member that is already terminating as a successful no-op``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only: the NtSuspendProcess NTSTATUS classification."

            use group = create ()

            match! group.StartAsync(sleeper |> Command.timeout (TimeSpan.FromSeconds 15.0)) with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let originalSuspend = Windows.suspendProcessHook

                try
                    // A member that exits between the membership check and the native call is the ordinary
                    // short-lived-grandchild race, not a delivery failure — freezing what is already gone
                    // is a no-op, and failing the whole verb over it would be a false alarm.
                    Windows.suspendProcessHook <- fun _ -> WindowsMemberControlInterop.STATUS_PROCESS_IS_TERMINATING

                    match group.Suspend() with
                    | Ok() -> ()
                    | Error error -> Assert.Fail $"a terminating member must not fail Suspend, got {error}"
                finally
                    Windows.suspendProcessHook <- originalSuspend

                running.Kill()
                let! _ = running.WaitAsync()
                ()
        }
        :> Task

    [<Test>]
    member _.``Windows Suspend skips a member that vanished before the open and still reaches the rest``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only: the OpenProcess classification of the suspend/resume walk."

            use group = create ()

            // Sleeps briefly then prints; the 10s timeout turns a suspend that was never resumed into a
            // TimedOut outcome instead of a hung test.
            let printer =
                shell "ping -n 2 127.0.0.1 >nul & echo done"
                |> Command.timeout (TimeSpan.FromSeconds 10.0)

            match! group.StartAsync printer with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let livePid =
                    match running.Pid with
                    | Some pid -> pid
                    | None -> failwith "expected a pid"

                // A pid the walk is told is a member but that no longer exists. Driven through the open
                // seam ("this pid does not exist") rather than by guessing a dead pid number, which a live
                // process could take at any moment.
                let vanishedPid = 424242

                let originalMembers = Windows.queryInformationJobObjectHook
                let originalOpen = Windows.openControlHandleForTests
                let originalSuspend = Windows.suspendProcessHook
                let suspended = ResizeArray<nativeint>()

                try
                    Windows.queryInformationJobObjectHook <-
                        fun _job _infoClass buffer _bufferSize ->
                            WindowsMemberControlInterop.writeMemberList buffer [ vanishedPid; livePid ]

                    Windows.openControlHandleForTests <-
                        Some(fun pid ->
                            if pid = vanishedPid then
                                Error WindowsMemberControlInterop.ERROR_INVALID_PARAMETER
                            else
                                WindowsMemberControlInterop.openLikeProduction pid)

                    // Count the deliveries, then run the real native call: the surviving member must be
                    // suspended for real (and resumed for real), the vanished one never touched.
                    Windows.suspendProcessHook <-
                        fun handle ->
                            suspended.Add handle
                            originalSuspend handle

                    match group.Suspend() with
                    | Ok() -> ()
                    | Error error -> Assert.Fail $"a vanished member must not fail Suspend, got {error}"

                    match group.Resume() with
                    | Ok() -> ()
                    | Error error -> Assert.Fail $"a vanished member must not fail Resume, got {error}"
                finally
                    Windows.queryInformationJobObjectHook <- originalMembers
                    Windows.openControlHandleForTests <- originalOpen
                    Windows.suspendProcessHook <- originalSuspend

                Assert.That(
                    suspended.Count,
                    Is.EqualTo 1,
                    "exactly the surviving member should have been suspended — the vanished pid must never reach the native call"
                )

                let! outcome = running.WaitAsync()

                match outcome with
                | Outcome.Exited _ -> Assert.Pass()
                | Outcome.TimedOut -> Assert.Fail "process stayed suspended — Resume did not thaw it"
                | other -> Assert.Fail $"{other}"
        }
        :> Task

    [<Test>]
    member _.``Windows Suspend reports a member it could not open while the job still lists it``() =
        // No real processes: both native calls the walk would make are seams, so the classification itself
        // is what is under test. ERROR_ACCESS_DENIED proves only that the open was refused — NOT that the
        // member is gone — and the job still reports the pid, so this is a member that did not get the
        // operation and the caller must hear about it.
        let originalMembers = Windows.queryInformationJobObjectHook
        let originalOpen = Windows.openControlHandleForTests

        try
            Windows.queryInformationJobObjectHook <-
                fun _job _infoClass buffer _bufferSize -> WindowsMemberControlInterop.writeMemberList buffer [ 4242 ]

            Windows.openControlHandleForTests <- Some(fun _ -> Error WindowsMemberControlInterop.ERROR_ACCESS_DENIED)

            match Windows.suspendWindows IntPtr.Zero with
            | Error(ProcessError.Io message) ->
                Assert.That(message, Does.Contain "4242", "the error must name the member that was missed")
            | other -> Assert.Fail $"expected Error(ProcessError.Io), got {other}"
        finally
            Windows.queryInformationJobObjectHook <- originalMembers
            Windows.openControlHandleForTests <- originalOpen

    [<Test>]
    member _.``Windows Suspend accepts a member it could not open once the job no longer lists it``() =
        // Same refused open, opposite verdict: by the time the walk asks the job for a second opinion the
        // pid has left it, which is exactly the exit/recycle race the fail-safe skip exists for. The job
        // is the only authority on its own membership, so this one is a benign no-op, not a failure.
        let originalMembers = Windows.queryInformationJobObjectHook
        let originalOpen = Windows.openControlHandleForTests
        let mutable queries = 0

        try
            Windows.queryInformationJobObjectHook <-
                fun _job _infoClass buffer _bufferSize ->
                    queries <- queries + 1

                    if queries = 1 then
                        WindowsMemberControlInterop.writeMemberList buffer [ 4242 ]
                    else
                        WindowsMemberControlInterop.writeMemberList buffer []

            Windows.openControlHandleForTests <- Some(fun _ -> Error WindowsMemberControlInterop.ERROR_ACCESS_DENIED)

            match Windows.suspendWindows IntPtr.Zero with
            | Ok() -> ()
            | Error error -> Assert.Fail $"a member that left the job must not fail Suspend, got {error}"

            Assert.That(queries, Is.GreaterThan 1, "the refused open must be re-checked against the job")
        finally
            Windows.queryInformationJobObjectHook <- originalMembers
            Windows.openControlHandleForTests <- originalOpen

    // --- T-333: Windows hard termination must report a REFUSAL instead of fabricating success. Neither
    // native kill can be made to fail on demand against a handle we own with full access, so the two seams
    // replace exactly the `TerminateProcess`/`TerminateJobObject` calls and nothing else — everything that
    // follows a refusal (the "is the target actually dead?" classification, and the verbs that carry its
    // verdict out to the caller) runs for real, against real process and Job handles. The injected failure
    // is ERROR_ACCESS_DENIED because that is the code Windows returns BOTH for a kill it will not perform
    // AND for a process that has already terminated: the number alone can never decide between them, which
    // is the whole reason the classification asks the handle instead. ---

    [<Test>]
    member _.``Windows: a refused TerminateProcess on a still-running process is an honest failure``() =
        if not isWindows then
            Assert.Ignore "Windows-only: the TerminateProcess kill path."

        // A REAL, live process: only the terminate call is replaced, so the classification that follows
        // interrogates a genuine handle and can only answer "still running" honestly.
        use external = startExternalSleeper ()
        let original = Windows.terminateProcessHook

        try
            Windows.terminateProcessHook <- fun _ _ -> WindowsMemberControlInterop.refuseTermination ()

            match Windows.terminateWindowsProcess external.Handle with
            | Error(ProcessError.Io message) ->
                Assert.That(
                    message,
                    Does.Contain "still running",
                    "the error must say the target survived, not merely that a call failed"
                )
            | other -> Assert.Fail $"a refused kill of a live process must not be reported as success, got {other}"
        finally
            Windows.terminateProcessHook <- original

        // The seam replaced the whole native call, so nothing was killed and the real kill still works.
        external.Kill()
        external.WaitForExit()

    [<Test>]
    member _.``Windows: a refused TerminateProcess on an already-exited process stays a successful no-op``() =
        if not isWindows then
            Assert.Ignore "Windows-only: the TerminateProcess kill path."

        let original = Windows.terminateProcessHook

        try
            Windows.terminateProcessHook <- fun _ _ -> WindowsMemberControlInterop.refuseTermination ()

            // 7 is an ordinary exit code; 259 is `STILL_ACTIVE`, a legal exit code whose collision with
            // `GetExitCodeProcess`'s "not exited" sentinel is resolved by the process object's signalled
            // state. Both are corpses, so both must be accepted — a kill cannot fail for lack of a target.
            for exitCode in [ 7; 259 ] do
                use exited = WindowsMemberControlInterop.startAndAwaitExit exitCode

                match Windows.terminateWindowsProcess exited.Handle with
                | Ok() -> ()
                | other ->
                    Assert.Fail
                        $"a refused kill of a process that had already exited with {exitCode} must stay Ok, got {other}"
        finally
            Windows.terminateProcessHook <- original

    [<Test>]
    member _.``Windows: a refused TerminateJobObject fails KillAll and Signal Kill on a live tree``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only: the TerminateJobObject tree-kill path."

            use group = create ()

            // A 30-ping child outlasts the whole test, so "the Job still holds live members" is a fact
            // rather than a race with the child's own exit.
            let longSleeper =
                (Command.create "ping" |> Command.args [ "-n"; "30"; "127.0.0.1" ])
                    .Stdout(StdioMode.Null)
                    .Timeout(TimeSpan.FromSeconds 25.0)

            match! group.StartAsync longSleeper with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let original = Windows.terminateJobObjectHook

                try
                    Windows.terminateJobObjectHook <- fun _ _ -> WindowsMemberControlInterop.refuseTermination ()

                    match group.KillAll() with
                    | Error(ProcessError.Io message) ->
                        Assert.That(
                            message,
                            Does.Contain "still live",
                            "the error must say the tree survived the refused terminate"
                        )
                    | other -> Assert.Fail $"a refused job terminate must fail KillAll, got {other}"

                    match group.Signal Signal.Kill with
                    | Error(ProcessError.Io _) -> ()
                    | other -> Assert.Fail $"a refused job terminate must fail Signal Kill, got {other}"
                finally
                    Windows.terminateJobObjectHook <- original

                // The refusal was pure reporting — the tree was never touched, so the real kill still
                // succeeds and the child concludes.
                match group.KillAll() with
                | Ok() -> ()
                | Error error -> Assert.Fail $"the real kill must succeed once the seam is restored, got {error}"

                let! outcome = running.WaitAsync()

                match outcome with
                | Outcome.TimedOut -> Assert.Fail "the child outlived a successful KillAll"
                | _ -> ()
        }
        :> Task

    [<Test>]
    member _.``Windows: a refused TerminateJobObject on a drained tree stays a successful no-op``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only: the TerminateJobObject tree-kill path."

            use group = create ()

            match! group.StartAsync(shell "echo drained") with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                // Run it to completion first: the Job is real and open, but holds no live member, so a
                // refused terminate had nothing left to kill.
                let! _ = running.WaitAsync()
                let original = Windows.terminateJobObjectHook

                try
                    Windows.terminateJobObjectHook <- fun _ _ -> WindowsMemberControlInterop.refuseTermination ()

                    match group.KillAll() with
                    | Ok() -> ()
                    | other -> Assert.Fail $"a refused terminate of an already-drained tree must stay Ok, got {other}"
                finally
                    Windows.terminateJobObjectHook <- original
        }
        :> Task

    [<Test>]
    member _.``Windows: killing the same child twice reports success both times``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows-only: the TerminateProcess kill path."

            use group = create ()

            let longSleeper =
                (Command.create "ping" |> Command.args [ "-n"; "30"; "127.0.0.1" ])
                    .Stdout(StdioMode.Null)
                    .Timeout(TimeSpan.FromSeconds 25.0)

            // No seam at all here: this is the REAL `TerminateProcess`, whose second call lands on a
            // process that has already exited — the case Windows itself answers with ERROR_ACCESS_DENIED,
            // and the exact regression an error-number-based classification would introduce.
            match! group.StartAsync longSleeper with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                match running.Signal Signal.Kill with
                | Ok() -> ()
                | Error error -> Assert.Fail $"the first kill must succeed, got {error}"

                // Awaiting the shared exit observation does not tear the run down, so the repeat kill below
                // still reaches the backend — with the child provably concluded by then. (It does claim the
                // handle's one consumption, so the teardown here is a dispose rather than `WaitAsync`.)
                let! outcome = running.ExitTask

                match outcome with
                | Outcome.TimedOut -> Assert.Fail "the child outlived the first kill"
                | _ -> ()

                match running.Signal Signal.Kill with
                | Ok() -> ()
                | Error error -> Assert.Fail $"killing an already-dead child must stay idempotent, got {error}"

                do! (running :> IAsyncDisposable).DisposeAsync()
        }
        :> Task

    [<Test>]
    member _.``a ProcessGroup is an IProcessRunner that runs into the shared group``() : Task =
        task {
            use group = create ()
            let runner: IProcessRunner = group

            match! runner.OutputStringAsync(shell "echo shared", CancellationToken.None) with
            | Ok result -> Assert.That(result.Stdout, Does.Contain "shared")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``Supervisor can restart into a shared ProcessGroup``() : Task =
        task {
            use group = create ()

            let sup =
                Supervisor(shell "echo supervised").Restart(RestartPolicy.Never).WithRunner(group :> IProcessRunner)

            match! sup.RunAsync() with
            | Ok outcome -> Assert.That(outcome.FinalResult.Stdout, Does.Contain "supervised")
            | Error error -> Assert.Fail $"{error}"
        }
        :> Task

    [<Test>]
    member _.``a pipeline timeout reaps a long-running first stage``() : Task =
        task {
            // The sleeper is the FIRST stage (its own pgid on POSIX); the consumer exits at once.
            // Only the multi-child containment fix kills the first stage promptly — without it the
            // pipeline would block on the 30s sleeper's natural exit.
            let longSleeper =
                if isWindows then
                    shell "ping -n 31 127.0.0.1 >nul"
                else
                    shell "sleep 30"

            let pipeline =
                longSleeper.Pipe(shell "echo done").Timeout(TimeSpan.FromMilliseconds 300.0)

            let stopwatch = Stopwatch.StartNew()
            let! result = pipeline.RunAsync()
            stopwatch.Stop()

            match result with
            | Error(ProcessError.Timeout _) -> ()
            | other -> Assert.Fail $"expected Timeout, got {other}"

            Assert.That(
                stopwatch.Elapsed,
                Is.LessThan(TimeSpan.FromSeconds 10.0),
                "the long-running first stage was not reaped promptly"
            )
        }
        :> Task

    // --- T-069: a cancellable, fully observable stdin feeder (RunningProcess + ProcessGroup call sites).
    // The `HangingStdinAsyncLines` / `FaultyStdin*` doubles are shared from `PipelineTests.fs`. The fault
    // tests feed a stdin-reading child (`sort`, reads to EOF then exits 0) so a source fault that closes
    // stdin as its last act is always observed before the child — and thus the run — exits. ---

    [<Test>]
    member _.``a run stops a hung async stdin feed on teardown and disposes its enumerator``() : Task =
        task {
            // The child exits at once without reading stdin, so the `FromAsyncLines` feed is left parked
            // in `MoveNextAsync`. The run's teardown must Stop the feeder — cancelling the hung feed and
            // disposing the user's enumerator — instead of leaking it past the run (early child exit).
            let source = HangingStdinAsyncLines()
            let cmd = (shell "exit 0") |> Command.stdin (Stdin.FromAsyncLines source)

            match! cmd.OutputStringAsync() with
            | Ok _ -> ()
            | Error error -> Assert.Fail $"expected a successful run, got {error}"

            let! completed = Task.WhenAny(source.Disposed, Task.Delay 5000)
            Assert.That(completed, Is.SameAs source.Disposed, "the hung async enumerator was never disposed")
        }
        :> Task

    [<Test>]
    member _.``cancelling a run during a hung stdin feed reports Cancelled and disposes the enumerator``() : Task =
        task {
            // A live child plus a hung `FromAsyncLines` feed: cancelling the run mid-feed must report
            // `Cancelled` and — via teardown — Stop the feeder so the parked enumerator is disposed.
            let source = HangingStdinAsyncLines()
            let cmd = sleeper |> Command.stdin (Stdin.FromAsyncLines source)
            use cts = new CancellationTokenSource()
            let run = cmd.OutputStringAsync cts.Token

            // Cancel only once the feed is genuinely parked in `MoveNextAsync`.
            let! started = Task.WhenAny(source.Started, Task.Delay 5000)
            Assert.That(started, Is.SameAs source.Started, "the async feed never started")
            cts.Cancel()

            match! run with
            | Error(ProcessError.Cancelled _) -> ()
            | Error other -> Assert.Fail $"expected Cancelled, got {other.Message}"
            | Ok _ -> Assert.Fail "expected a cancelled run to error"

            let! completed = Task.WhenAny(source.Disposed, Task.Delay 5000)
            Assert.That(completed, Is.SameAs source.Disposed, "the parked enumerator was never disposed on cancel")
        }
        :> Task

    [<Test>]
    member _.``a FromLines source that throws at GetEnumerator surfaces as ProcessError.Stdin``() : Task =
        task {
            // The sync source faults acquiring its enumerator — the entry stage the pre-fix code let
            // slip past into the benign-broken-pipe bucket. On an otherwise-successful run it must
            // surface as `ProcessError.Stdin`.
            let cmd =
                (Command.create "sort")
                |> Command.stdin (Stdin.FromLines(FaultyStdinLines AtGetEnumerator))

            match! cmd.OutputStringAsync() with
            | Error(ProcessError.Stdin _) -> ()
            | Error other -> Assert.Fail $"expected ProcessError.Stdin, got {other.Message}"
            | Ok _ -> Assert.Fail "expected a GetEnumerator fault to surface as ProcessError.Stdin"
        }
        :> Task

    [<Test>]
    member _.``a FromAsyncLines source that throws at MoveNextAsync surfaces as ProcessError.Stdin``() : Task =
        task {
            let cmd =
                (Command.create "sort")
                |> Command.stdin (Stdin.FromAsyncLines(FaultyStdinAsyncLines AtMoveNext))

            match! cmd.OutputStringAsync() with
            | Error(ProcessError.Stdin _) -> ()
            | Error other -> Assert.Fail $"expected ProcessError.Stdin, got {other.Message}"
            | Ok _ -> Assert.Fail "expected a MoveNextAsync fault to surface as ProcessError.Stdin"
        }
        :> Task

    [<Test>]
    member _.``a FromAsyncLines source that throws at Current surfaces as ProcessError.Stdin``() : Task =
        task {
            let cmd =
                (Command.create "sort")
                |> Command.stdin (Stdin.FromAsyncLines(FaultyStdinAsyncLines AtCurrent))

            match! cmd.OutputStringAsync() with
            | Error(ProcessError.Stdin _) -> ()
            | Error other -> Assert.Fail $"expected ProcessError.Stdin, got {other.Message}"
            | Ok _ -> Assert.Fail "expected a Current fault to surface as ProcessError.Stdin"
        }
        :> Task

    [<Test>]
    member _.``ProcessGroup StartAsync stops a hung stdin feed when the shared run is disposed``() : Task =
        task {
            // The shared-group start path (BuildHost with ownsGroup=false) must Stop the feeder on the
            // run's teardown too: disposing the `RunningProcess` cancels the hung feed and disposes the
            // user's enumerator.
            use group = create ()
            let source = HangingStdinAsyncLines()
            let cmd = (shell "exit 0") |> Command.stdin (Stdin.FromAsyncLines source)

            match! group.StartAsync cmd with
            | Error error -> Assert.Fail $"{error}"
            | Ok running ->
                let! started = Task.WhenAny(source.Started, Task.Delay 5000)
                Assert.That(started, Is.SameAs source.Started, "the shared-run async feed never started")
                let! _ = running.WaitAsync()
                do! (running :> IAsyncDisposable).DisposeAsync()

                let! completed = Task.WhenAny(source.Disposed, Task.Delay 5000)
                Assert.That(completed, Is.SameAs source.Disposed, "disposing the shared run did not stop the hung feed")
        }
        :> Task

    [<Test>]
    member _.``Stdin factories reject null arguments at the API boundary``() =
        Assert.Throws<ArgumentNullException>(Action(fun () -> Stdin.FromString(Unchecked.defaultof<string>) |> ignore))
        |> ignore

        Assert.Throws<ArgumentNullException>(Action(fun () -> Stdin.FromBytes(Unchecked.defaultof<byte[]>) |> ignore))
        |> ignore

        Assert.Throws<ArgumentNullException>(Action(fun () -> Stdin.FromFile(Unchecked.defaultof<string>) |> ignore))
        |> ignore

        Assert.Throws<ArgumentNullException>(
            Action(fun () -> Stdin.FromStream(Unchecked.defaultof<IO.Stream>) |> ignore)
        )
        |> ignore

        Assert.Throws<ArgumentNullException>(
            Action(fun () -> Stdin.FromLines(Unchecked.defaultof<seq<string>>) |> ignore)
        )
        |> ignore

        Assert.Throws<ArgumentNullException>(
            Action(fun () ->
                Stdin.FromAsyncLines(Unchecked.defaultof<Collections.Generic.IAsyncEnumerable<string>>)
                |> ignore)
        )
        |> ignore

    // --- ProcessGroup.Adopt: bring an already-running EXTERNAL process into the container (T-187) ---

    [<Test>]
    member _.``Adopt of an already-exited process is an honest typed error, never a silent success``() : Task =
        task {
            // The dead-pid / TOCTOU guard, cross-platform: a concluded process cannot be adopted.
            // `ProcessGroup.Adopt`'s pre-adopt liveness check refuses it with a typed `ProcessError.Adopt`
            // BEFORE the mechanism is even consulted — so this holds on every platform, adopting or not.
            let psi =
                if isWindows then
                    ProcessStartInfo("cmd.exe", "/c exit 0")
                else
                    ProcessStartInfo("/bin/sh", "-c \"exit 0\"")

            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true

            use external =
                match Process.Start psi with
                | null -> failwith "failed to start the external test process"
                | p -> p

            do! external.WaitForExitAsync()

            use group = create ()

            match group.Adopt external with
            | Error(ProcessError.Adopt _) -> ()
            | other -> Assert.Fail $"expected ProcessError.Adopt for an already-exited process, got {other}"
        }
        :> Task

    [<Test>]
    member _.``Adopt places an external process into the group and kill-on-dispose reaps it (Windows)``() : Task =
        task {
            if not isWindows then
                Assert.Ignore "Windows Job Object adopts an external process; the POSIX refusal is covered separately"
            else
                let external = startExternalSleeper ()

                try
                    use group = create ()

                    match group.Adopt external with
                    | Error error -> Assert.Fail $"adopt should succeed on the Job Object mechanism, got {error}"
                    | Ok() ->
                        // Now a full Job member: it shows up in the membership snapshot...
                        match group.Members() with
                        | Ok pids -> Assert.That(pids, Does.Contain external.Id)
                        | Error error -> Assert.Fail $"Members failed: {error}"

                        // ...and disposing the group (kill-on-dispose) terminates it, even though we never
                        // started it — the whole point of adoption.
                        (group :> IDisposable).Dispose()

                        let! _ = Task.WhenAny(external.WaitForExitAsync(), Task.Delay 5000)

                        Assert.That(
                            external.HasExited,
                            Is.True,
                            "kill-on-dispose should have reaped the adopted process"
                        )
                finally
                    try
                        if not external.HasExited then
                            external.Kill true
                    with _ ->
                        // Best-effort test cleanup: the process was likely already killed by group dispose,
                        // or the handle is racing teardown — nothing to recover here.
                        ()

                    external.Dispose()
        }
        :> Task

    [<Test>]
    member _.``Adopt on the POSIX process-group mechanism is an honest Unsupported (non-Windows)``() : Task =
        task {
            if isWindows then
                Assert.Ignore "POSIX process-group mechanism only; Windows adopts via the Job Object"
            else
                let external = startExternalSleeper ()

                try
                    // A limit-free group on POSIX uses the process-group backend, which cannot relocate a
                    // foreign process (setpgid only moves our own children, before exec) — an honest typed
                    // refusal, never a silent no-op that would leave the process uncontained.
                    use group = create ()

                    match group.Adopt external with
                    | Error(ProcessError.Unsupported _) -> ()
                    | other -> Assert.Fail $"expected Unsupported adopting into a POSIX process group, got {other}"
                finally
                    try
                        if not external.HasExited then
                            external.Kill true
                    with _ ->
                        // Best-effort cleanup; the sleeper exits on its own if the test outlives it.
                        ()

                    external.Dispose()
        }
        :> Task
